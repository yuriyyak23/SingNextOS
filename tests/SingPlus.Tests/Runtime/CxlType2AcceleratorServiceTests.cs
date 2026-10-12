using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class CxlType2AcceleratorServiceTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ActualType2SubmitRetainsEffectAfterFabricManagerSourceLossAndReclaimsOriginal(int fault)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", PrivateFields)!.GetValue(s.FabricManager)!;
        var original = routes[s.Fabric.BindingId]!;
        var parent = FabricRoutes(s)[s.Fabric.BindingId]!;
        hook!.AfterSubmit = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(CxlFabricManagerAuthority).GetField("_gate", PrivateFields)!.GetValue(s.FabricManager)!));
            if (fault == 0) routes[s.Fabric.BindingId] = CloneFabric(original);
            else
            {
                var state = original.GetType().GetProperty("State")!;
                if (fault == 2) state.SetValue(original, CxlFabricResourceState.Draining);
                state.SetValue(original, CxlFabricResourceState.Bound);
            }
        };
        var submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(submitted.IsSuccess);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        Assert.NotNull(hook.LastSubmission);
        Assert.Equal(1, OperationPins(parent));
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        hook.AfterSubmit = null; routes[s.Fabric.BindingId] = original;
        var closed = s.Kernel.TerminateProcess(s.Handle);
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, hook.ReleaseCalls); Assert.Equal(0, OperationPins(parent));
        Assert.False(s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(region => region.Handle == s.Input.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ActualType2PublicationCannotRefreshFabricManagerSourceAfterAction(int fault)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", PrivateFields)!.GetValue(s.FabricManager)!;
        var original = routes[s.Fabric.BindingId]!;
        var parent = FabricRoutes(s)[s.Fabric.BindingId]!;
        var actions = 0;
        var published = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            actions++;
            if (fault == 0) routes[s.Fabric.BindingId] = CloneFabric(original);
            else
            {
                var state = original.GetType().GetProperty("State")!;
                if (fault == 2) state.SetValue(original, CxlFabricResourceState.Draining);
                state.SetValue(original, CxlFabricResourceState.Bound);
            }
        });
        Assert.False(published.IsSuccess); Assert.Equal(1, actions);
        Assert.NotEqual(ExternalOperationState.Published, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        routes[s.Fabric.BindingId] = original;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook!.ReleaseCalls); Assert.Equal(1, OperationPins(parent));
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalType2LostReleaseCannotRepeatDuringActualProcessReclaim(bool lostReply)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        if (lostReply) hook!.AfterRelease = () => throw new IOException("original Release reply lost");
        else s.Accelerator.ReleaseFails = true;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook!.ReleaseCalls);
        hook.AfterRelease = null;
        s.Accelerator.ReleaseFails = false;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(1, OperationPins(FabricRoutes(s)[s.Fabric.BindingId]!));
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalType2EffectInventoryRefusesAbsentOrCopiedRouteThroughActualTeardown(bool copied)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        _ = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var effects = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService)
            .GetField("_effects", PrivateFields)!.GetValue(s.Service)!;
        var key = Assert.Single(effects.Keys.Cast<object>());
        var original = effects[key]!;
        var originalFabric = FabricRoutes(s)[s.Fabric.BindingId]!;
        if (copied) effects[key] = CloneFabric(original); else effects.Remove(key);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(0, hook!.ReleaseCalls);
        Assert.Equal(1, OperationPins(FabricRoutes(s)[s.Fabric.BindingId]!));
        effects[key] = original;
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.Equal(0, OperationPins(originalFabric));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void OriginalType2KnownReleaseNeedsSameFactForActualLocalConsumption(int fault)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var effects = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService)
            .GetField("_effects", PrivateFields)!.GetValue(s.Service)!;
        var key = Assert.Single(effects.Keys.Cast<object>());
        var original = effects[key]!;
        var fabric = FabricRoutes(s)[s.Fabric.BindingId]!;
        hook!.AfterRelease = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(CxlType2AcceleratorService).GetField("_effectGate", PrivateFields)!.GetValue(s.Service)!));
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            effects[key] = CloneFabric(original);
        };
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.Equal(1, OperationPins(fabric));
        Assert.False((bool)original.GetType().GetProperty("ClosureInFlight")!.GetValue(original)!);
        var property = original.GetType().GetProperty("OriginalClosure")!;
        var fact = property.GetValue(original)!;
        Assert.NotNull(fact.GetType().GetProperty("RawReleaseObservation")!.GetValue(fact));
        hook.AfterRelease = null;
        effects[key] = original;
        if (fault == 1) property.SetValue(original, CloneFabric(fact));
        if (fault is 2 or 5) fact.GetType().GetProperty("RawReleaseObservation")!.SetValue(fact, null);
        if (fault == 3) fact.GetType().GetField("<Submission>k__BackingField", PrivateFields)!.SetValue(fact, execution.Submission with { });
        if (fault == 4) fact.GetType().GetField("<Source>k__BackingField", PrivateFields)!.SetValue(fact, CloneFabric(original));
        if (fault == 5) original.GetType().GetProperty("ProviderClosed")!.SetValue(original, true);
        var retry = s.Kernel.TerminateProcess(s.Handle);
        Assert.Equal(fault == 0, retry.IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.Equal(fault == 0 ? 0 : 1, OperationPins(fabric));
        Assert.Equal(fault == 0, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State == ExternalOperationState.Released);
        if (fault != 0) Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
    }

    [Fact]
    public void OriginalType2SubmitFactClosesAfterPublishedExecutionRouteLoss()
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var live = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService).GetField("_live", PrivateFields)!.GetValue(s.Service)!;
        live.Remove(execution.Operation.OperationId);
        var fabric = FabricRoutes(s)[s.Fabric.BindingId]!;
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook!.ReleaseCalls);
        Assert.Equal(0, OperationPins(fabric));
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualType2CapturesMutablePlanBeforeValidationAndProviderCallback(bool throwOnCapture)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var originalUses = s.Plan.RequiredRegionUses.ToArray();
        var changedUses = new OperationRegionUseRequest[]
        {
            new(s.Output.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(s.Input.Handle, RegionUseMode.StagedOutput, new(0, 8))
        };
        var suppliedUses = new ChangingComputeUses(originalUses, changedUses, throwOnCapture);
        var nodes = s.Plan.Dependencies.Nodes.ToList();
        var edges = s.Plan.Dependencies.Edges.ToList();
        var supplied = s.Plan with { RequiredRegionUses = suppliedUses,
            Dependencies = new ComputeDependencyGraph(nodes, edges) };
        hook!.AfterSubmit = () => { nodes.Clear(); edges.Clear(); };
        var before = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        var result = s.Service.Submit(s.Handle, supplied, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.Equal(1, suppliedUses.Reads);
        if (throwOnCapture)
        {
            Assert.Equal(KernelError.InvalidMessage, result.Error);
            Assert.Null(hook.LastRequest);
            Assert.Equal(before, s.Kernel.ExternalOperations.InspectionSnapshot().ToArray());
            Assert.Equal(0, s.Accelerator.SubmitCount);
            Assert.Equal(0, OperationPins(FabricRoutes(s)[s.Fabric.BindingId]!));
            return;
        }
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(s.Input.Handle, hook.LastRequest!.RegionUses[0].Region);
        Assert.Equal(s.Output.Handle, hook.LastRequest.RegionUses[1].Region);
        Assert.Equal(originalUses, result.Value!.Plan.RequiredRegionUses.ToArray());
        Assert.True(ComputePlanner.ValidateGraph(result.Value.Plan.Dependencies).IsSuccess);
        Assert.Empty(nodes);
        Assert.Empty(edges);
        var publications = 0;
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, result.Value, [s.Candidate], () => publications++).IsSuccess);
        Assert.Equal(1, publications);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
    }

    private sealed class ChangingComputeUses(OperationRegionUseRequest[] original,
        OperationRegionUseRequest[] changed, bool fail) : IReadOnlyList<OperationRegionUseRequest>
    {
        public int Reads { get; private set; }
        public int Count => original.Length;
        public OperationRegionUseRequest this[int index] => original[index];
        public IEnumerator<OperationRegionUseRequest> GetEnumerator()
        {
            Reads++;
            if (fail) throw new IOException("Injected caller collection capture failure.");
            return ((IEnumerable<OperationRegionUseRequest>)(Reads <= 2 ? original : changed)).GetEnumerator();
        }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ForeignOutputPathGraphCannotReachActualType2Effect(bool protectedSubmit, bool absentGraph)
    {
        var s = CreateScenario();
        var foreignGraph = new ComputeDependencyGraph(s.Plan.Dependencies.Nodes.Select(node =>
            new ComputeDependencyNode(node.NodeId, node.Kind == ComputeDependencyKind.StagedOutputReady
                ? ComputeDependencyKind.DirectOutputBinding : node.Kind)).ToArray(),
            s.Plan.Dependencies.Edges.ToArray());
        Assert.True(ComputePlanner.ValidateGraph(foreignGraph).IsSuccess);
        var forged = s.Plan with { Dependencies = absentGraph ? null! : foreignGraph };
        var uses = s.Kernel.Regions.SnapshotUses().ToArray();
        var operations = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        var labels = 0;
        var result = protectedSubmit
            ? s.Service.SubmitProtected(s.Handle, forged, [s.Candidate], s.Subject, s.Lease, s.Endpoint,
                s.Fabric, 1, _ => { labels++; throw new InvalidOperationException("Labels must not run."); })
            : s.Service.Submit(s.Handle, forged, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Equal(0, labels);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(uses, s.Kernel.Regions.SnapshotUses().ToArray());
        Assert.Equal(operations, s.Kernel.ExternalOperations.InspectionSnapshot().ToArray());
        Assert.Equal(0, OperationPins(FabricRoutes(s)[s.Fabric.BindingId]!));
        var valid = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(valid.IsSuccess, valid.Message);
        var publications = 0;
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, valid.Value!, [s.Candidate], () => publications++).IsSuccess);
        Assert.Equal(1, publications);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void InconsistentComputePlanCannotReachActualType2Submission(int fault)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var uses = s.Plan.RequiredRegionUses.ToArray();
        var forged = fault switch
        {
            0 => s.Plan with { RequiredRegionUses = [new(s.Output.Handle, RegionUseMode.ReadOnly, new(0, 8)),
                new(s.Input.Handle, RegionUseMode.StagedOutput, new(0, 8))] },
            1 => s.Plan with { RequiredRegionUses = [uses[0], new(s.Output.Handle, RegionUseMode.StagedOutput, new(0, 4))] },
            2 => s.Plan with { RequiredRegionUses = [] },
            3 => s.Plan with { RequiredRegionUses = [uses[1], uses[0]] },
            4 => s.Plan with { RequiredRegionUses = [uses[0], uses[0], uses[1]] },
            5 => s.Plan with { Intent = s.Plan.Intent with { Operation = (ComputeOperationKind)999 } },
            6 => s.Plan with { Intent = s.Plan.Intent with { PublicationPreference = ComputePublicationPreference.DirectRequired } },
            _ => s.Plan with { Intent = s.Plan.Intent with { Input = s.Plan.Intent.Output } }
        };
        var initialUses = s.Kernel.Regions.SnapshotUses().ToArray();
        var initialOperations = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        var original = FabricRoutes(s)[s.Fabric.BindingId]!;
        var result = s.Service.Submit(s.Handle, forged, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Null(hook!.LastRequest);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(initialUses, s.Kernel.Regions.SnapshotUses().ToArray());
        Assert.Equal(initialOperations, s.Kernel.ExternalOperations.InspectionSnapshot().ToArray());
        Assert.Equal(0, OperationPins(original));
        var valid = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
        Assert.DoesNotContain(s.Kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
    }

    [Theory]
    [InlineData(false, 1UL)] [InlineData(true, 1UL)]
    [InlineData(false, 1000UL)] [InlineData(true, 1000UL)]
    public void ExplicitResourceRequirementCannotBecomeUnaccountedType2Effect(bool protectedSubmit, ulong amount)
    {
        var s = CreateScenario();
        var intent = s.Plan.Intent with
        {
            ResourceRequirement = new ResourceEnvelopeV1(1, ResourceDimensionFamilyV1.Time,
                ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1")
        };
        var planned = s.Kernel.PlanCompute(s.Handle, intent, new(true, true, true), [s.Candidate]);
        Assert.True(planned.IsSuccess, planned.Message);
        var uses = s.Kernel.Regions.SnapshotUses().ToArray();
        var operations = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        var original = FabricRoutes(s)[s.Fabric.BindingId]!;
        var labels = 0;
        var result = protectedSubmit
            ? s.Service.SubmitProtected(s.Handle, planned.Value!, [s.Candidate], s.Subject, s.Lease,
                s.Endpoint, s.Fabric, 1, _ => { labels++; throw new InvalidOperationException("Labels must not run."); })
            : s.Service.Submit(s.Handle, planned.Value!, [s.Candidate], s.Subject, s.Lease,
                s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(0, labels);
        Assert.Equal(operations, s.Kernel.ExternalOperations.InspectionSnapshot().ToArray());
        Assert.Equal(uses, s.Kernel.Regions.SnapshotUses().ToArray());
        Assert.Equal(0, OperationPins(original));
        // Fresh ordinary admission is a distinct request, never a reinterpretation of the envelope.
        var ordinary = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(ordinary.IsSuccess, ordinary.Message);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        var published = 0;
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, ordinary.Value!, [s.Candidate], () => published++).IsSuccess);
        Assert.Equal(1, published);
        Assert.Equal(0, OperationPins(original));
        // Let the actual teardown consumer close FM tracking, fabric, device and Region uses in owner order.
        var teardown = s.Kernel.TerminateProcess(s.Handle);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.False(s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
        Assert.DoesNotContain(s.Kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void Type2OriginalParentRefusesAdmissionWithoutProviderEffect(bool foreign, bool duringQuery)
    {
        ClosureQueryDiscovery? discovery = null;
        var s = CreateScenario(discoveryFactory: model => discovery = new(model));
        var routes = FabricRoutes(s);
        var original = routes[s.Fabric.BindingId]!;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(BridgeGate(s)));
            lock (BridgeGate(s))
            {
                if (foreign) routes[s.Fabric.BindingId] = CloneFabric(original);
                else routes.Remove(s.Fabric.BindingId);
            }
        }
        if (duringQuery) discovery!.BeforeNextQuery = Lose;
        else Lose();
        var denied = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(denied.IsSuccess);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(0, OperationPins(original));
        Assert.False(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
        lock (BridgeGate(s)) routes[s.Fabric.BindingId] = original;
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void Type2OriginalParentLossRetainsActualEffectAndKnownClosure(bool foreign, bool afterRelease)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var routes = FabricRoutes(s);
        var original = routes[s.Fabric.BindingId]!;
        object? replacement = null;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(BridgeGate(s)));
            lock (BridgeGate(s))
            {
                if (foreign) routes[s.Fabric.BindingId] = replacement = CloneFabric(original);
                else routes.Remove(s.Fabric.BindingId);
            }
        }
        if (!afterRelease) hook!.AfterSubmit = Lose;
        var submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(afterRelease, submitted.IsSuccess);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        Assert.NotNull(hook!.LastSubmission);
        if (afterRelease) hook.AfterRelease = Lose;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
        Assert.Equal(1, OperationPins(original));
        if (foreign) Assert.Equal(1, OperationPins(replacement!));
        Assert.False(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
        Assert.Equal(afterRelease ? 1 : 0, hook.ReleaseCalls);
        var effects = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService)
            .GetField("_effects", PrivateFields)!.GetValue(s.Service)!;
        var effect = Assert.Single(effects.Values.Cast<object>());
        Assert.Equal(afterRelease, effect.GetType().GetProperty("ReleaseReceipt")!.GetValue(effect) is not null);
        // Restoring the owner is test arrangement, not a durable recovery claim.
        hook.AfterSubmit = null;
        hook.AfterRelease = null;
        lock (BridgeGate(s)) routes[s.Fabric.BindingId] = original;
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(0, OperationPins(original));
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.Equal(1, s.Accelerator.SubmitCount);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public void Type2PublicationRequiresOriginalParentBeforeAndAfterAction(bool foreign, int lossStage)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var routes = FabricRoutes(s);
        var original = routes[s.Fabric.BindingId]!;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(BridgeGate(s)));
            lock (BridgeGate(s))
            {
                if (foreign) routes[s.Fabric.BindingId] = CloneFabric(original);
                else routes.Remove(s.Fabric.BindingId);
            }
        }
        if (lossStage == 0) Lose();
        if (lossStage == 1) hook!.AfterVisibility = Lose;
        var actions = 0;
        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            actions++;
            if (lossStage == 2) Lose();
        });
        Assert.False(result.IsSuccess);
        Assert.Equal(lossStage == 2 ? 1 : 0, actions);
        Assert.Equal(1, OperationPins(original));
        Assert.Equal(0, hook!.ReleaseCalls);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
        Assert.Equal(1, OperationPins(original));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Type2ConcurrentCallbackRetainsOriginalPinAfterParentLoss(bool foreign)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var routes = FabricRoutes(s);
        var original = routes[s.Fabric.BindingId]!;
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook!.AfterSubmit = () =>
        {
            Assert.False(Monitor.IsEntered(BridgeGate(s)));
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        };
        var submitting = Task.Run(() => s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, OperationPins(original));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
            lock (BridgeGate(s))
            {
                if (foreign) routes[s.Fabric.BindingId] = CloneFabric(original);
                else routes.Remove(s.Fabric.BindingId);
            }
        }
        finally { resume.Set(); }
        var result = await submitting.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, OperationPins(original));
        Assert.Equal(1, s.Accelerator.SubmitCount);
        Assert.Equal(0, hook.ReleaseCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
    }

    private const global::System.Reflection.BindingFlags PrivateFields =
        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
    private static global::System.Collections.IDictionary FabricRoutes(Scenario s) =>
        (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", PrivateFields)!.GetValue(s.Bridge)!;
    private static object BridgeGate(Scenario s) => typeof(CxlAuthorityBridge).GetField("_gate", PrivateFields)!.GetValue(s.Bridge)!;
    private static object CloneFabric(object original) => typeof(object).GetMethod("MemberwiseClone", PrivateFields)!.Invoke(original, null)!;
    private static int OperationPins(object fabric)
    {
        var operations = fabric.GetType().GetProperty("Operations")!.GetValue(fabric)!;
        return (int)operations.GetType().GetProperty("Count")!.GetValue(operations)!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnLateSubmissionRefusalClosesNotInvokedButCannotEraseCompletedOriginal(bool completed)
    {
        var clock = new CleanupClock();
        var s = CreateScenario(clock: clock);
        Assert.True(s.Kernel.Traces.Start(s.Handle, 32, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        OperationBinding? binding = null;
        void Observe()
        {
            var original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
            if (original.State != ExternalOperationState.Submitted)
            {
                clock.Next = Observe;
                return;
            }
            binding = original.Binding;
            var worker = Task.Run(() =>
            {
                if (completed)
                    Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Handle,
                        new(binding!.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
                else Assert.True(s.Kernel.CancelExternalOperation(s.Handle, original.Operation, false).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Submitted trace must run outside owner locks.");
            worker.GetAwaiter().GetResult();
        }
        clock.Next = Observe;
        var refused = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(refused.IsSuccess);
        Assert.NotNull(binding);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        var original = s.Kernel.ExternalOperations.Query(binding.Value.Operation).Value!;
        Assert.Equal(binding, original.Binding);
        Assert.Equal(!completed, original.State == ExternalOperationState.Released);
        if (completed)
        {
            Assert.Equal(ExternalOperationDisposition.Completed, original.Disposition);
            Assert.False(s.Kernel.ReleaseRegion(s.Handle, s.Input).IsSuccess);
        }
        Assert.Equal(!completed, s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        if (completed) Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void LateLocalAdmissionFailureClosesPreparedButQuarantinesSubmittedOriginal(int fault)
    {
        var clock = new CleanupClock();
        var s = CreateScenario(clock: clock);
        Assert.True(s.Kernel.Traces.Start(s.Handle, 32, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        ExternalOperationHandle? operation = null;
        void Observe()
        {
            var original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
            if (original.State != (fault == 2 ? ExternalOperationState.Admitted : ExternalOperationState.Prepared))
            {
                clock.Next = Observe;
                return;
            }
            operation = original.Operation;
            var worker = Task.Run(() =>
            {
                if (fault == 0)
                    Assert.True(s.Kernel.CancelExternalOperation(s.Handle, original.Operation, false).IsSuccess);
                else
                {
                    var dependencies = new OperationDependencySnapshot(1, s.Endpoint.DeviceGeneration.Value, s.Fabric.Generation.Value);
                    if (fault != 2) Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, original.Operation, dependencies).IsSuccess);
                    var submitted = s.Kernel.RecordExternalOperationSubmission(s.Handle, original.Operation, dependencies);
                    Assert.True(submitted.IsSuccess);
                    if (fault == 3) Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Handle,
                        new(submitted.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
                }
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Trace must run outside owner locks.");
            worker.GetAwaiter().GetResult();
        }
        clock.Next = Observe;
        var refused = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(refused.IsSuccess);
        Assert.NotNull(operation);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        var actual = s.Kernel.ExternalOperations.Query(operation.Value).Value!;
        Assert.Equal(fault == 0, actual.State == ExternalOperationState.Released);
        if (fault != 0)
        {
            Assert.NotNull(actual.Binding);
            Assert.Equal(fault == 3 ? ExternalOperationDisposition.Discarded : ExternalOperationDisposition.CancellationPending, actual.Disposition);
            Assert.False(s.Kernel.ReleaseRegion(s.Handle, s.Input).IsSuccess);
        }
        Assert.Equal(fault == 0, s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        if (fault != 0)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
            Assert.Equal(actual.Binding, s.Kernel.ExternalOperations.Query(operation.Value).Value!.Binding);
        }
    }

    private sealed class CleanupClock : TimeProvider
    {
        public Action? Next;
        public override long GetTimestamp()
        {
            var callback = Next;
            Next = null;
            callback?.Invoke();
            return base.GetTimestamp();
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void TrackingQueryExceptionPreservesOriginalOutcomeAndBalancedTeardown(int outcome)
    {
        SubmitHookAccelerator? hook = null;
        RefusingTrackingFabric? fabric = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model), fabricFactory: model => fabric = new(model));
        s.Accelerator.SubmitAcceptanceAmbiguous = outcome == 1;
        s.Accelerator.ReturnMalformedSubmission = outcome == 2;
        var calls = 0;
        hook!.AfterSubmit = () =>
        {
            calls++;
            fabric!.ThrowNextQuery = true;
            if (outcome == 3) throw new IOException("submit response lost");
        };
        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        Assert.False(fabric!.ThrowNextQuery);
        var operation = hook.LastRequest!.OperationBinding.Operation;
        var original = s.Kernel.ExternalOperations.Query(operation).Value!;
        Assert.Equal(hook.LastRequest.OperationBinding, original.Binding);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, original.Disposition);
        Assert.Single(original.Transitions, item => item.Event == "ProviderLost");
        Assert.Equal(outcome == 0, s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        var retained = s.Kernel.ExternalOperations.Query(operation).Value!;
        Assert.Equal(outcome == 0, retained.State == ExternalOperationState.Released);
        if (outcome != 0)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
            foreach (var use in original.Admission!.RegionUses)
                Assert.Equal(KernelError.RegionUseConflict, s.Kernel.Regions.ReleaseUse(use.Handle, original.Principal).Error);
        }
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ExactNotAcceptedFactSurvivesSourceLossButUnknownAndGenerationDriftStayPinned(int fault)
    {
        SubmitHookAccelerator? hook = null;
        ClosureQueryDiscovery? discovery = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model),
            discoveryFactory: model => discovery = new(model));
        s.Accelerator.DeviceAvailable = fault == 3;
        s.Accelerator.SubmitAcceptanceAmbiguous = fault == 3;
        var calls = 0;
        hook!.AfterSubmit = () =>
        {
            calls++;
            s.Accelerator.DeviceAvailable = true;
            if (fault == 2) Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);
            else discovery!.NextFailure = fault == 1 ? 1 : 0;
        };
        var closureCalls = 0;
        hook.AfterCancel = () => closureCalls++;
        hook.AfterRelease = () => closureCalls++;
        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        var operation = hook.LastRequest!.OperationBinding.Operation;
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(operation).Value!.State);
        var outsideGate = false;
        var reentryBlocked = false;
        if (fault == 5)
            discovery!.BeforeNextQuery = () =>
            {
                var gate = typeof(CxlType2AcceleratorService).GetField("_effectGate",
                    global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Service)!;
                outsideGate = !Monitor.IsEntered(gate);
                reentryBlocked = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle,
                    new(s.Subject.DomainId, s.Handle.Generation)).Error == KernelError.PlatformBindingDraining;
            };
        discovery!.NextFailure = fault == 4 ? 1 : 0;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        discovery.NextFailure = null;
        var retry = s.Kernel.TerminateProcess(s.Handle);
        Assert.Equal(fault is 0 or 1 or 4 or 5, retry.IsSuccess);
        Assert.Equal(fault is 0 or 1 or 4 or 5,
            s.Kernel.ExternalOperations.Query(operation).Value!.State == ExternalOperationState.Released);
        Assert.Equal(1, calls);
        Assert.Equal(0, closureCalls);
        if (fault == 5) { Assert.True(outsideGate); Assert.True(reentryBlocked); }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UntrackedAcceptedSubmissionRetainsOriginalLossAndRetriesExactTeardown(int fault)
    {
        SubmitHookAccelerator? hook = null;
        RefusingTrackingFabric? fabric = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model),
            fabricFactory: model => fabric = new(model));
        hook!.AfterSubmit = () =>
        {
            fabric!.RefuseNextQuery = true;
            s.Accelerator.CancellationFails = fault == 1;
            s.Accelerator.ReleaseFails = fault == 2;
        };
        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        var receipt = Assert.IsType<CxlAcceleratorSubmission>(hook.LastSubmission);
        var original = s.Kernel.ExternalOperations.Query(receipt.OperationBinding.Operation).Value!;
        Assert.Equal(receipt.OperationBinding, original.Binding);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(ExternalOperationState.Submitted, original.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, original.Disposition);
        Assert.Single(original.Transitions, item => item.Event == "ProviderLost");
        Assert.False(s.Kernel.ReleaseExternalOperation(s.Handle, receipt.OperationBinding.Operation, new(false, false)).IsSuccess);
        if (fault != 0)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
            s.Accelerator.CancellationFails = false;
            s.Accelerator.ReleaseFails = false;
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.NotEqual(ExternalOperationState.Released,
                s.Kernel.ExternalOperations.Query(receipt.OperationBinding.Operation).Value!.State);
        }
        if (fault == 0)
        {
            Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        }
        Assert.Equal(fault == 0,
            s.Kernel.ExternalOperations.Query(receipt.OperationBinding.Operation).Value!.State == ExternalOperationState.Released);
        Assert.Equal(1, s.Accelerator.SubmitCount);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Type2KnownReleaseReplySurvivesLateSourceFailureWithoutDuplicateRelease(int fault)
    {
        SubmitHookAccelerator? hook = null;
        ClosureQueryDiscovery? discovery = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model),
            discoveryFactory: model => discovery = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var releases = 0;
        hook!.AfterRelease = () =>
        {
            releases++;
            if (fault == 2) Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);
            else discovery!.NextFailure = fault;
        };
        var owner = new RegionOwner(s.Subject.DomainId, s.Handle.Generation);
        var participant = (ICxlTeardownParticipant)s.Service;
        Assert.False(participant.CloseForProcess(s.Handle, owner).IsSuccess);
        Assert.Equal(1, releases);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, owner).IsSuccess);
        Assert.False(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate],
            () => throw new InvalidOperationException("closed provider output must not publish")).IsSuccess);
        var retry = participant.CloseForProcess(s.Handle, owner);
        Assert.Equal(fault != 2, retry.IsSuccess);
        Assert.Equal(1, releases);
        var snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Published or SemanticTraceEventKindV1.Released);
        if (fault == 2)
        {
            Assert.NotEqual(ExternalOperationState.Released, snapshot.State);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        }
        else
        {
            Assert.Equal(ExternalOperationState.Released, snapshot.State);
            Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        }
    }

    [Fact]
    public async Task Type2CapturedDrainCallbackAfterConcurrentCleanupDoesNotRepeatProviderReleaseOrFaultFabric()
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var blocker = s.Kernel.PrepareExternalOperation(s.Handle,
            [new(s.Input.Handle, RegionUseMode.ReadOnly, new(0, 1))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        Assert.True(s.FabricManager.TrackOperation(s.Fabric, s.Handle, blocker, () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("drain snapshot pause");
            return KernelResult.Ok();
        }).IsSuccess);
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var releases = 0;
        hook!.AfterRelease = () => Interlocked.Increment(ref releases);
        var drain = Task.Run(() => s.FabricManager.BeginReconfiguration(s.Fabric));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var closed = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle,
                new(s.Subject.DomainId, s.Handle.Generation));
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.Equal(ExternalOperationState.Released,
                s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!.State);
            Assert.Equal(1, Volatile.Read(ref releases));
        }
        finally { resume.Set(); }
        var result = await drain;
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, releases);
        Assert.True(s.FabricManager.CompleteReconfiguration(s.Fabric.BindingId).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        var released = s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!;
        Assert.All(released.Admission!.RegionUses, use =>
            Assert.DoesNotContain(s.Kernel.Regions.SnapshotUses(), current =>
                current.Handle == use.Handle && current.State == RegionUseState.Active));
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public void Type2InitialPublicationAdmissionUsesFreshCapabilityBeforeAction(bool revoke, bool releaseFails)
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.ReleaseFails = releaseFails;
        var actionCalls = 0;
        s.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () =>
        {
            var before = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
            Assert.Equal(ExternalOperationState.Visible, before.State);
            Assert.Equal(ExternalOperationDisposition.Completed, before.Disposition);
            if (revoke)
            {
                var revoking = new Thread(() => s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)));
                revoking.Start();
                Assert.True(revoking.Join(TimeSpan.FromSeconds(5)));
                Assert.False(s.Kernel.CapabilityAuthority.Validate(DeviceCapability(s), s.Subject.DomainId,
                    s.Handle.Generation, CapabilityRights.None).IsSuccess);
            }
            Assert.Equal(before.Transitions, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.Transitions);
        };
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => actionCalls++);
        }
        finally { s.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        var snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.DoesNotContain(snapshot.Transitions, item => item.Event == "PublicationEffectAmbiguous");
        if (!revoke)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(1, actionCalls);
            Assert.Equal(ExternalOperationState.Released, snapshot.State);
            return;
        }
        Assert.False(result.IsSuccess);
        Assert.Equal(0, actionCalls);
        Assert.DoesNotContain(snapshot.Transitions, item => item.Event == "Published");
        if (releaseFails)
        {
            Assert.NotEqual(ExternalOperationState.Released, snapshot.State);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.True(s.Kernel.Regions.Validate(s.Output.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
            // Failed original Release is unknown; restoring a flag does not resolve that invocation.
            s.Accelerator.ReleaseFails = false;
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        }
        else
        {
            Assert.Equal(ExternalOperationState.Released, snapshot.State);
            Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
            Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        }
    }

    [Fact]
    public void Type2PublishedLostReleaseReplyPreservesPublishedFactUntilExactCleanupRetry()
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        hook!.AfterRelease = () => throw new IOException("published release reply lost");
        var action = 0;
        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => action++);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, action);
        var published = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Published, published.State);
        Assert.DoesNotContain(published.Transitions, transition => transition.Event == "ProviderLost");
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        hook.AfterRelease = null;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        var recovered = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Published, recovered.State);
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(recovered, new string('a', 64));
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Published);
        Assert.DoesNotContain(trace, item => item.Kind == SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Quarantined or SemanticTraceEventKindV1.EffectClosedWithoutPublication or SemanticTraceEventKindV1.Settled or SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2ModelReceiptExhaustionRefusesNewEffectWithoutForgettingClosure(bool identity)
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var uses = s.Kernel.ExternalOperations.Query(execution.Operation).Value!.Admission!.RegionUses;
        var request = new CxlAcceleratorRequest(s.Plan.Intent.Operation, s.Plan.PublicationPath,
            execution.Binding, uses, execution.Submission.EndpointId,
            execution.Submission.DeviceGeneration, execution.Submission.FabricBinding);
        Assert.True(s.Accelerator.Release(execution.Submission).IsSuccess);
        if (identity)
            typeof(CxlType2ModelAccelerator).GetField("_nextId", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(s.Accelerator, ulong.MaxValue);
        else
            for (var i = 1; i < 4096; i++)
            {
                var receipt = s.Accelerator.Submit(request);
                Assert.True(receipt.IsSuccess);
                Assert.True(s.Accelerator.Release(receipt.Value!).IsSuccess);
            }
        var accepted = s.Accelerator.SubmitCount;
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, s.Accelerator.Submit(request).Status);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, s.Accelerator.Submit(request).Status);
        Assert.Equal(accepted, s.Accelerator.SubmitCount);
        Assert.True(s.Accelerator.Cancel(execution.Submission).IsSuccess);
        Assert.True(s.Accelerator.Release(execution.Submission).IsSuccess);
        Assert.True(((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
    }

    [Fact]
    public async Task Type2ModelConcurrentExactReleaseRetriesPreserveClosedState()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var replies = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => s.Accelerator.Release(execution.Submission))));
        Assert.All(replies, reply => Assert.True(reply.IsSuccess));
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        Assert.True(((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Type2ModelClosedReceiptCannotCloseForeignTupleOrRestoreVisibility(int drift)
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var exact = execution.Submission;
        Assert.True(s.Accelerator.Release(exact).IsSuccess);
        var foreign = drift switch
        {
            0 => exact with { SubmissionId = new(exact.SubmissionId.Value + 1) },
            1 => exact with { Generation = new(exact.Generation.Value + 1) },
            2 => exact with { OperationBinding = exact.OperationBinding with { Generation = exact.OperationBinding.Generation + 1 } },
            3 => exact with { OperationBinding = exact.OperationBinding with { Operation = exact.OperationBinding.Operation with { Generation = new(exact.OperationBinding.Operation.Generation.Value + 1) } } },
            4 => exact with { EndpointId = new("foreign-endpoint") },
            5 => exact with { DeviceGeneration = new(exact.DeviceGeneration.Value + 1) },
            6 => exact with { FabricBinding = exact.FabricBinding with { BindingId = new(exact.FabricBinding.BindingId.Value + 1) } },
            _ => exact with { FabricBinding = exact.FabricBinding with { Generation = new(exact.FabricBinding.Generation.Value + 1) } }
        };
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Cancel(foreign).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Release(foreign).Status);
        Assert.True(s.Accelerator.Cancel(exact).IsSuccess);
        Assert.True(s.Accelerator.Release(exact).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(exact).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.AcquireVisibility(exact, ExternalVisibilityRequirement.PublicationFence).Status);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.True(((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Type2CancelResetCannotStartReleaseWithStaleClosureSource(int reset)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var cancelled = 0;
        var released = 0;
        hook!.AfterCancel = () =>
        {
            cancelled++;
            if (reset == 1) Assert.True(s.Memory.HotRemove(s.Endpoint.EndpointId).IsSuccess);
            if (reset == 2)
            {
                Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);
                Assert.NotEqual(s.Endpoint.DeviceGeneration, s.Memory.QueryEndpoint(s.Endpoint.EndpointId).Value!.DeviceGeneration);
            }
        };
        hook.AfterRelease = () => released++;
        var closed = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
        Assert.Equal(1, cancelled);
        Assert.Equal(reset == 0 ? 1 : 0, released);
        Assert.Equal(reset == 0, closed.IsSuccess);
        var snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        if (reset != 0)
        {
            Assert.NotEqual(ExternalOperationState.Released, snapshot.State);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
            Assert.False(((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
            Assert.Equal(1, cancelled);
            Assert.Equal(0, released);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        }
        else
        {
            Assert.Equal(ExternalOperationState.Released, snapshot.State);
            Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Type2LostCancelReplyRetainsReceiptForExactClosureRetry(int fault)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var cancellations = 0;
        var releases = 0;
        hook!.AfterCancel = () =>
        {
            cancellations++;
            throw fault switch
            {
                0 => new TimeoutException("cancel reply lost"),
                1 => new OperationCanceledException("cancel reply lost"),
                _ => new IOException("cancel transport lost")
            };
        };
        hook.AfterRelease = () => releases++;
        var closed = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
        Assert.Equal(KernelError.ExternalEffectUncontained, closed.Error);
        Assert.Equal(1, cancellations);
        Assert.Equal(0, releases);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)).Error);
        var lost = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Single(lost.Transitions, transition => transition.Event == "ProviderLost");
        var prefix = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(lost, new string('a', 64));
        Assert.Contains(prefix, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        hook.AfterCancel = () => cancellations++;
        var retry = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, cancellations);
        Assert.Equal(0, releases);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, cancellations);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
    }

    [Fact]
    public void Type2ReentrantProcessStopDuringActionCannotCloseOrPublishStaleAuthority()
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var releases = 0;
        hook!.AfterRelease = () => releases++;
        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            s.Output.Span[0] = s.Input.Span[0];
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(0, releases);
        });
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(1, releases);
        var snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.NotEqual(ExternalOperationDisposition.Published, snapshot.Disposition);
        Assert.Contains(snapshot.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Type2PublicationActionExcludesProviderClosureAndRetainsAmbiguity(bool failAction)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var releases = 0;
        hook!.AfterRelease = () => Interlocked.Increment(ref releases);
        var pending = Task.Run(() => s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            foreach (var owner in new object[] { s.Service, s.Kernel.CapabilityAuthority, s.Kernel.ExternalOperations })
            {
                var name = owner == s.Service ? "_effectGate" : "_gate";
                var gate = owner.GetType().GetField(name, global::System.Reflection.BindingFlags.Instance |
                    global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
                Assert.False(Monitor.IsEntered(gate));
            }
            s.Output.Span[0] = s.Input.Span[0];
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test publication pause");
            if (failAction) throw new IOException("action failed after possible output publication");
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var duplicate = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("duplicate action"));
            Assert.Equal(KernelError.PlatformBindingDraining, duplicate.Error);
            var closure = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
            Assert.Equal(KernelError.PlatformBindingDraining, closure.Error);
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        }
        finally { resume.Set(); }
        var result = await pending;
        Assert.Equal(1, releases);
        var snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        if (failAction)
        {
            Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
            Assert.Contains(snapshot.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
            Assert.NotEqual(ExternalOperationState.Released, snapshot.State);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(1, releases);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(ExternalOperationState.Released, snapshot.State);
            Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        }
    }

    [Fact]
    public async Task Type2ClosureAdmissionRejectsNewObservationAndPublication()
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var observations = 0;
        hook!.AfterCompletion = () => Interlocked.Increment(ref observations);
        hook.AfterRelease = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var closing = Task.Run(() => ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("action after closure admission"));
            Assert.Equal(KernelError.PlatformBindingDraining, result.Error);
            Assert.Equal(0, observations);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        }
        finally { resume.Set(); }
        var closed = await closing;
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Type2PendingObservationBlocksOverlappingDeliveryAndClosure(bool visibility, bool lost)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var releases = 0;
        hook!.AfterRelease = () => Interlocked.Increment(ref releases);
        Action pause = () =>
        {
            var gate = typeof(CxlType2AcceleratorService).GetField("_effectGate",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Service)!;
            Assert.False(Monitor.IsEntered(gate));
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test observation pause");
            if (lost) throw new IOException("paused observation reply lost");
        };
        if (visibility) hook.AfterVisibility = pause;
        else hook.AfterCompletion = pause;
        var published = false;
        var pending = Task.Run(() => s.Service.CompleteVisiblePublish(s.Handle, execution,
            [s.Candidate], () => published = true));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var duplicate = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => published = true);
            Assert.Equal(KernelError.PlatformBindingDraining, duplicate.Error);
            Assert.False(s.FabricManager.BeginReconfiguration(s.Fabric).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
            Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        }
        finally { resume.Set(); }
        var result = await pending;
        Assert.False(result.IsSuccess);
        Assert.False(published);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, releases);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void Type2LostObservationRetainsReceiptUntilRealClosure(bool visibility, int fault)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Action lost = () => throw fault switch
        {
            0 => new TimeoutException("observation lost"),
            1 => new OperationCanceledException("observation cancelled"),
            _ => new IOException("observation transport lost")
        };
        if (visibility) hook!.AfterVisibility = lost;
        else hook!.AfterCompletion = lost;
        var published = false;
        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => published = true);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(published);
        var snapshot = s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!;
        Assert.Contains(snapshot.Transitions, transition => transition.Event == "ProviderLost");
        Assert.NotEqual(ExternalOperationState.Released, snapshot.State);
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        s.Accelerator.ReleaseFails = true;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        s.Accelerator.ReleaseFails = false;
        var retry = s.Kernel.TerminateProcess(s.Handle);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, hook!.ReleaseCalls);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2FinalOwnerPublicationCommitUsesFreshSourceOutsideActionLocks(bool revoke)
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        var actionRan = false;
        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            foreach (var owner in new object[] { s.Kernel.CapabilityAuthority, s.Kernel.ExternalOperations })
            {
                var gate = owner.GetType().GetField("_gate", global::System.Reflection.BindingFlags.Instance |
                    global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
                Assert.False(Monitor.IsEntered(gate));
            }
            actionRan = true;
            if (revoke) Assert.True(s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)).IsSuccess);
        });
        Assert.True(actionRan);
        Assert.Equal(!revoke, result.IsSuccess);
        var operation = s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!;
        if (revoke)
        {
            Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
            Assert.NotEqual(ExternalOperationState.Released, operation.State);
            Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        }
        else Assert.Equal(ExternalOperationState.Released, operation.State);
    }

    [Fact]
    public void Type2LiveOperationPinsFabricUntilExplicitProviderRelease()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Fact]
    public void Type2CancellationAcknowledgementCannotReleaseParentWithoutClosure()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Assert.True(s.Accelerator.Cancel(execution.Submission).IsSuccess);
        Assert.Equal(ExternalOperationCompletionDisposition.Cancelled, s.Accelerator.ObserveCompletion(execution.Submission).Value.Disposition);
        s.Accelerator.ReleaseFails = true;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        s.Accelerator.ReleaseFails = false;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public void Type2FreshSourceRejectsDirectCapabilityOwnerRevokeBeforeSubmit()
    {
        var s = CreateScenario();
        var cap = DeviceCapability(s);
        Assert.True(s.Kernel.CapabilityAuthority.Revoke(cap).IsSuccess);
        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
    }

    [Fact]
    public async Task Type2PendingCallbackPinsParentAndRejectsConcurrentReconfiguration()
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        hook!.AfterSubmit = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var pending = Task.Run(() => s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var effects = typeof(CxlType2AcceleratorService).GetField("_effects",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
                .GetValue(s.Service)!;
            var records = (global::System.Collections.IEnumerable)effects.GetType().GetProperty("Values")!.GetValue(effects)!;
            var effect = Assert.Single(records.Cast<object>());
            Assert.Equal(1, (int)effect.GetType().GetField("SubmitAdmitted")!.GetValue(effect)!);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, s.FabricManager.BeginReconfiguration(s.Fabric).Error);
        }
        finally { resume.Set(); }
        var execution = await pending;
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution.Value!, [s.Candidate], () => { }).IsSuccess);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
    }

    [Fact]
    public void Type2CallbackRevokeRetainsLateReceiptForRealTeardown()
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        hook!.AfterSubmit = () => Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
        var submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.ExternalEffectUncontained, submitted.Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    private static CapabilityId DeviceCapability(Scenario s) => s.Kernel.CapabilityAuthority.InspectionSnapshot()
        .Single(item => item.Descriptor.ResourceKind == ResourceKind.Device && item.Descriptor.ResourceId == s.Lease.Device.ResourceId)
        .Descriptor.CapabilityId;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Type2LostSubmitReceiptAfterMaterializationCannotReclaim(int fault)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var effects = 0;
        hook!.AfterSubmit = () =>
        {
            effects++;
            throw fault switch
            {
                0 => new TimeoutException("materialized submit receipt lost"),
                1 => new OperationCanceledException("materialized submit receipt lost"),
                _ => new IOException("materialized submit transport lost")
            };
        };
        var submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(1, effects);
        Assert.Equal(KernelError.ExternalEffectUncontained, submitted.Error);
        hook.AfterSubmit = null;
        var operation = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationDisposition.ProviderLost, operation.Disposition);
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(operation, new string('a', 64));
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind == SemanticTraceEventKindV1.EffectClosedWithoutPublication);
        Assert.DoesNotContain(trace, item => item.Kind == SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid); // Valid observations, not terminal closure.
        var tail = trace[^1];
        var released = tail with { Sequence = tail.Sequence + 1, Kind = SemanticTraceEventKindV1.Released };
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(released)).IsValid);
        var settled = tail with { Sequence = tail.Sequence + 1, Kind = SemanticTraceEventKindV1.Settled };
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(settled)
            .Append(released with { Sequence = tail.Sequence + 2 })).IsValid);
        Assert.False(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        Assert.False(s.FabricManager.BeginReconfiguration(s.Fabric).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle,
            new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Type2LostReleaseReplyOrSourceResetRetainsParent(bool resetSource)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        hook!.AfterRelease = () =>
        {
            if (resetSource) Assert.True(s.Memory.HotRemove(s.Endpoint.EndpointId).IsSuccess);
            else throw new IOException("Lost exact release reply after provider closure.");
        };
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        hook.AfterRelease = null;
        if (!resetSource)
        {
            var lost = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
            Assert.Contains(lost.Transitions, transition => transition.Event == "ProviderLost");
            var prefix = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(lost, new string('a', 64));
            Assert.Contains(prefix, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
            Assert.DoesNotContain(prefix, item => item.Kind is SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding);
            Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        }
        var retry = s.Kernel.TerminateProcess(s.Handle);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Fact]
    public void StagedOperationTraversesCommonLifecycleBeforePublication()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.Equal(ExternalOperationState.Submitted,
            s.Kernel.QueryExternalOperation(s.Handle, execution.Value!.Operation).Value!.State);
        Assert.Equal(0, s.Output.Span[0]);

        var finished = s.Service.CompleteVisiblePublish(s.Handle, execution.Value, [s.Candidate],
            () => s.Input.Span.CopyTo(s.Output.Span));
        Assert.True(finished.IsSuccess, finished.Message);
        Assert.Equal(ExternalOperationState.Released, finished.Value!.State);
        Assert.Equal(s.Input.Span.ToArray(), s.Output.Span.ToArray());
    }

    [Fact]
    public void StaleRegionUseRejectsBeforeProviderSubmission()
    {
        var s = CreateScenario();
        var conflicting = s.Kernel.AcquireRegionUse(s.Handle, s.Output.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(conflicting.IsSuccess);
        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void DeviceResetDuringStagedExecutionNeverPublishes()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        s.Output.Span.Fill(0);
        Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => s.Output.Span.Fill(9));
        Assert.False(result.IsSuccess);
        Assert.All(s.Output.Span.ToArray(), value => Assert.Equal(0, value));
        Assert.NotEqual(ExternalOperationDisposition.Published,
            s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!.Disposition);
    }

    [Fact]
    public void DirectModeIsFutureGatedBeforeProviderSubmission()
    {
        var s = CreateScenario();
        var directCandidate = s.Candidate with { Capabilities = ComputeProviderCapabilities.AcceleratorExecution |
            ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess };
        var gated = s.Kernel.PlanCompute(s.Handle, s.Plan.Intent with { PublicationPreference = ComputePublicationPreference.DirectRequired },
            new(true, true, true), [directCandidate]);
        Assert.False(gated.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, gated.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void AcceleratorRoleDoesNotReplaceOrdinaryDeviceAuthority()
    {
        var s = CreateScenario();
        Assert.Equal(new PlatformDeviceIdentity("device:type2-0"), s.Lease.Device);
        Assert.Contains(typeof(ICxlType2AcceleratorProvider), s.Accelerator.GetType().GetInterfaces());
        Assert.DoesNotContain(typeof(IPlatformDeviceLeaseProvider), s.Accelerator.GetType().GetInterfaces());
    }

    [Fact]
    public void PublicComputeDescriptorCannotNamePhysicalCxlTransport()
    {
        var names = typeof(CxlAcceleratorRequest).GetProperties().Select(property => property.Name).ToArray();
        foreach (var forbidden in new[] { "Decoder", "Dpa", "Hpa", "Route", "Register", "Mailbox", "Opcode", "Lane", "Pasid" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VirtualizedPlanCannotUseOrdinarySubmitWithoutExactKernelContext()
    {
        var s = CreateScenario();
        var virtualized = s.Plan with { Intent = s.Plan.Intent with { RequiresVirtualizedDomain = true } };

        var result = s.Service.Submit(s.Handle, virtualized, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void ForgedType2ExecutionCannotCompleteOrPublish()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        var forged = execution with { Plan = execution.Plan with { ProviderGeneration = execution.Plan.ProviderGeneration + 1 } };
        var published = false;

        var result = s.Service.CompleteVisiblePublish(s.Handle, forged, [s.Candidate], () => published = true);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.False(published);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);
    }

    [Fact]
    public void StaleAcceleratorCancelAndReleaseCannotDeleteCurrentSubmission()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        var stale = execution.Submission with { Generation = new(execution.Submission.Generation.Value + 1) };

        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Cancel(stale).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Release(stale).Status);
        Assert.True(s.Accelerator.ObserveCompletion(execution.Submission).IsSuccess);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);
    }

    [Fact]
    public void ProviderSubmitFailureLeavesNoHiddenOperationOrRegionUsePinned()
    {
        var s = CreateScenario();
        var activeBefore = s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active);
        s.Accelerator.DeviceAvailable = false;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(activeBefore, s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active));
        var operation = s.Kernel.ExternalOperations.Query(new(new(1), new(1)));
        Assert.True(operation.IsSuccess, operation.Message);
        Assert.Equal(ExternalOperationState.Released, operation.Value!.State);
    }

    [Fact]
    public void SecureRequiredType2RejectsBeforeProviderEffectWithoutCurrentReadiness()
    {
        var s = CreateScenario();
        var secureCandidate = s.Candidate with { Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence };
        var securePlan = s.Kernel.PlanCompute(s.Handle, s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [secureCandidate]).Value!;
        var securityAuthority = new CxlSecurityAuthority(s.Bridge, s.Security);

        var result = s.Service.Submit(s.Handle, securePlan, [secureCandidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, securityAuthority: securityAuthority,
            securityPolicy: new(CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated,
                new(1), RequiredForOperation: true));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(ExternalOperationState.Released,
            s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!.State);
    }

    [Fact]
    public void TrustEvidenceLossAfterOwnerAdmissionIsRevalidatedBeforeProviderEffect()
    {
        var s = CreateScenario();
        const CxlSecurityProperties properties = CxlSecurityProperties.IdeEnabled |
            CxlSecurityProperties.DeviceAuthenticated | CxlSecurityProperties.FirmwareMeasured;
        Assert.True(s.Security.Register(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, properties).IsSuccess);
        var state = s.Security.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        var obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 37);
        var resetOnSecondRead = new ResetOnSecondSecurityRead(s.Security, s.Endpoint.EndpointId);
        var securityAuthority = new CxlSecurityAuthority(s.Bridge, resetOnSecondRead);
        var secureCandidate = s.Candidate with
        {
            Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence
        };
        var securePlan = s.Kernel.PlanCompute(s.Handle,
            s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [secureCandidate]).Value!;

        var result = s.Service.Submit(s.Handle, securePlan, [secureCandidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, securityAuthority: securityAuthority,
            securityPolicy: new(properties, state.EvidenceGeneration, RequiredForOperation: true,
                TrustObligation: obligation, CurrentPolicyGeneration: 37));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(ExternalOperationState.Released,
            s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!.State);
    }

    [Fact]
    public void TrustResetDuringVisibilityCannotPublishStagedType2Output()
    {
        var s = CreateScenario();
        const CxlSecurityProperties properties = CxlSecurityProperties.IdeEnabled |
            CxlSecurityProperties.DeviceAuthenticated | CxlSecurityProperties.FirmwareMeasured;
        Assert.True(s.Security.Register(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, properties).IsSuccess);
        var state = s.Security.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        var obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 37);
        var candidate = s.Candidate with
        {
            Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence
        };
        var plan = s.Kernel.PlanCompute(s.Handle,
            s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [candidate]).Value!;
        var provider = new ResetOnVisibilityAccelerator(s.Accelerator, s.Security, s.Endpoint.EndpointId);
        var service = new CxlType2AcceleratorService(s.Kernel, s.Bridge, provider, s.FabricManager);
        var security = new CxlSecurityAuthority(s.Bridge, s.Security);
        var policy = new CxlSecurityPolicy(properties, state.EvidenceGeneration, RequiredForOperation: true,
            TrustObligation: obligation, CurrentPolicyGeneration: 37);
        var execution = service.Submit(s.Handle, plan, [candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, securityAuthority: security, securityPolicy: policy).Value!;
        var published = false;

        var result = service.CompleteVisiblePublish(s.Handle, execution, [candidate],
            () => published = true);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.False(published);
        Assert.Equal(1, provider.VisibilityCalls);
        Assert.Equal(ExternalOperationState.Released,
            s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Fact]
    public void TrustResetInsidePublicationActionRetainsAmbiguousEffect()
    {
        var s = CreateScenario();
        const CxlSecurityProperties properties = CxlSecurityProperties.IdeEnabled |
            CxlSecurityProperties.DeviceAuthenticated | CxlSecurityProperties.FirmwareMeasured;
        Assert.True(s.Security.Register(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, properties).IsSuccess);
        var state = s.Security.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        var obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 37);
        var candidate = s.Candidate with
        {
            Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence
        };
        var plan = s.Kernel.PlanCompute(s.Handle,
            s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [candidate]).Value!;
        var security = new CxlSecurityAuthority(s.Bridge, s.Security);
        var policy = new CxlSecurityPolicy(properties, state.EvidenceGeneration, RequiredForOperation: true,
            TrustObligation: obligation, CurrentPolicyGeneration: 37);
        var execution = s.Service.Submit(s.Handle, plan, [candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, securityAuthority: security, securityPolicy: policy).Value!;
        var actionRan = false;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [candidate], () =>
        {
            actionRan = true;
            Assert.True(s.Security.ResetSecuritySession(s.Endpoint.EndpointId).IsSuccess);
        });

        Assert.True(actionRan);
        Assert.False(result.IsSuccess);
        var owner = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.DoesNotContain(owner.Transitions, transition => transition.Event == "Published");
        Assert.Contains(owner.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, owner.EffectBoundary);
        Assert.NotEqual(ExternalOperationState.Released, owner.State);
    }

    [Fact]
    public void ProcessTeardownClosesLiveType2SubmissionBeforeRegionReclaim()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;

        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);

        Assert.Equal(ExternalOperationState.Released,
            s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void AmbiguousType2ClosureQuarantinesProcessAndBlocksRegionReclaim()
    {
        var s = CreateScenario();
        _ = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.CancellationFails = true;

        var teardown = s.Kernel.TerminateProcess(s.Handle);

        Assert.False(teardown.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, teardown.Error);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        var owner = new RegionOwner(s.Subject.DomainId, s.Handle.Generation);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MalformedType2SubmissionKeepsOriginalLossAndPinsRegardlessOfReleaseMode(bool releaseFails)
    {
        SubmitHookAccelerator? hook = null;
        var s = CreateScenario(acceleratorFactory: model => hook = new(model));
        s.Accelerator.ReturnMalformedSubmission = true;
        s.Accelerator.ReleaseFails = releaseFails;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        var operation = s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!;
        Assert.Equal(ExternalOperationState.Submitted, operation.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, operation.Disposition);
        Assert.NotEqual(operation.Binding, hook!.LastSubmission!.OperationBinding);
        Assert.Single(operation.Transitions, item => item.Event == "ProviderLost");
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(operation, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Published or SemanticTraceEventKindV1.Released);
        foreach (var use in operation.Admission!.RegionUses)
            Assert.Contains(s.Kernel.Regions.SnapshotUses(), current =>
                current.Handle == use.Handle && current.State == RegionUseState.Invalidated);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        s.Accelerator.ReleaseFails = false;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        var retained = s.Kernel.ExternalOperations.Query(operation.Operation).Value!;
        Assert.Equal(operation.Binding, retained.Binding);
        Assert.Single(retained.Transitions, item => item.Event == "ProviderLost");
        foreach (var use in operation.Admission!.RegionUses)
            Assert.Equal(KernelError.RegionUseConflict, s.Kernel.Regions.ReleaseUse(use.Handle, operation.Principal).Error);
    }

    [Fact]
    public void FailBeforePublication_ProviderReleaseFailure_QuarantinesInsteadOfReclaim()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.ReleaseFails = true;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(ExternalOperationState.Submitted, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Contains(s.Kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active && use.Region == s.Output.Handle);
        s.Accelerator.CancellationFails = true;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Output.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
    }

    [Fact]
    public void SubmitFailure_AmbiguousAcceptance_DoesNotUseProviderUnavailableAsClosure()
    {
        var s = CreateScenario();
        s.Accelerator.SubmitAcceptanceAmbiguous = true;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        var original = s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!;
        Assert.Equal(ExternalOperationState.Submitted, original.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, original.Disposition);
        Assert.Single(original.Transitions, item => item.Event == "ProviderLost");
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(original, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Published or SemanticTraceEventKindV1.Released);
        Assert.False(s.Kernel.ReleaseExternalOperation(s.Handle, original.Operation, new(false, false)).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        var retained = s.Kernel.ExternalOperations.Query(original.Operation).Value!;
        Assert.Equal(original.Binding, retained.Binding);
        Assert.Single(retained.Transitions, item => item.Event == "ProviderLost");
        foreach (var use in original.Admission!.RegionUses)
            Assert.Equal(KernelError.RegionUseConflict, s.Kernel.Regions.ReleaseUse(use.Handle, original.Principal).Error);
    }

    [Fact]
    public void ProviderExceptionAfterSubmitAcceptanceIsContainedAsUnrecoverableQuarantine()
    {
        var s = CreateScenario();
        s.Accelerator.SubmitThrowsAfterAcceptance = true;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(ExternalOperationState.Submitted,
            s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!.State);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
    }

    [Fact]
    public void Type2Submit_WhileFabricBindingDraining_IsRejectedBeforeProviderEffect()
    {
        var s = CreateScenario();
        Assert.True(s.FabricManager.BeginReconfiguration(s.Fabric).IsSuccess);

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void FabricReconfiguration_WithLiveType2Submission_ClosesProviderBeforeExternalOperationRelease()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;

        var reconfiguration = s.FabricManager.BeginReconfiguration(s.Fabric);

        Assert.True(reconfiguration.IsSuccess, reconfiguration.Message);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Fact]
    public void TwoBindingsSameEndpointSameGeneration_AreDistinctInAcceleratorContract()
    {
        var first = new CxlFabricBindingRef(new(41), new(1));
        var second = new CxlFabricBindingRef(new(42), new(1));

        Assert.NotEqual(first, second);
        Assert.Equal(first.Generation, second.Generation);
        Assert.Contains(nameof(CxlFabricBindingRef.BindingId), typeof(CxlFabricBindingRef).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("FabricGeneration", typeof(CxlAcceleratorRequest).GetProperties().Select(property => property.Name));

        var accelerator = new CxlType2ModelAccelerator();
        var operation = new OperationBinding(new(new(7), new(1)), new(1), 1);
        var uses = new[]
        {
            new RegionUseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), new(0, 8), RegionUseMode.ReadOnly, new(1), RegionUseState.Active),
            new RegionUseDescriptor(new(new(2), 1), new(new(2), new(1)), new(new(1), 1), new(0, 8), RegionUseMode.StagedOutput, new(1), RegionUseState.Active)
        };
        var firstSubmission = accelerator.Submit(new(ComputeOperationKind.Copy, ComputePublicationPath.Staged,
            operation, uses, new("shared-endpoint"), new(3), first));
        var secondSubmission = accelerator.Submit(new(ComputeOperationKind.Copy, ComputePublicationPath.Staged,
            operation, uses, new("shared-endpoint"), new(3), second));

        Assert.True(firstSubmission.IsSuccess);
        Assert.True(secondSubmission.IsSuccess);
        Assert.NotEqual(firstSubmission.Value!.FabricBinding, secondSubmission.Value!.FabricBinding);
        Assert.Equal(first, firstSubmission.Value.FabricBinding);
        Assert.Equal(second, secondSubmission.Value.FabricBinding);
    }

    [Fact]
    public void NotAcceptedStatusExtendsPlatformContractWithoutRenumberingExistingStatuses()
    {
        Assert.Equal(0, (int)PlatformAuthorityStatus.Success);
        Assert.Equal(1, (int)PlatformAuthorityStatus.Unavailable);
        Assert.Equal(7, (int)PlatformAuthorityStatus.Faulted);
        Assert.Equal(8, (int)PlatformAuthorityStatus.NotAccepted);
    }

    [Fact]
    public void FaultedCompletionClosesProviderAndReleasesRegionUses()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.DeviceAvailable = false;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void VisibilityFailureClosesProviderAndReleasesRegionUses()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.VisibilitySatisfied = false;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void PublicationExceptionClosesProviderButQuarantinesPossiblePublicationAndRegionUses()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate],
            () => throw new InvalidOperationException("publication failed"));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        var operation = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Visible, operation.State);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, operation.EffectBoundary);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Fact]
    public void CompletedSubmissionIsUntrackedBeforeLaterFabricReconfiguration()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);

        var reconfiguration = s.FabricManager.BeginReconfiguration(s.Fabric);

        Assert.True(reconfiguration.IsSuccess, reconfiguration.Message);
    }

    [Fact]
    public void ProtectedLabelsReachExactManagedType2ProviderSubmitBoundary()
    {
        var s = CreateScenario();
        AttachProtectedLabels(s);

        var execution = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));

        Assert.True(execution.IsSuccess, execution.Message);
        Assert.Equal(2, s.Accelerator.LastProtectedSideband.Count);
        Assert.All(s.Accelerator.LastProtectedSideband, value =>
        {
            Assert.Equal($"external-operation:{execution.Value!.Operation.OperationId.Value}", value.OperationId);
            Assert.Equal(execution.Value.Operation.Generation.Value, value.OperationGeneration);
            Assert.Equal(s.Plan.ProviderGeneration, value.ProviderGeneration);
            Assert.False(value.AuthorizesAccess);
            Assert.False(value.AuthorizesExecution);
        });
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution.Value!, [s.Candidate], () => { }).IsSuccess);
    }

    [Fact]
    public void ProtectedLabelsWithWrongProviderGenerationFailBeforeProviderEffect()
    {
        var s = CreateScenario();
        AttachProtectedLabels(s);

        var result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration + 1));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void ProtectedLabelLaunderingFailsBeforeProviderEffect()
    {
        var s = CreateScenario();
        AttachProtectedLabels(s);

        var result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation =>
            {
                var labels = ProtectedLabels(operation, s.Plan.ProviderGeneration);
                labels[1] = labels[1] with
                {
                    Label = new(1, ConfidentialityClassV1.Public, IntegrityClassV1.Trusted)
                };
                return labels;
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void ProviderWithoutProtectedBoundaryIsRejectedBeforeOperationOrEffect()
    {
        var s = CreateScenario();
        var ordinaryOnly = new OrdinaryOnlyAccelerator(s.Accelerator);
        var service = new CxlType2AcceleratorService(s.Kernel, s.Bridge, ordinaryOnly, s.FabricManager);

        var result = service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));

        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void ProtectedLabelsNeverReplaceOrdinaryRegionAuthority()
    {
        var s = CreateScenario();
        AttachProtectedLabels(s);
        Assert.True(s.Kernel.AcquireRegionUse(s.Handle, s.Output.Handle,
            RegionUseMode.ExclusiveWrite, new(0, 8)).IsSuccess);

        var result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Empty(s.Accelerator.LastProtectedSideband);
    }

    [Fact]
    public void ProtectedSidebandWithoutLiveRegionLabelsCannotReachProvider()
    {
        var s = CreateScenario();
        var result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void StaleProtectedInputLabelGenerationCannotReachProvider()
    {
        var s = CreateScenario();
        AttachProtectedLabels(s);
        var result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation =>
            {
                var labels = ProtectedLabels(operation, s.Plan.ProviderGeneration);
                labels[0] = labels[0] with { LabelGeneration = 2 };
                labels[1] = labels[1] with { LabelGeneration = 2 };
                return labels;
            });
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void LiveProtectedType2OperationPinsInputLabelUntilClosure()
    {
        var s = CreateScenario();
        AttachProtectedLabels(s);
        var execution = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));
        Assert.True(execution.IsSuccess, execution.Message);
        var process = s.Kernel.Processes.Resolve(s.Handle).Value!;
        var current = s.Kernel.Regions.QueryProtectedLabel(s.Input.Handle,
            new(process.DomainId, s.Handle.Generation)).Value!;
        var capability = s.Kernel.MintCapability(process.DomainId, s.Handle, ResourceKind.MemoryRegion,
            CapabilityResourceIds.InformationFlowTransition(s.Input.Handle.RegionId),
            CapabilityRights.Configure, resourceGeneration: s.Input.Handle.Generation.Value).Value!.CapabilityId;

        var transition = s.Kernel.TransitionV6ProtectedRegionLabel(s.Handle, current,
            new(1, InformationFlowTransitionKindV1.Declassify, current.Label,
                new(1, ConfidentialityClassV1.Public, IntegrityClassV1.Validated)), capability);

        Assert.Equal(KernelError.RegionUseConflict, transition.Error);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution.Value!, [s.Candidate], () => { }).IsSuccess);
    }

    private static void AttachProtectedLabels(Scenario scenario)
    {
        var label = new DataLabelV1(1, ConfidentialityClassV1.Protected, IntegrityClassV1.Validated);
        Assert.True(scenario.Kernel.AttachV6ProtectedRegionLabel(scenario.Handle, scenario.Input.Handle, label).IsSuccess);
        Assert.True(scenario.Kernel.AttachV6ProtectedRegionLabel(scenario.Handle, scenario.Output.Handle, label).IsSuccess);
    }

    private static ProtectedAcceleratorLabelSidebandV1[] ProtectedLabels(
        ExternalOperationHandle operation, ulong providerGeneration)
    {
        var operationId = $"external-operation:{operation.OperationId.Value}";
        var label = new DataLabelV1(1, ConfidentialityClassV1.Protected, IntegrityClassV1.Validated);
        return
        [
            new(1, operationId, "input", ProtectedAcceleratorValueKindV1.Input, 1,
                label, 1, providerGeneration, operation.Generation.Value),
            new(1, operationId, "output", ProtectedAcceleratorValueKindV1.Output, 2,
                label, 1, providerGeneration, operation.Generation.Value)
        ];
    }

    private static Scenario CreateScenario(bool direct = false,
        Func<CxlType2ModelAccelerator, ICxlType2AcceleratorProvider>? acceleratorFactory = null,
        Func<CxlType3ModelProvider, ICxlDiscoveryProvider>? discoveryFactory = null,
        Func<CxlType3ModelProvider, ICxlFabricManagementProvider>? fabricFactory = null,
        TimeProvider? clock = null)
    {
        var platform = new AuthorityProvider();
        var kernel = new RuntimeKernel(platform, clock);
        var (_, handle) = TestFixtures.Create(kernel, 971, 972);
        var process = kernel.Processes.Resolve(handle).Value!;
        var owner = new RegionOwner(process.DomainId, handle.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, handle);
        var domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        var deviceCap = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, "device:type2-0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var lease = kernel.BindPlatformDevice(handle, domain, deviceCap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var memory = new CxlType3ModelProvider();
        Assert.True(memory.RegisterEndpoint(new("type2-0"), lease.Device, 1024).IsSuccess);
        var endpoint = memory.QueryEndpoint(new("type2-0")).Value!;
        var security = new CxlSecurityModelProvider();
        var bridge = new CxlAuthorityBridge(kernel, discoveryFactory?.Invoke(memory) ?? memory, memory, memory, memory,
            new CoherentStub(), security);
        var control = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        var controlUse = kernel.AcquireRegionUse(handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 16)).Value!;
        var fabric = bridge.BindFabric(owner, controlUse.Handle, subject, lease,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var input = kernel.AllocateBuffer<byte>(handle, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(handle, 8).Value!;
        for (var i = 0; i < input.Length; i++) input.Span[i] = (byte)(i + 1);
        var preference = direct ? ComputePublicationPreference.DirectRequired : ComputePublicationPreference.StagedRequired;
        var capabilities = ComputeProviderCapabilities.AcceleratorExecution |
            (direct ? ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess : ComputeProviderCapabilities.StagedPublication);
        var candidate = new ComputeProviderCandidate(new("type2-model"), 1, capabilities, 1024, 1, 1, true, false);
        var plan = kernel.PlanCompute(handle, new(ComputeOperationKind.Copy,
                new(input.Handle, new(0, 8)), new(output.Handle, new(0, 8)), preference, false, false),
            new(true, true, true), [candidate]).Value!;
        var accelerator = new CxlType2ModelAccelerator();
        var manager = new CxlFabricManagerAuthority(kernel, fabricFactory?.Invoke(memory) ?? memory);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(kernel, bridge, acceleratorFactory?.Invoke(accelerator) ?? accelerator, manager);
        return new(kernel, handle, subject, lease, memory, endpoint, fabric, input, output, candidate, plan, accelerator, service, bridge, security, manager);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle, PlatformDomainIdentity Subject,
        PlatformDeviceLease Lease, CxlType3ModelProvider Memory, CxlEndpointSnapshot Endpoint, CxlFabricBinding Fabric,
        SingPlus.Sip.OwnedBuffer<byte> Input, SingPlus.Sip.OwnedBuffer<byte> Output, ComputeProviderCandidate Candidate,
        ComputePlan Plan, CxlType2ModelAccelerator Accelerator, CxlType2AcceleratorService Service,
        CxlAuthorityBridge Bridge, CxlSecurityModelProvider Security, CxlFabricManagerAuthority FabricManager);

    private sealed class RefusingTrackingFabric(ICxlFabricManagementProvider inner) : ICxlFabricManagementProvider
    {
        internal bool ThrowNextQuery { get; set; }
        internal bool RefuseNextQuery { get; set; }
        public PlatformAuthorityResult<CxlFabricResourceSnapshot> QueryResource(CxlFabricBindingId id)
        {
            if (ThrowNextQuery)
            {
                ThrowNextQuery = false;
                throw new IOException("fabric tracking query lost");
            }
            if (!RefuseNextQuery) return inner.QueryResource(id);
            RefuseNextQuery = false;
            return PlatformAuthorityResult<CxlFabricResourceSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "tracking source unavailable");
        }
        public PlatformAuthorityResult<CxlFabricReconfigurationTicket> BeginReconfiguration(CxlFabricBinding binding) => inner.BeginReconfiguration(binding);
        public PlatformAuthorityResult<CxlFabricBinding> CompleteReconfiguration(CxlFabricReconfigurationTicket ticket) => inner.CompleteReconfiguration(ticket);
        public PlatformAuthorityResult<CxlFabricPoolSnapshot> QueryPool(CxlFabricPoolId id) => inner.QueryPool(id);
        public PlatformAuthorityResult<CxlPoolAssignment> AssignPoolCapacity(CxlFabricPoolId pool, CxlEndpointId endpoint, long bytes) => inner.AssignPoolCapacity(pool, endpoint, bytes);
        public PlatformAuthorityResult ReleasePoolCapacity(CxlPoolAssignment assignment) => inner.ReleasePoolCapacity(assignment);
        public PlatformAuthorityResult<CxlPeerAccessEvidence> QueryPeerAccess(CxlPeerAccessRequest request) => inner.QueryPeerAccess(request);
    }

    private sealed class ClosureQueryDiscovery(ICxlDiscoveryProvider inner) : ICxlDiscoveryProvider
    {
        internal Action? BeforeNextQuery { get; set; }
        internal int? NextFailure { get; set; }
        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpointId)
        {
            var before = BeforeNextQuery;
            BeforeNextQuery = null;
            before?.Invoke();
            var failure = NextFailure;
            NextFailure = null;
            if (failure == 0) return PlatformAuthorityResult<CxlEndpointSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "late source query unavailable");
            if (failure == 1) throw new IOException("late source query transport lost");
            return inner.QueryEndpoint(endpointId);
        }
    }

    private sealed class SubmitHookAccelerator(ICxlType2AcceleratorProvider inner) : ICxlType2AcceleratorProvider
    {
        internal CxlAcceleratorRequest? LastRequest { get; private set; }
        internal CxlAcceleratorSubmission? LastSubmission { get; private set; }
        internal Action? AfterSubmit { get; set; }
        internal Action? AfterRelease { get; set; }
        internal int ReleaseCalls { get; private set; }
        internal Action? AfterCompletion { get; set; }
        internal Action? AfterVisibility { get; set; }
        internal Action? AfterCancel { get; set; }
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request)
        {
            LastRequest = request;
            var result = inner.Submit(request);
            if (result.IsSuccess) LastSubmission = result.Value;
            AfterSubmit?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission)
        {
            var result = inner.ObserveCompletion(submission);
            AfterCompletion?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement)
        {
            var result = inner.AcquireVisibility(submission, requirement);
            AfterVisibility?.Invoke();
            return result;
        }
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission)
        {
            var result = inner.Cancel(submission);
            AfterCancel?.Invoke();
            return result;
        }
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission)
        {
            ReleaseCalls++;
            var result = inner.Release(submission);
            AfterRelease?.Invoke();
            return result;
        }
    }

    private sealed class CoherentStub : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding f, RegionUseDescriptor u) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding b) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
    private sealed class EvidenceStub : ICxlSecurityEvidenceProvider
    {
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId id, CxlDeviceGeneration g) =>
            PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
    private sealed class ResetOnSecondSecurityRead(
        CxlSecurityModelProvider inner,
        CxlEndpointId endpoint) : ICxlSecurityStateProvider
    {
        private int reads;

        public PlatformAuthorityResult<CxlSecurityStateSnapshot> QuerySecurityState(
            CxlEndpointId endpointId, CxlDeviceGeneration expectedDeviceGeneration)
        {
            if (Interlocked.Increment(ref reads) == 2)
                Assert.True(inner.ResetSecuritySession(endpoint).IsSuccess);
            return inner.QuerySecurityState(endpointId, expectedDeviceGeneration);
        }

        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(
            CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration) =>
            inner.QuerySecurityEvidence(endpointId, expectedGeneration);
    }
    private sealed class ResetOnVisibilityAccelerator(
        ICxlType2AcceleratorProvider inner,
        CxlSecurityModelProvider security,
        CxlEndpointId endpoint) : ICxlType2AcceleratorProvider
    {
        public int VisibilityCalls { get; private set; }
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request) =>
            inner.Submit(request);
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission) =>
            inner.ObserveCompletion(submission);
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(
            CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement)
        {
            VisibilityCalls++;
            var receipt = inner.AcquireVisibility(submission, requirement);
            Assert.True(security.ResetSecuritySession(endpoint).IsSuccess);
            return receipt;
        }
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission) => inner.Cancel(submission);
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission) => inner.Release(submission);
    }
    private sealed class OrdinaryOnlyAccelerator(ICxlType2AcceleratorProvider inner) : ICxlType2AcceleratorProvider
    {
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request) =>
            inner.Submit(request);
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission) =>
            inner.ObserveCompletion(submission);
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(
            CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement) =>
            inner.AcquireVisibility(submission, requirement);
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission) => inner.Cancel(submission);
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission) => inner.Release(submission);
    }
    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(new("type2-platform"), 1, PlatformAuthorityFeatures.NeutralDomainBinding);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity s) => PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), s));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease d, PlatformDeviceIdentity i, PlatformDeviceRights r) => PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), d, i, r));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) => PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(PlatformAuthorityStatus.Unsupported, "unused");
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => PlatformAuthorityResult.Ok();
    }
}
