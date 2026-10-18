using SingPlus.Contracts;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class SemanticTraceInstrumentationTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("forged")]
    [InlineData("lost")]
    [InlineData("throw")]
    [InlineData("tuple")]
    public void LateChildCloseOwnerDriftRetainsExactClosureForLocalRetryWithoutErasingIndependentPins(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory;
        var created = adapter.CreateChildDomain(parent, new(new(1, 8192), new(rights, rights)));
        Assert.True(created.IsSuccess, created.Reason);
        var lease = created.Value;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", flags)!.GetValue(adapter)!;
        var original = owners[lease.Handle]!;
        bool replaced = false;
        injector.BeforeCloseForTest = () =>
        {
            var replacement = original.GetType().GetMethod("<Clone>$")!.Invoke(original, null)!;
            replacement.GetType().GetProperty("AdmissionPublished")!.SetValue(replacement, false);
            if (fault == "tuple") replacement.GetType().GetProperty("Neutral")!.SetValue(replacement, lease with { Epoch = new(2) });
            owners[lease.Handle] = replacement;
            replaced = true;
        };
        injector.ForgeClose = fault == "forged";
        injector.LoseCloseReceipt = fault == "lost";
        injector.ThrowAfterClose = fault == "throw";
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(lease).Status);
        injector.BeforeCloseForTest = null;
        Assert.True(replaced);
        Assert.Equal(1, injector.CloseCalls);
        var foreign = owners[lease.Handle]!;
        Assert.Null(foreign.GetType().GetProperty("CloseReceipt")!.GetValue(foreign));
        Assert.NotEqual(NeutralVirtualizationStatus.Success, adapter.CloseChildDomain(lease).Status);
        owners[lease.Handle] = original;
        var retained = original;
        var receiptProperty = retained.GetType().GetProperty("CloseReceipt")!;
        var receipt = receiptProperty.GetValue(retained);
        Assert.True((bool)retained.GetType().GetProperty("CloseMayHaveEffect")!.GetValue(retained)!);
        Assert.Equal(NeutralChildDomainState.Faulted, retained.GetType().GetProperty("State")!.GetValue(retained));
        Assert.Empty(((System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingChildCloses", flags)!.GetValue(adapter)!).Cast<object>());
        if (fault is "exact" or "tuple")
        {
            var exact = Assert.IsType<ExternalChildDomainCloseReceipt>(receipt);
            Assert.True(exact.IsTerminal);
            Assert.Equal(retained.GetType().GetProperty("External")!.GetValue(retained), exact.Lease);
            Assert.Equal(NeutralVirtualizationStatus.Stale, adapter.CloseChildDomain(lease with { Epoch = new(2) }).Status);
            receiptProperty.SetValue(retained, exact with { ManifestGeneration = exact.ManifestGeneration + 1 });
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(lease).Status);
            receiptProperty.SetValue(retained, exact);
            var operationProperty = retained.GetType().GetProperty("CloseOperation")!;
            var originalOperation = operationProperty.GetValue(retained);
            operationProperty.SetValue(retained, default(ExternalOperationIdentity));
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(lease).Status);
            operationProperty.SetValue(retained, originalOperation);
            var replacement = retained.GetType().GetMethod("<Clone>$")!.Invoke(retained, null)!;
            var differentLease = lease with { Epoch = new(2) };
            replacement.GetType().GetProperty("Neutral")!.SetValue(replacement, differentLease);
            owners[lease.Handle] = replacement;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(differentLease).Status);
            Assert.Same(replacement, owners[lease.Handle]);
            owners[lease.Handle] = retained;
            var mapPin = retained.GetType().GetProperty("GuestMapMayHaveEffect")!;
            mapPin.SetValue(retained, true);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(lease).Status);
            Assert.Same(retained, owners[lease.Handle]);
            Assert.Same(exact, receiptProperty.GetValue(retained));
            mapPin.SetValue(retained, false);
            var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
            operation.SetValue(adapter, ulong.MaxValue);
            var closed = adapter.CloseChildDomain(lease);
            Assert.True(closed.IsSuccess, closed.Reason);
            Assert.Equal(NeutralChildDomainState.Closed, closed.Value.ResultingState);
            Assert.Equal(ulong.MaxValue, operation.GetValue(adapter));
            Assert.Equal(NeutralVirtualizationStatus.Stale, adapter.CloseChildDomain(lease).Status);
        }
        else
        {
            Assert.Null(receipt);
            Assert.Equal(fault == "tuple" ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.Ambiguous,
                adapter.CloseChildDomain(lease).Status);
            Assert.Equal(NeutralChildDomainState.Faulted, retained.GetType().GetProperty("State")!.GetValue(retained));
        }
        Assert.Equal(1, injector.CloseCalls);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("forged")]
    [InlineData("lost")]
    [InlineData("throw")]
    [InlineData("tuple")]
    public void LateGuestUnmapOwnerDriftRetainsOnlyExactOriginalClosureAndRetriesLocalCleanup(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory;
        var childResult = adapter.CreateChildDomain(parent, new(new(1, 8192), new(rights, rights)));
        Assert.True(childResult.IsSuccess, childResult.Reason);
        var child = childResult.Value;
        var source = new NeutralOwnedRegionMappingLease(parent, new(0, 8192, NeutralMemoryAccess.Read),
            NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapped = adapter.MapGuestRegion(new(child, source, new(0, 8192), NeutralGuestMemoryAccess.Read));
        Assert.True(mapped.IsSuccess, mapped.Reason);
        var lease = mapped.Value;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("mappings", flags)!.GetValue(adapter)!;
        var original = owners[lease.Handle]!;
        bool replaced = false;
        injector.BeforeUnmapForTest = () =>
        {
            var replacement = original.GetType().GetMethod("<Clone>$")!.Invoke(original, null)!;
            replacement.GetType().GetProperty("AdmissionPublished")!.SetValue(replacement, false);
            if (fault == "tuple") replacement.GetType().GetProperty("Neutral")!.SetValue(replacement, lease with { Epoch = new(2) });
            owners[lease.Handle] = replacement;
            replaced = true;
        };
        injector.ForgeUnmap = fault == "forged";
        injector.LoseUnmapReceipt = fault == "lost";
        injector.ThrowAfterUnmap = fault == "throw";
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.UnmapGuestRegion(lease).Status);
        injector.BeforeUnmapForTest = null;
        Assert.True(replaced);
        Assert.Equal(1, injector.UnmapCalls);
        var foreign = owners[lease.Handle]!;
        Assert.Null(foreign.GetType().GetProperty("ClosureReceipt")!.GetValue(foreign));
        Assert.NotEqual(NeutralVirtualizationStatus.Success, adapter.UnmapGuestRegion(lease).Status);
        owners[lease.Handle] = original;
        var retained = original;
        var receiptProperty = retained.GetType().GetProperty("ClosureReceipt")!;
        var receipt = receiptProperty.GetValue(retained);
        Assert.True((bool)retained.GetType().GetProperty("ClosureMayHaveEffect")!.GetValue(retained)!);
        Assert.Empty(((System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingGuestUnmaps", flags)!.GetValue(adapter)!).Cast<object>());
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
        if (fault is "exact" or "tuple")
        {
            var exact = Assert.IsType<ExternalGuestMemoryUnmapReceipt>(receipt);
            Assert.True(exact.IsTerminal);
            Assert.Equal(retained.GetType().GetProperty("External")!.GetValue(retained), exact.Mapping);
            Assert.Equal(NeutralVirtualizationStatus.Stale, adapter.UnmapGuestRegion(lease with { Epoch = new(2) }).Status);
            receiptProperty.SetValue(retained, exact with { ManifestGeneration = exact.ManifestGeneration + 1 });
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.UnmapGuestRegion(lease).Status);
            Assert.True(owners.Contains(lease.Handle));
            receiptProperty.SetValue(retained, exact);
            var originalOperation = retained.GetType().GetProperty("ClosureOperation")!.GetValue(retained);
            retained.GetType().GetProperty("ClosureOperation")!.SetValue(retained, default(ExternalOperationIdentity));
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.UnmapGuestRegion(lease).Status);
            retained.GetType().GetProperty("ClosureOperation")!.SetValue(retained, originalOperation);
            var replacement = retained.GetType().GetMethod("<Clone>$")!.Invoke(retained, null)!;
            var differentLease = lease with { Epoch = new(2) };
            replacement.GetType().GetProperty("Neutral")!.SetValue(replacement, differentLease);
            owners[lease.Handle] = replacement;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.UnmapGuestRegion(differentLease).Status);
            Assert.Same(replacement, owners[lease.Handle]);
            owners[lease.Handle] = retained;
            var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
            operation.SetValue(adapter, ulong.MaxValue);
            Assert.True(adapter.UnmapGuestRegion(lease).IsSuccess);
            Assert.Equal(ulong.MaxValue, operation.GetValue(adapter));
            Assert.False(owners.Contains(lease.Handle));
            Assert.Equal(NeutralVirtualizationStatus.Stale, adapter.UnmapGuestRegion(lease).Status);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
        }
        else
        {
            Assert.Null(receipt);
            Assert.Equal(fault == "tuple" ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.Ambiguous,
                adapter.UnmapGuestRegion(lease).Status);
            Assert.True(owners.Contains(lease.Handle));
        }
        Assert.Equal(1, injector.UnmapCalls);
        Assert.Equal(0, injector.TransitionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingGuestUnmapRefusesChildStartAndDrainAndLostReceiptRemainsPinned(bool loseReceipt)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ExactStartWorkForTest = 3;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.GuestMemory | NeutralChildAuthorityClass.Execution;
        var childResult = adapter.CreateChildDomain(parent, new(new(1, 16384), new(rights, rights)));
        Assert.True(childResult.IsSuccess, childResult.Reason);
        var child = childResult.Value;
        var source = new NeutralOwnedRegionMappingLease(parent, new(0, 16384, NeutralMemoryAccess.Read),
            NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var executableMap = adapter.MapGuestRegion(new(child, source, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute));
        Assert.True(executableMap.IsSuccess, executableMap.Reason);
        var removableMap = adapter.MapGuestRegion(new(child, source, new(8192, 8192), NeutralGuestMemoryAccess.Read));
        Assert.True(removableMap.IsSuccess, removableMap.Reason);
        var artifact = adapter.BindExecutableArtifact(new(child, executableMap.Value, CreateExecutablePackage(), 3));
        Assert.True(artifact.IsSuccess, artifact.Reason);
        var request = new NeutralChildExecutionStartRequest(child, artifact.Value, new(17), new(3));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", flags)!.GetValue(adapter)!;
        var mappings = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("mappings", flags)!.GetValue(adapter)!;
        var originalChild = owners[child.Handle];
        var originalMapping = mappings[removableMap.Value.Handle];
        bool checkedRefusal = false;
        injector.LoseUnmapReceipt = loseReceipt;
        injector.BeforeUnmapForTest = () =>
        {
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.Equal(0, injector.StartCalls);
            Assert.Equal(0, injector.TransitionCalls);
            Assert.Empty(sink.Events);
            Assert.Same(originalChild, owners[child.Handle]);
            Assert.Same(originalMapping, mappings[removableMap.Value.Handle]);
            checkedRefusal = true;
        };
        var closed = adapter.UnmapGuestRegion(removableMap.Value);
        injector.BeforeUnmapForTest = null;
        Assert.True(checkedRefusal);
        Assert.Equal(1, injector.UnmapCalls);
        if (loseReceipt)
        {
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, closed.Status);
            Assert.True(mappings.Contains(removableMap.Value.Handle));
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.UnmapGuestRegion(removableMap.Value).Status);
            Assert.Equal(1, injector.UnmapCalls);
            Assert.Equal(0, injector.StartCalls);
            Assert.Empty(sink.Events);
        }
        else
        {
            Assert.True(closed.IsSuccess, closed.Reason);
            Assert.False(mappings.Contains(removableMap.Value.Handle));
            Assert.True(adapter.StartExecutableArtifact(request).IsSuccess);
            Assert.Equal(1, injector.StartCalls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingGuestAdmissionRefusesStartAndDrainWithoutTraceOrProviderEffect(bool bind)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ExactStartWorkForTest = 3;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", flags)!.GetValue(adapter)!;
        var original = owners[request.ChildLease.Handle];
        bool checkedRefusal = false;
        Action probe = () =>
        {
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.TransitionChildDomain(request.ChildLease, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.Equal(0, injector.StartCalls);
            Assert.Equal(0, injector.TransitionCalls);
            Assert.Empty(sink.Events);
            Assert.Same(original, owners[request.ChildLease.Handle]);
            checkedRefusal = true;
        };
        if (bind)
        {
            injector.BeforeArtifactBindForTest = probe;
            adapter.BindExecutableArtifact(new(request.ChildLease, mapping, CreateExecutablePackage(), 3));
            injector.BeforeArtifactBindForTest = null;
        }
        else
        {
            injector.BeforeMapForTest = probe;
            adapter.MapGuestRegion(new(request.ChildLease, mapping.ParentMapping,
                new(0, 8192), NeutralGuestMemoryAccess.Read));
            injector.BeforeMapForTest = null;
        }
        Assert.True(checkedRefusal);
        Assert.Equal(0, injector.StartCalls);

    }

    [Fact]
    public void PendingChildTransitionRefusesGuestUnmapBeforeEffectAndKeepsExactMapping()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory |
            NeutralChildAuthorityClass.Execution;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(rights, rights))).Value;
        var source = new NeutralOwnedRegionMappingLease(parent, new(0, 8192, NeutralMemoryAccess.Read),
            NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, source, new(0, 8192), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        var mappings = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("mappings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var original = mappings[mapping.Handle];
        bool checkedRefusal = false;
        injector.BeforeTransitionForTest = () =>
        {
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.UnmapGuestRegion(mapping).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.BindExecutableArtifact(new(child, mapping, new byte[] { 1 }, 1)).Status);
            Assert.Equal(0, injector.ArtifactBindCalls);
            Assert.Empty(((System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
                .GetField("artifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!).Values);
            Assert.Equal(0, injector.UnmapCalls);
            Assert.Same(original, mappings[mapping.Handle]);
            checkedRefusal = true;
        };
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
        Assert.True(checkedRefusal);
        Assert.Equal(1, injector.TransitionCalls);
        Assert.Equal(0, injector.UnmapCalls);
        Assert.Same(original, mappings[mapping.Handle]);
    }

    private static void InjectGuestMapUncertainty(HybridCpuExecutableChildAdapter adapter, NeutralChildDomainLease lease)
    {
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var original = owners[lease.Handle]!;
        original.GetType().GetProperty("GuestMapMayHaveEffect")!.SetValue(original, true);
        original.GetType().GetProperty("State")!.SetValue(original, NeutralChildDomainState.Faulted);
    }

    private static void InjectDrainingOwnerReplacement(HybridCpuExecutableChildAdapter adapter, NeutralChildDomainLease lease)
    {
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var original = owners[lease.Handle]!;
        original.GetType().GetProperty("State")!.SetValue(original, NeutralChildDomainState.Draining);
    }

    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void StartOperationCapacityPrecedesTraceAndEffectAndRetainsArtifact(ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime; injector.ExactStartWorkForTest = 3;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, value => mapping = value);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
        var identity = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", flags)!;
        var beforeIdentity = identity.GetValue(adapter);
        var artifacts = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("artifacts", flags)!.GetValue(adapter)!;
        var originalArtifact = artifacts.Values.Cast<object>().Single();
        operation.SetValue(adapter, capacity);
        bool reserved = false; NeutralVirtualizationStatus? nested = null;
        injector.BeforeStartForTest = () =>
        {
            reserved = (ulong)operation.GetValue(adapter)! == ulong.MaxValue;
            nested = adapter.StartExecutableArtifact(request).Status;
        };
        var result = adapter.StartExecutableArtifact(request); injector.BeforeStartForTest = null;
        var accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, result.IsSuccess);
        Assert.Equal(accepted ? 1 : 0, injector.StartCalls);
        Assert.Equal(accepted ? ulong.MaxValue : capacity, (ulong)operation.GetValue(adapter)!);
        Assert.Equal(beforeIdentity, identity.GetValue(adapter));
        Assert.Same(originalArtifact, artifacts.Values.Cast<object>().Single());
        var receipt = originalArtifact.GetType().GetProperty("ExecutionReceipt")!.GetValue(originalArtifact);
        if (accepted)
        {
            Assert.True(reserved); Assert.Equal(NeutralVirtualizationStatus.NotInvoked, nested);
            Assert.Equal(result.Value, receipt);
            Assert.Equal(new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.RetireOrComplete }, sink.Events.Select(item => item.Kind));
        }
        else { Assert.Null(nested); Assert.Null(receipt); Assert.Empty(sink.Events); }
        Assert.Equal(accepted ? NeutralVirtualizationStatus.Denied : NeutralVirtualizationStatus.NotInvoked,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(accepted ? 1 : 0, injector.StartCalls);
        Assert.False(adapter.UnmapGuestRegion(mapping).IsSuccess); Assert.Equal(0, injector.UnmapCalls);
        Assert.False(adapter.CloseChildDomain(request.ChildLease).IsSuccess); Assert.Equal(0, injector.CloseCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter).GetField("pendingChildStarts", flags)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
    }

    [Theory]
    [InlineData(false, 0UL)] [InlineData(false, ulong.MaxValue)] [InlineData(false, ulong.MaxValue - 1)]
    [InlineData(true, 0UL)] [InlineData(true, ulong.MaxValue)] [InlineData(true, ulong.MaxValue - 1)]
    public void CleanupOperationCapacityPreservesExactOwnerUntilClosure(bool unmap, ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy; injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        NeutralGuestMappingLease mapping = default;
        if (unmap)
        {
            var parentMapping = new NeutralOwnedRegionMappingLease(parent, new(0, 8192, NeutralMemoryAccess.Read),
                NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
            mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read)).Value;
        }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField(unmap ? "mappings" : "children", flags)!.GetValue(adapter)!;
        var original = owners.Values.Cast<object>().Single();
        operation.SetValue(adapter, capacity);
        NeutralVirtualizationStatus Invoke() => unmap ? adapter.UnmapGuestRegion(mapping).Status : adapter.CloseChildDomain(child).Status;
        bool reserved = false; NeutralVirtualizationStatus? nested = null;
        Action probe = () => { reserved = (ulong)operation.GetValue(adapter)! == ulong.MaxValue; nested = Invoke(); };
        injector.BeforeUnmapForTest = probe; injector.BeforeCloseForTest = probe;
        var status = Invoke(); injector.BeforeUnmapForTest = null; injector.BeforeCloseForTest = null;
        var accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, status == NeutralVirtualizationStatus.Success);
        Assert.Equal(accepted ? 1 : 0, unmap ? injector.UnmapCalls : injector.CloseCalls);
        Assert.Equal(accepted ? ulong.MaxValue : capacity, (ulong)operation.GetValue(adapter)!);
        if (accepted)
        {
            Assert.True(reserved); Assert.Equal(NeutralVirtualizationStatus.Denied, nested);
            if (unmap) Assert.Empty(owners);
            else Assert.Same(original, owners.Values.Cast<object>().Single());
        }
        else
        {
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, status); Assert.Null(nested);
            Assert.Same(original, owners.Values.Cast<object>().Single());
            if (unmap) { Assert.False(adapter.CloseChildDomain(child).IsSuccess); Assert.Equal(0, injector.CloseCalls); }
        }
        Assert.NotEqual(NeutralVirtualizationStatus.Success, Invoke());
        Assert.Equal(accepted ? 1 : 0, unmap ? injector.UnmapCalls : injector.CloseCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField(unmap ? "pendingGuestUnmaps" : "pendingChildCloses", flags)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
    }

    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void TransitionOperationCapacityRefusesWithoutFaultingAndLastEffectStaysPinned(ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy; injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.Execution;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
        var owners = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children", flags)!.GetValue(adapter)!;
        var original = owners.Values.Cast<object>().Single(); operation.SetValue(adapter, capacity);
        bool reserved = false; NeutralVirtualizationStatus? nested = null;
        injector.BeforeTransitionForTest = () =>
        {
            reserved = (ulong)operation.GetValue(adapter)! == ulong.MaxValue;
            nested = adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status;
        };
        var result = adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park);
        injector.BeforeTransitionForTest = null;
        var invoked = capacity == ulong.MaxValue - 1;
        // Actual provider rejects Park of a Created child; existing ambiguity policy pins that invocation.
        Assert.Equal(invoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, result.Status);
        Assert.Equal(invoked ? 1 : 0, injector.TransitionCalls);
        Assert.Equal(invoked ? ulong.MaxValue : capacity, (ulong)operation.GetValue(adapter)!);
        if (invoked) { Assert.True(reserved); Assert.Equal(NeutralVirtualizationStatus.NotInvoked, nested); }
        else { Assert.Null(nested); Assert.Same(original, owners.Values.Cast<object>().Single()); }
        var beforeClose = owners.Values.Cast<object>().Single();
        Assert.False(adapter.CloseChildDomain(child).IsSuccess); Assert.Equal(0, injector.CloseCalls);
        Assert.Same(beforeClose, owners.Values.Cast<object>().Single());
        Assert.Equal(invoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(invoked ? 1 : 0, injector.TransitionCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter).GetField("pendingChildTransitions", flags)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
    }

    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void ArtifactBindOperationCapacityRefusesBeforeAllocationAndKeepsKnownPins(ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy; injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory | NeutralChildAuthorityClass.Execution;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent, new(0, 8192, NeutralMemoryAccess.Read),
            NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        var request = new NeutralExecutableArtifactRequest(child, mapping, CreateExecutablePackage(), 3);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
        var identity = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", flags)!;
        var before = (ulong)identity.GetValue(adapter)!; operation.SetValue(adapter, capacity);
        bool reserved = false; NeutralVirtualizationStatus? nested = null;
        injector.BeforeArtifactBindForTest = () =>
        {
            reserved = (ulong)operation.GetValue(adapter)! == ulong.MaxValue;
            nested = adapter.BindExecutableArtifact(request).Status;
        };
        var result = adapter.BindExecutableArtifact(request); injector.BeforeArtifactBindForTest = null;
        var accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, result.IsSuccess); Assert.Equal(accepted ? 1 : 0, injector.ArtifactBindCalls);
        Assert.Equal(before + (accepted ? 1UL : 0UL), (ulong)identity.GetValue(adapter)!);
        Assert.Equal(accepted ? ulong.MaxValue : capacity, (ulong)operation.GetValue(adapter)!);
        if (accepted) { Assert.True(reserved); Assert.Equal(NeutralVirtualizationStatus.Denied, nested); }
        else Assert.Null(nested);
        Assert.False(adapter.BindExecutableArtifact(request).IsSuccess);
        Assert.Equal(accepted ? 1 : 0, injector.ArtifactBindCalls);
        var artifacts = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("artifacts", flags)!.GetValue(adapter)!;
        Assert.Equal(accepted ? 1 : 0, artifacts.Count);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter).GetField("pendingArtifactBinds", flags)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        if (accepted) { Assert.False(adapter.UnmapGuestRegion(mapping).IsSuccess); Assert.Equal(0, injector.UnmapCalls); }
    }
    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void GuestMapOperationCapacityRefusesBeforeLocalAllocationOrEffect(ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy; injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory;
        var intent = new NeutralChildDomainIntent(new(1, 8192), new(authority, authority));
        var child = adapter.CreateChildDomain(parent, intent).Value;
        var mapping = new NeutralOwnedRegionMappingLease(parent, new(0, 8192, NeutralMemoryAccess.Read),
            NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var request = new NeutralGuestMappingRequest(child, mapping, new(0, 8192), NeutralGuestMemoryAccess.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
        var identity = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", flags)!;
        var before = (ulong)identity.GetValue(adapter)!; operation.SetValue(adapter, capacity);
        bool reserved = false; NeutralVirtualizationStatus? nested = null;
        injector.BeforeMapForTest = () =>
        {
            reserved = (ulong)operation.GetValue(adapter)! == ulong.MaxValue;
            nested = adapter.MapGuestRegion(request).Status;
        };
        var result = adapter.MapGuestRegion(request); injector.BeforeMapForTest = null;
        var accepted = capacity == ulong.MaxValue - 1;
        Assert.Equal(accepted, result.IsSuccess);
        Assert.Equal(accepted ? 1 : 0, injector.MapCalls);
        Assert.Equal(before + (accepted ? 1UL : 0UL), (ulong)identity.GetValue(adapter)!);
        Assert.Equal(accepted ? ulong.MaxValue : capacity, (ulong)operation.GetValue(adapter)!);
        if (accepted) { Assert.True(reserved); Assert.Equal(NeutralVirtualizationStatus.NotInvoked, nested); }
        else Assert.Null(nested);
        Assert.False(adapter.MapGuestRegion(request).IsSuccess);
        Assert.Equal(accepted ? 1 : 0, injector.MapCalls);
        var pending = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("pendingGuestMaps", flags)!.GetValue(adapter)!;
        Assert.Empty(pending);
    }
    [Theory]
    [InlineData(false, 0UL)] [InlineData(false, ulong.MaxValue)] [InlineData(false, ulong.MaxValue - 1)]
    [InlineData(false, ulong.MaxValue - 2)]
    [InlineData(true, 0UL)] [InlineData(true, ulong.MaxValue)] [InlineData(true, ulong.MaxValue - 1)]
    public void ChildCreateReservesApplicableOperationTupleBeforeFirstEffect(bool existingParent, ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var rootProxy = DispatchProxy.Create<IHybridCpuExternalRuntime, AdapterBoundaryTests.FaultingRootRuntimeProxy>();
        var root = (AdapterBoundaryTests.FaultingRootRuntimeProxy)rootProxy; root.Inner = runtime;
        var childProxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var child = (AdapterBoundaryTests.FaultingChildRuntimeProxy)childProxy; child.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(rootProxy, childProxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.Execution;
        var intent = new NeutralChildDomainIntent(new(1, 4096), new(authority, authority));
        if (existingParent) Assert.True(adapter.CreateChildDomain(parent, intent).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var operation = typeof(HybridCpuExecutableChildAdapter).GetField("nextOperation", flags)!;
        var identity = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", flags)!;
        var identitiesBefore = (ulong)identity.GetValue(adapter)!;
        operation.SetValue(adapter, capacity);
        var parents = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("parents", flags)!.GetValue(adapter)!;
        var originalParents = parents.Values.Cast<object>().ToArray();
        bool reserved = false; NeutralVirtualizationStatus? nested = null;
        Action probe = () =>
        {
            reserved = (ulong)operation.GetValue(adapter)! == ulong.MaxValue;
            nested = adapter.CreateChildDomain(new(new(99), new(1)), intent).Status;
        };
        root.BeforeBindForTest = probe; child.BeforeCreateForTest = probe;
        var result = adapter.CreateChildDomain(parent, intent);
        root.BeforeBindForTest = null; child.BeforeCreateForTest = null;
        var accepted = capacity == (existingParent ? ulong.MaxValue - 1 : ulong.MaxValue - 2);
        Assert.Equal(accepted, result.IsSuccess);
        var prior = existingParent ? 1 : 0;
        Assert.Equal(prior + (accepted && !existingParent ? 1 : 0), root.BindCalls);
        Assert.Equal(prior + (accepted ? 1 : 0), child.CreateCalls);
        Assert.Equal(accepted ? ulong.MaxValue : capacity, (ulong)operation.GetValue(adapter)!);
        Assert.Equal(identitiesBefore + (accepted ? 1UL : 0UL), (ulong)identity.GetValue(adapter)!);
        if (accepted)
        {
            Assert.True(reserved); Assert.Equal(NeutralVirtualizationStatus.NotInvoked, nested);
            Assert.False(adapter.CreateChildDomain(parent, intent).IsSuccess);
            Assert.Equal(prior + 1, child.CreateCalls);
        }
        else
        {
            Assert.Null(nested); Assert.Equal(originalParents, parents.Values.Cast<object>().ToArray());
        }
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter).GetField("pendingParentBinds", flags)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
    }
    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void GuestIdentityCapacityPrecedesMapEffectAndActualCleanupNeverRecycles(ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy; injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory;
        var intent = new NeutralChildDomainIntent(new(1, 8192), new(authority, authority));
        var child = adapter.CreateChildDomain(parent, intent).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var request = new NeutralGuestMappingRequest(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read);
        var counter = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        counter.SetValue(adapter, capacity);
        bool reserved = false; NeutralVirtualizationStatus? nestedMap = null; NeutralVirtualizationStatus? nestedChild = null;
        injector.BeforeMapForTest = () =>
        {
            reserved = (ulong)counter.GetValue(adapter)! == ulong.MaxValue;
            nestedMap = adapter.MapGuestRegion(request).Status;
            nestedChild = adapter.CreateChildDomain(new(new(99), new(1)), intent).Status;
        };
        var result = adapter.MapGuestRegion(request); injector.BeforeMapForTest = null;
        if (capacity == ulong.MaxValue - 1)
        {
            Assert.True(result.IsSuccess); Assert.True(reserved);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, nestedMap); Assert.Equal(NeutralVirtualizationStatus.NotInvoked, nestedChild);
            Assert.Equal(capacity, result.Value.Handle.Value); Assert.Equal(1, injector.MapCalls);
            Assert.False(adapter.MapGuestRegion(request).IsSuccess); Assert.Equal(1, injector.MapCalls);
            Assert.True(adapter.UnmapGuestRegion(result.Value).IsSuccess); Assert.Equal(1, injector.UnmapCalls);
            Assert.Equal(ulong.MaxValue, (ulong)counter.GetValue(adapter)!);
        }
        else
        {
            Assert.False(result.IsSuccess); Assert.Equal(0, injector.MapCalls); Assert.Equal(0, injector.UnmapCalls);
            Assert.Null(nestedMap); Assert.Null(nestedChild); Assert.Equal(capacity, (ulong)counter.GetValue(adapter)!);
        }
        var pending = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingGuestMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(capacity == ulong.MaxValue - 1 ? ulong.MaxValue : capacity, (ulong)counter.GetValue(adapter)!);
    }
    [Theory]
    [InlineData(false, 0UL)] [InlineData(false, ulong.MaxValue)] [InlineData(false, ulong.MaxValue - 1)]
    [InlineData(true, 0UL)] [InlineData(true, ulong.MaxValue)] [InlineData(true, ulong.MaxValue - 1)]
    public void ChildIdentityCapacityPrecedesParentBindAndChildEffect(bool existingParent, ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var rootProxy = DispatchProxy.Create<IHybridCpuExternalRuntime, AdapterBoundaryTests.FaultingRootRuntimeProxy>();
        var root = (AdapterBoundaryTests.FaultingRootRuntimeProxy)rootProxy; root.Inner = runtime;
        var childProxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var child = (AdapterBoundaryTests.FaultingChildRuntimeProxy)childProxy; child.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(rootProxy, childProxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.Execution;
        var intent = new NeutralChildDomainIntent(new(1, 4096), new(authority, authority));
        NeutralChildDomainLease original = default;
        if (existingParent) original = adapter.CreateChildDomain(parent, intent).Value;
        var counter = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        counter.SetValue(adapter, capacity);
        bool reservedBeforeProvider = false;
        NeutralVirtualizationStatus? nestedStatus = null;
        Action probe = () =>
        {
            reservedBeforeProvider = (ulong)counter.GetValue(adapter)! == ulong.MaxValue;
            nestedStatus = adapter.CreateChildDomain(new(new(99), new(1)), intent).Status;
        };
        root.BeforeBindForTest = probe; child.BeforeCreateForTest = probe;
        var result = adapter.CreateChildDomain(parent, intent);
        root.BeforeBindForTest = null; child.BeforeCreateForTest = null;
        var prior = existingParent ? 1 : 0;
        if (capacity == ulong.MaxValue - 1)
        {
            Assert.True(result.IsSuccess); Assert.True(reservedBeforeProvider);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, nestedStatus);
            Assert.Equal(capacity, result.Value.Handle.Value);
            Assert.Equal(ulong.MaxValue, (ulong)counter.GetValue(adapter)!);
            Assert.False(adapter.CreateChildDomain(parent, intent).IsSuccess);
            Assert.Equal(1, root.BindCalls); Assert.Equal(prior + 1, child.CreateCalls);
            Assert.True(adapter.CloseChildDomain(result.Value).IsSuccess);
            Assert.Equal(ulong.MaxValue, (ulong)counter.GetValue(adapter)!);
        }
        else
        {
            Assert.False(result.IsSuccess); Assert.Null(nestedStatus);
            Assert.Equal(prior, root.BindCalls); Assert.Equal(prior, child.CreateCalls);
            Assert.Equal(capacity, (ulong)counter.GetValue(adapter)!);
        }
        if (existingParent) Assert.True(adapter.CloseChildDomain(original).IsSuccess);
    }
    [Theory]
    [InlineData(0UL)] [InlineData(ulong.MaxValue)] [InlineData(ulong.MaxValue - 1)]
    public void ArtifactIdentityCapacityIsReservedBeforeProviderEffect(ulong capacity)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        var request = new NeutralExecutableArtifactRequest(child, mapping, CreateExecutablePackage(), 3);
        var counter = typeof(HybridCpuExecutableChildAdapter).GetField("nextIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        counter.SetValue(adapter, capacity);
        bool reservedBeforeCallback = false;
        injector.BeforeArtifactBindForTest = () => reservedBeforeCallback = (ulong)counter.GetValue(adapter)! == ulong.MaxValue;
        var result = adapter.BindExecutableArtifact(request);
        if (capacity == ulong.MaxValue - 1)
        {
            Assert.True(result.IsSuccess); Assert.True(reservedBeforeCallback);
            Assert.Equal(capacity, result.Value.ArtifactHandle.Value);
            Assert.Equal(ulong.MaxValue, (ulong)counter.GetValue(adapter)!);
            Assert.False(adapter.BindExecutableArtifact(request).IsSuccess);
            Assert.Equal(1, injector.ArtifactBindCalls);
            Assert.False(adapter.UnmapGuestRegion(mapping).IsSuccess); // accepted artifact has no release evidence
        }
        else
        {
            Assert.False(result.IsSuccess); Assert.Equal(0, injector.ArtifactBindCalls);
            Assert.Equal(capacity, (ulong)counter.GetValue(adapter)!);
            Assert.True(adapter.UnmapGuestRegion(mapping).IsSuccess);
            Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        }
    }
    [Theory]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    public void AcceptedExecutionSurvivesSuccessObserverReentryAndException(bool throwObserver, int work)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime; injector.ExactStartWorkForTest = work;
        var sink = new SuccessObserverSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);
        var gate = typeof(HybridCpuExecutableChildAdapter).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        bool ownerLockObserved = false; bool crossThreadCompleted = true;
        var observed = new List<NeutralVirtualizationStatus>();
        sink.Observe = () =>
        {
            ownerLockObserved |= Monitor.IsEntered(gate);
            observed.Add(adapter.StartExecutableArtifact(request).Status);
            var retry = Task.Run(() => adapter.StartExecutableArtifact(request with { OperationId = new(99) }).Status);
            var completed = retry.Wait(TimeSpan.FromSeconds(5));
            crossThreadCompleted &= completed;
            if (completed) observed.Add(retry.Result);
            if (throwObserver) throw new IOException("success observation unavailable");
        };
        var result = adapter.StartExecutableArtifact(request);
        Assert.Equal(work == 3, result.IsSuccess);
        Assert.False(ownerLockObserved); Assert.True(crossThreadCompleted);
        Assert.Equal(6, observed.Count);
        Assert.All(observed, status => Assert.Equal(work == 3 ? NeutralVirtualizationStatus.Denied : NeutralVirtualizationStatus.Ambiguous, status));
        Assert.Equal(1, injector.StartCalls);
        Assert.Equal(3, sink.Events.Count);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        var records = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("artifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var fact = records[request.Artifact.ArtifactHandle]!;
        var raw = (ExternalChildExecutionReceipt)fact.GetType().GetProperty("TupleMatchedExecutionObservation")!.GetValue(fact)!;
        Assert.Equal(work, raw.RetiredPipelineCycles);
        Assert.Equal(work == 3, fact.GetType().GetProperty("ExecutionReceipt")!.GetValue(fact) is not null);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(0, injector.UnmapCalls);
        Assert.Equal(work == 3 ? NeutralVirtualizationStatus.Denied : NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.StartCalls);
    }
    [Fact]
    public void AcceptedExecutionReceiptPreventsSameAndFreshOperationProviderRepeat()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime; injector.ExactStartWorkForTest = 3;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);
        Assert.True(adapter.StartExecutableArtifact(request).IsSuccess);
        var artifacts = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("artifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var artifact = Assert.Single(artifacts.Values.Cast<object>());
        Assert.NotNull(artifact.GetType().GetProperty("ExecutionReceipt")!.GetValue(artifact));
        foreach (var retry in new[] { request, request with { OperationId = new(99), OperationGeneration = new(9) } })
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.StartExecutableArtifact(retry).Status);
        Assert.Equal(1, injector.StartCalls);
        Assert.Equal(3, sink.Events.Count);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }
    [Theory]
    [InlineData(3)] [InlineData(4)]
    public void ExactExecutionWorkBoundaryProducesOwnerTraceAndExcessQuarantine(int work)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime; injector.ExactStartWorkForTest = work;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);
        Assert.Equal(3, request.Artifact.MaximumExecutionSteps);
        var result = adapter.StartExecutableArtifact(request);
        Assert.Equal(work == 3, result.IsSuccess);
        Assert.Equal(1, injector.StartCalls);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            work == 3 ? SemanticTraceEventKindV1.RetireOrComplete : SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(x => x.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        if (work == 4)
        {
            var records = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
                .GetField("artifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
            var fact = records[request.Artifact.ArtifactHandle]!;
            var observation = (ExternalChildExecutionReceipt)fact.GetType()
                .GetProperty("TupleMatchedExecutionObservation")!.GetValue(fact)!;
            Assert.Equal(4, observation.RetiredPipelineCycles);
            Assert.Null(fact.GetType().GetProperty("ExecutionReceipt")!.GetValue(fact));
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.StartExecutableArtifact(request with { OperationId = new(99) }).Status);
            Assert.Equal(1, injector.StartCalls);
            Assert.Equal(3, sink.Events.Count);
            foreach (var forbidden in new[] { SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Released })
            {
                var forged = sink.Events.Append(sink.Events[^1] with { Sequence = 4, Kind = forbidden }).ToArray();
                Assert.False(SemanticTraceValidatorV1.Validate(forged).IsValid);
            }
        }
    }
    [Fact]
    public void DistinctChildOwnersWithSameEpochsHaveDistinctGenerationTraceDigests()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);
        var first = PrepareStart(adapter);
        var second = PrepareStart(adapter);
        Assert.Equal(first.ChildLease.Epoch, second.ChildLease.Epoch);
        Assert.NotEqual(first.ChildLease.Handle, second.ChildLease.Handle);
        _ = adapter.StartExecutableArtifact(first);
        var firstDigest = sink.Events[0].GenerationVectorDigest;
        var count = sink.Events.Count;
        _ = adapter.StartExecutableArtifact(second);
        Assert.NotEqual(firstDigest, sink.Events[count].GenerationVectorDigest);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void ChildCreateReplyRevalidatesParentUncertaintyFromNestedCreate(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var intent = new NeutralChildDomainIntent(new(1, 8192), new(authority, authority));
        NeutralChildDomainLease nested = default;
        bool checkedNested = false;
        injector.BeforeCreateForTest = () =>
        {
            injector.BeforeCreateForTest = null;
            Task.Run(() =>
            {
            injector.LoseCreateReceipt = fault == "lost";
            injector.ThrowAfterCreate = fault == "throw";
            var inner = adapter.CreateChildDomain(parent, intent);
            injector.LoseCreateReceipt = false;
            injector.ThrowAfterCreate = false;
            Assert.Equal(fault == "none" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
                inner.Status);
            nested = inner.Value;
            checkedNested = true;
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        var outer = adapter.CreateChildDomain(parent, intent);
        Assert.True(checkedNested);
        Assert.Equal(2, injector.CreateCalls);
        if (fault == "none")
        {
            Assert.True(outer.IsSuccess, outer.Reason);
            Assert.True(adapter.CloseChildDomain(nested).IsSuccess);
            Assert.True(adapter.CloseChildDomain(outer.Value).IsSuccess);
        }
        else
        {
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, outer.Status);
            Assert.Contains("continuity", outer.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
            Assert.Equal(2, injector.CreateCalls);
            Assert.Equal(0, injector.CloseCalls);
            var retained = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
                .GetField("children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
            var fact = Assert.Single(retained.Values.Cast<object>());
            Assert.False((bool)fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact)!);
            Assert.NotNull(fact.GetType().GetProperty("CreateReceipt")!.GetValue(fact));
            var exact = (NeutralChildDomainLease)fact.GetType().GetProperty("Neutral")!.GetValue(fact)!;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.TransitionChildDomain(exact, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.True(adapter.CloseChildDomain(exact).IsSuccess);
            Assert.Equal(1, injector.CloseCalls);
            Assert.Equal(NeutralVirtualizationStatus.Stale, adapter.CloseChildDomain(exact).Status);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
            Assert.Equal(2, injector.CreateCalls);
            Assert.Equal(1, injector.CloseCalls);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void ParentBindCallbackCannotReenterCreateAcrossParentEpochs(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var root = DispatchProxy.Create<IHybridCpuExternalRuntime, AdapterBoundaryTests.FaultingRootRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingRootRuntimeProxy)root;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(root, runtime);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var intent = new NeutralChildDomainIntent(new(1, 8192), new(authority, authority));
        bool checkedAdmission = false;
        injector.BeforeBindForTest = () =>
        {
            injector.BeforeBindForTest = null;
            Task.Run(() =>
            {
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.CreateChildDomain(parent, intent).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.CreateChildDomain(parent with { Epoch = new(2) }, intent).Status);
            Assert.Equal(1, injector.BindCalls);
            checkedAdmission = true;
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        injector.LoseBindReceipt = fault == "lost";
        injector.ThrowAfterBind = fault == "throw";
        var result = adapter.CreateChildDomain(parent, intent);
        Assert.True(checkedAdmission);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        Assert.Equal(1, injector.BindCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingParentBinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        if (fault != "success")
        {
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.CreateChildDomain(parent with { Epoch = new(2) }, intent).Status);
            Assert.Equal(1, injector.BindCalls);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ChildStartAndProviderTransitionCannotOverlapByReentry(bool startFirst, bool throwAfter)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var request = PrepareStart(adapter);
        bool checkedAdmission = false;
        if (startFirst)
        {
            injector.AfterStartForTest = () =>
            {
                Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                    adapter.TransitionChildDomain(request.ChildLease, NeutralChildDomainTransition.Park).Status);
                checkedAdmission = true;
            };
            injector.ThrowAfterStart = throwAfter;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(1, injector.StartCalls);
            Assert.Equal(0, injector.TransitionCalls);
        }
        else
        {
            injector.BeforeTransitionForTest = () =>
            {
                Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.StartExecutableArtifact(request).Status);
                checkedAdmission = true;
            };
            injector.ThrowAfterTransition = throwAfter;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.TransitionChildDomain(request.ChildLease, NeutralChildDomainTransition.Park).Status);
            Assert.Equal(0, injector.StartCalls);
            Assert.Equal(1, injector.TransitionCalls);
        }
        Assert.True(checkedAdmission);
        foreach (string field in new[] { "pendingChildStarts", "pendingChildTransitions" })
        {
            var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
            Assert.Empty(pending.Cast<object>());
        }
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(startFirst ? 1 : 0, injector.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutableStartCallbackCannotAdmitSecondProviderStart(bool throwAfter)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var request = PrepareStart(adapter);
        bool checkedAdmission = false;
        injector.AfterStartForTest = () =>
        {
            injector.AfterStartForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.StartExecutableArtifact(request with { OperationId = new(18), OperationGeneration = new(4) }).Status);
            Assert.Equal(1, injector.StartCalls);
            checkedAdmission = true;
        };
        injector.ThrowAfterStart = throwAfter;
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.True(checkedAdmission);
        Assert.Equal(1, injector.StartCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingChildStarts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InFlightChildTransitionRejectsCloseAndDuplicateProviderTransition(bool throwAfter)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        bool checkedAdmissions = false;
        injector.BeforeTransitionForTest = () =>
        {
            injector.BeforeTransitionForTest = null;
            Task.Run(() =>
            {
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.CloseChildDomain(child).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.Equal(0, injector.CloseCalls);
            Assert.Equal(1, injector.TransitionCalls);
            checkedAdmissions = true;
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        injector.ThrowAfterTransition = throwAfter;
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
        Assert.True(checkedAdmissions);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingChildTransitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(0, injector.CloseCalls);
        Assert.Equal(1, injector.TransitionCalls);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(1, injector.TransitionCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightGuestMapRejectsChildCloseBeforeLeasePublication(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        int refused = 0;
        void CheckClose()
        {
            Task.Run(() =>
            {
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.CloseChildDomain(child).Status);
            Assert.Equal(0, injector.CloseCalls);
            refused++;
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        injector.BeforeMapForTest = CheckClose;
        injector.AfterMapForTest = CheckClose;
        injector.LoseMapReceipt = fault == "lost";
        injector.ThrowAfterMap = fault == "throw";
        var result = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read));
        Assert.Equal(2, refused);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        var pending = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingGuestMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending);
        if (fault == "success")
        {
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
            Assert.True(adapter.UnmapGuestRegion(result.Value).IsSuccess);
            Assert.True(adapter.CloseChildDomain(child).IsSuccess);
            Assert.Equal(1, injector.CloseCalls);
        }
        else
        {
            Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
            Assert.Equal(0, injector.CloseCalls);
        }
    }

    [Fact]
    public void NestedGuestMapCompletionCannotClearOuterAdmissionInterlock()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var request = new NeutralGuestMappingRequest(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read);
        NeutralGuestMappingLease nested = default;
        bool checkedOuter = false;
        injector.BeforeMapForTest = () =>
        {
            injector.BeforeMapForTest = null;
            var inner = adapter.MapGuestRegion(request);
            Assert.True(inner.IsSuccess, inner.Reason);
            nested = inner.Value;
            Assert.True(adapter.UnmapGuestRegion(nested).IsSuccess);
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.CloseChildDomain(child).Status);
            Assert.Equal(0, injector.CloseCalls);
            checkedOuter = true;
        };
        var outer = adapter.MapGuestRegion(request);
        Assert.True(checkedOuter);
        Assert.True(outer.IsSuccess, outer.Reason);
        Assert.True(adapter.UnmapGuestRegion(outer.Value).IsSuccess);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(2, injector.MapCalls);
        Assert.Equal(2, injector.UnmapCalls);
        Assert.Equal(1, injector.CloseCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightChildCloseRejectsReentrantEffectsAndDuplicateClose(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var map = new NeutralGuestMappingRequest(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read);
        bool checkedAdmissions = false;
        injector.BeforeCloseForTest = () =>
        {
            injector.BeforeCloseForTest = null;
            Task.Run(() =>
            {
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.MapGuestRegion(map).Status);
            Assert.Equal(NeutralVirtualizationStatus.Denied,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.CloseChildDomain(child).Status);
            Assert.Equal(0, injector.MapCalls);
            Assert.Equal(1, injector.CloseCalls);
            checkedAdmissions = true;
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        injector.LoseCloseReceipt = fault == "lost";
        injector.ThrowAfterClose = fault == "throw";
        var result = adapter.CloseChildDomain(child);
        Assert.True(checkedAdmissions);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingChildCloses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.Ambiguous,
            adapter.CloseChildDomain(child).Status);
        Assert.False(adapter.MapGuestRegion(map).IsSuccess);
        Assert.Equal(0, injector.MapCalls);
        Assert.Equal(1, injector.CloseCalls);
    }

    [Theory]
    [InlineData("map-return")]
    [InlineData("map-throw")]
    [InlineData("drain-return")]
    public void ChildTransitionReplyCannotOverwriteCallbackOwnerChanges(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        bool changed = false;
        injector.BeforeTransitionForTest = () =>
        {
            injector.BeforeTransitionForTest = null;
            if (fault == "drain-return")
                InjectDrainingOwnerReplacement(adapter, child);
            else
            {
                injector.LoseMapReceipt = true;
                var parentMapping = new NeutralOwnedRegionMappingLease(parent,
                    new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
                Assert.Equal(NeutralVirtualizationStatus.NotInvoked, adapter.MapGuestRegion(new(child,
                    parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
                InjectGuestMapUncertainty(adapter, child);
            }
            changed = true;
        };
        injector.ThrowAfterTransition = fault == "map-throw";
        var result = adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park);
        Assert.True(changed);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        if (fault == "drain-return") Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        else
        {
            Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
            Assert.Equal(0, injector.CloseCalls);
        }
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(1, injector.TransitionCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightGuestUnmapRejectsReentrantBindAndDuplicateUnmap(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        var request = new NeutralExecutableArtifactRequest(child, mapping, CreateExecutablePackage(), 3);
        bool checkedAdmissions = false;
        injector.BeforeUnmapForTest = () =>
        {
            injector.BeforeUnmapForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.BindExecutableArtifact(request).Status);
            Assert.Equal(0, injector.ArtifactBindCalls);
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
            Assert.Equal(1, injector.UnmapCalls);
            checkedAdmissions = true;
        };
        injector.LoseUnmapReceipt = fault == "lost";
        injector.ThrowAfterUnmap = fault == "throw";
        var result = adapter.UnmapGuestRegion(mapping);
        Assert.True(checkedAdmissions);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        Assert.Equal(0, injector.ArtifactBindCalls);
        Assert.Equal(1, injector.UnmapCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingGuestUnmaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.False(adapter.BindExecutableArtifact(request).IsSuccess);
        Assert.Equal(0, injector.ArtifactBindCalls);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.Ambiguous,
            adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(1, injector.UnmapCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightArtifactBindRejectsReentrantUnmapAndDuplicateBind(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        var request = new NeutralExecutableArtifactRequest(child, mapping, CreateExecutablePackage(), 3);
        bool checkedBothAdmissions = false;
        injector.BeforeArtifactBindForTest = () =>
        {
            injector.BeforeArtifactBindForTest = null;
            Task.Run(() =>
            {
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
            Assert.Equal(0, injector.UnmapCalls);
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.BindExecutableArtifact(request).Status);
            Assert.Equal(1, injector.ArtifactBindCalls);
            checkedBothAdmissions = true;
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        injector.LoseBindReceipt = fault == "lost";
        injector.ThrowAfterBind = fault == "throw";
        var result = adapter.BindExecutableArtifact(request);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        Assert.Equal(0, injector.UnmapCalls);
        Assert.Equal(1, injector.ArtifactBindCalls);
        Assert.True(checkedBothAdmissions);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingArtifactBinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    [InlineData("nested-map-throw")]
    public void ArtifactBindReplyAfterReentrantDrainCannotPublishReceipt(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        injector.AfterArtifactBindForTest = () =>
        {
            if (fault == "nested-map-throw")
            {
                injector.LoseMapReceipt = true;
                Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.MapGuestRegion(new(child,
                    parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
            }
            else InjectDrainingOwnerReplacement(adapter, child);
        };
        injector.LoseBindReceipt = fault == "lost";
        injector.ThrowAfterBind = fault is "throw" or "nested-map-throw";
        var result = adapter.BindExecutableArtifact(new(child, mapping, CreateExecutablePackage(), 3));
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        if (fault is "success" or "lost") Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(0, injector.CloseCalls);
        if (fault == "nested-map-throw") Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
        var retained = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("artifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Equal(fault == "success" ? 1 : 0, retained.Count);
        if (fault == "success")
        {
            var fact = Assert.Single(retained.Values.Cast<object>());
            Assert.False((bool)fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact)!);
            var exact = (NeutralExecutableArtifactReceipt)fact.GetType().GetProperty("Neutral")!.GetValue(fact)!;
            var external = (ExternalChildArtifactBindReceipt)fact.GetType().GetProperty("External")!.GetValue(fact)!;
            Assert.Equal(exact.ContentDigest, external.PackageSha256);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.StartExecutableArtifact(new(child, exact, new(61), new(1))).Status);
            Assert.Equal(0, injector.StartCalls);
        }
        Assert.False(adapter.BindExecutableArtifact(new(child, mapping, CreateExecutablePackage(), 3)).IsSuccess);
        Assert.Equal(1, injector.ArtifactBindCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MappingReplyAfterReentrantDrainCannotPublishLease(bool lostReceipt, bool otherUnknown)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        injector.AfterMapForTest = () =>
        {
            injector.AfterMapForTest = null;
            if (otherUnknown)
            {
                injector.LoseMapReceipt = true;
                Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                    adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
                injector.LoseMapReceipt = lostReceipt;
            }
            else InjectDrainingOwnerReplacement(adapter, child);
        };
        injector.LoseMapReceipt = lostReceipt;
        var result = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read));
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        var retained = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("mappings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Equal(lostReceipt ? 0 : 1, retained.Count);
        Assert.False(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(0, injector.CloseCalls);
        if (lostReceipt)
        {
            Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
            return;
        }
        var fact = Assert.Single(retained.Values.Cast<object>());
        Assert.False((bool)fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact)!);
        var exact = (NeutralGuestMappingLease)fact.GetType().GetProperty("Neutral")!.GetValue(fact)!;
        Assert.False(adapter.BindExecutableArtifact(new(child, exact, CreateExecutablePackage(), 3)).IsSuccess);
        Assert.Equal(0, injector.ArtifactBindCalls);
        Assert.True(adapter.UnmapGuestRegion(exact).IsSuccess);
        Assert.Equal(1, injector.UnmapCalls);
        if (otherUnknown)
        {
            Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
            Assert.Equal(0, injector.CloseCalls);
            Assert.Equal(2, injector.MapCalls);
            return;
        }
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(1, injector.CloseCalls);
        Assert.Equal(1, injector.MapCalls);
    }

    [Fact]
    public void StartExceptionPreservesGuestMapUncertaintyCreatedDuringCallback()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ThrowAfterStart = true;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var request = PrepareStart(adapter);
        injector.AfterStartForTest = () =>
        {
            injector.LoseMapReceipt = true;
            var parentMapping = new NeutralOwnedRegionMappingLease(request.ChildLease.ParentLease,
                new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
            Assert.Equal(NeutralVirtualizationStatus.NotInvoked,
                adapter.MapGuestRegion(new(request.ChildLease, parentMapping,
                    new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
            InjectGuestMapUncertainty(adapter, request.ChildLease);
        };
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        var records = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var child = records[request.ChildLease.Handle]!;
        Assert.True((bool)child.GetType().GetProperty("GuestMapMayHaveEffect")!.GetValue(child)!);
        Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(request.ChildLease).Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    [InlineData(4)]
    public void ProviderStartReentryCannotPublishAgainstChangedChildLifecycle(int? work)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ExactStartWorkForTest = work;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);
        injector.AfterStartForTest = () => InjectDrainingOwnerReplacement(adapter, request.ChildLease);
        var result = adapter.StartExecutableArtifact(request);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, injector.StartCalls);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        var retained = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("artifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var fact = retained[request.Artifact.ArtifactHandle]!;
        var receipt = fact.GetType().GetProperty("ExecutionReceipt")!.GetValue(fact);
        Assert.Equal(work == 3, receipt is not null);
        var raw = fact.GetType().GetProperty("TupleMatchedExecutionObservation")!.GetValue(fact);
        Assert.Equal(work is 3 or 4, raw is not null);
        if (receipt is NeutralChildExecutionReceipt exact) Assert.Equal(3, exact.RetiredWorkUnits);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.StartCalls);
    }

    [Fact]
    public void TraceCallbackRunsAfterStartDecisionOutsideOwnerLock()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var sink = new ReentrantSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);
        var completedOutsideLock = false;
        var callsAtObservation = 0;
        sink.Callback = () =>
        {
            callsAtObservation = injector.StartCalls;
            var otherThread = Task.Run(() => adapter.TransitionChildDomain(request.ChildLease,
                NeutralChildDomainTransition.BeginDrain));
            completedOutsideLock = otherThread.Wait(TimeSpan.FromSeconds(2));
        };
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.True(completedOutsideLock);
        Assert.Equal(1, callsAtObservation);
        Assert.Equal(1, injector.StartCalls);
    }

    private sealed class ReentrantSink : ISemanticTraceSinkV1
    {
        internal Action? Callback;
        public bool TryRecord(SemanticTraceEventV1 item)
        {
            var callback = Callback;
            Callback = null;
            callback?.Invoke();
            return true;
        }
    }

    [Fact]
    public void BoundArtifactKeepsExactGuestMappingUntilReleaseEvidenceExists()
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);

        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
    }

    [Fact]
    public void DrainingChildCannotBindOrStartExecutableArtifact()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);
        Assert.Equal(1, injector.ArtifactBindCalls);
        Assert.True(adapter.TransitionChildDomain(request.ChildLease,
            NeutralChildDomainTransition.BeginDrain).IsSuccess);

        Assert.Equal(NeutralVirtualizationStatus.Denied,
            adapter.BindExecutableArtifact(new(request.ChildLease, mapping,
                CreateExecutablePackage(), 3)).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.ArtifactBindCalls);
        Assert.Equal(0, injector.StartCalls);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("forged")]
    [InlineData("digest")]
    [InlineData("throw")]
    public void UnreceiptedExternalArtifactBindPinsExactGuestMapping(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.LoseBindReceipt = fault == "lost";
        injector.ForgeBind = fault == "forged";
        injector.ForgeBindDigest = fault == "digest";
        injector.ThrowAfterBind = fault == "throw";
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.BindExecutableArtifact(new(child, mapping, CreateExecutablePackage(), 3)).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(0, injector.UnmapCalls);
    }

    [Fact]
    public void ExecutableStartEmitsCanonicalNonAuthoritativeLifecycleDirectly()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);
        var request = PrepareStart(adapter);

        var result = adapter.StartExecutableArtifact(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(
            [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
             SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.All(sink.Events, item =>
        {
            Assert.False(item.AuthorizesExecution);
            Assert.False(item.AuthorizesEffect);
            Assert.False(item.AuthorizesPublication);
            Assert.Equal("HybridCPU.ExternalRuntime.V3", item.Source);
        });
    }

    [Fact]
    public void ObservationFailureCannotChangeExecutableOutcome()
    {
        var baselineAdapter = new HybridCpuExecutableChildAdapter();
        var baseline = baselineAdapter.StartExecutableArtifact(PrepareStart(baselineAdapter));
        var observedAdapter = new HybridCpuExecutableChildAdapter(new ThrowingSink());
        var observed = observedAdapter.StartExecutableArtifact(PrepareStart(observedAdapter));

        Assert.Equal(baseline.Status, observed.Status);
        Assert.Equal(baseline.Reason, observed.Reason);
    }

    [Fact]
    public void ForgedSuccessfulStartReceiptIsQuarantinedAndCannotBeRetried()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        ((AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy).Inner = runtime;
        ((AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy).ForgeStart = true;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined], sink.Events.Select(static item => item.Kind));
        var retained = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("artifacts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var fact = retained[request.Artifact.ArtifactHandle]!;
        Assert.Null(fact.GetType().GetProperty("TupleMatchedExecutionObservation")!.GetValue(fact));
        Assert.Null(fact.GetType().GetProperty("ExecutionReceipt")!.GetValue(fact));
    }

    [Fact]
    public void ThrowingExternalStartPinsChildAndEmitsQuarantineWithoutRetry()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ThrowAfterStart = true;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.StartCalls);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined], sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void ExecutableStartMatchesReferenceQuarantineOwnerProjection()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);

        var result = adapter.StartExecutableArtifact(PrepareStart(adapter));

        Assert.False(result.IsSuccess);
        SemanticTraceEventKindV1[] referenceKinds =
        [
            SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined,
        ];
        var reference = referenceKinds.Select((kind, index) => new SemanticTraceEventV1(
            SemanticTraceEventV1.CurrentVersion, "reference-child-start:17:3", checked((ulong)index + 1),
            kind, "SingNext.Reference.V1", Digest("reference-admitted-generation"),
            Digest($"reference-evidence:{kind}"))).ToArray();
        var comparison = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, sink.Events,
            Digest("singnext-reference-source-tuple"), Digest("hybridcpu-executable-source-tuple"));

        Assert.True(comparison.IsEquivalent);
        Assert.Null(comparison.Difference);
    }

    private static NeutralChildExecutionStartRequest PrepareStart(HybridCpuExecutableChildAdapter adapter,
        Action<NeutralGuestMappingLease>? onBound = null)
    {
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var childResult = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority)));
        Assert.True(childResult.IsSuccess, childResult.Reason);
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapResult = adapter.MapGuestRegion(new(childResult.Value, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute));
        Assert.True(mapResult.IsSuccess, mapResult.Reason);
        var artifactResult = adapter.BindExecutableArtifact(new(childResult.Value, mapResult.Value,
            CreateExecutablePackage(), 3));
        Assert.True(artifactResult.IsSuccess, artifactResult.Reason);
        onBound?.Invoke(mapResult.Value);
        return new(childResult.Value, artifactResult.Value, new(17), new(3));
    }

    private static byte[] CreateExecutablePackage()
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

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class CollectingSink : ISemanticTraceSinkV1
    {
        public List<SemanticTraceEventV1> Events { get; } = [];
        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            return true;
        }
    }

    private sealed class SuccessObserverSink : ISemanticTraceSinkV1
    {
        public Action? Observe { get; set; }
        public List<SemanticTraceEventV1> Events { get; } = [];
        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            Observe?.Invoke();
            return true;
        }
    }
    private sealed class ThrowingSink : ISemanticTraceSinkV1
    {
        public bool TryRecord(SemanticTraceEventV1 traceEvent) =>
            throw new InvalidOperationException("observation channel unavailable");
    }
}
