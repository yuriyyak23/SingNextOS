using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class ExecutableAdapterStartAdmissionTests
{
    [Theory]
    [InlineData("admission-no-commit")] [InlineData("source-no-commit")]
    [InlineData("admission-fail")] [InlineData("admission-throw")]
    [InlineData("source-fail")] [InlineData("source-throw")]
    [InlineData("admission-reentry")] [InlineData("source-reentry")]
    public void StartAdmissionCallbacksCannotLeakOrInventPendingInterlocks(string mode)
    {
        Scenario s = CreateScenario();
        KernelResult Callback(Func<KernelResult> commit)
        {
            if (mode.EndsWith("no-commit")) return KernelResult.Ok();
            Assert.True(commit().IsSuccess);
            if (mode.EndsWith("reentry")) Assert.False(commit().IsSuccess);
            if (mode.EndsWith("throw")) throw new IOException("Admission callback lost after commit.");
            return KernelResult.Fail(KernelError.PlatformDenied, "Admission refused after commit.");
        }
        KernelResult result = s.Bridge.StartChildExecutableArtifact(s.Artifact,
            admissionConsumer: mode.StartsWith("admission") ? Callback : null,
            sourceCommitConsumer: mode.StartsWith("source") ? Callback : null);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Spy.StartCalls);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object artifact = Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId);
        AssertBalanced(child, guest, artifact);
        Assert.Equal(PlatformChildDomainState.Created, Property(child, "State"));
        Assert.False((bool)Property(artifact, "Started")!);
        Assert.True(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(1, s.Spy.StartCalls);
        AssertBalanced(child, guest, artifact);
    }

    [Theory]
    [InlineData(false, "exact")] [InlineData(true, "exact")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    [InlineData(false, "throw")] [InlineData(true, "throw")]
    [InlineData(false, "tuple")] [InlineData(true, "tuple")]
    public void PlatformCleanupOwnerReplacementRetainsOriginalOutcomeWithoutPublishingOrRemovingReplacement(bool unmap, string fault)
    {
        Scenario s = CreateScenario(childOnly: !unmap, guestOnly: unmap, closeOutcome: NeutralVirtualizationStatus.NotInvoked);
        GenericFailureProvider wrapper = (GenericFailureProvider)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_childRuntime", Private)!.GetValue(s.Provider)!;
        wrapper.CloseOutcome = fault == "unknown" && !unmap ? NeutralVirtualizationStatus.Denied : null;
        wrapper.UnmapOutcome = fault == "unknown" && unmap ? NeutralVirtualizationStatus.Denied : null;
        wrapper.ThrowAfterCleanup = fault == "throw";
        PlatformProviderChildDomainLease childLease = (PlatformProviderChildDomainLease)Property(Record(s.Bridge, "_childBindings", s.Child.BindingId), "ProviderLease")!;
        PlatformProviderGuestRegionMappingLease guestLease = unmap ? (PlatformProviderGuestRegionMappingLease)Property(
            Record(s.Bridge, "_guestBindings", s.Guest.MappingId), "ProviderLease")! : default;
        string field = unmap ? "_providerGuestMappings" : "_providerChildren";
        object key = unmap ? guestLease.LeaseId : childLease.LeaseId;
        IDictionary owners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField(field, Private)!.GetValue(s.Provider)!;
        object original = owners[key]!;
        object? replacement = null;
        Action replace = () =>
        {
            replacement = original.GetType().GetMethod("<Clone>$")!.Invoke(original, null)!;
            if (fault == "tuple") replacement.GetType().GetProperty("Lease")!.SetValue(replacement,
                unmap ? guestLease with { Generation = new(2) } : childLease with { Generation = new(2) });
            owners[key] = replacement;
        };
        if (unmap) s.Spy.BeforeUnmap = replace;
        else wrapper.BeforeClose = replace;
        PlatformAuthorityStatus Invoke() => unmap ? s.Provider.UnmapGuestRegion(guestLease).Status : s.Provider.CloseChildDomain(childLease).Status;
        if (fault == "throw") Assert.Throws<InvalidOperationException>(() => Invoke());
        else if (fault == "unknown") Assert.NotEqual(PlatformAuthorityStatus.Success, Invoke());
        else Assert.Equal(PlatformAuthorityStatus.Faulted, Invoke());
        wrapper.BeforeClose = null;
        s.Spy.BeforeUnmap = null;
        Assert.NotNull(replacement);
        Assert.Same(replacement, owners[key]);
        Assert.Equal(1, unmap ? wrapper.UnmapInvocations : wrapper.CloseInvocations);
        Assert.Equal(1, unmap ? s.Spy.UnmapCalls : s.Spy.CloseCalls);
        Assert.Equal(PlatformAuthorityStatus.Denied, s.Provider.RevokeDomain(childLease.ParentDomainLease).Status);
        if (fault == "tuple")
        {
            Assert.NotNull(Property(original, "ClosureReceipt"));
            Assert.Null(Property(replacement!, "ClosureReceipt"));
            Assert.Equal(PlatformAuthorityStatus.Stale, Invoke());
        }
        else
        {
            Assert.Null(Property(replacement!, "ClosureReceipt"));
            Assert.Null(Property(replacement!, unmap ? "FailedUnmap" : "FailedClose"));
            Assert.NotEqual(PlatformAuthorityStatus.Success, Invoke());
            Assert.Same(replacement, owners[key]);
            owners[key] = original;
            Assert.Equal(false, Property(original, unmap ? "UnmapInFlight" : "CloseInFlight"));
            if (fault == "exact")
            {
                Assert.NotNull(Property(original, "ClosureReceipt"));
                Assert.NotNull(Property(original, "ClosureSource"));
                if (!unmap) Assert.Equal(false, Property(original, "ClosurePublished"));
                PropertyInfo receiptProperty = original.GetType().GetProperty("ClosureReceipt")!;
                object? exactReceipt = receiptProperty.GetValue(original);
                receiptProperty.SetValue(original, unmap ?
                    ((NeutralGuestMappingCloseReceipt)exactReceipt!) with { MappingEpoch = new(2) } :
                    ((NeutralChildDomainCloseReceipt)exactReceipt!) with { ChildEpoch = new(2) });
                Assert.Equal(PlatformAuthorityStatus.Faulted, Invoke());
                Assert.Same(original, owners[key]);
                receiptProperty.SetValue(original, exactReceipt);
                PropertyInfo sourceProperty = original.GetType().GetProperty("ClosureSource")!;
                object exactSource = sourceProperty.GetValue(original)!;
                object wrongSource = exactSource.GetType().GetMethod("<Clone>$")!.Invoke(exactSource, null)!;
                wrongSource.GetType().GetProperty("Lease")!.SetValue(wrongSource,
                    unmap ? guestLease with { Generation = new(2) } : childLease with { Generation = new(2) });
                sourceProperty.SetValue(original, wrongSource);
                Assert.Equal(PlatformAuthorityStatus.Faulted, Invoke());
                Assert.Same(original, owners[key]);
                sourceProperty.SetValue(original, exactSource);
                typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!.SetValue(s.Adapter, ulong.MaxValue);
                Assert.Equal(PlatformAuthorityStatus.Success, Invoke());
                if (unmap) Assert.False(owners.Contains(key));
                else
                {
                    Assert.Equal(true, Property(original, "ClosurePublished"));
                    sourceProperty.SetValue(original, wrongSource);
                    Assert.Equal(PlatformAuthorityStatus.Denied, s.Provider.RevokeDomain(childLease.ParentDomainLease).Status);
                    sourceProperty.SetValue(original, exactSource);
                    receiptProperty.SetValue(original, ((NeutralChildDomainCloseReceipt)exactReceipt!) with { ChildEpoch = new(2) });
                    Assert.Equal(PlatformAuthorityStatus.Denied, s.Provider.RevokeDomain(childLease.ParentDomainLease).Status);
                    receiptProperty.SetValue(original, exactReceipt);
                    Assert.True(s.Provider.RevokeDomain(childLease.ParentDomainLease).IsSuccess);
                    Assert.Equal(PlatformAuthorityStatus.Success, Invoke()); // already-published original closure stays idempotent
                }
            }
            else
            {
                object? failure = Property(original, unmap ? "FailedUnmap" : "FailedClose");
                Assert.NotNull(failure);

                Assert.Null(Property(original, "ClosureReceipt"));
                Assert.Equal(PlatformAuthorityStatus.Faulted, Invoke());
                Assert.Same(failure, Property(original, unmap ? "FailedUnmap" : "FailedClose"));
            }
        }
        Assert.Equal(1, unmap ? wrapper.UnmapInvocations : wrapper.CloseInvocations);
        Assert.Equal(1, unmap ? s.Spy.UnmapCalls : s.Spy.CloseCalls);
    }

    [Fact]
    public void PlatformUnmapRetainsClosureAndRefusesRemovalWhenChildOwnerChangesDuringCallback()
    {
        Scenario s = CreateScenario(guestOnly: true, closeOutcome: NeutralVirtualizationStatus.NotInvoked);
        GenericFailureProvider wrapper = (GenericFailureProvider)typeof(HybridCpuPlatformAuthorityProvider).GetField("_childRuntime", Private)!.GetValue(s.Provider)!;
        PlatformProviderGuestRegionMappingLease lease = (PlatformProviderGuestRegionMappingLease)Property(Record(s.Bridge, "_guestBindings", s.Guest.MappingId), "ProviderLease")!;
        object mapping = Record(s.Provider, "_providerGuestMappings", lease.LeaseId);
        IDictionary children = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerChildren", Private)!.GetValue(s.Provider)!;
        object originalChild = children[lease.ChildLease.LeaseId]!;
        object? replacement = null;
        s.Spy.BeforeUnmap = () =>
        {
            replacement = originalChild.GetType().GetMethod("<Clone>$")!.Invoke(originalChild, null)!;
            children[lease.ChildLease.LeaseId] = replacement;
        };
        Assert.Equal(PlatformAuthorityStatus.Faulted, s.Provider.UnmapGuestRegion(lease).Status);
        s.Spy.BeforeUnmap = null;
        Assert.Same(replacement, children[lease.ChildLease.LeaseId]);
        Assert.Same(mapping, Record(s.Provider, "_providerGuestMappings", lease.LeaseId));
        Assert.NotNull(Property(mapping, "ClosureReceipt"));
        Assert.Equal(false, Property(mapping, "UnmapInFlight"));
        Assert.False(s.Provider.UnmapGuestRegion(lease).IsSuccess);
        Assert.Same(mapping, Record(s.Provider, "_providerGuestMappings", lease.LeaseId));
        Assert.Equal(1, wrapper.UnmapInvocations);
        // Exact original source restoration is test arrangement, not a production recovery route.
        children[lease.ChildLease.LeaseId] = originalChild;
        Assert.True(s.Provider.UnmapGuestRegion(lease).IsSuccess);
        Assert.Equal(1, wrapper.UnmapInvocations);
        Assert.Equal(1, s.Spy.UnmapCalls);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("unmap")]
    [InlineData("transition")]
    [InlineData("active")]
    public void PlatformChildCloseRefusesPendingOrMappedDependenciesWithoutNeutralInvocationOrFailurePin(string seam)
    {
        bool hasGuest = seam is "unmap" or "active";
        Scenario s = CreateScenario(closeOnly: !hasGuest, guestOnly: hasGuest, closeOutcome: NeutralVirtualizationStatus.NotInvoked);
        GenericFailureProvider wrapper = (GenericFailureProvider)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_childRuntime", Private)!.GetValue(s.Provider)!;
        PlatformProviderChildDomainLease lease = (PlatformProviderChildDomainLease)Property(Record(s.Bridge, "_childBindings", s.Child.BindingId), "ProviderLease")!;
        object owner = Record(s.Provider, "_providerChildren", lease.LeaseId);
        bool checkedRefusal = false;
        Action probe = () =>
        {
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, s.Provider.CloseChildDomain(lease).Status);
            Assert.Equal(0, wrapper.CloseInvocations);
            Assert.Equal(0, s.Spy.CloseCalls);
            Assert.Null(Property(owner, "FailedClose"));
            Assert.Null(Property(owner, "ClosureReceipt"));
            Assert.Equal(false, Property(owner, "CloseInFlight"));
            Assert.Same(owner, Record(s.Provider, "_providerChildren", lease.LeaseId));
            checkedRefusal = true;
        };
        if (seam == "map")
        {
            s.Spy.BeforeMap = probe;
            KernelResult<PlatformGuestMapping> mapped = s.Bridge.MapChildGuestRegion(s.Child, s.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read);
            s.Spy.BeforeMap = null;
            Assert.True(mapped.IsSuccess, mapped.Message);
            Assert.True(s.Bridge.UnmapChildGuestRegion(mapped.Value).IsSuccess);
        }
        else if (seam == "unmap")
        {
            s.Spy.BeforeUnmap = probe;
            Assert.True(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
            s.Spy.BeforeUnmap = null;
        }
        else if (seam == "transition")
        {
            wrapper.BeforeTransition = probe;
            Assert.True(s.Bridge.TransitionChildDomain(s.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            wrapper.BeforeTransition = null;
        }
        else
        {
            probe();
            Assert.True(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        }
        Assert.True(checkedRefusal);
        wrapper.CloseOutcome = null;
        PlatformAuthorityResult<PlatformChildDomainClosureReceipt> closed = s.Provider.CloseChildDomain(lease);
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.True(PlatformChildDomainContract.ValidateClosureReceipt(lease, closed.Value!).IsSuccess);
        Assert.Equal(1, wrapper.CloseInvocations);
        Assert.Equal(1, s.Spy.CloseCalls);
        Assert.Null(Property(owner, "FailedClose"));
        Assert.NotNull(Property(owner, "ClosureReceipt"));
        Assert.True(s.Provider.CloseChildDomain(lease).IsSuccess);
        Assert.Equal(1, wrapper.CloseInvocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlatformChildCloseKeepsOriginalUnknownTransitionOrUnmapPinnedWithoutInvokingNeutralClose(bool unmap)
    {
        Scenario s = CreateScenario(closeOnly: !unmap, guestOnly: unmap,
            closeOutcome: NeutralVirtualizationStatus.NotInvoked, unmapOutcome: unmap ? NeutralVirtualizationStatus.Denied : null);
        GenericFailureProvider wrapper = (GenericFailureProvider)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_childRuntime", Private)!.GetValue(s.Provider)!;
        PlatformProviderChildDomainLease lease = (PlatformProviderChildDomainLease)Property(Record(s.Bridge, "_childBindings", s.Child.BindingId), "ProviderLease")!;
        object owner = Record(s.Provider, "_providerChildren", lease.LeaseId);
        object failureOwner = owner;
        if (unmap)
        {
            PlatformProviderGuestRegionMappingLease guestLease = (PlatformProviderGuestRegionMappingLease)Property(Record(s.Bridge, "_guestBindings", s.Guest.MappingId), "ProviderLease")!;
            failureOwner = Record(s.Provider, "_providerGuestMappings", guestLease.LeaseId);
            Assert.False(s.Provider.UnmapGuestRegion(guestLease).IsSuccess);
        }
        else Assert.False(s.Provider.TransitionChildDomain(lease, PlatformChildDomainTransition.Park).IsSuccess);
        object? failure = Property(failureOwner, unmap ? "FailedUnmap" : "FailedTransition");
        Assert.NotNull(failure);
        Assert.Equal(PlatformAuthorityStatus.Faulted, s.Provider.CloseChildDomain(lease).Status);
        Assert.Equal(PlatformAuthorityStatus.Faulted, s.Provider.CloseChildDomain(lease).Status);
        Assert.Equal(0, wrapper.CloseInvocations);
        Assert.Equal(0, s.Spy.CloseCalls);
        Assert.Same(failure, Property(failureOwner, unmap ? "FailedUnmap" : "FailedTransition"));
        Assert.Null(Property(owner, "FailedClose"));
        Assert.Null(Property(owner, "ClosureReceipt"));
        Assert.Equal(false, Property(owner, "CloseInFlight"));
    }

    [Theory]
    [InlineData(false, "denied")] [InlineData(true, "denied")]
    [InlineData(false, "unsupported")] [InlineData(true, "unsupported")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")]
    [InlineData(false, "ambiguous")] [InlineData(true, "ambiguous")]
    [InlineData(false, "throw")] [InlineData(true, "throw")]
    [InlineData(false, "malformed")] [InlineData(true, "malformed")]
    [InlineData(false, "notInvoked")] [InlineData(true, "notInvoked")]
    public void PlatformCleanupPinsOriginalFailureBeforeNeutralRetryAndNotInvokedRemainsRetryable(bool unmap, string fault)
    {
        NeutralVirtualizationStatus outcome = fault switch
        {
            "unsupported" => NeutralVirtualizationStatus.Unsupported,
            "unknown" => (NeutralVirtualizationStatus)777,
            "ambiguous" => NeutralVirtualizationStatus.Ambiguous,
            "malformed" => NeutralVirtualizationStatus.Success,
            "notInvoked" => NeutralVirtualizationStatus.NotInvoked,
            _ => NeutralVirtualizationStatus.Denied,
        };
        Scenario s = CreateScenario(closeOnly: !unmap, guestOnly: unmap,
            closeOutcome: unmap ? null : outcome, unmapOutcome: unmap ? outcome : null);
        GenericFailureProvider wrapper = (GenericFailureProvider)typeof(HybridCpuPlatformAuthorityProvider)
            .GetField("_childRuntime", Private)!.GetValue(s.Provider)!;
        wrapper.ThrowAfterCleanup = fault == "throw";
        PlatformProviderChildDomainLease childLease = (PlatformProviderChildDomainLease)Property(Record(s.Bridge, "_childBindings", s.Child.BindingId), "ProviderLease")!;
        PlatformProviderGuestRegionMappingLease guestLease = unmap ? (PlatformProviderGuestRegionMappingLease)Property(
            Record(s.Bridge, "_guestBindings", s.Guest.MappingId), "ProviderLease")! : default;
        object owner = unmap ? Record(s.Provider, "_providerGuestMappings", guestLease.LeaseId) :
            Record(s.Provider, "_providerChildren", childLease.LeaseId);
        PlatformAuthorityStatus Invoke() => unmap ? s.Provider.UnmapGuestRegion(guestLease).Status :
            s.Provider.CloseChildDomain(childLease).Status;
        if (fault == "throw") Assert.Throws<InvalidOperationException>(() => Invoke());
        else Assert.NotEqual(PlatformAuthorityStatus.Success, Invoke());
        Assert.Equal(1, unmap ? wrapper.UnmapInvocations : wrapper.CloseInvocations);
        Assert.Equal(fault == "notInvoked" ? 0 : 1, unmap ? s.Spy.UnmapCalls : s.Spy.CloseCalls);
        Assert.Equal(false, Property(owner, unmap ? "UnmapInFlight" : "CloseInFlight"));
        object? failure = Property(owner, unmap ? "FailedUnmap" : "FailedClose");
        wrapper.CloseOutcome = null;
        wrapper.UnmapOutcome = null;
        wrapper.ThrowAfterCleanup = false;
        if (fault == "notInvoked")
        {
            Assert.Null(failure);
            Assert.Equal(PlatformAuthorityStatus.Success, Invoke());
            Assert.Equal(2, unmap ? wrapper.UnmapInvocations : wrapper.CloseInvocations);
            Assert.Equal(1, unmap ? s.Spy.UnmapCalls : s.Spy.CloseCalls);
            Invoke();
            Assert.Equal(2, unmap ? wrapper.UnmapInvocations : wrapper.CloseInvocations);
        }
        else
        {
            Assert.NotNull(failure);
            Assert.Equal(Property(owner, "Neutral"), Property(failure!, "Request"));
            if (fault == "throw") Assert.Null(Property(failure!, "Observation"));
            Assert.Equal(PlatformAuthorityStatus.Faulted, Invoke());
            Assert.Equal(PlatformAuthorityStatus.Faulted, Invoke());
            Assert.Equal(1, unmap ? wrapper.UnmapInvocations : wrapper.CloseInvocations);
            Assert.Equal(1, unmap ? s.Spy.UnmapCalls : s.Spy.CloseCalls);
            Assert.Same(failure, Property(owner, unmap ? "FailedUnmap" : "FailedClose"));
            Assert.Same(owner, unmap ? Record(s.Provider, "_providerGuestMappings", guestLease.LeaseId) :
                Record(s.Provider, "_providerChildren", childLease.LeaseId));
            if (!unmap) Assert.Null(Property(owner, "ClosureReceipt"));
            Assert.Equal(false, Property(owner, unmap ? "UnmapInFlight" : "CloseInFlight"));
        }
    }

    [Fact]
    public void LifecycleStartRefusalIsPreEffectAndDoesNotPinExactExecutableStart()
    {
        Scenario s = CreateScenario();
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        PlatformProviderChildDomainLease lease = (PlatformProviderChildDomainLease)Property(child, "ProviderLease");
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            s.Provider.TransitionChildDomain(lease, PlatformChildDomainTransition.Start).Status);
        Assert.Equal(0, s.Spy.TransitionCalls);
        IDictionary facts = (IDictionary)s.Provider.GetType().GetField("_providerChildren", Private)!.GetValue(s.Provider)!;
        object owner = facts[lease.LeaseId]!;
        Assert.Null(Property(owner, "FailedTransition"));
        Assert.False((bool)Property(owner, "TransitionInFlight"));
        s.Spy.BeforeStart = () =>
        {
            IDictionary adapterChildren = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", Private)!.GetValue(s.Adapter)!;
            object adapterChild = Assert.Single(adapterChildren.Values.Cast<object>());
            NeutralChildDomainLease neutral = (NeutralChildDomainLease)Property(adapterChild, "Neutral");
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                s.Adapter.TransitionChildDomain(neutral, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.Same(adapterChild, Assert.Single(adapterChildren.Values.Cast<object>()));
            IDictionary adapterMappings = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("mappings", Private)!.GetValue(s.Adapter)!;
            object originalMapping = Assert.Single(adapterMappings.Values.Cast<object>());
            NeutralGuestMappingLease guestLease = (NeutralGuestMappingLease)Property(originalMapping, "Neutral");
            int beforeMap = s.Spy.MapCalls;
            IDictionary artifactOwners = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("artifacts", Private)!.GetValue(s.Adapter)!;
            object originalArtifact = Assert.Single(artifactOwners.Values.Cast<object>());
            int beforeBind = s.Spy.ArtifactBindCalls;
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                s.Adapter.BindExecutableArtifact(new(neutral, guestLease, new byte[] { 1 }, 1)).Status);
            Assert.Equal(beforeBind, s.Spy.ArtifactBindCalls);
            Assert.Same(originalArtifact, Assert.Single(artifactOwners.Values.Cast<object>()));
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, s.Adapter.UnmapGuestRegion(guestLease).Status);
            Assert.Equal(0, s.Spy.UnmapCalls);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, s.Adapter.MapGuestRegion(new(neutral,
                guestLease.ParentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
            Assert.Equal(beforeMap, s.Spy.MapCalls);
            Assert.Same(originalMapping, Assert.Single(adapterMappings.Values.Cast<object>()));
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                s.Adapter.TransitionChildDomain(neutral, NeutralChildDomainTransition.Park).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, s.Adapter.CloseChildDomain(neutral).Status);
            Assert.Equal(0, s.Spy.CloseCalls);
            Assert.Equal(0, s.Spy.TransitionCalls);
        };
        Assert.True(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.NotNull(Property(Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId), "ExecutionReceipt"));
        Assert.Equal(0, s.Spy.TransitionCalls);
    }

    private static PlatformChildDomainIntent CapacityIntent()
    {
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory;
        return new(new(1, 8192), new(authority, authority));
    }

    [Theory]
    [InlineData(false, false, 0UL)] [InlineData(false, false, ulong.MaxValue)]
    [InlineData(false, false, ulong.MaxValue - 1)] [InlineData(false, false, ulong.MaxValue - 2)]
    [InlineData(false, true, 0UL)] [InlineData(false, true, ulong.MaxValue)] [InlineData(false, true, ulong.MaxValue - 1)]
    [InlineData(true, false, 0UL)] [InlineData(true, false, ulong.MaxValue)] [InlineData(true, false, ulong.MaxValue - 1)]
    [InlineData(true, true, 0UL)] [InlineData(true, true, ulong.MaxValue)] [InlineData(true, true, ulong.MaxValue - 1)]
    public void ChildCreateCapacityRefusesBeforeRootBindOrReservesEntireTuple(bool existing, bool identity, ulong capacity)
    {
        Scenario s = CreateScenario(rootOnly: !existing, closeOnly: existing);
        object root = Record(s.Bridge, "_domains", s.Root.BindingId);
        FieldInfo identityCounter = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", Private)!;
        FieldInfo operationCounter = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        FieldInfo counter = identity ? identityCounter : operationCounter;
        counter.SetValue(s.Adapter, capacity);
        ulong originalIdentity = (ulong)identityCounter.GetValue(s.Adapter)!;
        ulong originalOperation = (ulong)operationCounter.GetValue(s.Adapter)!;
        int binds = s.Spy.ParentBindCalls, creates = s.Spy.CreateCalls;
        ulong operations = existing ? 1UL : 2UL;
        bool accepted = identity ? capacity == ulong.MaxValue - 1 : capacity == ulong.MaxValue - operations;
        bool reserved = false;
        void Probe()
        {
            reserved = (ulong)identityCounter.GetValue(s.Adapter)! == originalIdentity + 1 &&
                (ulong)operationCounter.GetValue(s.Adapter)! == originalOperation + operations;
            Assert.False(s.Bridge.CreateChildDomain(s.Root, s.Subject, CapacityIntent()).IsSuccess);
        }
        s.Spy.BeforeParentBind = Probe; s.Spy.BeforeCreate = Probe;
        KernelResult<PlatformChildBinding> result = s.Bridge.CreateChildDomain(s.Root, s.Subject, CapacityIntent());
        Assert.Equal(accepted, result.IsSuccess);
        Assert.Equal(accepted, reserved);
        Assert.Equal(binds + (accepted && !existing ? 1 : 0), s.Spy.ParentBindCalls);
        Assert.Equal(creates + (accepted ? 1 : 0), s.Spy.CreateCalls);
        Assert.Equal(originalIdentity + (accepted ? 1UL : 0UL), (ulong)identityCounter.GetValue(s.Adapter)!);
        Assert.Equal(originalOperation + (accepted ? operations : 0UL), (ulong)operationCounter.GetValue(s.Adapter)!);
        Assert.Same(root, Record(s.Bridge, "_domains", s.Root.BindingId));
        Assert.Equal(0, Property(root, "PendingChildCreates"));
        Assert.True(s.Bridge.ValidateDomain(s.Root, s.Subject).IsSuccess);
        if (existing) Assert.True(s.Bridge.CommitActiveChildDomainConsumer(s.Child, KernelResult.Ok).IsSuccess);
        IDictionary children = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings", Private)!.GetValue(s.Bridge)!;
        Assert.Equal((existing ? 1 : 0) + (accepted ? 1 : 0), children.Count);
        Assert.False(s.Bridge.CreateChildDomain(s.Root, s.Subject, CapacityIntent()).IsSuccess);
        Assert.Equal(creates + (accepted ? 1 : 0), s.Spy.CreateCalls);
        if (accepted || existing) Assert.False(s.Bridge.RevokeDomain(s.Root, s.Subject).IsSuccess);
        else Assert.True(s.Bridge.RevokeDomain(s.Root, s.Subject).IsSuccess);
    }

    [Theory]
    [InlineData(false, 0UL)] [InlineData(false, ulong.MaxValue)] [InlineData(false, ulong.MaxValue - 1)]
    [InlineData(true, 0UL)] [InlineData(true, ulong.MaxValue)] [InlineData(true, ulong.MaxValue - 1)]
    public void GuestMapCapacityPreservesChildAndParentBackingWithoutInventingClosure(bool identity, ulong capacity)
    {
        Scenario s = CreateScenario(closeOnly: true);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object parent = Record(s.Bridge, "_mappings", s.Mapping.MappingId);
        FieldInfo identityCounter = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", Private)!;
        FieldInfo operationCounter = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        (identity ? identityCounter : operationCounter).SetValue(s.Adapter, capacity);
        ulong originalIdentity = (ulong)identityCounter.GetValue(s.Adapter)!;
        ulong originalOperation = (ulong)operationCounter.GetValue(s.Adapter)!;
        bool reserved = false;
        KernelResult<PlatformGuestMapping> Map() => s.Bridge.MapChildGuestRegion(s.Child, s.Mapping,
            new(0, 8192), PlatformGuestMemoryAccess.Read);
        s.Spy.BeforeMap = () =>
        {
            reserved = (ulong)identityCounter.GetValue(s.Adapter)! == originalIdentity + 1 &&
                (ulong)operationCounter.GetValue(s.Adapter)! == originalOperation + 1;
            Assert.False(Map().IsSuccess);
        };
        KernelResult<PlatformGuestMapping> result = Map(); bool accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, result.IsSuccess); Assert.Equal(accepted, reserved);
        Assert.Equal(accepted ? 1 : 0, s.Spy.MapCalls);
        Assert.Equal(originalIdentity + (accepted ? 1UL : 0UL), (ulong)identityCounter.GetValue(s.Adapter)!);
        Assert.Equal(originalOperation + (accepted ? 1UL : 0UL), (ulong)operationCounter.GetValue(s.Adapter)!);
        Assert.Same(child, Record(s.Bridge, "_childBindings", s.Child.BindingId));
        Assert.Same(parent, Record(s.Bridge, "_mappings", s.Mapping.MappingId));
        Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
        Assert.True(s.Bridge.CommitActiveChildDomainConsumer(s.Child, KernelResult.Ok).IsSuccess);
        IDictionary guests = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_guestBindings", Private)!.GetValue(s.Bridge)!;
        Assert.Equal(accepted ? 1 : 0, guests.Count);
        Assert.False(Map().IsSuccess); Assert.Equal(accepted ? 1 : 0, s.Spy.MapCalls);
        Assert.False(s.Bridge.RevokeDomain(s.Root, s.Subject).IsSuccess);
        if (accepted)
        {
            Assert.Equal(PlatformExternalClosureState.Active, Property(guests[result.Value.MappingId]!, "Closure"));
            Assert.Null(Property(guests[result.Value.MappingId]!, "LastClosureReceipt"));
        }
    }

    [Theory]
    [InlineData(false, NeutralVirtualizationStatus.Denied)] [InlineData(false, NeutralVirtualizationStatus.Unsupported)]
    [InlineData(false, (NeutralVirtualizationStatus)777)]
    [InlineData(true, NeutralVirtualizationStatus.Denied)] [InlineData(true, NeutralVirtualizationStatus.Unsupported)]
    [InlineData(true, (NeutralVirtualizationStatus)777)]
    public void GenericCreateOrMapFailureAfterActualEffectKeepsOriginalInvocationPinned(bool map, NeutralVirtualizationStatus status)
    {
        Scenario s = CreateScenario(rootOnly: !map, closeOnly: map,
            createOutcome: map ? null : status, mapOutcome: map ? status : null);
        if (map)
        {
            Assert.False(s.Bridge.MapChildGuestRegion(s.Child, s.Mapping, new(0, 8192), PlatformGuestMemoryAccess.Read).IsSuccess);
            Assert.Equal(1, s.Spy.MapCalls);
            Assert.False(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
            Assert.False(s.Bridge.CommitActiveChildDomainConsumer(s.Child, KernelResult.Ok).IsSuccess);
        }
        else
        {
            Assert.False(s.Bridge.CreateChildDomain(s.Root, s.Subject, CapacityIntent()).IsSuccess);
            Assert.Equal(1, s.Spy.ParentBindCalls); Assert.Equal(1, s.Spy.CreateCalls);
            Assert.False(s.Bridge.ValidateDomain(s.Root, s.Subject).IsSuccess);
            Assert.False(s.Bridge.CreateChildDomain(s.Root, s.Subject, CapacityIntent()).IsSuccess);
            Assert.Equal(1, s.Spy.CreateCalls);
        }
        Assert.False(s.Bridge.RevokeDomain(s.Root, s.Subject).IsSuccess);
    }

    [Theory]
    [InlineData(false, 0UL)] [InlineData(false, ulong.MaxValue)] [InlineData(false, ulong.MaxValue - 1)]
    [InlineData(true, 0UL)] [InlineData(true, ulong.MaxValue)] [InlineData(true, ulong.MaxValue - 1)]
    public void ArtifactBindCapacityDispositionKeepsExactDependenciesAndAcceptedReceiptPinned(bool identity, ulong capacity)
    {
        Scenario s = CreateScenario(guestOnly: true);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object? lease = Property(guest, "ProviderLease");
        FieldInfo identityCounter = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", Private)!;
        FieldInfo operationCounter = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        FieldInfo counter = identity ? identityCounter : operationCounter;
        counter.SetValue(s.Adapter, capacity);
        ulong originalIdentity = (ulong)identityCounter.GetValue(s.Adapter)!;
        ulong originalOperation = (ulong)operationCounter.GetValue(s.Adapter)!;
        byte[] package = CreateExecutablePackage();
        bool reserved = false;
        s.Spy.BeforeArtifactBind = () =>
        {
            reserved = (ulong)counter.GetValue(s.Adapter)! == ulong.MaxValue;
            Assert.Equal(KernelError.PlatformBindingActive,
                s.Bridge.BindChildExecutableArtifact(s.Child, s.Guest, package, 3).Error);
        };
        KernelResult<PlatformExecutableArtifactBinding> bound = s.Bridge.BindChildExecutableArtifact(s.Child, s.Guest, package, 3);
        bool accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, bound.IsSuccess);
        Assert.Equal(accepted ? 1 : 0, s.Spy.ArtifactBindCalls);
        Assert.Equal(accepted, reserved);
        Assert.Equal(originalIdentity + (accepted ? 1UL : 0UL), (ulong)identityCounter.GetValue(s.Adapter)!);
        Assert.Equal(originalOperation + (accepted ? 1UL : 0UL), (ulong)operationCounter.GetValue(s.Adapter)!);
        Assert.Same(child, Record(s.Bridge, "_childBindings", s.Child.BindingId));
        Assert.Same(guest, Record(s.Bridge, "_guestBindings", s.Guest.MappingId));
        Assert.Equal(lease, Property(guest, "ProviderLease"));
        Assert.Equal(PlatformChildDomainState.Created, Property(child, "State"));
        Assert.Equal(PlatformExternalClosureState.Active, Property(guest, "Closure"));
        Assert.Equal(0, Property(child, "PendingChildEffects"));
        Assert.Equal(0, Property(guest, "PendingExecutableEffects"));
        Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
        IDictionary artifacts = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_executableArtifacts", Private)!.GetValue(s.Bridge)!;
        Assert.Equal(accepted ? 1 : 0, artifacts.Count);
        Assert.False(s.Bridge.BindChildExecutableArtifact(s.Child, s.Guest, package, 3).IsSuccess);
        Assert.Equal(accepted ? 1 : 0, s.Spy.ArtifactBindCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.RevokeDomain(s.Root, s.Subject).Error);
        if (accepted)
        {
            Assert.NotNull(Property(Record(s.Bridge, "_executableArtifacts", bound.Value.BindingId), "ProviderReceipt"));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.UnmapChildGuestRegion(s.Guest).Error);
            Assert.Equal(0, s.Spy.UnmapCalls);
        }
        else
        {
            Assert.True(s.Bridge.CommitSecureGuestMapping(s.Guest, s.Mapping, true, KernelResult.Ok, out Action? endPending, out _).IsSuccess);
            endPending!();
        }
    }

    [Theory]
    [InlineData(NeutralVirtualizationStatus.Denied)]
    [InlineData(NeutralVirtualizationStatus.Unsupported)]
    [InlineData((NeutralVirtualizationStatus)777)]
    public void GenericArtifactBindFailureAfterActualEffectKeepsDependenciesQuarantined(NeutralVirtualizationStatus status)
    {
        Scenario s = CreateScenario(guestOnly: true, bindOutcome: status);
        byte[] package = CreateExecutablePackage();
        Assert.False(s.Bridge.BindChildExecutableArtifact(s.Child, s.Guest, package, 3).IsSuccess);
        Assert.Equal(1, s.Spy.ArtifactBindCalls);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        Assert.Equal(PlatformChildDomainState.Faulted, Property(child, "State"));
        Assert.Equal(PlatformExternalClosureState.Faulted, Property(guest, "Closure"));
        Assert.Equal(0, Property(child, "PendingChildEffects"));
        Assert.Equal(0, Property(guest, "PendingExecutableEffects"));
        Assert.False(s.Bridge.BindChildExecutableArtifact(s.Child, s.Guest, package, 3).IsSuccess);
        Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        Assert.Equal(0, s.Spy.UnmapCalls);
        Assert.False(s.Bridge.RevokeDomain(s.Root, s.Subject).IsSuccess);
        Assert.Equal(1, s.Spy.ArtifactBindCalls);
    }

    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void GuestUnmapCapacityRefusalRestoresOnlyLocalStageAndRetainsParentPin(ulong capacity)
    {
        Scenario s = CreateScenario(guestOnly: true);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object? lease = Property(guest, "ProviderLease");
        FieldInfo operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        operation.SetValue(s.Adapter, capacity);
        bool reserved = false;
        s.Spy.BeforeUnmap = () =>
        {
            reserved = (ulong)operation.GetValue(s.Adapter)! == ulong.MaxValue;
            Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        };
        KernelResult result = s.Bridge.UnmapChildGuestRegion(s.Guest);
        bool accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, result.IsSuccess);
        Assert.Equal(accepted ? 1 : 0, s.Spy.UnmapCalls);
        Assert.Equal(accepted, reserved);
        Assert.Same(guest, Record(s.Bridge, "_guestBindings", s.Guest.MappingId));
        Assert.Equal(lease, Property(guest, "ProviderLease"));
        Assert.Equal(accepted ? PlatformExternalClosureState.Closed : PlatformExternalClosureState.Active, Property(guest, "Closure"));
        Assert.Equal(false, Property(guest, "ClosureInFlight"));
        Assert.Equal(accepted, Property(guest, "LastClosureReceipt") is not null);
        if (!accepted)
        {
            Assert.Equal(capacity, (ulong)operation.GetValue(s.Adapter)!);
            Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
            Assert.True(s.Bridge.CommitSecureGuestMapping(s.Guest, s.Mapping, begin: true, KernelResult.Ok, out Action? endPending, out _).IsSuccess);
            endPending!();
            Assert.Equal(0, Property(guest, "PendingSecureRegionEffects"));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.RevokeDomain(s.Root, s.Subject).Error);
            Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
        }
        else Assert.True(s.Bridge.UnmapChildGuestRegion(s.Guest, allowCommittedClosure: true).IsSuccess);
        Assert.Equal(accepted ? 1 : 0, s.Spy.UnmapCalls);
    }

    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void ChildParkCapacityRetainsKnownExecutionOrPinsInvokedFailure(ulong capacity)
    {
        Scenario s = CreateScenario();
        Assert.True(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object artifact = Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId);
        object? receipt = Property(artifact, "ExecutionReceipt");
        Assert.NotNull(receipt);
        FieldInfo operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        operation.SetValue(s.Adapter, capacity);
        bool reserved = false;
        s.Spy.BeforeTransition = () =>
        {
            reserved = (ulong)operation.GetValue(s.Adapter)! == ulong.MaxValue;
            Assert.Equal(KernelError.PlatformBindingActive,
                s.Bridge.TransitionChildDomain(s.Child, PlatformChildDomainTransition.BeginDrain).Error);
        };
        KernelResult result = s.Bridge.TransitionChildDomain(s.Child, PlatformChildDomainTransition.Park);
        bool invoked = capacity == ulong.MaxValue - 1;
        // Controlled Start work receipts do not change actual machine/provider state.
        // This actual provider rejects Park: once invoked, retain its ambiguity policy.
        Assert.False(result.IsSuccess);
        Assert.Equal(invoked ? 1 : 0, s.Spy.TransitionCalls);
        Assert.Equal(invoked, reserved);
        Assert.Equal(invoked ? PlatformChildDomainState.Faulted : PlatformChildDomainState.Running, Property(child, "State"));
        Assert.Equal(false, Property(child, "TransitionInFlight"));
        Assert.Equal(receipt, Property(artifact, "ExecutionReceipt"));
        Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
        Assert.False(s.Bridge.TransitionChildDomain(s.Child,
            invoked ? PlatformChildDomainTransition.Resume : PlatformChildDomainTransition.Park).IsSuccess);
        Assert.Equal(invoked ? 1 : 0, s.Spy.TransitionCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.UnmapChildGuestRegion(s.Guest).Error);
        Assert.Equal(0, s.Spy.UnmapCalls);
    }

    [Theory]
    [InlineData(NeutralVirtualizationStatus.Denied)]
    [InlineData(NeutralVirtualizationStatus.Unsupported)]
    [InlineData((NeutralVirtualizationStatus)777)]
    public void GuestUnmapGenericFailureAfterRealClosureDoesNotRestoreActive(NeutralVirtualizationStatus status)
    {
        Scenario s = CreateScenario(guestOnly: true, unmapOutcome: status);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        Assert.Equal(1, s.Spy.UnmapCalls);
        Assert.Equal(PlatformExternalClosureState.Faulted, Property(guest, "Closure"));
        Assert.Null(Property(guest, "LastClosureReceipt"));
        Assert.Equal(false, Property(guest, "ClosureInFlight"));
        Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        Assert.False(s.Bridge.CommitSecureGuestMapping(s.Guest, s.Mapping, begin: true, KernelResult.Ok, out _, out _).IsSuccess);
        Assert.False(s.Bridge.RevokeDomain(s.Root, s.Subject).IsSuccess);
        Assert.Equal(1, s.Spy.UnmapCalls);
    }

    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void ChildCloseCapacityRefusalRetainsDrainingOwnerAndRootPin(ulong capacity)
    {
        Scenario s = CreateScenario(closeOnly: true);
        Assert.True(s.Bridge.TransitionChildDomain(s.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        FieldInfo operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        operation.SetValue(s.Adapter, capacity);
        bool reserved = false;
        s.Spy.BeforeClose = () =>
        {
            reserved = (ulong)operation.GetValue(s.Adapter)! == ulong.MaxValue;
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.CloseChildDomain(s.Child).Error);
        };
        KernelResult result = s.Bridge.CloseChildDomain(s.Child);
        bool accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, result.IsSuccess);
        Assert.Equal(accepted ? 1 : 0, s.Spy.CloseCalls);
        Assert.Equal(accepted, reserved);
        Assert.Same(child, Record(s.Bridge, "_childBindings", s.Child.BindingId));
        Assert.Equal(accepted ? PlatformChildDomainState.Closed : PlatformChildDomainState.Draining, Property(child, "State"));
        Assert.Equal(false, Property(child, "CloseInFlight"));
        Assert.Equal(0, Property(child, "PendingChildEffects"));
        Assert.Equal(accepted, Property(child, "LastClosureReceipt") is not null);
        if (!accepted)
        {
            Assert.Equal(capacity, (ulong)operation.GetValue(s.Adapter)!);
            Assert.False(s.Bridge.CloseChildDomain(s.Child).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.RevokeDomain(s.Root, s.Subject).Error);
            Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
        }
        else Assert.True(s.Bridge.CloseChildDomain(s.Child, allowCommittedClosure: true).IsSuccess);
        Assert.Equal(accepted ? 1 : 0, s.Spy.CloseCalls);
    }

    [Theory]
    [InlineData(NeutralVirtualizationStatus.Denied, false)]
    [InlineData(NeutralVirtualizationStatus.Unsupported, false)]
    [InlineData((NeutralVirtualizationStatus)777, false)]
    [InlineData(NeutralVirtualizationStatus.NotInvoked, true)]
    public void ChildCloseGenericOutcomeOrResetNeverPublishesClosure(NeutralVirtualizationStatus status, bool reset)
    {
        Scenario s = CreateScenario(closeOnly: true, closeOutcome: status,
            beforeClose: reset ? bridge => { bridge.ObserveBackendReset(); } : null);
        Assert.True(s.Bridge.TransitionChildDomain(s.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        Assert.False(s.Bridge.CloseChildDomain(s.Child).IsSuccess);
        Assert.Equal(reset ? 0 : 1, s.Spy.CloseCalls);
        Assert.Equal(PlatformChildDomainState.Faulted, Property(child, "State"));
        Assert.Null(Property(child, "LastClosureReceipt"));
        Assert.Equal(false, Property(child, "CloseInFlight"));
        Assert.False(s.Bridge.CloseChildDomain(s.Child).IsSuccess);
        Assert.False(s.Bridge.RevokeDomain(s.Root, s.Subject).IsSuccess);
        Assert.Equal(reset ? 0 : 1, s.Spy.CloseCalls);
    }

    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)]
    public void ExactCapacityRefusalKeepsBridgeDependenciesUsableAndArtifactPinned(ulong capacity)
    {
        Scenario s = CreateScenario();
        FieldInfo operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        operation.SetValue(s.Adapter, capacity);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object artifact = Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId);
        object? receipt = Property(artifact, "ProviderReceipt");

        PlatformAuthorityResult<PlatformChildExecutionReceipt> direct = s.Provider.StartExecutableArtifact(new(
            (PlatformProviderChildDomainLease)Property(child, "ProviderLease")!,
            (PlatformExecutableArtifactReceipt)receipt!, new(99), new(1)));
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, direct.Status);

        Assert.False(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(0, s.Spy.StartCalls);
        Assert.Equal(capacity, (ulong)operation.GetValue(s.Adapter)!);
        Assert.Equal(PlatformChildDomainState.Created, Property(child, "State"));
        Assert.Equal(PlatformExternalClosureState.Active, Property(guest, "Closure"));
        Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
        Assert.Same(child, Record(s.Bridge, "_childBindings", s.Child.BindingId));
        Assert.Same(guest, Record(s.Bridge, "_guestBindings", s.Guest.MappingId));
        Assert.Same(artifact, Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId));
        Assert.Equal(receipt, Property(artifact, "ProviderReceipt"));
        Assert.False((bool)Property(artifact, "Started")!);
        Assert.Null(Property(artifact, "ExecutionReceipt"));
        AssertBalanced(child, guest, artifact);

        // Refusal neither authorizes retry with a recycled generation nor closes the admitted artifact.
        Assert.False(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(0, s.Spy.StartCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.UnmapChildGuestRegion(s.Guest).Error);
        Assert.Equal(0, s.Spy.UnmapCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.RevokeDomain(s.Root, s.Subject).Error);
        Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void LastGenerationStartRetainsReceiptOrQuarantineWithoutInventingNoEffect(int fault)
    {
        Scenario s = CreateScenario(fault);
        FieldInfo operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", Private)!;
        operation.SetValue(s.Adapter, ulong.MaxValue - 1);
        bool reserved = false;
        s.Spy.BeforeStart = () => reserved = (ulong)operation.GetValue(s.Adapter)! == ulong.MaxValue;
        KernelResult result = s.Bridge.StartChildExecutableArtifact(s.Artifact);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.True(reserved); Assert.Equal(1, s.Spy.StartCalls);
        Assert.Equal(ulong.MaxValue, (ulong)operation.GetValue(s.Adapter)!);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object artifact = Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId);
        AssertBalanced(child, guest, artifact);
        if (fault == 0)
        {
            Assert.True((bool)Property(artifact, "Started")!);
            Assert.NotNull(Property(artifact, "ExecutionReceipt"));
            Assert.True(s.Bridge.ValidateMapping(s.Mapping, s.Subject).IsSuccess);
        }
        else
        {
            Assert.Equal(PlatformChildDomainState.Faulted, Property(child, "State"));
            Assert.Equal(PlatformExternalClosureState.Faulted, Property(guest, "Closure"));
            Assert.Equal(KernelError.PlatformFaulted, s.Bridge.ValidateMapping(s.Mapping, s.Subject).Error);
            Assert.Null(Property(artifact, "ExecutionReceipt"));
        }
        Assert.False(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(1, s.Spy.StartCalls);
        Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        Assert.Equal(0, s.Spy.UnmapCalls);
    }

    [Theory]
    [InlineData(NeutralVirtualizationStatus.Denied)]
    [InlineData(NeutralVirtualizationStatus.Unsupported)]
    [InlineData((NeutralVirtualizationStatus)777)]
    public void GenericFailureAfterActualStartDoesNotBecomeNotAccepted(NeutralVirtualizationStatus status)
    {
        Scenario s = CreateScenario(genericOutcome: status);
        Assert.False(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(1, s.Spy.StartCalls);
        Assert.Equal(KernelError.PlatformFaulted, s.Bridge.ValidateMapping(s.Mapping, s.Subject).Error);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object artifact = Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId);
        Assert.Equal(PlatformChildDomainState.Faulted, Property(child, "State"));
        AssertBalanced(child, guest, artifact);
        Assert.False(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(1, s.Spy.StartCalls);
        Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        Assert.Equal(0, s.Spy.UnmapCalls);
    }

    [Fact]
    public void AdditiveDispositionPreservesV1NumericFailureValues()
    {
        string[] original = ["Success", "Unavailable", "Unsupported", "Denied", "Stale", "Revoked",
            "WrongParent", "WrongOwner", "Faulted", "Ambiguous"];
        for (int value = 0; value < original.Length; value++)
            Assert.Equal(original[value], ((NeutralVirtualizationStatus)value).ToString());
        Assert.Equal(10, (int)NeutralVirtualizationStatus.NotInvoked);
        Assert.False(new NeutralVirtualizationResult(NeutralVirtualizationStatus.NotInvoked, "refused").IsSuccess);
    }

    [Theory]
    [InlineData(false, false, "exact")] [InlineData(false, true, "exact")]
    [InlineData(true, false, "exact")] [InlineData(true, true, "exact")]
    [InlineData(false, false, "generic")] [InlineData(false, true, "generic")]
    [InlineData(true, false, "generic")] [InlineData(true, true, "generic")]
    [InlineData(true, false, "throw")] [InlineData(true, true, "throw")]
    public void ArtifactFailurePinsCapturedParentWithoutWritingForeignOwner(bool start, bool absent, string fault)
    {
        Scenario s = CreateScenario(guestOnly: !start, fault: fault == "throw" ? 3 : 0,
            genericOutcome: start && fault == "generic" ? NeutralVirtualizationStatus.Denied : null,
            bindOutcome: !start && fault == "generic" ? NeutralVirtualizationStatus.Denied : null);
        IDictionary parents = (IDictionary)s.Bridge.GetType().GetField("_mappings", Private)!.GetValue(s.Bridge)!;
        object original = parents[s.Mapping.MappingId]!;
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object foreign = Activator.CreateInstance(original.GetType(), s.Mapping, Property(original, "ProviderLease"),
            Property(original, "AuthorityCapabilityId"), Property(original, "ProviderIncarnation"), Property(original, "BackendEpoch"))!;
        object gate = s.Bridge.GetType().GetField("_secureDomainLifecycleGate", Private)!.GetValue(s.Bridge)!;
        Action replace = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            if (absent) parents.Remove(s.Mapping.MappingId); else parents[s.Mapping.MappingId] = foreign;
        };
        if (start) s.Spy.BeforeStart = replace; else s.Spy.BeforeArtifactBind = replace;
        bool Invoke() => start ? s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess :
            s.Bridge.BindChildExecutableArtifact(s.Child, s.Guest, CreateExecutablePackage(), 3).IsSuccess;
        Assert.False(Invoke());
        s.Spy.BeforeStart = null; s.Spy.BeforeArtifactBind = null;
        Assert.Equal(PlatformExternalClosureState.Faulted, Property(original, "ClosureState"));
        Assert.Equal(PlatformExternalClosureState.Active, Property(foreign, "ClosureState"));
        Assert.False((bool)Property(original, "LocalReservationReleased")!);
        Assert.False((bool)Property(foreign, "LocalReservationReleased")!);
        Assert.Equal(PlatformChildDomainState.Faulted, Property(child, "State"));
        Assert.Equal(PlatformExternalClosureState.Faulted, Property(guest, "Closure"));
        Assert.Equal(0, Property(child, "PendingChildEffects"));
        Assert.Equal(0, Property(guest, "PendingExecutableEffects"));
        if (absent) Assert.False(parents.Contains(s.Mapping.MappingId)); else Assert.Same(foreign, parents[s.Mapping.MappingId]);
        IDictionary artifacts = (IDictionary)s.Bridge.GetType().GetField("_executableArtifacts", Private)!.GetValue(s.Bridge)!;
        if (fault == "exact")
        {
            object fact = Assert.Single(artifacts.Values.Cast<object>());
            if (start) Assert.NotNull(Property(fact, "ExecutionReceipt"));
            else Assert.NotNull(Property(fact, "ProviderReceipt"));
        }
        if (start) Assert.Equal(false, Property(Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId), "StartInFlight"));
        Assert.False(Invoke());
        Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        Assert.Equal(1, s.Spy.ArtifactBindCalls);
        Assert.Equal(start ? 1 : 0, s.Spy.StartCalls);
        Assert.Equal(0, s.Spy.UnmapCalls);
        // Restoring the original route is test arrangement; it must retain the original quarantine.
        parents[s.Mapping.MappingId] = original;
        Assert.Equal(KernelError.PlatformFaulted, s.Bridge.ValidateMapping(s.Mapping, s.Subject).Error);
        Assert.False(Invoke());
        Assert.Equal(PlatformExternalClosureState.Active, Property(foreign, "ClosureState"));
    }

    [Theory]
    [InlineData("child", false)] [InlineData("child", true)]
    [InlineData("guest", false)] [InlineData("guest", true)]
    [InlineData("parent", false)] [InlineData("parent", true)]
    [InlineData("root", false)] [InlineData("root", true)]
    public void ArtifactStartRequiresOriginalBindOwnersBeforeReservationAndProviderEffect(string source, bool absent)
    {
        Scenario s = CreateScenario();
        string field = source switch { "child" => "_childBindings", "guest" => "_guestBindings", "parent" => "_mappings", _ => "_domains" };
        object key = source switch { "child" => s.Child.BindingId, "guest" => s.Guest.MappingId, "parent" => s.Mapping.MappingId, _ => s.Root.BindingId };
        IDictionary owners = (IDictionary)s.Bridge.GetType().GetField(field, Private)!.GetValue(s.Bridge)!;
        object original = owners[key]!;
        object foreign = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
        FieldInfo operation = s.Bridge.GetType().GetField("_nextChildStartOperationId", Private)!;
        object? before = operation.GetValue(s.Bridge);
        object artifact = Record(s.Bridge, "_executableArtifacts", s.Artifact.BindingId);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        if (absent) owners.Remove(key); else owners[key] = foreign;
        Assert.False(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(before, operation.GetValue(s.Bridge));
        Assert.Equal(0, s.Spy.StartCalls);
        Assert.Equal(false, Property(artifact, "Started"));
        Assert.Null(Property(artifact, "ExecutionReceipt"));
        AssertBalanced(child, guest, artifact);
        if (absent) Assert.False(owners.Contains(key)); else Assert.Same(foreign, owners[key]);
        // Exact restoration is a test positive control, not production recovery or permission.
        owners[key] = original;
        Assert.True(s.Bridge.StartChildExecutableArtifact(s.Artifact).IsSuccess);
        Assert.Equal(1, s.Spy.StartCalls);
        Assert.NotNull(Property(artifact, "ExecutionReceipt"));
        AssertBalanced(child, guest, artifact);
    }

    [Theory]
    [InlineData(false, "child", false)] [InlineData(false, "child", true)]
    [InlineData(false, "parent", false)] [InlineData(false, "parent", true)]
    [InlineData(false, "root", false)] [InlineData(false, "root", true)]
    [InlineData(true, "child", false)] [InlineData(true, "child", true)]
    [InlineData(true, "parent", false)] [InlineData(true, "parent", true)]
    [InlineData(true, "root", false)] [InlineData(true, "root", true)]
    public void GuestConsumersRequireOriginalMapOwnersBeforeBindOrUnmapEffect(bool unmap, string source, bool absent)
    {
        Scenario s = CreateScenario(guestOnly: true);
        string field = source switch { "child" => "_childBindings", "parent" => "_mappings", _ => "_domains" };
        object key = source switch { "child" => s.Child.BindingId, "parent" => s.Mapping.MappingId, _ => s.Root.BindingId };
        IDictionary owners = (IDictionary)s.Bridge.GetType().GetField(field, Private)!.GetValue(s.Bridge)!;
        object original = owners[key]!;
        object foreign = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
        FieldInfo counter = s.Bridge.GetType().GetField("_nextExecutableArtifactBindingId", Private)!;
        object? before = counter.GetValue(s.Bridge);
        object guest = Record(s.Bridge, "_guestBindings", s.Guest.MappingId);
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        if (absent) owners.Remove(key); else owners[key] = foreign;
        bool Invoke() => unmap ? s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess :
            s.Bridge.BindChildExecutableArtifact(s.Child, s.Guest, CreateExecutablePackage(), 3).IsSuccess;
        Assert.False(Invoke());
        Assert.False(Invoke());
        Assert.Equal(before, counter.GetValue(s.Bridge));
        Assert.Equal(0, s.Spy.ArtifactBindCalls);
        Assert.Equal(0, s.Spy.UnmapCalls);
        Assert.Equal(0, Property(child, "PendingChildEffects"));
        Assert.Equal(0, Property(guest, "PendingExecutableEffects"));
        Assert.Equal(false, Property(guest, "ClosureInFlight"));
        Assert.Equal(PlatformExternalClosureState.Active, Property(guest, "Closure"));
        Assert.Null(Property(guest, "LastClosureReceipt"));
        if (absent) Assert.False(owners.Contains(key)); else Assert.Same(foreign, owners[key]);
        // Original source restoration is a test positive control, not recovery permission.
        owners[key] = original;
        Assert.True(Invoke());
        Assert.Equal(unmap ? 1 : 0, s.Spy.UnmapCalls);
        Assert.Equal(unmap ? 0 : 1, s.Spy.ArtifactBindCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void GuestMapAndUnmapRetainExactReceiptWithoutPublicationAfterOriginalRootLoss(bool unmap, bool absent)
    {
        Scenario s = CreateScenario(closeOnly: !unmap, guestOnly: unmap);
        IDictionary roots = (IDictionary)s.Bridge.GetType().GetField("_domains", Private)!.GetValue(s.Bridge)!;
        object original = roots[s.Root.BindingId]!;
        object foreign = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
        object gate = s.Bridge.GetType().GetField("_secureDomainLifecycleGate", Private)!.GetValue(s.Bridge)!;
        Action replace = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            if (absent) roots.Remove(s.Root.BindingId); else roots[s.Root.BindingId] = foreign;
        };
        if (unmap) s.Spy.BeforeUnmap = replace; else s.Spy.BeforeMap = replace;
        PlatformGuestMapping? known = null;
        if (unmap) Assert.False(s.Bridge.UnmapChildGuestRegion(s.Guest).IsSuccess);
        else Assert.False(s.Bridge.MapChildGuestRegion(s.Child, s.Mapping, new(0, 8192),
            PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Execute, out known).IsSuccess);
        s.Spy.BeforeUnmap = null; s.Spy.BeforeMap = null;
        PlatformGuestMapping guestMapping = unmap ? s.Guest : known!.Value;
        object guest = Record(s.Bridge, "_guestBindings", guestMapping.MappingId);
        Assert.Equal(PlatformExternalClosureState.Faulted, Property(guest, "Closure"));
        Assert.Equal(false, Property(guest, "ClosureInFlight"));
        if (unmap) Assert.NotNull(Property(guest, "LastClosureReceipt"));
        else Assert.NotNull(Property(guest, "ProviderLease"));
        Assert.NotNull(Property(guest, "AdmissionSource"));
        Assert.Equal(1, s.Spy.MapCalls);
        Assert.Equal(unmap ? 1 : 0, s.Spy.UnmapCalls);
        Assert.False(s.Bridge.UnmapChildGuestRegion(guestMapping).IsSuccess);
        Assert.Equal(unmap ? 1 : 0, s.Spy.UnmapCalls);
        if (absent) Assert.False(roots.Contains(s.Root.BindingId)); else Assert.Same(foreign, roots[s.Root.BindingId]);
        // Restoration cannot turn the retained original outcome into admission or closure.
        roots[s.Root.BindingId] = original;
        Assert.False(s.Bridge.UnmapChildGuestRegion(guestMapping).IsSuccess);
        Assert.Equal(unmap ? 1 : 0, s.Spy.UnmapCalls);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public void ChildLifecycleRequiresOriginalCreateRootBeforeEffectAndFinalCommit(bool close, bool absent, bool late)
    {
        Scenario s = CreateScenario(childOnly: true, unmapOutcome: NeutralVirtualizationStatus.NotInvoked);
        GenericFailureProvider wrapper = (GenericFailureProvider)s.Provider.GetType().GetField("_childRuntime", Private)!.GetValue(s.Provider)!;
        object child = Record(s.Bridge, "_childBindings", s.Child.BindingId);
        if (close) Assert.True(s.Bridge.TransitionChildDomain(s.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        IDictionary roots = (IDictionary)s.Bridge.GetType().GetField("_domains", Private)!.GetValue(s.Bridge)!;
        object original = roots[s.Root.BindingId]!;
        Assert.Same(original, Property(child, "CreateRoot"));
        object foreign = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
        object? priorForeign = Property(foreign, "AuthorityState");
        int invocations = 0;
        object gate = s.Bridge.GetType().GetField("_secureDomainLifecycleGate", Private)!.GetValue(s.Bridge)!;
        Action replace = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            if (absent) roots.Remove(s.Root.BindingId); else roots[s.Root.BindingId] = foreign;
        };
        bool Invoke() => close ? s.Bridge.CloseChildDomain(s.Child).IsSuccess :
            s.Bridge.TransitionChildDomain(s.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess;
        Action callback = () => { invocations++; if (late) replace(); };
        if (close) wrapper.BeforeClose = callback; else wrapper.BeforeTransition = callback;
        if (!late) replace();
        Assert.False(Invoke());
        wrapper.BeforeClose = null; wrapper.BeforeTransition = null;
        Assert.False(Invoke());
        Assert.Equal(late ? 1 : 0, invocations);
        Assert.Equal(close && late ? 1 : 0, s.Spy.CloseCalls);
        Assert.Equal(0, Property(child, "PendingChildEffects"));
        Assert.Equal(false, Property(child, "TransitionInFlight"));
        Assert.Equal(false, Property(child, "CloseInFlight"));
        Assert.Equal(priorForeign, Property(foreign, "AuthorityState"));
        Assert.Same(original, Property(child, "CreateRoot"));
        if (late)
        {
            Assert.Equal(PlatformChildDomainState.Faulted, Property(child, "State"));
            if (close) Assert.NotNull(Property(child, "LastClosureReceipt"));
        }
        if (absent) Assert.False(roots.Contains(s.Root.BindingId)); else Assert.Same(foreign, roots[s.Root.BindingId]);
        // Restoration arranges the positive control; it does not close a late unknown/failed invocation.
        roots[s.Root.BindingId] = original;
        if (late) Assert.False(Invoke());
        else Assert.True(Invoke());
        Assert.Equal(priorForeign, Property(foreign, "AuthorityState"));
    }

    private static void AssertBalanced(object child, object guest, object artifact)
    {
        Assert.Equal(0, Property(child, "PendingChildEffects"));
        Assert.Equal(0, Property(guest, "PendingExecutableEffects"));
        Assert.Equal(false, Property(artifact, "StartInFlight"));
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Record(object owner, string field, object key) =>
        ((IDictionary)owner.GetType().GetField(field, Private)!.GetValue(owner)!)[key]!;
    private static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    private sealed record Scenario(PlatformAuthorityBridge Bridge, HybridCpuPlatformAuthorityProvider Provider,
        HybridCpuExecutableChildAdapter Adapter,
        StartRuntimeProxy Spy, PlatformDomainIdentity Subject, PlatformDomainBinding Root,
        PlatformRegionMapping Mapping, PlatformChildBinding Child, PlatformGuestMapping Guest,
        PlatformExecutableArtifactBinding Artifact);

    private static Scenario CreateScenario(int fault = 0, NeutralVirtualizationStatus? genericOutcome = null,
        bool closeOnly = false, NeutralVirtualizationStatus? closeOutcome = null,
        Action<PlatformAuthorityBridge>? beforeClose = null, bool guestOnly = false,
        NeutralVirtualizationStatus? unmapOutcome = null, NeutralVirtualizationStatus? bindOutcome = null,
        bool rootOnly = false, NeutralVirtualizationStatus? createOutcome = null, NeutralVirtualizationStatus? mapOutcome = null,
        bool childOnly = false)
    {
        HybridCpuExternalRuntime runtime = new HybridCpuExternalRuntime();
        IHybridCpuChildDomainRuntimeV3 proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, StartRuntimeProxy>();
        StartRuntimeProxy spy = (StartRuntimeProxy)proxy; spy.Inner = runtime; spy.Fault = fault;
        IHybridCpuExternalRuntime rootProxy = DispatchProxy.Create<IHybridCpuExternalRuntime, RootRuntimeProxy>();
        RootRuntimeProxy rootSpy = (RootRuntimeProxy)rootProxy; rootSpy.Inner = runtime;
        rootSpy.BeforeBind = () => { spy.ParentBindCalls++; spy.BeforeParentBind?.Invoke(); };
        ConstructorInfo constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        HybridCpuExecutableChildAdapter adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([rootProxy, proxy, null]);
        GenericFailureProvider wrapper = new GenericFailureProvider(adapter, genericOutcome ?? NeutralVirtualizationStatus.Denied)
            { CloseOutcome = closeOutcome, UnmapOutcome = unmapOutcome, BindOutcome = bindOutcome,
                CreateOutcome = createOutcome, MapOutcome = mapOutcome };
        INeutralChildDomainProvider composed = genericOutcome is not null || closeOutcome is not null || unmapOutcome is not null ||
            bindOutcome is not null || createOutcome is not null || mapOutcome is not null ? wrapper : adapter;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), composed);
        PlatformAuthorityBridge bridge = new PlatformAuthorityBridge(provider);
        wrapper.BeforeClose = () => beforeClose?.Invoke(bridge);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        KernelResult<PlatformDomainBinding> rootResult = bridge.BindDomain(subject); Assert.True(rootResult.IsSuccess, rootResult.Message);
        PlatformDomainBinding root = rootResult.Value;
        if (rootOnly) return new(bridge, provider, adapter, spy, subject, root, default, default, default, default);
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory;
        KernelResult<PlatformChildBinding> childResult = bridge.CreateChildDomain(root, subject, new(new(1, 8192), new(authority, authority)));
        Assert.True(childResult.IsSuccess, childResult.Message); PlatformChildBinding child = childResult.Value;
        if (childOnly) return new(bridge, provider, adapter, spy, subject, root, default, child, default, default);
        PlatformRegionIdentity region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(subject.DomainId, subject.ProcessGeneration), 8192);
        KernelResult<PlatformRegionMapping> mapped = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region, PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message); PlatformRegionMapping mapping = mapped.Value;
        if (closeOnly) return new(bridge, provider, adapter, spy, subject, root, mapping, child, default, default);
        KernelResult<PlatformGuestMapping> guestResult = bridge.MapChildGuestRegion(child, mapping, new(0, 8192),
            PlatformGuestMemoryAccess.Read | PlatformGuestMemoryAccess.Execute);
        Assert.True(guestResult.IsSuccess, guestResult.Message); PlatformGuestMapping guest = guestResult.Value;
        if (guestOnly) return new(bridge, provider, adapter, spy, subject, root, mapping, child, guest, default);
        KernelResult<PlatformExecutableArtifactBinding> bound = bridge.BindChildExecutableArtifact(child, guest, CreateExecutablePackage(), 3);
        Assert.True(bound.IsSuccess, bound.Message);
        return new(bridge, provider, adapter, spy, subject, root, mapping, child, guest, bound.Value);
    }

    public class RootRuntimeProxy : DispatchProxy
    {
        public IHybridCpuExternalRuntime Inner { get; set; } = null!;
        public Action? BeforeBind { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IHybridCpuExternalRuntime.BindDomain)) BeforeBind?.Invoke();
            return method.Invoke(Inner, args);
        }
    }

    public class StartRuntimeProxy : DispatchProxy
    {
        public IHybridCpuChildDomainRuntimeV3 Inner { get; set; } = null!;
        public int Fault { get; set; }
        public int StartCalls { get; private set; }
        public int UnmapCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int TransitionCalls { get; private set; }
        public int ArtifactBindCalls { get; private set; }
        public int ParentBindCalls { get; set; }
        public int CreateCalls { get; private set; }
        public int MapCalls { get; private set; }
        public Action? BeforeParentBind { get; set; }
        public Action? BeforeCreate { get; set; }
        public Action? BeforeMap { get; set; }
        public Action? BeforeArtifactBind { get; set; }
        public Action? BeforeTransition { get; set; }
        public Action? BeforeUnmap { get; set; }
        public Action? BeforeClose { get; set; }
        public Action? BeforeStart { get; set; }
        private ExternalChildArtifactBindReceipt? artifact;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IHybridCpuChildDomainRuntimeV3.UnmapChildGuestMemory)) { UnmapCalls++; BeforeUnmap?.Invoke(); }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.TransitionChildDomain)) { TransitionCalls++; BeforeTransition?.Invoke(); }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CloseChildDomain)) { CloseCalls++; BeforeClose?.Invoke(); }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact)) { ArtifactBindCalls++; BeforeArtifactBind?.Invoke(); }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CreateChildDomain)) { CreateCalls++; BeforeCreate?.Invoke(); }
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.MapChildGuestMemory)) { MapCalls++; BeforeMap?.Invoke(); }
            bool start = method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution);
            if (start) { StartCalls++; BeforeStart?.Invoke(); }
            object? result = method.Invoke(Inner, args);
            if (result is ExternalChildResult<ExternalChildArtifactBindReceipt> bound) artifact = bound.Receipt;
            if (!start) return result;
            if (Fault == 1) return new ExternalChildResult<ExternalChildExecutionReceipt>(ExternalRuntimeOutcome.Denied, null, "after invocation");
            if (Fault == 2) return new ExternalChildResult<ExternalChildExecutionReceipt>(ExternalRuntimeOutcome.Unknown, null, "lost receipt");
            if (Fault == 3) throw new IOException("reply lost after invocation");
            ExternalChildArtifactBindReceipt admitted = artifact!;
            ExternalOperationIdentity operation = ((ExternalChildExecutionStartRequest)args![1]!).Operation;
            // Controlled exact-work evidence, matching existing adapter tests; never a physical qualification claim.
            ExternalChildExecutionReceipt receipt = new ExternalChildExecutionReceipt(admitted.ArtifactHandle, admitted.ArtifactEpoch,
                admitted.ChildHandle, admitted.ChildEpoch, admitted.MappingHandle, admitted.MappingEpoch,
                admitted.Parent, admitted.PackageSha256, new(1), 3, 1, 0, operation,
                admitted.ContractVersion, admitted.ManifestGeneration + (Fault == 4 ? 1UL : 0UL), true);
            return new ExternalChildResult<ExternalChildExecutionReceipt>(ExternalRuntimeOutcome.Succeeded, receipt, string.Empty);
        }
    }

    private sealed class GenericFailureProvider(HybridCpuExecutableChildAdapter inner, NeutralVirtualizationStatus status) :
        INeutralChildDomainProvider, INeutralGuestMemoryProvider, INeutralChildExecutionProvider, INeutralRuntimeFeatureProvider
    {
        public NeutralVirtualizationStatus? CloseOutcome { get; set; }
        public NeutralVirtualizationStatus? UnmapOutcome { get; set; }
        public bool ThrowAfterCleanup { get; set; }
        public int CloseInvocations { get; private set; }
        public int UnmapInvocations { get; private set; }
        public NeutralVirtualizationStatus? BindOutcome { get; init; }
        public NeutralVirtualizationStatus? CreateOutcome { get; init; }
        public NeutralVirtualizationStatus? MapOutcome { get; init; }
        public Action? BeforeClose { get; set; }
        public Action? BeforeTransition { get; set; }
        public NeutralRuntimeFeatureManifest QueryNeutralFeatures() => inner.QueryNeutralFeatures();
        public NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(NeutralDomainBindingLease parent, NeutralChildDomainIntent intent)
        {
            NeutralVirtualizationResult<NeutralChildDomainLease> result = inner.CreateChildDomain(parent, intent);
            if (CreateOutcome is not { } outcome) return result;
            Assert.True(result.IsSuccess);
            return new(outcome, default, "generic failure after actual root bind/child create");
        }
        public NeutralVirtualizationResult TransitionChildDomain(NeutralChildDomainLease child, NeutralChildDomainTransition transition)
        {
            BeforeTransition?.Invoke();
            return inner.TransitionChildDomain(child, transition);
        }
        public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease child)
        {
            CloseInvocations++;
            BeforeClose?.Invoke();
            if (CloseOutcome == NeutralVirtualizationStatus.NotInvoked)
                return new(NeutralVirtualizationStatus.NotInvoked, default, "refused before invocation");
            NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> result = inner.CloseChildDomain(child);
            if (ThrowAfterCleanup) throw new InvalidOperationException("Lost original close outcome after provider effect.");
            if (CloseOutcome is not { } outcome) return result;
            Assert.True(result.IsSuccess);
            return new(outcome, default, "generic failure after actual closure");
        }
        public NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegion(NeutralGuestMappingRequest request)
        {
            NeutralVirtualizationResult<NeutralGuestMappingLease> result = inner.MapGuestRegion(request);
            if (MapOutcome is not { } outcome) return result;
            Assert.True(result.IsSuccess);
            return new(outcome, default, "generic failure after actual guest map");
        }
        public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegion(NeutralGuestMappingLease mapping)
        {
            UnmapInvocations++;
            if (UnmapOutcome == NeutralVirtualizationStatus.NotInvoked)
                return new(NeutralVirtualizationStatus.NotInvoked, default, "refused before invocation");
            NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> result = inner.UnmapGuestRegion(mapping);
            if (ThrowAfterCleanup) throw new InvalidOperationException("Lost original unmap outcome after provider effect.");
            if (UnmapOutcome is not { } outcome) return result;
            Assert.True(result.IsSuccess);
            return new(outcome, default, "generic failure after actual unmap");
        }
        public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> BindExecutableArtifact(NeutralExecutableArtifactRequest request)
        {
            NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> result = inner.BindExecutableArtifact(request);
            if (BindOutcome is not { } outcome) return result;
            Assert.True(result.IsSuccess);
            return new(outcome, default, "generic bind failure after actual invocation");
        }
        public NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifact(NeutralChildExecutionStartRequest request)
        {
            Assert.True(inner.StartExecutableArtifact(request).IsSuccess);
            return new(status, default, "generic provider failure after actual invocation");
        }
    }

    internal static byte[] CreateExecutablePackage()
    {
        Assembly compiler = Assembly.Load("HybridCPU.Compiler.Core");
        Type optionsType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedStartupOptionsV1");
        object options = optionsType.GetProperty("Production", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        object linkOptions = compiler.GetType("HybridCPU.Compiler.Core.Target.Link.HybridCpuStaticLinkOptionsV1")!
            .GetProperty("Production", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        ulong imageBase = (ulong)linkOptions.GetType().GetProperty("ImageBase")!.GetValue(linkOptions)!;
        string optionsDigest = (string)linkOptions.GetType().GetProperty("OptionsDigest")!.GetValue(linkOptions)!;
        byte[] image = new byte[1024];
        string imageDigest = Convert.ToHexStringLower(SHA256.HashData(image));
        string mapDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("p05-hybridcpu-trace")));

        Type sectionType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkedSectionV1");
        Type sectionKind = Required("HybridCPU.Compiler.Core.Target.HybridCpuObjectSectionKind");
        object section = Activator.CreateInstance(sectionType, "p05", ".text", Enum.Parse(sectionKind, "Code"),
            imageBase, (ulong)image.Length, 256)!;
        Type symbolType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkedSymbolV1");
        Type bindingType = Required("HybridCPU.Compiler.Core.Target.HybridCpuSymbolBinding");
        Type visibilityType = Required("HybridCPU.Compiler.Core.Target.HybridCpuSymbolVisibility");
        object symbol = Activator.CreateInstance(symbolType, "main", "p05", Enum.Parse(bindingType, "Global"),
            Enum.Parse(visibilityType, "Default"), imageBase, 256UL)!;

        Type artifactType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuStaticLinkArtifactV1");
        Type statusType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkStatusV1");
        object artifact = Activator.CreateInstance(artifactType, Enum.Parse(statusType, "Success"), imageBase, image,
            imageDigest, mapDigest, optionsDigest, ArrayOf(sectionType, section), ArrayOf(symbolType, symbol),
            Empty("HybridCPU.Compiler.Core.Target.Link.HybridCpuAppliedRelocationV1"),
            Empty("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkDiagnosticV1"))!;
        Type requestType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedStartupRequestV1");
        object request = Activator.CreateInstance(requestType, artifact, "main", 0, null, null)!;
        Type builderType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedImageBuilderV1");
        object built = builderType.GetMethod("Build")!.Invoke(Activator.CreateInstance(builderType), [request, options])!;
        return (byte[])built.GetType().GetProperty("PackageBytes")!.GetValue(built)!;

        Type Required(string name) => compiler.GetType(name) ?? throw new TypeLoadException(name);
        Array Empty(string name) => Array.CreateInstance(Required(name), 0);
        static Array ArrayOf(Type type, object value)
        {
            Array array = Array.CreateInstance(type, 1);
            array.SetValue(value, 0);
            return array;
        }
    }



}
