using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlType2AcceleratorServiceTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ActualType2SubmitRetainsEffectAfterFabricManagerSourceLossAndReclaimsOriginal(int fault)
    {
        SubmitHookAccelerator? hook = null;
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", PrivateFields)!.GetValue(s.FabricManager)!;
        object original = routes[s.Fabric.BindingId]!;
        object parent = FabricRoutes(s)[s.Fabric.BindingId]!;
        hook!.AfterSubmit = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(CxlFabricManagerAuthority).GetField("_gate", PrivateFields)!.GetValue(s.FabricManager)!));
            if (fault == 0) routes[s.Fabric.BindingId] = CloneFabric(original);
            else
            {
                PropertyInfo state = original.GetType().GetProperty("State")!;
                if (fault == 2) state.SetValue(original, CxlFabricResourceState.Draining);
                state.SetValue(original, CxlFabricResourceState.Bound);
            }
        };
        KernelResult<CxlType2Execution> submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(submitted.IsSuccess);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        Assert.NotNull(hook.LastSubmission);
        Assert.Equal(1, OperationPins(parent));
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        hook.AfterSubmit = null; routes[s.Fabric.BindingId] = original;
        KernelResult closed = s.Kernel.TerminateProcess(s.Handle);
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", PrivateFields)!.GetValue(s.FabricManager)!;
        object original = routes[s.Fabric.BindingId]!;
        object parent = FabricRoutes(s)[s.Fabric.BindingId]!;
        int actions = 0;
        KernelResult<ExternalOperationSnapshot> published = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            actions++;
            if (fault == 0) routes[s.Fabric.BindingId] = CloneFabric(original);
            else
            {
                PropertyInfo state = original.GetType().GetProperty("State")!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        _ = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        IDictionary effects = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService)
            .GetField("_effects", PrivateFields)!.GetValue(s.Service)!;
        object key = Assert.Single(effects.Keys.Cast<object>());
        object original = effects[key]!;
        object originalFabric = FabricRoutes(s)[s.Fabric.BindingId]!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        IDictionary effects = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService)
            .GetField("_effects", PrivateFields)!.GetValue(s.Service)!;
        object key = Assert.Single(effects.Keys.Cast<object>());
        object original = effects[key]!;
        object fabric = FabricRoutes(s)[s.Fabric.BindingId]!;
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
        PropertyInfo property = original.GetType().GetProperty("OriginalClosure")!;
        object fact = property.GetValue(original)!;
        Assert.NotNull(fact.GetType().GetProperty("RawReleaseObservation")!.GetValue(fact));
        hook.AfterRelease = null;
        effects[key] = original;
        if (fault == 1) property.SetValue(original, CloneFabric(fact));
        if (fault is 2 or 5) fact.GetType().GetProperty("RawReleaseObservation")!.SetValue(fact, null);
        if (fault == 3) fact.GetType().GetField("<Submission>k__BackingField", PrivateFields)!.SetValue(fact, execution.Submission with { });
        if (fault == 4) fact.GetType().GetField("<Source>k__BackingField", PrivateFields)!.SetValue(fact, CloneFabric(original));
        if (fault == 5) original.GetType().GetProperty("ProviderClosed")!.SetValue(original, true);
        KernelResult retry = s.Kernel.TerminateProcess(s.Handle);
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        IDictionary live = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService).GetField("_live", PrivateFields)!.GetValue(s.Service)!;
        live.Remove(execution.Operation.OperationId);
        object fabric = FabricRoutes(s)[s.Fabric.BindingId]!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        OperationRegionUseRequest[] originalUses = s.Plan.RequiredRegionUses.ToArray();
        OperationRegionUseRequest[] changedUses = new OperationRegionUseRequest[]
        {
            new(s.Output.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(s.Input.Handle, RegionUseMode.StagedOutput, new(0, 8))
        };
        ChangingComputeUses suppliedUses = new ChangingComputeUses(originalUses, changedUses, throwOnCapture);
        List<ComputeDependencyNode> nodes = s.Plan.Dependencies.Nodes.ToList();
        List<ComputeDependencyEdge> edges = s.Plan.Dependencies.Edges.ToList();
        ComputePlan supplied = s.Plan with { RequiredRegionUses = suppliedUses,
            Dependencies = new ComputeDependencyGraph(nodes, edges) };
        hook!.AfterSubmit = () => { nodes.Clear(); edges.Clear(); };
        ExternalOperationSnapshot[] before = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, supplied, [s.Candidate], s.Subject, s.Lease,
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
        int publications = 0;
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
        Scenario s = CreateScenario();
        ComputeDependencyGraph foreignGraph = new ComputeDependencyGraph(s.Plan.Dependencies.Nodes.Select(node =>
            new ComputeDependencyNode(node.NodeId, node.Kind == ComputeDependencyKind.StagedOutputReady
                ? ComputeDependencyKind.DirectOutputBinding : node.Kind)).ToArray(),
            s.Plan.Dependencies.Edges.ToArray());
        Assert.True(ComputePlanner.ValidateGraph(foreignGraph).IsSuccess);
        ComputePlan forged = s.Plan with { Dependencies = absentGraph ? null! : foreignGraph };
        RegionUseDescriptor[] uses = s.Kernel.Regions.SnapshotUses().ToArray();
        ExternalOperationSnapshot[] operations = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        int labels = 0;
        KernelResult<CxlType2Execution> result = protectedSubmit
            ? s.Service.SubmitProtected(s.Handle, forged, [s.Candidate], s.Subject, s.Lease, s.Endpoint,
                s.Fabric, 1, _ => { labels++; throw new InvalidOperationException("Labels must not run."); })
            : s.Service.Submit(s.Handle, forged, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Equal(0, labels);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(uses, s.Kernel.Regions.SnapshotUses().ToArray());
        Assert.Equal(operations, s.Kernel.ExternalOperations.InspectionSnapshot().ToArray());
        Assert.Equal(0, OperationPins(FabricRoutes(s)[s.Fabric.BindingId]!));
        KernelResult<CxlType2Execution> valid = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(valid.IsSuccess, valid.Message);
        int publications = 0;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        OperationRegionUseRequest[] uses = s.Plan.RequiredRegionUses.ToArray();
        ComputePlan forged = fault switch
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
        RegionUseDescriptor[] initialUses = s.Kernel.Regions.SnapshotUses().ToArray();
        ExternalOperationSnapshot[] initialOperations = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        object original = FabricRoutes(s)[s.Fabric.BindingId]!;
        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, forged, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Null(hook!.LastRequest);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(initialUses, s.Kernel.Regions.SnapshotUses().ToArray());
        Assert.Equal(initialOperations, s.Kernel.ExternalOperations.InspectionSnapshot().ToArray());
        Assert.Equal(0, OperationPins(original));
        KernelResult<CxlType2Execution> valid = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
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
        Scenario s = CreateScenario();
        ComputeIntent intent = s.Plan.Intent with
        {
            ResourceRequirement = new ResourceEnvelopeV1(1, ResourceDimensionFamilyV1.Time,
                ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1")
        };
        KernelResult<ComputePlan> planned = s.Kernel.PlanCompute(s.Handle, intent, new(true, true, true), [s.Candidate]);
        Assert.True(planned.IsSuccess, planned.Message);
        RegionUseDescriptor[] uses = s.Kernel.Regions.SnapshotUses().ToArray();
        ExternalOperationSnapshot[] operations = s.Kernel.ExternalOperations.InspectionSnapshot().ToArray();
        object original = FabricRoutes(s)[s.Fabric.BindingId]!;
        int labels = 0;
        KernelResult<CxlType2Execution> result = protectedSubmit
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
        KernelResult<CxlType2Execution> ordinary = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(ordinary.IsSuccess, ordinary.Message);
        Assert.Equal(1, s.Accelerator.SubmitCount);
        int published = 0;
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, ordinary.Value!, [s.Candidate], () => published++).IsSuccess);
        Assert.Equal(1, published);
        Assert.Equal(0, OperationPins(original));
        // Let the actual teardown consumer close FM tracking, fabric, device and Region uses in owner order.
        KernelResult teardown = s.Kernel.TerminateProcess(s.Handle);
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
        Scenario s = CreateScenario(discoveryFactory: model => discovery = new(model));
        IDictionary routes = FabricRoutes(s);
        object original = routes[s.Fabric.BindingId]!;
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
        KernelResult<CxlType2Execution> denied = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        IDictionary routes = FabricRoutes(s);
        object original = routes[s.Fabric.BindingId]!;
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
        KernelResult<CxlType2Execution> submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
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
        IDictionary effects = (global::System.Collections.IDictionary)typeof(CxlType2AcceleratorService)
            .GetField("_effects", PrivateFields)!.GetValue(s.Service)!;
        object effect = Assert.Single(effects.Values.Cast<object>());
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        IDictionary routes = FabricRoutes(s);
        object original = routes[s.Fabric.BindingId]!;
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
        int actions = 0;
        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        IDictionary routes = FabricRoutes(s);
        object original = routes[s.Fabric.BindingId]!;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook!.AfterSubmit = () =>
        {
            Assert.False(Monitor.IsEntered(BridgeGate(s)));
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        };
        Task<KernelResult<CxlType2Execution>> submitting = Task.Run(() => s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1));
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
        KernelResult<CxlType2Execution> result = await submitting.WaitAsync(TimeSpan.FromSeconds(15));
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
        object operations = fabric.GetType().GetProperty("Operations")!.GetValue(fabric)!;
        return (int)operations.GetType().GetProperty("Count")!.GetValue(operations)!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnLateSubmissionRefusalClosesNotInvokedButCannotEraseCompletedOriginal(bool completed)
    {
        CleanupClock clock = new CleanupClock();
        Scenario s = CreateScenario(clock: clock);
        Assert.True(s.Kernel.Traces.Start(s.Handle, 32, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        OperationBinding? binding = null;
        void Observe()
        {
            ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
            if (original.State != ExternalOperationState.Submitted)
            {
                clock.Next = Observe;
                return;
            }
            binding = original.Binding;
            Task worker = Task.Run(() =>
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
        KernelResult<CxlType2Execution> refused = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(refused.IsSuccess);
        Assert.NotNull(binding);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        ExternalOperationSnapshot original = s.Kernel.ExternalOperations.Query(binding.Value.Operation).Value!;
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
        CleanupClock clock = new CleanupClock();
        Scenario s = CreateScenario(clock: clock);
        Assert.True(s.Kernel.Traces.Start(s.Handle, 32, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        ExternalOperationHandle? operation = null;
        void Observe()
        {
            ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
            if (original.State != (fault == 2 ? ExternalOperationState.Admitted : ExternalOperationState.Prepared))
            {
                clock.Next = Observe;
                return;
            }
            operation = original.Operation;
            Task worker = Task.Run(() =>
            {
                if (fault == 0)
                    Assert.True(s.Kernel.CancelExternalOperation(s.Handle, original.Operation, false).IsSuccess);
                else
                {
                    OperationDependencySnapshot dependencies = new OperationDependencySnapshot(1, s.Endpoint.DeviceGeneration.Value, s.Fabric.Generation.Value);
                    if (fault != 2) Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, original.Operation, dependencies).IsSuccess);
                    KernelResult<OperationBinding> submitted = s.Kernel.RecordExternalOperationSubmission(s.Handle, original.Operation, dependencies);
                    Assert.True(submitted.IsSuccess);
                    if (fault == 3) Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Handle,
                        new(submitted.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
                }
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Trace must run outside owner locks.");
            worker.GetAwaiter().GetResult();
        }
        clock.Next = Observe;
        KernelResult<CxlType2Execution> refused = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(refused.IsSuccess);
        Assert.NotNull(operation);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        ExternalOperationSnapshot actual = s.Kernel.ExternalOperations.Query(operation.Value).Value!;
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
            Action? callback = Next;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model), fabricFactory: model => fabric = new(model));
        s.Accelerator.SubmitAcceptanceAmbiguous = outcome == 1;
        s.Accelerator.ReturnMalformedSubmission = outcome == 2;
        int calls = 0;
        hook!.AfterSubmit = () =>
        {
            calls++;
            fabric!.ThrowNextQuery = true;
            if (outcome == 3) throw new IOException("submit response lost");
        };
        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        Assert.False(fabric!.ThrowNextQuery);
        ExternalOperationHandle operation = hook.LastRequest!.OperationBinding.Operation;
        ExternalOperationSnapshot original = s.Kernel.ExternalOperations.Query(operation).Value!;
        Assert.Equal(hook.LastRequest.OperationBinding, original.Binding);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, original.Disposition);
        Assert.Single(original.Transitions, item => item.Event == "ProviderLost");
        Assert.Equal(outcome == 0, s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        ExternalOperationSnapshot retained = s.Kernel.ExternalOperations.Query(operation).Value!;
        Assert.Equal(outcome == 0, retained.State == ExternalOperationState.Released);
        if (outcome != 0)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
            foreach (RegionUseDescriptor use in original.Admission!.RegionUses)
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model),
            discoveryFactory: model => discovery = new(model));
        s.Accelerator.DeviceAvailable = fault == 3;
        s.Accelerator.SubmitAcceptanceAmbiguous = fault == 3;
        int calls = 0;
        hook!.AfterSubmit = () =>
        {
            calls++;
            s.Accelerator.DeviceAvailable = true;
            if (fault == 2) Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);
            else discovery!.NextFailure = fault == 1 ? 1 : 0;
        };
        int closureCalls = 0;
        hook.AfterCancel = () => closureCalls++;
        hook.AfterRelease = () => closureCalls++;
        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        ExternalOperationHandle operation = hook.LastRequest!.OperationBinding.Operation;
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(operation).Value!.State);
        bool outsideGate = false;
        bool reentryBlocked = false;
        if (fault == 5)
            discovery!.BeforeNextQuery = () =>
            {
                object gate = typeof(CxlType2AcceleratorService).GetField("_effectGate",
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
        KernelResult retry = s.Kernel.TerminateProcess(s.Handle);
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model),
            fabricFactory: model => fabric = new(model));
        hook!.AfterSubmit = () =>
        {
            fabric!.RefuseNextQuery = true;
            s.Accelerator.CancellationFails = fault == 1;
            s.Accelerator.ReleaseFails = fault == 2;
        };
        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        CxlAcceleratorSubmission receipt = Assert.IsType<CxlAcceleratorSubmission>(hook.LastSubmission);
        ExternalOperationSnapshot original = s.Kernel.ExternalOperations.Query(receipt.OperationBinding.Operation).Value!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model),
            discoveryFactory: model => discovery = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        int releases = 0;
        hook!.AfterRelease = () =>
        {
            releases++;
            if (fault == 2) Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);
            else discovery!.NextFailure = fault;
        };
        RegionOwner owner = new RegionOwner(s.Subject.DomainId, s.Handle.Generation);
        ICxlTeardownParticipant participant = (ICxlTeardownParticipant)s.Service;
        Assert.False(participant.CloseForProcess(s.Handle, owner).IsSuccess);
        Assert.Equal(1, releases);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, owner).IsSuccess);
        Assert.False(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate],
            () => throw new InvalidOperationException("closed provider output must not publish")).IsSuccess);
        KernelResult retry = participant.CloseForProcess(s.Handle, owner);
        Assert.Equal(fault != 2, retry.IsSuccess);
        Assert.Equal(1, releases);
        ExternalOperationSnapshot snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        ExternalOperationHandle blocker = s.Kernel.PrepareExternalOperation(s.Handle,
            [new(s.Input.Handle, RegionUseMode.ReadOnly, new(0, 1))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        Assert.True(s.FabricManager.TrackOperation(s.Fabric, s.Handle, blocker, () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("drain snapshot pause");
            return KernelResult.Ok();
        }).IsSuccess);
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        int releases = 0;
        hook!.AfterRelease = () => Interlocked.Increment(ref releases);
        Task<KernelResult<CxlFabricManagedBinding>> drain = Task.Run(() => s.FabricManager.BeginReconfiguration(s.Fabric));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            KernelResult closed = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle,
                new(s.Subject.DomainId, s.Handle.Generation));
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.Equal(ExternalOperationState.Released,
                s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!.State);
            Assert.Equal(1, Volatile.Read(ref releases));
        }
        finally { resume.Set(); }
        KernelResult<CxlFabricManagedBinding> result = await drain;
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, releases);
        Assert.True(s.FabricManager.CompleteReconfiguration(s.Fabric.BindingId).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        ExternalOperationSnapshot released = s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!;
        Assert.All(released.Admission!.RegionUses, use =>
            Assert.DoesNotContain(s.Kernel.Regions.SnapshotUses(), current =>
                current.Handle == use.Handle && current.State == RegionUseState.Active));
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public void Type2InitialPublicationAdmissionUsesFreshCapabilityBeforeAction(bool revoke, bool releaseFails)
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.ReleaseFails = releaseFails;
        int actionCalls = 0;
        s.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () =>
        {
            ExternalOperationSnapshot before = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
            Assert.Equal(ExternalOperationState.Visible, before.State);
            Assert.Equal(ExternalOperationDisposition.Completed, before.Disposition);
            if (revoke)
            {
                Thread revoking = new Thread(() => s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)));
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
        ExternalOperationSnapshot snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        hook!.AfterRelease = () => throw new IOException("published release reply lost");
        int action = 0;
        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => action++);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, action);
        ExternalOperationSnapshot published = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Published, published.State);
        Assert.DoesNotContain(published.Transitions, transition => transition.Event == "ProviderLost");
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        hook.AfterRelease = null;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        ExternalOperationSnapshot recovered = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Published, recovered.State);
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(recovered, new string('a', 64));
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Published);
        Assert.DoesNotContain(trace, item => item.Kind == SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Quarantined or SemanticTraceEventKindV1.EffectClosedWithoutPublication or SemanticTraceEventKindV1.Settled or SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2ModelReceiptExhaustionRefusesNewEffectWithoutForgettingClosure(bool identity)
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        IReadOnlyList<RegionUseDescriptor> uses = s.Kernel.ExternalOperations.Query(execution.Operation).Value!.Admission!.RegionUses;
        CxlAcceleratorRequest request = new CxlAcceleratorRequest(s.Plan.Intent.Operation, s.Plan.PublicationPath,
            execution.Binding, uses, execution.Submission.EndpointId,
            execution.Submission.DeviceGeneration, execution.Submission.FabricBinding);
        Assert.True(s.Accelerator.Release(execution.Submission).IsSuccess);
        if (identity)
            typeof(CxlType2ModelAccelerator).GetField("_nextId", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(s.Accelerator, ulong.MaxValue);
        else
            for (int i = 1; i < 4096; i++)
            {
                PlatformAuthorityResult<CxlAcceleratorSubmission> receipt = s.Accelerator.Submit(request);
                Assert.True(receipt.IsSuccess);
                Assert.True(s.Accelerator.Release(receipt.Value!).IsSuccess);
            }
        int accepted = s.Accelerator.SubmitCount;
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
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        PlatformAuthorityResult[] replies = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => s.Accelerator.Release(execution.Submission))));
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
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        CxlAcceleratorSubmission exact = execution.Submission;
        Assert.True(s.Accelerator.Release(exact).IsSuccess);
        CxlAcceleratorSubmission foreign = drift switch
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        int cancelled = 0;
        int released = 0;
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
        KernelResult closed = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
        Assert.Equal(1, cancelled);
        Assert.Equal(reset == 0 ? 1 : 0, released);
        Assert.Equal(reset == 0, closed.IsSuccess);
        ExternalOperationSnapshot snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        int cancellations = 0;
        int releases = 0;
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
        KernelResult closed = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
        Assert.Equal(KernelError.ExternalEffectUncontained, closed.Error);
        Assert.Equal(1, cancellations);
        Assert.Equal(0, releases);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)).Error);
        ExternalOperationSnapshot lost = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Single(lost.Transitions, transition => transition.Event == "ProviderLost");
        IReadOnlyList<SemanticTraceEventV1> prefix = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(lost, new string('a', 64));
        Assert.Contains(prefix, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        hook.AfterCancel = () => cancellations++;
        KernelResult retry = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        int releases = 0;
        hook!.AfterRelease = () => releases++;
        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            s.Output.Span[0] = s.Input.Span[0];
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(0, releases);
        });
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(1, releases);
        ExternalOperationSnapshot snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        int releases = 0;
        hook!.AfterRelease = () => Interlocked.Increment(ref releases);
        Task<KernelResult<ExternalOperationSnapshot>> pending = Task.Run(() => s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            foreach (object owner in new object[] { s.Service, s.Kernel.CapabilityAuthority, s.Kernel.ExternalOperations })
            {
                string name = owner == s.Service ? "_effectGate" : "_gate";
                object gate = owner.GetType().GetField(name, global::System.Reflection.BindingFlags.Instance |
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
            KernelResult<ExternalOperationSnapshot> duplicate = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("duplicate action"));
            Assert.Equal(KernelError.PlatformBindingDraining, duplicate.Error);
            KernelResult closure = ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation));
            Assert.Equal(KernelError.PlatformBindingDraining, closure.Error);
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        }
        finally { resume.Set(); }
        KernelResult<ExternalOperationSnapshot> result = await pending;
        Assert.Equal(1, releases);
        ExternalOperationSnapshot snapshot = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        int observations = 0;
        hook!.AfterCompletion = () => Interlocked.Increment(ref observations);
        hook.AfterRelease = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        Task<KernelResult> closing = Task.Run(() => ((ICxlTeardownParticipant)s.Service).CloseForProcess(s.Handle, new(s.Subject.DomainId, s.Handle.Generation)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("action after closure admission"));
            Assert.Equal(KernelError.PlatformBindingDraining, result.Error);
            Assert.Equal(0, observations);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        }
        finally { resume.Set(); }
        KernelResult closed = await closing;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        int releases = 0;
        hook!.AfterRelease = () => Interlocked.Increment(ref releases);
        Action pause = () =>
        {
            object gate = typeof(CxlType2AcceleratorService).GetField("_effectGate",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Service)!;
            Assert.False(Monitor.IsEntered(gate));
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test observation pause");
            if (lost) throw new IOException("paused observation reply lost");
        };
        if (visibility) hook.AfterVisibility = pause;
        else hook.AfterCompletion = pause;
        bool published = false;
        Task<KernelResult<ExternalOperationSnapshot>> pending = Task.Run(() => s.Service.CompleteVisiblePublish(s.Handle, execution,
            [s.Candidate], () => published = true));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            KernelResult<ExternalOperationSnapshot> duplicate = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => published = true);
            Assert.Equal(KernelError.PlatformBindingDraining, duplicate.Error);
            Assert.False(s.FabricManager.BeginReconfiguration(s.Fabric).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
            Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        }
        finally { resume.Set(); }
        KernelResult<ExternalOperationSnapshot> result = await pending;
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Action lost = () => throw fault switch
        {
            0 => new TimeoutException("observation lost"),
            1 => new OperationCanceledException("observation cancelled"),
            _ => new IOException("observation transport lost")
        };
        if (visibility) hook!.AfterVisibility = lost;
        else hook!.AfterCompletion = lost;
        bool published = false;
        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => published = true);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(published);
        ExternalOperationSnapshot snapshot = s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!;
        Assert.Contains(snapshot.Transitions, transition => transition.Event == "ProviderLost");
        Assert.NotEqual(ExternalOperationState.Released, snapshot.State);
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        s.Accelerator.ReleaseFails = true;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        s.Accelerator.ReleaseFails = false;
        KernelResult retry = s.Kernel.TerminateProcess(s.Handle);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, hook!.ReleaseCalls);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2FinalOwnerPublicationCommitUsesFreshSourceOutsideActionLocks(bool revoke)
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        bool actionRan = false;
        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () =>
        {
            foreach (object owner in new object[] { s.Kernel.CapabilityAuthority, s.Kernel.ExternalOperations })
            {
                object gate = owner.GetType().GetField("_gate", global::System.Reflection.BindingFlags.Instance |
                    global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
                Assert.False(Monitor.IsEntered(gate));
            }
            actionRan = true;
            if (revoke) Assert.True(s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)).IsSuccess);
        });
        Assert.True(actionRan);
        Assert.Equal(!revoke, result.IsSuccess);
        ExternalOperationSnapshot operation = s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!;
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
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Fact]
    public void Type2CancellationAcknowledgementCannotReleaseParentWithoutClosure()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
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
        Scenario s = CreateScenario();
        CapabilityId cap = DeviceCapability(s);
        Assert.True(s.Kernel.CapabilityAuthority.Revoke(cap).IsSuccess);
        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
    }

    [Fact]
    public async Task Type2PendingCallbackPinsParentAndRejectsConcurrentReconfiguration()
    {
        SubmitHookAccelerator? hook = null;
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        hook!.AfterSubmit = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        Task<KernelResult<CxlType2Execution>> pending = Task.Run(() => s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            object effects = typeof(CxlType2AcceleratorService).GetField("_effects",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
                .GetValue(s.Service)!;
            IEnumerable records = (global::System.Collections.IEnumerable)effects.GetType().GetProperty("Values")!.GetValue(effects)!;
            object effect = Assert.Single(records.Cast<object>());
            Assert.Equal(1, (int)effect.GetType().GetField("SubmitAdmitted")!.GetValue(effect)!);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(s.Fabric).Error);
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, s.FabricManager.BeginReconfiguration(s.Fabric).Error);
        }
        finally { resume.Set(); }
        KernelResult<CxlType2Execution> execution = await pending;
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution.Value!, [s.Candidate], () => { }).IsSuccess);
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
    }

    [Fact]
    public void Type2CallbackRevokeRetainsLateReceiptForRealTeardown()
    {
        SubmitHookAccelerator? hook = null;
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        hook!.AfterSubmit = () => Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
        KernelResult<CxlType2Execution> submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        int effects = 0;
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
        KernelResult<CxlType2Execution> submitted = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject,
            s.Lease, s.Endpoint, s.Fabric, 1);
        Assert.Equal(1, effects);
        Assert.Equal(KernelError.ExternalEffectUncontained, submitted.Error);
        hook.AfterSubmit = null;
        ExternalOperationSnapshot operation = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationDisposition.ProviderLost, operation.Disposition);
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(operation, new string('a', 64));
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind == SemanticTraceEventKindV1.EffectClosedWithoutPublication);
        Assert.DoesNotContain(trace, item => item.Kind == SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid); // Valid observations, not terminal closure.
        SemanticTraceEventV1 tail = trace[^1];
        SemanticTraceEventV1 released = tail with { Sequence = tail.Sequence + 1, Kind = SemanticTraceEventKindV1.Released };
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(released)).IsValid);
        SemanticTraceEventV1 settled = tail with { Sequence = tail.Sequence + 1, Kind = SemanticTraceEventKindV1.Settled };
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
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
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
            ExternalOperationSnapshot lost = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
            Assert.Contains(lost.Transitions, transition => transition.Event == "ProviderLost");
            IReadOnlyList<SemanticTraceEventV1> prefix = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(lost, new string('a', 64));
            Assert.Contains(prefix, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
            Assert.DoesNotContain(prefix, item => item.Kind is SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding);
            Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        }
        KernelResult retry = s.Kernel.TerminateProcess(s.Handle);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
        Assert.NotEqual(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Fact]
    public void StagedOperationTraversesCommonLifecycleBeforePublication()
    {
        Scenario s = CreateScenario();
        KernelResult<CxlType2Execution> execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.Equal(ExternalOperationState.Submitted,
            s.Kernel.QueryExternalOperation(s.Handle, execution.Value!.Operation).Value!.State);
        Assert.Equal(0, s.Output.Span[0]);

        KernelResult<ExternalOperationSnapshot> finished = s.Service.CompleteVisiblePublish(s.Handle, execution.Value, [s.Candidate],
            () => s.Input.Span.CopyTo(s.Output.Span));
        Assert.True(finished.IsSuccess, finished.Message);
        Assert.Equal(ExternalOperationState.Released, finished.Value!.State);
        Assert.Equal(s.Input.Span.ToArray(), s.Output.Span.ToArray());
    }

    [Fact]
    public void StaleRegionUseRejectsBeforeProviderSubmission()
    {
        Scenario s = CreateScenario();
        KernelResult<RegionUseDescriptor> conflicting = s.Kernel.AcquireRegionUse(s.Handle, s.Output.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(conflicting.IsSuccess);
        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void DeviceResetDuringStagedExecutionNeverPublishes()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        s.Output.Span.Fill(0);
        Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);

        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => s.Output.Span.Fill(9));
        Assert.False(result.IsSuccess);
        Assert.All(s.Output.Span.ToArray(), value => Assert.Equal(0, value));
        Assert.NotEqual(ExternalOperationDisposition.Published,
            s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!.Disposition);
    }

    [Fact]
    public void DirectModeIsFutureGatedBeforeProviderSubmission()
    {
        Scenario s = CreateScenario();
        ComputeProviderCandidate directCandidate = s.Candidate with { Capabilities = ComputeProviderCapabilities.AcceleratorExecution |
            ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess };
        KernelResult<ComputePlan> gated = s.Kernel.PlanCompute(s.Handle, s.Plan.Intent with { PublicationPreference = ComputePublicationPreference.DirectRequired },
            new(true, true, true), [directCandidate]);
        Assert.False(gated.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, gated.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void AcceleratorRoleDoesNotReplaceOrdinaryDeviceAuthority()
    {
        Scenario s = CreateScenario();
        Assert.Equal(new PlatformDeviceIdentity("device:type2-0"), s.Lease.Device);
        Assert.Contains(typeof(ICxlType2AcceleratorProvider), s.Accelerator.GetType().GetInterfaces());
        Assert.DoesNotContain(typeof(IPlatformDeviceLeaseProvider), s.Accelerator.GetType().GetInterfaces());
    }

    [Fact]
    public void PublicComputeDescriptorCannotNamePhysicalCxlTransport()
    {
        string[] names = typeof(CxlAcceleratorRequest).GetProperties().Select(property => property.Name).ToArray();
        foreach (string? forbidden in new[] { "Decoder", "Dpa", "Hpa", "Route", "Register", "Mailbox", "Opcode", "Lane", "Pasid" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VirtualizedPlanCannotUseOrdinarySubmitWithoutExactKernelContext()
    {
        Scenario s = CreateScenario();
        ComputePlan virtualized = s.Plan with { Intent = s.Plan.Intent with { RequiresVirtualizedDomain = true } };

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, virtualized, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void ForgedType2ExecutionCannotCompleteOrPublish()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        CxlType2Execution forged = execution with { Plan = execution.Plan with { ProviderGeneration = execution.Plan.ProviderGeneration + 1 } };
        bool published = false;

        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, forged, [s.Candidate], () => published = true);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.False(published);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);
    }

    [Fact]
    public void StaleAcceleratorCancelAndReleaseCannotDeleteCurrentSubmission()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        CxlAcceleratorSubmission stale = execution.Submission with { Generation = new(execution.Submission.Generation.Value + 1) };

        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Cancel(stale).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Release(stale).Status);
        Assert.True(s.Accelerator.ObserveCompletion(execution.Submission).IsSuccess);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);
    }

    [Fact]
    public void ProviderSubmitFailureLeavesNoHiddenOperationOrRegionUsePinned()
    {
        Scenario s = CreateScenario();
        int activeBefore = s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active);
        s.Accelerator.DeviceAvailable = false;

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(activeBefore, s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active));
        KernelResult<ExternalOperationSnapshot> operation = s.Kernel.ExternalOperations.Query(new(new(1), new(1)));
        Assert.True(operation.IsSuccess, operation.Message);
        Assert.Equal(ExternalOperationState.Released, operation.Value!.State);
    }

    [Fact]
    public void SecureRequiredType2RejectsBeforeProviderEffectWithoutCurrentReadiness()
    {
        Scenario s = CreateScenario();
        ComputeProviderCandidate secureCandidate = s.Candidate with { Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence };
        ComputePlan securePlan = s.Kernel.PlanCompute(s.Handle, s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [secureCandidate]).Value!;
        CxlSecurityAuthority securityAuthority = new CxlSecurityAuthority(s.Bridge, s.Security);

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, securePlan, [secureCandidate], s.Subject, s.Lease,
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
        Scenario s = CreateScenario();
        const CxlSecurityProperties properties = CxlSecurityProperties.IdeEnabled |
            CxlSecurityProperties.DeviceAuthenticated | CxlSecurityProperties.FirmwareMeasured;
        Assert.True(s.Security.Register(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, properties).IsSuccess);
        CxlSecurityStateSnapshot state = s.Security.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        TrustObligationV1 obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 37);
        ResetOnSecondSecurityRead resetOnSecondRead = new ResetOnSecondSecurityRead(s.Security, s.Endpoint.EndpointId);
        CxlSecurityAuthority securityAuthority = new CxlSecurityAuthority(s.Bridge, resetOnSecondRead);
        ComputeProviderCandidate secureCandidate = s.Candidate with
        {
            Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence
        };
        ComputePlan securePlan = s.Kernel.PlanCompute(s.Handle,
            s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [secureCandidate]).Value!;

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, securePlan, [secureCandidate], s.Subject, s.Lease,
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
        Scenario s = CreateScenario();
        const CxlSecurityProperties properties = CxlSecurityProperties.IdeEnabled |
            CxlSecurityProperties.DeviceAuthenticated | CxlSecurityProperties.FirmwareMeasured;
        Assert.True(s.Security.Register(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, properties).IsSuccess);
        CxlSecurityStateSnapshot state = s.Security.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        TrustObligationV1 obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 37);
        ComputeProviderCandidate candidate = s.Candidate with
        {
            Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence
        };
        ComputePlan plan = s.Kernel.PlanCompute(s.Handle,
            s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [candidate]).Value!;
        ResetOnVisibilityAccelerator provider = new ResetOnVisibilityAccelerator(s.Accelerator, s.Security, s.Endpoint.EndpointId);
        CxlType2AcceleratorService service = new CxlType2AcceleratorService(s.Kernel, s.Bridge, provider, s.FabricManager);
        CxlSecurityAuthority security = new CxlSecurityAuthority(s.Bridge, s.Security);
        CxlSecurityPolicy policy = new CxlSecurityPolicy(properties, state.EvidenceGeneration, RequiredForOperation: true,
            TrustObligation: obligation, CurrentPolicyGeneration: 37);
        CxlType2Execution execution = service.Submit(s.Handle, plan, [candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, securityAuthority: security, securityPolicy: policy).Value!;
        bool published = false;

        KernelResult<ExternalOperationSnapshot> result = service.CompleteVisiblePublish(s.Handle, execution, [candidate],
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
        Scenario s = CreateScenario();
        const CxlSecurityProperties properties = CxlSecurityProperties.IdeEnabled |
            CxlSecurityProperties.DeviceAuthenticated | CxlSecurityProperties.FirmwareMeasured;
        Assert.True(s.Security.Register(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, properties).IsSuccess);
        CxlSecurityStateSnapshot state = s.Security.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        TrustObligationV1 obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 37);
        ComputeProviderCandidate candidate = s.Candidate with
        {
            Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence
        };
        ComputePlan plan = s.Kernel.PlanCompute(s.Handle,
            s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [candidate]).Value!;
        CxlSecurityAuthority security = new CxlSecurityAuthority(s.Bridge, s.Security);
        CxlSecurityPolicy policy = new CxlSecurityPolicy(properties, state.EvidenceGeneration, RequiredForOperation: true,
            TrustObligation: obligation, CurrentPolicyGeneration: 37);
        CxlType2Execution execution = s.Service.Submit(s.Handle, plan, [candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, securityAuthority: security, securityPolicy: policy).Value!;
        bool actionRan = false;

        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [candidate], () =>
        {
            actionRan = true;
            Assert.True(s.Security.ResetSecuritySession(s.Endpoint.EndpointId).IsSuccess);
        });

        Assert.True(actionRan);
        Assert.False(result.IsSuccess);
        ExternalOperationSnapshot owner = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.DoesNotContain(owner.Transitions, transition => transition.Event == "Published");
        Assert.Contains(owner.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, owner.EffectBoundary);
        Assert.NotEqual(ExternalOperationState.Released, owner.State);
    }

    [Fact]
    public void ProcessTeardownClosesLiveType2SubmissionBeforeRegionReclaim()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;

        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);

        Assert.Equal(ExternalOperationState.Released,
            s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void AmbiguousType2ClosureQuarantinesProcessAndBlocksRegionReclaim()
    {
        Scenario s = CreateScenario();
        _ = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.CancellationFails = true;

        KernelResult teardown = s.Kernel.TerminateProcess(s.Handle);

        Assert.False(teardown.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, teardown.Error);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        RegionOwner owner = new RegionOwner(s.Subject.DomainId, s.Handle.Generation);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MalformedType2SubmissionKeepsOriginalLossAndPinsRegardlessOfReleaseMode(bool releaseFails)
    {
        SubmitHookAccelerator? hook = null;
        Scenario s = CreateScenario(acceleratorFactory: model => hook = new(model));
        s.Accelerator.ReturnMalformedSubmission = true;
        s.Accelerator.ReleaseFails = releaseFails;

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        ExternalOperationSnapshot operation = s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!;
        Assert.Equal(ExternalOperationState.Submitted, operation.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, operation.Disposition);
        Assert.NotEqual(operation.Binding, hook!.LastSubmission!.OperationBinding);
        Assert.Single(operation.Transitions, item => item.Event == "ProviderLost");
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(operation, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Published or SemanticTraceEventKindV1.Released);
        foreach (RegionUseDescriptor use in operation.Admission!.RegionUses)
            Assert.Contains(s.Kernel.Regions.SnapshotUses(), current =>
                current.Handle == use.Handle && current.State == RegionUseState.Invalidated);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        s.Accelerator.ReleaseFails = false;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        ExternalOperationSnapshot retained = s.Kernel.ExternalOperations.Query(operation.Operation).Value!;
        Assert.Equal(operation.Binding, retained.Binding);
        Assert.Single(retained.Transitions, item => item.Event == "ProviderLost");
        foreach (RegionUseDescriptor use in operation.Admission!.RegionUses)
            Assert.Equal(KernelError.RegionUseConflict, s.Kernel.Regions.ReleaseUse(use.Handle, operation.Principal).Error);
    }

    [Fact]
    public void FailBeforePublication_ProviderReleaseFailure_QuarantinesInsteadOfReclaim()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.ReleaseFails = true;

        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [], () => Assert.Fail("publication ran"));

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
        Scenario s = CreateScenario();
        s.Accelerator.SubmitAcceptanceAmbiguous = true;

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        ExternalOperationSnapshot original = s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!;
        Assert.Equal(ExternalOperationState.Submitted, original.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, original.Disposition);
        Assert.Single(original.Transitions, item => item.Event == "ProviderLost");
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(original, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Contains(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Published or SemanticTraceEventKindV1.Released);
        Assert.False(s.Kernel.ReleaseExternalOperation(s.Handle, original.Operation, new(false, false)).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        ExternalOperationSnapshot retained = s.Kernel.ExternalOperations.Query(original.Operation).Value!;
        Assert.Equal(original.Binding, retained.Binding);
        Assert.Single(retained.Transitions, item => item.Event == "ProviderLost");
        foreach (RegionUseDescriptor use in original.Admission!.RegionUses)
            Assert.Equal(KernelError.RegionUseConflict, s.Kernel.Regions.ReleaseUse(use.Handle, original.Principal).Error);
    }

    [Fact]
    public void ProviderExceptionAfterSubmitAcceptanceIsContainedAsUnrecoverableQuarantine()
    {
        Scenario s = CreateScenario();
        s.Accelerator.SubmitThrowsAfterAcceptance = true;

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

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
        Scenario s = CreateScenario();
        Assert.True(s.FabricManager.BeginReconfiguration(s.Fabric).IsSuccess);

        KernelResult<CxlType2Execution> result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void FabricReconfiguration_WithLiveType2Submission_ClosesProviderBeforeExternalOperationRelease()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;

        KernelResult<CxlFabricManagedBinding> reconfiguration = s.FabricManager.BeginReconfiguration(s.Fabric);

        Assert.True(reconfiguration.IsSuccess, reconfiguration.Message);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Fact]
    public void TwoBindingsSameEndpointSameGeneration_AreDistinctInAcceleratorContract()
    {
        CxlFabricBindingRef first = new CxlFabricBindingRef(new(41), new(1));
        CxlFabricBindingRef second = new CxlFabricBindingRef(new(42), new(1));

        Assert.NotEqual(first, second);
        Assert.Equal(first.Generation, second.Generation);
        Assert.Contains(nameof(CxlFabricBindingRef.BindingId), typeof(CxlFabricBindingRef).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("FabricGeneration", typeof(CxlAcceleratorRequest).GetProperties().Select(property => property.Name));

        CxlType2ModelAccelerator accelerator = new CxlType2ModelAccelerator();
        OperationBinding operation = new OperationBinding(new(new(7), new(1)), new(1), 1);
        RegionUseDescriptor[] uses = new[]
        {
            new RegionUseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), new(0, 8), RegionUseMode.ReadOnly, new(1), RegionUseState.Active),
            new RegionUseDescriptor(new(new(2), 1), new(new(2), new(1)), new(new(1), 1), new(0, 8), RegionUseMode.StagedOutput, new(1), RegionUseState.Active)
        };
        PlatformAuthorityResult<CxlAcceleratorSubmission> firstSubmission = accelerator.Submit(new(ComputeOperationKind.Copy, ComputePublicationPath.Staged,
            operation, uses, new("shared-endpoint"), new(3), first));
        PlatformAuthorityResult<CxlAcceleratorSubmission> secondSubmission = accelerator.Submit(new(ComputeOperationKind.Copy, ComputePublicationPath.Staged,
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
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.DeviceAvailable = false;

        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void VisibilityFailureClosesProviderAndReleasesRegionUses()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.VisibilitySatisfied = false;

        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void PublicationExceptionClosesProviderButQuarantinesPossiblePublicationAndRegionUses()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;

        KernelResult<ExternalOperationSnapshot> result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate],
            () => throw new InvalidOperationException("publication failed"));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        ExternalOperationSnapshot operation = s.Kernel.ExternalOperations.Query(execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Visible, operation.State);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, operation.EffectBoundary);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Fact]
    public void CompletedSubmissionIsUntrackedBeforeLaterFabricReconfiguration()
    {
        Scenario s = CreateScenario();
        CxlType2Execution execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);

        KernelResult<CxlFabricManagedBinding> reconfiguration = s.FabricManager.BeginReconfiguration(s.Fabric);

        Assert.True(reconfiguration.IsSuccess, reconfiguration.Message);
    }

    [Fact]
    public void ProtectedLabelsReachExactManagedType2ProviderSubmitBoundary()
    {
        Scenario s = CreateScenario();
        AttachProtectedLabels(s);

        KernelResult<CxlType2Execution> execution = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
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
        Scenario s = CreateScenario();
        AttachProtectedLabels(s);

        KernelResult<CxlType2Execution> result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration + 1));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void ProtectedLabelLaunderingFailsBeforeProviderEffect()
    {
        Scenario s = CreateScenario();
        AttachProtectedLabels(s);

        KernelResult<CxlType2Execution> result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation =>
            {
                ProtectedAcceleratorLabelSidebandV1[] labels = ProtectedLabels(operation, s.Plan.ProviderGeneration);
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
        Scenario s = CreateScenario();
        OrdinaryOnlyAccelerator ordinaryOnly = new OrdinaryOnlyAccelerator(s.Accelerator);
        CxlType2AcceleratorService service = new CxlType2AcceleratorService(s.Kernel, s.Bridge, ordinaryOnly, s.FabricManager);

        KernelResult<CxlType2Execution> result = service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));

        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void ProtectedLabelsNeverReplaceOrdinaryRegionAuthority()
    {
        Scenario s = CreateScenario();
        AttachProtectedLabels(s);
        Assert.True(s.Kernel.AcquireRegionUse(s.Handle, s.Output.Handle,
            RegionUseMode.ExclusiveWrite, new(0, 8)).IsSuccess);

        KernelResult<CxlType2Execution> result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Empty(s.Accelerator.LastProtectedSideband);
    }

    [Fact]
    public void ProtectedSidebandWithoutLiveRegionLabelsCannotReachProvider()
    {
        Scenario s = CreateScenario();
        KernelResult<CxlType2Execution> result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void StaleProtectedInputLabelGenerationCannotReachProvider()
    {
        Scenario s = CreateScenario();
        AttachProtectedLabels(s);
        KernelResult<CxlType2Execution> result = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation =>
            {
                ProtectedAcceleratorLabelSidebandV1[] labels = ProtectedLabels(operation, s.Plan.ProviderGeneration);
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
        Scenario s = CreateScenario();
        AttachProtectedLabels(s);
        KernelResult<CxlType2Execution> execution = s.Service.SubmitProtected(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, operation => ProtectedLabels(operation, s.Plan.ProviderGeneration));
        Assert.True(execution.IsSuccess, execution.Message);
        SingProcess process = s.Kernel.Processes.Resolve(s.Handle).Value!;
        ProtectedRegionLabelDescriptorV1 current = s.Kernel.Regions.QueryProtectedLabel(s.Input.Handle,
            new(process.DomainId, s.Handle.Generation)).Value!;
        CapabilityId capability = s.Kernel.MintCapability(process.DomainId, s.Handle, ResourceKind.MemoryRegion,
            CapabilityResourceIds.InformationFlowTransition(s.Input.Handle.RegionId),
            CapabilityRights.Configure, resourceGeneration: s.Input.Handle.Generation.Value).Value!.CapabilityId;

        KernelResult<ProtectedRegionLabelDescriptorV1> transition = s.Kernel.TransitionV6ProtectedRegionLabel(s.Handle, current,
            new(1, InformationFlowTransitionKindV1.Declassify, current.Label,
                new(1, ConfidentialityClassV1.Public, IntegrityClassV1.Validated)), capability);

        Assert.Equal(KernelError.RegionUseConflict, transition.Error);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution.Value!, [s.Candidate], () => { }).IsSuccess);
    }

    private static void AttachProtectedLabels(Scenario scenario)
    {
        DataLabelV1 label = new DataLabelV1(1, ConfidentialityClassV1.Protected, IntegrityClassV1.Validated);
        Assert.True(scenario.Kernel.AttachV6ProtectedRegionLabel(scenario.Handle, scenario.Input.Handle, label).IsSuccess);
        Assert.True(scenario.Kernel.AttachV6ProtectedRegionLabel(scenario.Handle, scenario.Output.Handle, label).IsSuccess);
    }

    private static ProtectedAcceleratorLabelSidebandV1[] ProtectedLabels(
        ExternalOperationHandle operation, ulong providerGeneration)
    {
        string operationId = $"external-operation:{operation.OperationId.Value}";
        DataLabelV1 label = new DataLabelV1(1, ConfidentialityClassV1.Protected, IntegrityClassV1.Validated);
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
        AuthorityProvider platform = new AuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(platform, clock);
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 971, 972);
        SingProcess process = kernel.Processes.Resolve(handle).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, handle.Generation);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, handle);
        PlatformDomainBinding domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        CapabilityDescriptorV1 deviceCap = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, "device:type2-0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease lease = kernel.BindPlatformDevice(handle, domain, deviceCap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        CxlType3ModelProvider memory = new CxlType3ModelProvider();
        Assert.True(memory.RegisterEndpoint(new("type2-0"), lease.Device, 1024).IsSuccess);
        CxlEndpointSnapshot endpoint = memory.QueryEndpoint(new("type2-0")).Value!;
        CxlSecurityModelProvider security = new CxlSecurityModelProvider();
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(kernel, discoveryFactory?.Invoke(memory) ?? memory, memory, memory, memory,
            new CoherentStub(), security);
        OwnedBuffer<byte> control = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        RegionUseDescriptor controlUse = kernel.AcquireRegionUse(handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 16)).Value!;
        CxlFabricBinding fabric = bridge.BindFabric(owner, controlUse.Handle, subject, lease,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(handle, 8).Value!;
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(handle, 8).Value!;
        for (int i = 0; i < input.Length; i++) input.Span[i] = (byte)(i + 1);
        ComputePublicationPreference preference = direct ? ComputePublicationPreference.DirectRequired : ComputePublicationPreference.StagedRequired;
        ComputeProviderCapabilities capabilities = ComputeProviderCapabilities.AcceleratorExecution |
            (direct ? ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess : ComputeProviderCapabilities.StagedPublication);
        ComputeProviderCandidate candidate = new ComputeProviderCandidate(new("type2-model"), 1, capabilities, 1024, 1, 1, true, false);
        ComputePlan plan = kernel.PlanCompute(handle, new(ComputeOperationKind.Copy,
                new(input.Handle, new(0, 8)), new(output.Handle, new(0, 8)), preference, false, false),
            new(true, true, true), [candidate]).Value!;
        CxlType2ModelAccelerator accelerator = new CxlType2ModelAccelerator();
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(kernel, fabricFactory?.Invoke(memory) ?? memory);
        Assert.True(manager.Register(fabric).IsSuccess);
        CxlType2AcceleratorService service = new CxlType2AcceleratorService(kernel, bridge, acceleratorFactory?.Invoke(accelerator) ?? accelerator, manager);
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
            Action? before = BeforeNextQuery;
            BeforeNextQuery = null;
            before?.Invoke();
            int? failure = NextFailure;
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
            PlatformAuthorityResult<CxlAcceleratorSubmission> result = inner.Submit(request);
            if (result.IsSuccess) LastSubmission = result.Value;
            AfterSubmit?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission)
        {
            PlatformAuthorityResult<CxlAcceleratorCompletion> result = inner.ObserveCompletion(submission);
            AfterCompletion?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement)
        {
            PlatformAuthorityResult<CxlAcceleratorVisibility> result = inner.AcquireVisibility(submission, requirement);
            AfterVisibility?.Invoke();
            return result;
        }
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission)
        {
            PlatformAuthorityResult result = inner.Cancel(submission);
            AfterCancel?.Invoke();
            return result;
        }
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission)
        {
            ReleaseCalls++;
            PlatformAuthorityResult result = inner.Release(submission);
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
            PlatformAuthorityResult<CxlAcceleratorVisibility> receipt = inner.AcquireVisibility(submission, requirement);
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
