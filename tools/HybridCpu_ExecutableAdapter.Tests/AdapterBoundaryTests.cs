using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Authority;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void VersionedExternalRuntimePackageExposesAdapterSessionWithoutGrantingAuthority()
    {
        var assembly = typeof(ExternalOperationAdapterSession).Assembly;
        Assert.Equal("HybridCPU_ExternalRuntime", assembly.GetName().Name);
        Assert.Equal(new Version(1, 6, 0, 0), assembly.GetName().Version);
        Assert.Throws<ArgumentNullException>(() => new ExternalOperationAdapterSession(null!));
    }

    [Fact]
    public void OperationalChildAdapterPublishesOnlyQualifiedFamilies()
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var features = adapter.QueryNeutralFeatures();
        Assert.Equal(NeutralRuntimeFeatureAvailability.Executable,
            features.Resolve(NeutralRuntimeFeatureFamily.ChildExecutableArtifact).Availability);
        Assert.Equal(NeutralRuntimeFeatureAvailability.Executable,
            features.Resolve(NeutralRuntimeFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal(NeutralRuntimeFeatureAvailability.Unavailable,
            features.Resolve(NeutralRuntimeFeatureFamily.ChildTrapDelivery).Availability);
        Assert.IsNotAssignableFrom<INeutralVirtualTrapProvider>(adapter);
        Assert.False(typeof(INeutralDomainRuntime).IsAssignableFrom(adapter.GetType()));
    }

    [Fact]
    public void NoProviderCompositionCannotBecomeAnExecutableFallback()
    {
        var admissionOnly = new NeutralRuntimeFeatureManifest(
        [
            new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, 1, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        ]);

        Assert.DoesNotContain(admissionOnly.Features,
            feature => feature.Availability == NeutralRuntimeFeatureAvailability.Executable);
        Assert.False(typeof(INeutralDomainRuntime).IsAssignableFrom(typeof(HybridCpuExecutableChildAdapter)));
    }

    [Fact]
    public void RejectedExternalParkQuarantinesChildUntilExplicitClose()
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution;
        var child = adapter.CreateChildDomain(parent,
            new(new(1, 4096), new(authority, authority)));
        Assert.True(child.IsSuccess, child.Reason);

        var park = adapter.TransitionChildDomain(child.Value, NeutralChildDomainTransition.Park);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, park.Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child.Value, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child.Value, NeutralChildDomainTransition.BeginDrain).Status);

        var close = adapter.CloseChildDomain(child.Value);
        Assert.True(close.IsSuccess, close.Reason);
        Assert.True(close.Value.IsTerminal);
    }

    [Fact]
    public void ForgedTransitionReceiptCannotAdvanceOrReleaseChild()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        ((FaultingChildRuntimeProxy)proxy).Inner = runtime;
        ((FaultingChildRuntimeProxy)proxy).ForgeTransition = true;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var child = CreateExecutionChild(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
    }

    [Fact]
    public void ForgedTerminalCloseReceiptCannotReleaseChild()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, FaultingChildRuntimeProxy>();
        ((FaultingChildRuntimeProxy)proxy).Inner = runtime;
        ((FaultingChildRuntimeProxy)proxy).ForgeClose = true;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var child = CreateExecutionChild(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
    }

    private static NeutralChildDomainLease CreateExecutionChild(HybridCpuExecutableChildAdapter adapter)
    {
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution;
        var child = adapter.CreateChildDomain(parent,
            new(new(1, 4096), new(authority, authority)));
        Assert.True(child.IsSuccess, child.Reason);
        return child.Value;
    }

    public class FaultingChildRuntimeProxy : DispatchProxy
    {
        public IHybridCpuChildDomainRuntimeV3 Inner { get; set; } = null!;
        public bool ForgeTransition { get; set; }
        public bool ForgeClose { get; set; }
        public bool ForgeStart { get; set; }
        private ExternalChildArtifactBindReceipt? boundArtifact;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result = method!.Invoke(Inner, args);
            if (method.Name == nameof(IHybridCpuChildDomainRuntimeV3.BindChildExecutableArtifact) &&
                result is ExternalChildResult<ExternalChildArtifactBindReceipt> bind)
                boundArtifact = bind.Receipt;
            if (ForgeStart && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.StartChildExecution) &&
                boundArtifact is { } artifact)
            {
                var operation = ((ExternalChildExecutionStartRequest)args![1]!).Operation;
                var receipt = new ExternalChildExecutionReceipt(artifact.ArtifactHandle,
                    artifact.ArtifactEpoch, artifact.ChildHandle, artifact.ChildEpoch,
                    artifact.MappingHandle, artifact.MappingEpoch, artifact.Parent,
                    artifact.PackageSha256, new(1), 1, 1, 0, operation,
                    artifact.ContractVersion, artifact.ManifestGeneration + 1, true);
                return new ExternalChildResult<ExternalChildExecutionReceipt>(
                    ExternalRuntimeOutcome.Succeeded, receipt, string.Empty);
            }
            if (ForgeTransition && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.TransitionChildDomain))
            {
                var lease = (ExternalChildDomainLease)args![0]!;
                var transition = (ExternalChildDomainTransition)args[1]!;
                var operation = (ExternalOperationIdentity)args[2]!;
                var manifest = ((HybridCpuExternalRuntime)Inner).QueryFeatures();
                var receipt = new ExternalChildDomainTransitionReceipt(lease, operation,
                    manifest.ContractVersion, manifest.Generation + 1, transition,
                    ExternalChildDomainState.Parked);
                return new ExternalChildResult<ExternalChildDomainTransitionReceipt>(
                    ExternalRuntimeOutcome.Succeeded, receipt, string.Empty);
            }
            if (ForgeClose && method.Name == nameof(IHybridCpuChildDomainRuntimeV3.CloseChildDomain) &&
                result is ExternalChildResult<ExternalChildDomainCloseReceipt> close && close.Receipt is not null)
                return close with { Receipt = close.Receipt with { ManifestGeneration = close.Receipt.ManifestGeneration + 1 } };
            return result;
        }
    }

    [Fact]
    public void AssemblyReferencesContractsButNotModelAuthorityOrHybridCpuInternals()
    {
        var references = typeof(HybridCpuExecutableAdapterProject).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, x => x.Name == "HybridCPU_NeutralRuntime.Contracts");
        Assert.DoesNotContain(references, x => x.Name?.Contains("Model", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(references, x => x.Name == "HybridCPU_NeutralRuntime.AuthorityCore");
        Assert.DoesNotContain(references, x => x.Name?.Contains("ISE", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes(),
            type => type.Name.Contains("Scaffold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LifecycleFixtureIncludesEveryFailClosedStateAndRejectsOptimisticTransitions()
    {
        Assert.Equal(
            ["Opening", "Active", "Draining", "Closed", "Revoked", "Faulted", "Quarantined"],
            Enum.GetNames<AdapterResourceLifecycle>());

        var fixture = new LifecycleSemanticsFixture();
        Assert.False(fixture.TryTransition(AdapterResourceLifecycle.Closed));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Active));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Draining));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Closed));
        Assert.False(fixture.TryTransition(AdapterResourceLifecycle.Active));
    }

    [Fact]
    public void AuthorityReservationsAreUniqueAndLateGenerationCannotCrossApply()
    {
        var operations = new OperationReservationRegistry();
        var first = operations.Reserve();
        var second = operations.Reserve();
        Assert.NotEqual(first.Handle, second.Handle);
        Assert.Equal(NeutralLateResultDisposition.RejectedDifferentGeneration,
            operations.Reconcile(first with { Generation = new(first.Generation.Value + 1) }, true));
        Assert.Equal(NeutralLateResultDisposition.Applied, operations.Reconcile(first, true));
    }

    [Fact]
    public void UnknownBackendOutcomeQuarantinesWithoutReleasingReservation()
    {
        var operations = new OperationReservationRegistry();
        var operation = operations.Reserve();
        Assert.Equal(NeutralLateResultDisposition.QuarantinedUnknownOutcome,
            operations.Reconcile(operation, false));
        Assert.True(operations.IsReserved(operation));
        Assert.Equal(NeutralLateResultDisposition.Applied, operations.Reconcile(operation, true));
        Assert.False(operations.IsReserved(operation));
    }

    [Fact]
    public void PublicSurfaceContainsNoRawHardwareIdentityOrDmaExecutionOperation()
    {
        string[] forbidden = ["DomainTag", "AddressSpaceTag", "Vmx", "Vmcs", "Apic", "Msi", "Gsi", "Iommu", "Physical", "BusAddress", "SubmitDma", "CompleteDma", "Descriptor", "Queue"];
        var names = typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(member => member.Name);
        foreach (var fragment in forbidden)
            Assert.DoesNotContain(names, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class LifecycleSemanticsFixture
    {
        public AdapterResourceLifecycle Lifecycle { get; private set; } = AdapterResourceLifecycle.Opening;

        public bool TryTransition(AdapterResourceLifecycle next)
        {
            bool allowed = (Lifecycle, next) switch
            {
                (AdapterResourceLifecycle.Opening, AdapterResourceLifecycle.Active or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                (AdapterResourceLifecycle.Active, AdapterResourceLifecycle.Draining or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                (AdapterResourceLifecycle.Draining, AdapterResourceLifecycle.Closed or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                _ => false,
            };
            if (allowed) Lifecycle = next;
            return allowed;
        }
    }
}
