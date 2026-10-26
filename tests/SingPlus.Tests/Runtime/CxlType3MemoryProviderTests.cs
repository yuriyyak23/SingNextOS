using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlType3MemoryProviderTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void OriginalType3UnknownClosureCannotRepeatOrReclaimAttachedCharge(bool fabricReply, bool lostReply)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        BudgetAccountHandle budget = default;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model),
            beforeAllocation: (kernel, handle) => budget = AttachOriginalType3Budget(kernel, handle));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        if (fabricReply)
        {
            if (lostReply) fabric!.UnbindHook = () => throw new IOException("original fabric outcome lost");
            else s.Model.FabricUnbindFails = true;
        }
        else
        {
            if (lostReply) memory!.ReleaseHook = () => throw new IOException("original memory outcome lost");
            else s.Model.MemoryReleaseFails = true;
        }
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory!.ReleaseCalls);
        Assert.Equal(fabricReply ? 1 : 0, fabric!.UnbindCalls);
        memory.ReleaseHook = null; fabric.UnbindHook = null;
        s.Model.MemoryReleaseFails = false; s.Model.FabricUnbindFails = false;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(fabricReply ? 1 : 0, fabric.UnbindCalls);
        Assert.Equal(64UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == s.Buffer.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OriginalType3PlacementCannotTransferClosureOrHideBackingThroughRouteProjection(int fault)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        BudgetAccountHandle budget = default;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model),
            beforeAllocation: (kernel, handle) => budget = AttachOriginalType3Budget(kernel, handle));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlType3MemoryAuthority).GetField("_placements", flags)!.GetValue(s.Memory)!;
        object original = routes[placement.PlacementId]!;
        PropertyInfo snapshot = original.GetType().GetProperty("Snapshot")!;
        if (fault == 0) snapshot.SetValue(original, placement with { State = CxlMemoryPlacementState.Released });
        if (fault == 1) routes[placement.PlacementId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (fault == 2) routes.Remove(placement.PlacementId);
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.False(((ICxlTeardownParticipant)s.Memory).CloseForProcess(s.Handle, s.Owner).IsSuccess);
        Assert.Equal(0, memory!.ReleaseCalls);
        Assert.Equal(64UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        routes[placement.PlacementId] = original; snapshot.SetValue(original, placement);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls); Assert.Equal(1, fabric!.UnbindCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == s.Buffer.Handle).State);
        Assert.Equal(0UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void OriginalType3KnownClosureConsumesLocalFaultAndChargeWithoutRepeatedProviderEffects(int fault)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        BudgetAccountHandle budget = default;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model),
            beforeAllocation: (kernel, handle) => budget = AttachOriginalType3Budget(kernel, handle));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlType3MemoryAuthority).GetField("_placements", flags)!.GetValue(s.Memory)!;
        object record = routes[placement.PlacementId]!;
        PropertyInfo snapshot = record.GetType().GetProperty("Snapshot")!;
        if (fault == 4) record.GetType().GetField("<SourceRevision>k__BackingField", flags)!.SetValue(record, ulong.MaxValue);
        s.Memory.BeforePlacementClosureLocalCommit = () =>
        {
            foreach (object target in new object[] { s.Memory, s.Bridge })
                Assert.False(Monitor.IsEntered(target.GetType().GetField("_gate", flags)!.GetValue(target)!));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Memory.Close(placement.PlacementId).Error);
            if (fault == 0) throw new IOException("local backing consumption lost");
            CxlMemoryPlacementSnapshot original = (CxlMemoryPlacementSnapshot)snapshot.GetValue(record)!;
            if (fault == 1) snapshot.SetValue(record, original);
            if (fault == 2) { snapshot.SetValue(record, original with { State = CxlMemoryPlacementState.Active }); snapshot.SetValue(record, original); }
            if (fault == 3) routes[placement.PlacementId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(record, null)!;
        };
        Assert.Equal(fault == 4, s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory!.ReleaseCalls); Assert.Equal(1, fabric!.UnbindCalls);
        Assert.Equal(64UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False((bool)record.GetType().GetProperty("CloseInFlight")!.GetValue(record)!);
        s.Memory.BeforePlacementClosureLocalCommit = null;
        routes[placement.PlacementId] = record;
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        // Completed original lifetime cannot be re-pinned by a route/state projection.
        snapshot.SetValue(record, placement);
        routes.Remove(placement.PlacementId);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls); Assert.Equal(1, fabric.UnbindCalls);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == s.Buffer.Handle).State);
        Assert.Equal(0UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalType3HealthRejectsEqualOrAbaLocalSourceBeforeActualRegionDamage(bool aba)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId, ProviderHealthStateV1.Degraded,
            ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlType3MemoryAuthority).GetField("_placements", flags)!.GetValue(s.Memory)!;
        object record = routes[placement.PlacementId]!; PropertyInfo snapshot = record.GetType().GetProperty("Snapshot")!;
        s.Memory.BeforeHealthConsequenceCommit = () =>
        {
            CxlMemoryPlacementSnapshot original = (CxlMemoryPlacementSnapshot)snapshot.GetValue(record)!;
            if (aba) snapshot.SetValue(record, original with { State = CxlMemoryPlacementState.MigrationRequired });
            snapshot.SetValue(record, original);
        };
        Assert.False(s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model).IsSuccess);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        s.Memory.BeforeHealthConsequenceCommit = null;
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void OriginalType3PartialConsumptionCannotUseCopiedOrLostBackingClosureFact(bool fabricFact, int fault)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        BudgetAccountHandle budget = default;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model),
            beforeAllocation: (kernel, handle) => budget = AttachOriginalType3Budget(kernel, handle));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        s.Memory.BeforePlacementClosureLocalCommit = () => throw new IOException("local consumption deferred");
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlType3MemoryAuthority).GetField("_placements", flags)!.GetValue(s.Memory)!;
        object record = routes[placement.PlacementId]!;
        object sources = record.GetType().GetProperty("CleanupSources")!.GetValue(record)!;
        object source = sources.GetType().GetProperty(fabricFact ? "Fabric" : "Memory")!.GetValue(sources)!;
        PropertyInfo closure = source.GetType().GetProperty("Closure")!;
        object fact = closure.GetValue(source)!;
        if (fault == 0) closure.SetValue(source, typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(fact, null));
        if (fault == 1) fact.GetType().GetProperty("RawObservation")!.SetValue(fact, null);
        if (fault == 2) fact.GetType().GetField("<Source>k__BackingField", flags)!.SetValue(fact,
            typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(source, null));
        s.Memory.BeforePlacementClosureLocalCommit = null;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, memory!.ReleaseCalls); Assert.Equal(1, fabric!.UnbindCalls);
        Assert.Equal(64UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == s.Buffer.Handle).State);
    }

    private static BudgetAccountHandle AttachOriginalType3Budget(RuntimeKernel kernel, ProcessHandle handle)
    {
        ProcessHandle admin = TestFixtures.Create(kernel, 95, 995).Handle;
        CapabilityId cap = kernel.MintCapability(new(995), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        return kernel.AdmitProcessBudget(admin, cap, handle, "original-type3-cleanup",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public void OriginalMemoryCleanupRefusesRouteLossAndRetainsKnownRelease(bool memoryRoute, bool foreign, bool afterRelease)
    {
        CallbackMemoryProvider? provider = null;
        Scenario s = CreateScenario(memoryFactory: model => provider = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding memory = s.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : memory.FabricBinding.BindingId;
        object original = routes[key]!;
        IDictionary memories = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedMemory", flags)!.GetValue(s.Bridge)!;
        object originalMemory = memories[memory.BindingId]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
        }
        if (afterRelease) provider!.AfterReleaseHook = Lose;
        else provider!.QueryHook = Lose;
        Assert.False(s.Bridge.ReleaseMemory(memory).IsSuccess);
        Assert.Equal(afterRelease ? 1 : 0, provider.ReleaseCalls);
        Assert.Equal(afterRelease, originalMemory.GetType().GetProperty("ReleasedBinding")!.GetValue(originalMemory) is not null);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
        Assert.Equal(afterRelease ? 1 : 0, provider.ReleaseCalls);
        provider.QueryHook = null;
        provider.AfterReleaseHook = null;
        // Exact original owner restoration only arranges the cleanup positive.
        lock (gate) routes[key] = original;
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, provider.ReleaseCalls);
    }

    [Theory]
    [InlineData(false, false, 0)] [InlineData(false, true, 0)]
    [InlineData(true, false, 0)] [InlineData(true, true, 0)]
    [InlineData(false, false, 1)] [InlineData(false, true, 1)]
    [InlineData(true, false, 1)] [InlineData(true, true, 1)]
    [InlineData(false, false, 2)] [InlineData(false, true, 2)]
    [InlineData(true, false, 2)] [InlineData(true, true, 2)]
    public void HealthConsequenceRequiresOriginalFabricAndMemoryOwner(bool memoryRoute, bool foreign, int lossStage)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding liveMemory = s.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        RegionUseDescriptor use = s.Kernel.AcquireRegionUse(s.Handle, s.Buffer.Handle, RegionUseMode.ReadOnly, new(8, 8)).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : liveMemory.FabricBinding.BindingId;
        object original = routes[key]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        bool lost = false;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            lost = true;
        }
        int queries = 0;
        CallbackHealthProvider provider = new CallbackHealthProvider(s.Model, () =>
        {
            queries++;
            if (lossStage == 1) Lose();
        });
        if (lossStage == 0) Lose();
        if (lossStage == 2) s.Memory.BeforeHealthConsequenceCommit = Lose;
        KernelResult<RegionDamageDescriptorV1> result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, provider);
        Assert.True(lost);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(lossStage == 0 ? 0 : 1, queries);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.True(s.Kernel.ValidateRegionUse(s.Handle, use.Handle).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Active, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.False(s.Kernel.ReleaseRegion(s.Handle, s.Buffer).IsSuccess);
        Assert.False(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
        Assert.Equal(liveMemory, s.Model.QueryMemory(liveMemory.BindingId).Value!);
        s.Memory.BeforeHealthConsequenceCommit = null;
        // Original owner restoration is a test arrangement, not durable recovery.
        lock (gate) routes[key] = original;
        Assert.True(s.Kernel.ReleaseRegionUse(s.Handle, use.Handle).IsSuccess);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task ConcurrentHealthReplyCannotCommitDamageThroughForeignOriginalRoute(bool memoryRoute, bool foreign)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding memory = s.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : memory.FabricBinding.BindingId;
        object original = routes[key]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CallbackHealthProvider provider = new CallbackHealthProvider(s.Model, () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        });
        Task<KernelResult<RegionDamageDescriptorV1>> observing = Task.Run(() => s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, provider));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Empty(s.Kernel.Regions.SnapshotDamage());
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
        }
        finally { resume.Set(); }
        KernelResult<RegionDamageDescriptorV1> result = await observing.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
        Assert.Equal(memory, s.Model.QueryMemory(memory.BindingId).Value!);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void HotRemoveCapacityFaultRetainsUnavailableAndOriginalPinsAcrossActualConsumers(int fault)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding original = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        Type type = typeof(CxlType3ModelProvider);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        if (fault == 0)
        {
            IDictionary endpoints = (global::System.Collections.IDictionary)type.GetField("_endpoints", flags)!.GetValue(s.Model)!;
            object endpoint = endpoints[s.Endpoint.EndpointId]!;
            endpoint.GetType().GetProperty("DeviceGeneration")!.SetValue(endpoint, ulong.MaxValue);
        }
        if (fault == 1)
        {
            IDictionary fabric = (global::System.Collections.IDictionary)type.GetField("_fabric", flags)!.GetValue(s.Model)!;
            object old = fabric[original.FabricBinding.BindingId]!;
            fabric[original.FabricBinding.BindingId] = Activator.CreateInstance(old.GetType(),
                original.FabricBinding with { Generation = new(ulong.MaxValue) }, 64L)!;
        }
        if (fault == 2)
        {
            IDictionary memory = (global::System.Collections.IDictionary)type.GetField("_memory", flags)!.GetValue(s.Model)!;
            memory[original.BindingId] = Activator.CreateInstance(memory[original.BindingId]!.GetType(),
                original with { Generation = new(ulong.MaxValue) })!;
        }
        CxlEndpointSnapshot beforeEndpoint = s.Model.QueryEndpoint(s.Endpoint.EndpointId).Value!;
        CxlFabricResourceSnapshot beforeFabric = s.Model.QueryResource(original.FabricBinding.BindingId).Value!;
        CxlMemoryBinding beforeMemory = s.Model.QueryMemory(original.BindingId).Value!;
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(s.Kernel, s.Model);
        Assert.True(manager.Register(beforeFabric.Binding).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Faulted, s.Model.HotRemove(s.Endpoint.EndpointId).Status);
        CxlEndpointSnapshot endpointAfter = s.Model.QueryEndpoint(s.Endpoint.EndpointId).Value!;
        Assert.False(endpointAfter.Available);
        Assert.Equal(beforeEndpoint.DeviceGeneration, endpointAfter.DeviceGeneration);
        Assert.Equal(beforeFabric with { State = CxlFabricResourceState.Faulted }, s.Model.QueryResource(original.FabricBinding.BindingId).Value!);
        Assert.Equal(beforeMemory, s.Model.QueryMemory(original.BindingId).Value!);
        Assert.False(manager.ValidateAdmission(beforeFabric.Binding).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, s.Model.Rebind(s.Endpoint.EndpointId).Status);
        Assert.False(s.Model.QueryEndpoint(s.Endpoint.EndpointId).Value!.Available);
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
            Assert.Equal(beforeMemory, s.Model.QueryMemory(original.BindingId).Value!);
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        }
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RebindCapacityRefusalPreservesAffectedSourceAndActualCleanupPins(int fault)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding original = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        Type type = typeof(CxlType3ModelProvider);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        if (fault == 0)
        {
            IDictionary endpoints = (global::System.Collections.IDictionary)type.GetField("_endpoints", flags)!.GetValue(s.Model)!;
            object endpoint = endpoints[s.Endpoint.EndpointId]!;
            endpoint.GetType().GetProperty("DeviceGeneration")!.SetValue(endpoint, ulong.MaxValue);
        }
        if (fault == 1)
        {
            IDictionary fabric = (global::System.Collections.IDictionary)type.GetField("_fabric", flags)!.GetValue(s.Model)!;
            object old = fabric[original.FabricBinding.BindingId]!;
            fabric[original.FabricBinding.BindingId] = Activator.CreateInstance(old.GetType(),
                original.FabricBinding with { Generation = new(ulong.MaxValue) }, 64L)!;
        }
        if (fault == 2)
        {
            IDictionary memory = (global::System.Collections.IDictionary)type.GetField("_memory", flags)!.GetValue(s.Model)!;
            memory[original.BindingId] = Activator.CreateInstance(memory[original.BindingId]!.GetType(),
                original with { Generation = new(ulong.MaxValue) })!;
        }
        CxlMemoryCapacitySnapshot beforeCapacity = s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!;
        CxlFabricResourceSnapshot beforeFabric = s.Model.QueryResource(original.FabricBinding.BindingId).Value!;
        CxlMemoryBinding beforeMemory = s.Model.QueryMemory(original.BindingId).Value!;
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, s.Model.Rebind(s.Endpoint.EndpointId).Status);
        Assert.Equal(beforeCapacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
        Assert.Equal(beforeFabric, s.Model.QueryResource(original.FabricBinding.BindingId).Value!);
        Assert.Equal(beforeMemory, s.Model.QueryMemory(original.BindingId).Value!);
        Assert.Equal(placement, s.Memory.Query(placement.PlacementId).Value!);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        KernelResult close = s.Memory.Close(placement.PlacementId);
        Assert.Equal(fault != 0, close.IsSuccess);
        if (fault == 0)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
            Assert.Equal(beforeMemory, s.Model.QueryMemory(original.BindingId).Value!);
        }
        else Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void HealthSequenceCapacityPreservesActualRegionConsequencesAndOriginalClosure(int fault)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding original = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        RegionUseDescriptor use = s.Kernel.AcquireRegionUse(s.Handle, s.Buffer.Handle, RegionUseMode.ReadOnly, new(8, 8)).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        FieldInfo counter = typeof(CxlType3ModelProvider).GetField("_nextHealthObservation",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        counter.SetValue(s.Model, fault == 0 ? 0UL : fault == 1 ? ulong.MaxValue : ulong.MaxValue - 1);
        KernelResult<RegionDamageDescriptorV1> consequence = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model);
        if (fault == 2)
        {
            Assert.True(consequence.IsSuccess, consequence.Message);
            Assert.Equal(ulong.MaxValue - 1, consequence.Value!.EvidenceObservationSequence);
            Assert.Single(s.Kernel.Regions.SnapshotDamage());
            Assert.Equal(KernelError.StaleGeneration, s.Kernel.ValidateRegionUse(s.Handle, use.Handle).Error);
            Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        }
        else
        {
            Assert.Equal(KernelError.PlatformUnavailable, consequence.Error);
            Assert.Empty(s.Kernel.Regions.SnapshotDamage());
            Assert.True(s.Kernel.ValidateRegionUse(s.Handle, use.Handle).IsSuccess);
            Assert.Equal(placement, s.Memory.Query(placement.PlacementId).Value!);
        }
        CxlType3HealthObservationRequest request = new CxlType3HealthObservationRequest(original.FabricBinding.EndpointId,
            original.FabricBinding.DeviceGeneration, original.FabricBinding.BindingId,
            original.FabricBinding.Generation, original.BindingId, original.Generation);
        Assert.Equal(PlatformAuthorityStatus.Unavailable, s.Model.QueryHealth(request).Status);
        Assert.Equal(fault == 0 ? 0UL : ulong.MaxValue, (ulong)counter.GetValue(s.Model)!);
        Assert.Equal(original, s.Model.QueryMemory(original.BindingId).Value!);
        Assert.Equal(fault == 2 ? 1 : 0, s.Kernel.Regions.SnapshotDamage().Count);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        KernelResult teardown = s.Kernel.TerminateProcess(s.Handle);
        Assert.Equal(fault != 2, teardown.IsSuccess);
        if (fault == 2)
        {
            Assert.Equal(KernelError.Quarantined, teardown.Error);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
        }
        Assert.Equal(fault == 2 ? 1 : 0, s.Kernel.Regions.SnapshotDamage().Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateDrainingPlacementCompensationClosesExactPendingReconfigurationOnlyAfterUnbind(bool failClosure)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model));
        CxlMemoryCapacitySnapshot initialCapacity = s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!;
        CxlFabricReconfigurationTicket? ticket = null;
        memory!.BeforeBindHook = binding =>
        {
            PlatformAuthorityResult<CxlFabricReconfigurationTicket> started = s.Model.BeginReconfiguration(binding);
            Assert.True(started.IsSuccess, started.Message);
            ticket = started.Value!;
        };
        s.Model.FabricUnbindFails = failClosure;
        KernelResult<CxlMemoryPlacementSnapshot> refusal = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(failClosure ? KernelError.ExternalEffectUncontained : KernelError.PlatformUnavailable, refusal.Error);
        Assert.NotNull(ticket);
        Assert.Equal(1, fabric!.UnbindCalls);
        Assert.Equal(0, memory.ReleaseCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        Type type = typeof(CxlType3ModelProvider);
        int Tickets() => ((global::System.Collections.IDictionary)type.GetField("_reconfigurations", flags)!.GetValue(s.Model)!).Count;
        int Draining() => ((HashSet<CxlFabricBindingId>)type.GetField("_draining", flags)!.GetValue(s.Model)!).Count;
        Assert.Equal(failClosure ? 1 : 0, Tickets());
        Assert.Equal(failClosure ? 1 : 0, Draining());
        Assert.Equal(1UL, (ulong)type.GetField("_nextMemory", flags)!.GetValue(s.Model)!);
        memory.BeforeBindHook = null;
        if (failClosure)
        {
            Assert.Equal(CxlFabricResourceState.Draining, s.Model.QueryResource(ticket!.PreviousBinding.BindingId).Value!.State);
            Assert.NotEqual(initialCapacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
            s.Model.FabricUnbindFails = false;
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(1, fabric.UnbindCalls);
            Assert.Equal(1, Tickets()); Assert.Equal(1, Draining());
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
            return;
        }
        else
        {
            Assert.Equal(initialCapacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
            KernelResult<CxlMemoryPlacementSnapshot> retry = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
                s.Endpoint, CxlMemoryPersistence.Volatile);
            Assert.True(retry.IsSuccess, retry.Message);
            Assert.True(s.Memory.Close(retry.Value!.PlacementId).IsSuccess);
            Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        }
        Assert.Equal(0, Tickets());
        Assert.Equal(0, Draining());
        Assert.Equal(initialCapacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Model.CompleteReconfiguration(ticket!).Status);
        Assert.False(s.Model.Query(ticket!.PreviousBinding.BindingId).IsSuccess);
        Assert.Equal(initialCapacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DrainingFabricRefusesActualBridgeChildWithoutLosingOriginalCleanup(bool lateDrain)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding original = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        CxlFabricReconfigurationTicket? ticket = null;
        CxlMemoryBinding? drainingMemory = null;
        object bridgeGate = typeof(CxlAuthorityBridge).GetField("_gate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Bridge)!;
        void Drain(CxlFabricBinding binding)
        {
            Assert.False(Monitor.IsEntered(bridgeGate));
            PlatformAuthorityResult<CxlFabricReconfigurationTicket> started = s.Model.BeginReconfiguration(binding);
            Assert.True(started.IsSuccess, started.Message);
            ticket = started.Value!;
            drainingMemory = s.Model.QueryMemory(original.BindingId).Value!;
        }
        if (lateDrain) memory!.BeforeBindHook = Drain;
        else Drain(original.FabricBinding);
        CxlMemoryCapacitySnapshot capacity = s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!;
        KernelResult<CxlMemoryBinding> refusal = s.Bridge.BindMemory(s.Owner, original.BackingLease, original.FabricBinding);
        Assert.Equal(KernelError.PlatformUnavailable, refusal.Error);
        Assert.NotNull(ticket);
        Assert.Equal(CxlFabricResourceState.Draining, s.Model.QueryResource(original.FabricBinding.BindingId).Value!.State);
        Assert.Equal(drainingMemory, s.Model.QueryMemory(original.BindingId).Value!);
        Assert.Equal(capacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(0, memory!.ReleaseCalls);
        Type type = typeof(CxlType3ModelProvider);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        Assert.Equal(2UL, (ulong)type.GetField("_nextMemory", flags)!.GetValue(s.Model)!);
        Assert.Equal(1, ((global::System.Collections.IDictionary)type.GetField("_memory", flags)!.GetValue(s.Model)!).Count);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        memory.BeforeBindHook = null;
        Assert.True(s.Model.CompleteReconfiguration(ticket!).IsSuccess);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(s.Kernel, 961, 962);
        Assert.True(s.Kernel.TransferRegion(s.Handle, target, s.Buffer).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void FabricIdentityCapacityRefusalPreservesCapacityAndActualPlacementOwnership(int fault)
    {
        Scenario s = CreateScenario();
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        Type type = typeof(CxlType3ModelProvider);
        FieldInfo counter = type.GetField("_nextFabric", flags)!;
        counter.SetValue(s.Model, fault == 0 ? 0UL : fault == 1 ? ulong.MaxValue : ulong.MaxValue - 1);
        CxlMemoryCapacitySnapshot capacity = s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!;
        if (fault == 2)
        {
            KernelResult<CxlMemoryPlacementSnapshot> final = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
            Assert.True(final.IsSuccess, final.Message);
            Assert.Equal(ulong.MaxValue - 1, s.Model.QueryMemory(final.Value!.MemoryBindingId).Value!.FabricBinding.BindingId.Value);
            Assert.NotEqual(capacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
            Assert.True(s.Memory.Close(final.Value.PlacementId).IsSuccess);
        }
        KernelResult<CxlMemoryPlacementSnapshot> refusal = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformUnavailable, refusal.Error);
        Assert.Equal(fault == 0 ? 0UL : ulong.MaxValue, (ulong)counter.GetValue(s.Model)!);
        Assert.Equal(capacity, s.Model.QueryCapacity(s.Endpoint.EndpointId).Value!);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(0, ((global::System.Collections.IDictionary)type.GetField("_memory", flags)!.GetValue(s.Model)!).Count);
        Assert.Equal(0, ((global::System.Collections.IDictionary)type.GetField("_fabric", flags)!.GetValue(s.Model)!).Count);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(s.Kernel, 959, 960);
        Assert.True(s.Kernel.TransferRegion(s.Handle, target, s.Buffer).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MemoryIdentityCapacityRefusalCompensatesActualPlacementWithoutHiddenPins(int fault)
    {
        Scenario s = CreateScenario();
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        Type type = typeof(CxlType3ModelProvider);
        FieldInfo counter = type.GetField("_nextMemory", flags)!;
        counter.SetValue(s.Model, fault == 0 ? 0UL : fault == 1 ? ulong.MaxValue : ulong.MaxValue - 1);
        if (fault == 2)
        {
            KernelResult<CxlMemoryPlacementSnapshot> final = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
            Assert.True(final.IsSuccess, final.Message);
            Assert.Equal(ulong.MaxValue - 1, final.Value!.MemoryBindingId.Value);
            Assert.True(s.Memory.Close(final.Value.PlacementId).IsSuccess);
        }
        KernelResult<CxlMemoryPlacementSnapshot> refusal = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformUnavailable, refusal.Error);
        Assert.Equal(fault == 0 ? 0UL : ulong.MaxValue, (ulong)counter.GetValue(s.Model)!);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(0, ((global::System.Collections.IDictionary)type.GetField("_memory", flags)!.GetValue(s.Model)!).Count);
        Assert.Equal(0, ((global::System.Collections.IDictionary)type.GetField("_fabric", flags)!.GetValue(s.Model)!).Count);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(s.Kernel, 959, 960);
        Assert.True(s.Kernel.TransferRegion(s.Handle, target, s.Buffer).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }
    [Theory]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    public void ProcessCxlStagePreservesExactReleaseAndParticipantInventory(bool memoryStage, int fault)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        LateClosureDiscovery? discovery = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model),
            discoveryFactory: model => discovery = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        object gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        bool callbacksOutside = true;
        bool callbackObserved = false;
        bool observedPending = false;
        memory!.QueryHook = () => callbacksOutside &= !Monitor.IsEntered(gate);
        fabric!.QueryHook = () => callbacksOutside &= !Monitor.IsEntered(gate);
        Action late = () =>
        {
            callbackObserved = true;
            callbacksOutside &= !Monitor.IsEntered(gate);
            KernelResult<ProcessTeardownSnapshot> observation = s.Kernel.ObserveProcessTeardown(s.Handle);
            observedPending = observation.IsSuccess && !observation.Value!.LocalReclaimCompleted;
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.TerminateProcess(s.Handle).Error);
            Assert.False(s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
                s.Endpoint, CxlMemoryPersistence.Volatile).IsSuccess);
            if (fault == 0) discovery!.NextFailure = 1;
            if (fault == 1) Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
            if (fault == 2) throw new IOException("Release took effect but reply was lost");
            if (fault == 3) _ = new CxlType3MemoryAuthority(s.Kernel, s.Bridge);
        };
        if (memoryStage) memory.AfterReleaseHook = late;
        else fabric.AfterUnbindHook = late;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(callbackObserved);
        Assert.True(callbacksOutside);
        Assert.True(observedPending);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.True(s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
        memory.AfterReleaseHook = null;
        fabric.AfterUnbindHook = null;
        KernelResult retry = s.Kernel.TerminateProcess(s.Handle);
        Assert.Equal(fault is 0 or 3, retry.IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(fault is 0 or 3 ? 1 : memoryStage ? 0 : 1, fabric.UnbindCalls);
        Assert.Equal(fault is 1 or 2, s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
        if (fault is 1 or 2)
            Assert.False(s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).IsSuccess);
    }
    [Fact]
    public async Task PausedProcessCxlReleaseAllowsConcurrentObservationWithoutReclaim()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        Assert.True(s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).IsSuccess);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        bool released = false;
        memory!.ReleaseHook = () => { entered.Set(); released = release.Wait(TimeSpan.FromSeconds(5)); };
        Task<KernelResult> close = Task.Run(() => s.Kernel.TerminateProcess(s.Handle));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.TerminateProcess(s.Handle).Error);
            Assert.False(s.Kernel.ObserveProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value!.LocalReclaimCompleted);
            Assert.False(s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
                s.Endpoint, CxlMemoryPersistence.Volatile).IsSuccess);
            Assert.Equal(0, s.Platform.DeviceRevokeCalls);
            Assert.Equal(1, memory.ReleaseCalls);
        }
        finally { release.Set(); }
        Assert.True((await close).IsSuccess);
        Assert.True(released);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(1, memory.BindCalls);
        Assert.False(s.Kernel.Processes.Resolve(s.Handle).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void FinalPlacementPublicationUsesExactSourceAndRetainsKnownReceiptOnRefusal(int fault)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryBinding? received = null;
        bool mutationSucceeded = fault == 3;
        s.Memory.AfterPlacementReceipt = receipt =>
        {
            received = receipt;
            if (fault == 1)
            {
                CxlFabricReconfigurationTicket ticket = s.Model.BeginReconfiguration(receipt.FabricBinding).Value!;
                mutationSucceeded = s.Model.CompleteReconfiguration(ticket).IsSuccess;
            }
            if (fault == 2) mutationSucceeded = s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess;
            if (fault == 3) memory!.QueryHook = () => throw new IOException("final memory observation lost");
        };
        s.Memory.BeforePlacementPublication = () =>
        {
            if (fault == 0) mutationSucceeded = s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)).IsSuccess;
        };
        KernelResult<CxlMemoryPlacementSnapshot> result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(mutationSucceeded, "Fault injection must succeed independently of the runtime exception boundary.");
        Assert.NotNull(received);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        CxlMemoryPlacementSnapshot placement = s.Memory.Query(new CxlMemoryPlacementId(1)).Value!;
        Assert.Equal(received!.BindingId, placement.MemoryBindingId);
        Assert.Equal(received.Generation, placement.BackingGeneration);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, placement.State);
        Assert.Equal(0, memory!.ReleaseCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        memory.QueryHook = null;
        s.Memory.AfterPlacementReceipt = null;
        s.Memory.BeforePlacementPublication = null;
        Assert.Equal(fault != 2, s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(fault == 2 ? CxlMemoryPlacementState.Quarantined : CxlMemoryPlacementState.Released,
            s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(fault == 2 ? 0 : 1, memory.ReleaseCalls);
        if (fault != 2)
            Assert.True(s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).IsSuccess);
        else
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
    }
    [Theory]
    [InlineData(true, 0, false)] [InlineData(true, 1, false)] [InlineData(true, 2, false)]
    [InlineData(false, 0, false)] [InlineData(false, 1, false)] [InlineData(false, 2, false)]
    [InlineData(true, 0, true)] [InlineData(true, 1, true)] [InlineData(true, 2, true)]
    [InlineData(false, 0, true)] [InlineData(false, 1, true)] [InlineData(false, 2, true)]
    public void KnownReleaseReplyRetainsExactReconfiguredTupleUntilSourceAndLocalCleanup(bool memoryStage, int fault, bool teardown)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        LateClosureDiscovery? discovery = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model),
            discoveryFactory: model => discovery = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding admitted = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        CxlFabricReconfigurationTicket ticket = s.Model.BeginReconfiguration(admitted.FabricBinding).Value!;
        PlatformAuthorityResult<CxlFabricBinding> replacement = s.Model.CompleteReconfiguration(ticket);
        Assert.True(replacement.IsSuccess);
        CxlMemoryBinding exact = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        Assert.NotEqual(admitted.Generation, exact.Generation);
        Action lateFailure = () =>
        {
            if (fault == 2) Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
            else discovery!.NextFailure = fault;
        };
        if (memoryStage) memory!.AfterReleaseHook = lateFailure;
        else fabric!.AfterUnbindHook = lateFailure;
        Assert.False(teardown ? s.Kernel.TerminateProcess(s.Handle).IsSuccess : s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory!.ReleaseCalls);
        Assert.Equal(memoryStage ? 0 : 1, fabric!.UnbindCalls);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        if (memoryStage)
            Assert.Equal(KernelError.StaleGeneration, s.Bridge.ReleaseMemory(exact with { Generation = admitted.Generation }).Error);
        else
            Assert.Equal(KernelError.StaleGeneration, s.Bridge.ReleaseFabric(admitted.FabricBinding).Error);
        memory.AfterReleaseHook = null;
        fabric.AfterUnbindHook = null;
        bool retrySucceeded = teardown
            ? s.Kernel.ObserveProcessTeardown(s.Handle).Value!.LocalReclaimCompleted
            : s.Memory.Close(placement.PlacementId).IsSuccess;
        Assert.Equal(fault != 2, retrySucceeded);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(fault == 2 && memoryStage ? 0 : 1, fabric.UnbindCalls);
        Assert.Equal(fault == 2 ? CxlMemoryPlacementState.Quarantined : CxlMemoryPlacementState.Released,
            s.Memory.Query(placement.PlacementId).Value!.State);
        if (fault == 2)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
            Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
            Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        }
        else if (!teardown)
        {
            KernelResult<RegionBackingLeaseDescriptor> backing = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner);
            Assert.True(backing.IsSuccess, backing.Message);
            Assert.True(s.Kernel.Regions.ReleaseBacking(backing.Value!.Handle, s.Owner).IsSuccess);
            Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
        }
    }

    [Fact]
    public async Task MemoryReleaseCallbackInterlockExcludesDuplicateReleaseAndParentClosure()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding exact = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        memory!.ReleaseHook = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("memory release pause");
        };
        Task<KernelResult> close = Task.Run(() => s.Memory.Close(placement.PlacementId));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Bridge.ReleaseMemory(exact).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, s.Memory.Close(placement.PlacementId).Error);
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(exact.FabricBinding).Error);
            Assert.Equal(1, memory.ReleaseCalls);
        }
        finally { resume.Set(); }
        Assert.True((await close).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void HealthConsequenceRequiresExactLocalSourceAtCommit(int fault)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding memory = s.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        bool locked = false;
        s.Memory.BeforeHealthConsequenceCommit = () =>
        {
            foreach ((object? target, string? name) in new[] { ((object)s.Memory, "_gate"), ((object)s.Bridge, "_gate"),
                ((object)s.Kernel, "_platformMemoryUseGate"), ((object)s.Kernel.CapabilityAuthority, "_gate") })
                locked |= Monitor.IsEntered(target.GetType().GetField(name, global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!);
            if (fault == 1) Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
            if (fault == 2) Assert.True(s.Kernel.Regions.BeginBackingClosure(memory.BackingLease, s.Owner).IsSuccess);
            if (fault == 3) Assert.True(s.Bridge.ReleaseMemory(memory).IsSuccess);
        };
        KernelResult<RegionDamageDescriptorV1> result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model);
        Assert.False(locked);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(fault == 0 ? 1 : 0, s.Kernel.Regions.SnapshotDamage().Count);
        Assert.Equal(fault == 0 ? CxlMemoryPlacementState.Quarantined : fault == 1 ? CxlMemoryPlacementState.Released : CxlMemoryPlacementState.Active,
            s.Memory.Query(placement.PlacementId).Value!.State);
        s.Memory.BeforeHealthConsequenceCommit = null;
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        if (fault == 0)
        {
            Assert.Equal(KernelError.Quarantined, s.Kernel.TerminateProcess(s.Handle).Error);
            Assert.Single(s.Kernel.Regions.SnapshotDamage());
            Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        }
        else Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Fact]
    public void CxlLiveReceiptsHoldExactDeviceUntilAllPlacementsClose()
    {
        Scenario s = CreateScenario();
        OwnedBuffer<byte> other = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        CxlMemoryPlacementSnapshot first = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryPlacementSnapshot second = s.Memory.Place(s.Owner, other.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.True(s.Memory.Close(first.PlacementId).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.True(s.Memory.Close(second.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
        Assert.Equal(1, s.Platform.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CxlCreationCallbackCannotCloseParentBeforeLateReceipt(bool memoryStage, bool capabilityRevoke)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model));
        CapabilityId capability = DeviceCapability(s);
        KernelResult closure = KernelResult.Ok();
        Action callback = () => closure = capabilityRevoke ? s.Kernel.RevokeCapability(capability)
            : s.Kernel.RevokePlatformDevice(s.Handle, s.Lease);
        if (memoryStage) memory!.BindHook = callback;
        else fabric!.BindHook = callback;
        KernelResult<CxlMemoryPlacementSnapshot> result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformBindingActive, closure.Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(!capabilityRevoke, result.IsSuccess);
        if (capabilityRevoke) Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        KernelResult exited = s.Kernel.TerminateProcess(s.Handle);
        Assert.True(exited.IsSuccess, $"{exited.Error}: {exited.Message}; {s.Kernel.QueryProcessTeardown(s.Handle).Value}");
        Assert.Equal(KernelError.InvalidRegionState, s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(1, s.Platform.DeviceRevokeCalls);
    }

    [Fact]
    public async Task CxlPendingReceiptBlocksConcurrentParentDeviceClosure()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        memory!.BindHook = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        Task<KernelResult<CxlMemoryPlacementSnapshot>> pending = Task.Run(() => s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
            Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        }
        finally { resume.Set(); }
        KernelResult<CxlMemoryPlacementSnapshot> result = await pending;
        Assert.True(result.IsSuccess, result.Message);
        Assert.True(s.Memory.Close(result.Value!.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Fact]
    public void CxlFreshDeviceCapabilityAdmissionRejectsDirectOwnerRevocation()
    {
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(fabricFactory: model => fabric = new(model));
        Assert.True(s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)).IsSuccess);
        KernelResult<CxlMemoryPlacementSnapshot> result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, fabric!.BindCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Fact]
    public void CxlRevokedSourceForbidsNewMemoryEffectButAllowsExactClosureRetry()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding binding = s.Model.QueryMemory(placed.MemoryBindingId).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
        Assert.Equal(KernelError.CapabilityRevoked, s.Bridge.BindMemory(s.Owner, binding.BackingLease, binding.FabricBinding).Error);
        Assert.Equal(1, memory!.BindCalls);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.True(s.Memory.Close(placed.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokeCapability(DeviceCapability(s)).IsSuccess);
        Assert.Equal(1, s.Platform.DeviceRevokeCalls);
    }

    [Fact]
    public void CxlPendingChildBlocksFabricReleaseUntilReceiptTracking()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        KernelResult release = KernelResult.Ok();
        memory!.BindHook = () =>
        {
            RegionBackingLeaseDescriptor backing = s.Kernel.Regions.InspectionSnapshot().Single(item => item.Region.Handle == s.Buffer.Handle).BackingLease!;
            CxlMemoryBinding binding = s.Model.QueryMemory(new(1)).Value!;
            Assert.Equal(backing.Handle, binding.BackingLease);
            release = s.Bridge.ReleaseFabric(binding.FabricBinding);
        };
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformBindingActive, release.Error);
        Assert.True(placed.IsSuccess, placed.Message);
        Assert.True(s.Memory.Close(placed.Value!.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    private static CapabilityId DeviceCapability(Scenario s) => s.Kernel.CapabilityAuthority.InspectionSnapshot()
        .Single(item => item.Descriptor.ResourceKind == ResourceKind.Device && item.Descriptor.ResourceId == s.Lease.Device.ResourceId)
        .Descriptor.CapabilityId;

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    public void MemoryAdmissionAndFinalPublicationUseCapturedOriginalAdmittedParent(bool foreign, int stage)
    {
        CallbackFabricProvider? fabric = null;
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(fabricFactory: model => fabric = new(model), memoryFactory: model => memory = new(model));
        RegionBackingLeaseDescriptor backing = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Value!;
        CxlFabricBindingRequest request = new CxlFabricBindingRequest(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive);
        CxlFabricBinding bound = s.Bridge.BindFabricForBacking(s.Owner, backing.Handle, s.Subject, s.Lease, request).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = fabrics[bound.BindingId]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        object? replacement = null;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign)
                {
                    replacement = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                    replacement.GetType().GetProperty("PendingChildren")!.SetValue(replacement, 17);
                    fabrics[bound.BindingId] = replacement;
                }
                else fabrics.Remove(bound.BindingId);
            }
        }
        if (stage == 0) Lose();
        if (stage == 1) fabric!.QueryHook = Lose;
        if (stage == 2) memory!.BindHook = Lose;
        if (stage == 3) memory!.QueryHook = Lose;
        if (stage == 4)
        {
            int queries = 0;
            fabric!.QueryHook = () => { if (++queries == 2) Lose(); };
        }
        KernelResult<CxlMemoryBinding> result = s.Bridge.BindMemory(s.Owner, backing.Handle, bound);
        fabric!.QueryHook = null; memory!.BindHook = null; memory.QueryHook = null;
        KernelError expected = stage == 0 && !foreign ? KernelError.PlatformBindingNotFound
            : stage < 2 ? KernelError.StaleGeneration : KernelError.ExternalEffectUncontained;
        Assert.Equal(expected, result.Error);
        Assert.Equal(stage < 2 ? 0 : 1, memory.BindCalls);
        Assert.Equal(0, memory.ReleaseCalls);
        Assert.Equal(0, fabric.UnbindCalls);
        Assert.Equal(0, original.GetType().GetProperty("PendingChildren")!.GetValue(original));
        if (foreign) Assert.Equal(17, replacement!.GetType().GetProperty("PendingChildren")!.GetValue(replacement));
        IEnumerable facts = (global::System.Collections.IEnumerable)original.GetType().GetProperty("MemoryInvocations")!.GetValue(original)!;
        if (stage < 2) Assert.Empty(facts.Cast<object>());
        else
        {
            object fact = Assert.Single(facts.Cast<object>());
            Assert.Same(original, fact.GetType().GetProperty("Parent")!.GetValue(fact));
            RegionBackingLeaseDescriptor exactBacking = Assert.IsType<RegionBackingLeaseDescriptor>(fact.GetType().GetProperty("Backing")!.GetValue(fact));
            Assert.Equal(backing, exactBacking);
            Assert.Equal(memory.ReturnedReceipt, Assert.IsType<PlatformAuthorityResult<CxlMemoryBinding>>(fact.GetType().GetProperty("Outcome")!.GetValue(fact)).Value);
            Assert.True(s.Model.QueryMemory(memory.OriginalReceipt!.BindingId).IsSuccess);
            Assert.False(s.Bridge.ReleaseMemory(memory.ReturnedReceipt!).IsSuccess);
        }
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.False(s.Bridge.ReleaseFabric(bound).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(stage < 2 ? 0 : 1, memory.BindCalls);
        Assert.Equal(0, memory.ReleaseCalls);
        Assert.Equal(0, fabric.UnbindCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ActualPlacementRetainsOriginalParentAndRefusesMemoryPublicationAfterRouteLoss(bool foreign, bool late)
    {
        CallbackFabricProvider? fabric = null;
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(fabricFactory: model => fabric = new(model), memoryFactory: model => memory = new(model));
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object? original = null;
        object? replacement = null;
        void Lose()
        {
            CxlFabricBinding bound = fabric!.ReturnedReceipt!;
            object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign)
                {
                    replacement = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                    replacement.GetType().GetProperty("PendingChildren")!.SetValue(replacement, 17);
                    fabrics[bound.BindingId] = replacement;
                }
                else fabrics.Remove(bound.BindingId);
            }
        }
        int queries = 0;
        fabric!.QueryHook = () =>
        {
            if (++queries == 1) original = fabrics[fabric.ReturnedReceipt!.BindingId]!;
            if (queries == 2 && !late) Lose();
        };
        if (late) memory!.BindHook = Lose;
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        fabric.QueryHook = null; memory!.BindHook = null;
        Assert.Equal(KernelError.ExternalEffectUncontained, placed.Error);
        Assert.Equal(late ? 1 : 0, memory.BindCalls);
        Assert.Equal(0, original!.GetType().GetProperty("PendingChildren")!.GetValue(original));
        if (foreign) Assert.Equal(17, replacement!.GetType().GetProperty("PendingChildren")!.GetValue(replacement));
        IEnumerable facts = (global::System.Collections.IEnumerable)original.GetType().GetProperty("MemoryInvocations")!.GetValue(original)!;
        if (late) Assert.Same(original, Assert.Single(facts.Cast<object>()).GetType().GetProperty("Parent")!.GetValue(Assert.Single(facts.Cast<object>())));
        else Assert.Empty(facts.Cast<object>());
        Assert.Equal(0, memory.ReleaseCalls);
        Assert.Equal(0, fabric.UnbindCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(0, memory.ReleaseCalls);
        Assert.Equal(0, fabric.UnbindCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KnownMemoryReplyAfterOriginalPublicationRefusalStillSupportsExactCleanup(bool foreign)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        RegionBackingLeaseDescriptor backing = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Value!;
        CxlFabricBinding bound = s.Bridge.BindFabricForBacking(s.Owner, backing.Handle, s.Subject, s.Lease,
            new(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, 64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = fabrics[bound.BindingId]!;
        memory!.BindHook = () =>
        {
            if (foreign) fabrics[bound.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else fabrics.Remove(bound.BindingId);
        };
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.BindMemory(s.Owner, backing.Handle, bound).Error);
        Assert.Equal(1, memory.BindCalls);
        memory.BindHook = null;
        fabrics[bound.BindingId] = original; // test arrangement, not durable recovery or renewed use permission
        Assert.True(s.Bridge.ReleaseMemory(memory.ReturnedReceipt!).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.True(s.Bridge.ReleaseFabric(bound).IsSuccess);
        Assert.True(s.Kernel.Regions.ReleaseBacking(backing.Handle, s.Owner).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ConcurrentMemoryEffectAndParentLossBalanceOriginalAdmissionWithoutAnotherCallback(int loss)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        RegionBackingLeaseDescriptor backing = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Value!;
        CxlFabricBinding bound = s.Bridge.BindFabricForBacking(s.Owner, backing.Handle, s.Subject, s.Lease,
            new(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, 64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = fabrics[bound.BindingId]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory!.BeforeBindHook = _ =>
        {
            Assert.False(Monitor.IsEntered(gate));
            entered.TrySetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        };
        Task<KernelResult<CxlMemoryBinding>> binding = Task.Run(() => s.Bridge.BindMemory(s.Owner, backing.Handle, bound));
        object? replacement = null;
        KernelResult<CxlMemoryBinding> result = default;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, original.GetType().GetProperty("PendingChildren")!.GetValue(original));
            Assert.Equal(KernelError.PlatformBindingActive, s.Bridge.ReleaseFabric(bound).Error);
            if (loss != 0)
                lock (gate)
                {
                    if (loss == 2)
                    {
                        replacement = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                        replacement.GetType().GetProperty("PendingChildren")!.SetValue(replacement, 17);
                        fabrics[bound.BindingId] = replacement;
                    }
                    else fabrics.Remove(bound.BindingId);
                }
        }
        finally { resume.Set(); result = await binding.WaitAsync(TimeSpan.FromSeconds(15)); }
        Assert.Equal(1, memory.BindCalls);
        Assert.Equal(0, original.GetType().GetProperty("PendingChildren")!.GetValue(original));
        if (replacement is not null) Assert.Equal(17, replacement.GetType().GetProperty("PendingChildren")!.GetValue(replacement));
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        if (loss == 0)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(s.Bridge.ReleaseMemory(result.Value!).IsSuccess);
            Assert.True(s.Bridge.ReleaseFabric(bound).IsSuccess);
            Assert.Equal(1, memory.ReleaseCalls);
        }
        else
        {
            Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
            Assert.True(s.Model.QueryMemory(memory.ReturnedReceipt!.BindingId).IsSuccess);
            Assert.False(s.Bridge.BindMemory(s.Owner, backing.Handle, bound).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
            Assert.Equal(0, memory.ReleaseCalls);
        }
        Assert.Equal(1, memory.BindCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedMemoryReturnedObjectCleanupCannotReleaseOriginalPlacementInvocation(int fault)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        memory!.ReturnedObjectCleanupSucceeds = true;
        memory.RewriteReceipt = receipt => fault switch
        {
            0 => receipt with { FabricBinding = receipt.FabricBinding with { BindingId = new(101) } },
            1 => receipt with { BackingLease = receipt.BackingLease with { Generation = receipt.BackingLease.Generation + 1 } },
            2 => receipt with { BindingId = new(0) },
            3 => receipt,
            _ => receipt with { BindingId = new(101) }
        };
        if (fault == 3) memory.QueryHook = () => { memory.QueryHook = null; throw new IOException("original memory query lost"); };
        if (fault == 5) memory.BindHook = () => throw new IOException("original memory bind reply lost");
        memory.ReleaseHook = () =>
        {
            object gate = typeof(CxlAuthorityBridge).GetField("_gate", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Bridge)!;
            Assert.False(Monitor.IsEntered(gate));
            Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseMemory(memory.ReturnedReceipt!).Error);
            Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseFabric(memory.OriginalReceipt!.FabricBinding).Error);
            if (fault == 4) throw new IOException("returned memory cleanup reply lost");
        };
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.ExternalEffectUncontained, placed.Error);
        Assert.Equal(1, memory.BindCalls);
        Assert.Equal(fault is 3 or 5 ? 0 : 1, memory.ReleaseCalls);
        CxlMemoryBinding original = s.Model.QueryMemory(memory.OriginalReceipt!.BindingId).Value!;
        Assert.Equal(memory.OriginalReceipt, original); // successful returned cleanup leaves original provider effect live
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Bridge)!;
        object parent = fabrics[original.FabricBinding.BindingId]!;
        IEnumerable facts = (global::System.Collections.IEnumerable)parent.GetType().GetProperty("MemoryInvocations")!.GetValue(parent)!;
        object fact = Assert.Single(facts.Cast<object>());
        Assert.Same(parent, fact.GetType().GetProperty("Parent")!.GetValue(fact));
        RegionBackingLeaseDescriptor backing = Assert.IsType<RegionBackingLeaseDescriptor>(fact.GetType().GetProperty("Backing")!.GetValue(fact));
        Assert.Equal(original.BackingLease, backing.Handle);
        Assert.Equal(s.Buffer.Handle, backing.Region);
        Assert.Equal(false, fact.GetType().GetProperty("ReceiptValidated")!.GetValue(fact));
        object? outcome = fact.GetType().GetProperty("Outcome")!.GetValue(fact);
        if (fault == 5) Assert.Null(outcome);
        else Assert.Equal(memory.ReturnedReceipt, Assert.IsType<PlatformAuthorityResult<CxlMemoryBinding>>(outcome).Value);
        object? compensation = fact.GetType().GetProperty("ReturnedObjectCompensation")!.GetValue(fact);
        if (fault is 3 or 4 or 5) Assert.Null(compensation);
        else Assert.True(Assert.IsType<PlatformAuthorityResult>(compensation).IsSuccess);
        if (fault != 5)
            for (int retry = 0; retry < 2; retry++)
                Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseMemory(memory.ReturnedReceipt!).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseFabric(original.FabricBinding).Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(fault is 3 or 5 ? 0 : 1, memory.ReleaseCalls);
        Assert.Single(facts.Cast<object>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualPlacementRefusesMemoryEffectAfterFabricPublicationLosesOriginalOwner(bool foreign)
    {
        CallbackFabricProvider? fabric = null;
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(fabricFactory: model => fabric = new(model), memoryFactory: model => memory = new(model));
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object? original = null;
        object? replacement = null;
        fabric!.QueryHook = () =>
        {
            fabric.QueryHook = null;
            original = fabrics[fabric.ReturnedReceipt!.BindingId]!;
            if (foreign)
            {
                replacement = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                fabrics[fabric.ReturnedReceipt.BindingId] = replacement;
            }
            else fabrics.Remove(fabric.ReturnedReceipt.BindingId);
        };
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.ExternalEffectUncontained, placed.Error);
        Assert.Equal(1, fabric.BindCalls);
        Assert.Equal(0, memory!.BindCalls);
        Assert.Equal(0, fabric.UnbindCalls);
        Assert.Equal(false, original!.GetType().GetProperty("Admitted")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("CreationInFlight")!.GetValue(original));
        if (replacement is not null)
            Assert.Equal(true, replacement.GetType().GetProperty("CreationInFlight")!.GetValue(replacement));
        Assert.True(s.Model.Query(fabric.OriginalReceipt!.BindingId).IsSuccess);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(0, memory.BindCalls);
        Assert.Equal(0, fabric.UnbindCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualPlacementCloseKeepsBackingAfterKnownFabricClosureLosesOriginalOwner(bool foreign)
    {
        CallbackFabricProvider? fabric = null;
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(fabricFactory: model => fabric = new(model), memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlFabricBinding originalBinding = s.Model.QueryMemory(placement.MemoryBindingId).Value!.FabricBinding;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = fabrics[originalBinding.BindingId]!;
        fabric!.AfterUnbindHook = () =>
        {
            if (foreign) fabrics[originalBinding.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else fabrics.Remove(originalBinding.BindingId);
        };
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(originalBinding, original.GetType().GetProperty("ReleasedBinding")!.GetValue(original));
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory!.ReleaseCalls);
        Assert.Equal(1, fabric.UnbindCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(1, fabric.UnbindCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ExactMemoryClosureRetainsOriginalFabricAttemptAcrossParentLoss(bool foreign, bool late)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding receipt = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary fabrics = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = fabrics[receipt.FabricBinding.BindingId]!;
        IEnumerable facts = (global::System.Collections.IEnumerable)original.GetType().GetProperty("MemoryInvocations")!.GetValue(original)!;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!));
            if (foreign) fabrics[receipt.FabricBinding.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else fabrics.Remove(receipt.FabricBinding.BindingId);
        }
        if (late) memory!.AfterReleaseHook = Lose;
        else Lose();
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Single(facts.Cast<object>());
        memory!.AfterReleaseHook = null;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(late ? 1 : 0, memory.ReleaseCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        fabrics[receipt.FabricBinding.BindingId] = original; // test restoration, not durable routing qualification
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Empty(facts.Cast<object>());
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Fact]
    public void CxlMalformedChildReceiptCannotUnpinOriginalDeviceOrFabric()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        memory!.RewriteReceipt = binding => binding with { FabricBinding = binding.FabricBinding with { BindingId = new(binding.FabricBinding.BindingId.Value + 100) } };
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.ExternalEffectUncontained, placed.Error);
        CxlMemoryBinding actual = s.Model.QueryMemory(new(1)).Value!;
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseFabric(actual.FabricBinding).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CxlResetBeforeOrDuringClosureCannotReleaseParentReservation(bool duringClosure)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        if (duringClosure) memory!.AfterReleaseHook = () => Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        else Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        KernelResult close = s.Memory.Close(placed.PlacementId);
        Assert.Equal(duringClosure ? KernelError.ExternalEffectUncontained : KernelError.StaleGeneration, close.Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CxlStaleParentGenerationOrSubjectHasNoEffectOrRetainedReservation(bool wrongSubject)
    {
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(fabricFactory: model => fabric = new(model));
        PlatformDeviceLease lease = wrongSubject ? s.Lease with
        {
            DomainBinding = s.Lease.DomainBinding with { Subject = s.Subject with { Process = s.Handle with { Generation = s.Handle.Generation + 1 } } }
        } : s.Lease with { Generation = new(s.Lease.Generation.Value + 1) };
        KernelResult<CxlMemoryPlacementSnapshot> result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(wrongSubject ? KernelError.WrongPlatformDomain : KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, fabric!.BindCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        KernelResult<RegionBackingLeaseDescriptor> retry = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner);
        Assert.True(retry.IsSuccess);
        Assert.True(s.Kernel.Regions.ReleaseBacking(retry.Value!.Handle, s.Owner).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NotAcceptedBackingCreationRequiresExactFreshTuple(bool memoryStage, bool reset)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        Action reject = () =>
        {
            if (reset) Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        };
        if (memoryStage) memory!.RejectBind = reject;
        else fabric!.RejectBind = reject;
        KernelResult<CxlMemoryPlacementSnapshot> result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(reset ? KernelError.ExternalEffectUncontained : KernelError.PlatformUnavailable, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        if (reset)
        {
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        }
        else
        {
            KernelResult<RegionBackingLeaseDescriptor> retry = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner);
            Assert.True(retry.IsSuccess);
            Assert.True(s.Kernel.Regions.ReleaseBacking(retry.Value!.Handle, s.Owner).IsSuccess);
            Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        }
    }

    [Fact]
    public void NotAcceptedMemoryCreationWithFailedTupleObservationRetainsBacking()
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        memory!.RejectBind = () => fabric!.QueryHook = () => throw new InvalidOperationException("tuple observation lost");
        KernelResult<CxlMemoryPlacementSnapshot> result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        fabric!.QueryHook = null;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
    }

    [Fact]
    public async Task PendingMemoryCreationBlocksConcurrentProcessReclaimUntilReceiptTracking()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        memory!.BindHook = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        Task<KernelResult<CxlMemoryPlacementSnapshot>> creation = Task.Run(() => s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.TerminateProcess(s.Handle).Error);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.ExternalEffectUncontained, (await creation).Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.True(s.Kernel.ObserveProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetDuringBackingCreationBalancesAdmissionWithoutInventingClosure(bool memoryStage)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        bool reset = false;
        Action callback = () => reset = s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess;
        if (memoryStage) memory!.BindHook = callback;
        else fabric!.BindHook = callback;
        KernelResult<CxlMemoryPlacementSnapshot> result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(reset);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessExitInsideBackingCreationRetainsLateReceiptForActualTeardown(bool memoryStage)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        KernelResult exit = KernelResult.Ok();
        Action callback = () => exit = s.Kernel.TerminateProcess(s.Handle);
        if (memoryStage) memory!.BindHook = callback;
        else fabric!.BindHook = callback;
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformBindingDraining, exit.Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, placed.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        KernelResult<ProcessTeardownSnapshot> retried = s.Kernel.ObserveProcessTeardown(s.Handle);
        Assert.True(retried.IsSuccess);
        Assert.True(retried.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void ClosureAdmissionInsideMemoryCreationCannotLosePendingReceipt()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        KernelResult closure = KernelResult.Ok();
        memory!.BindHook = () =>
        {
            RegionBackingLeaseDescriptor lease = s.Kernel.Regions.InspectionSnapshot().Single(item => item.Region.Handle == s.Buffer.Handle).BackingLease!;
            closure = s.Kernel.Regions.BeginBackingClosure(lease.Handle, s.Owner);
        };
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);
        Assert.Equal(KernelError.PlatformBindingDraining, closure.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.True(s.Memory.Close(placed.Value!.PlacementId).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackingClosureBlocksNewMappingAndBackingEffectsUntilExactRelease(bool fails)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding binding = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        memory!.ReleaseHook = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        s.Model.MemoryReleaseFails = fails;
        Task<KernelResult> close = Task.Run(() => s.Memory.Close(placement.PlacementId));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability, s.Buffer.Handle,
                    PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Error);
            Assert.False(s.Kernel.Regions.HasPlatformMappingReservation(s.Buffer.Handle, s.Owner));
            Assert.Equal(KernelError.PlatformBindingDraining,
                s.Bridge.BindMemory(s.Owner, binding.BackingLease, binding.FabricBinding).Error);
        }
        finally { resume.Set(); }
        Assert.Equal(!fails, (await close).IsSuccess);
        memory.ReleaseHook = null;
        if (fails)
        {
            Assert.Equal(KernelError.PlatformBindingDraining,
                s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability, s.Buffer.Handle,
                    PlatformMemoryAccess.Read).Error);
            s.Model.MemoryReleaseFails = false;
            Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
            Assert.Equal(1, memory.ReleaseCalls);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            return;
        }
        KernelResult<PlatformRegionMapping> mapping = s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability,
            s.Buffer.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongOwnerOrStaleBackingClosureAdmissionCannotPartiallyDenyMapping(bool stale)
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding binding = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        RegionBackingLeaseHandle handle = stale ? binding.BackingLease with { Generation = binding.BackingLease.Generation + 1 } : binding.BackingLease;
        RegionOwner owner = stale ? s.Owner : new RegionOwner(new(9999), s.Handle.Generation);
        Assert.False(s.Kernel.Regions.BeginBackingClosure(handle, owner).IsSuccess);
        Assert.True(s.Kernel.Regions.ValidateBacking(binding.BackingLease, s.Owner).IsSuccess);
        Assert.True(s.Kernel.Regions.ReservePlatformMapping(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Memory.Close(placement.PlacementId).Error);
        Assert.True(s.Kernel.Regions.ReleasePlatformMappingReservation(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MemoryClosureSurvivesFabricFailureForDirectAndTeardownRetry(bool throws, bool teardown)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        if (throws) fabric!.UnbindHook = () => throw new InvalidOperationException("fabric closure failure");
        else s.Model.FabricUnbindFails = true;
        Assert.False(teardown ? s.Kernel.TerminateProcess(s.Handle).IsSuccess : s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(1, memory!.ReleaseCalls);
        Assert.Equal(1, fabric!.UnbindCalls);
        fabric.UnbindHook = null;
        s.Model.FabricUnbindFails = false;
        if (teardown)
        {
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
            KernelResult<ProcessTeardownSnapshot> retried = s.Kernel.ObserveProcessTeardown(s.Handle);
            Assert.True(retried.IsSuccess);
            Assert.False(retried.Value.LocalReclaimCompleted);
        }
        else Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(1, fabric.UnbindCalls);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostSuccessfulClosureReplyNeverBecomesClosureFromNotFound(bool memoryReply)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Action lost = () => throw new InvalidOperationException("successful provider closure reply lost");
        if (memoryReply) memory!.AfterReleaseHook = lost;
        else fabric!.AfterUnbindHook = lost;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        memory!.AfterReleaseHook = null;
        fabric!.AfterUnbindHook = null;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(memoryReply ? 0 : 1, fabric.UnbindCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedClosureBalancesInterlockAndRetainsBackingUntilRealRetry(bool throws)
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        if (throws) memory!.ReleaseHook = () => throw new InvalidOperationException("closure exception");
        else s.Model.MemoryReleaseFails = true;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive,
            s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        memory!.ReleaseHook = null;
        s.Model.MemoryReleaseFails = false;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
    }

    [Fact]
    public async Task ConcurrentHealthClosePreservesReleasedWithoutLateDamage()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        Task<KernelResult<RegionDamageDescriptorV1>> health = Task.Run(() => s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner,
            new CallbackHealthProvider(s.Model, () =>
            {
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            })));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
            Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Refresh(placement.PlacementId).Value!.State);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.StaleGeneration, (await health).Error);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public async Task OverlappingHealthObservationsCommitOnePlacementConsequence()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        using CountdownEvent entered = new CountdownEvent(2);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        CallbackHealthProvider provider = new CallbackHealthProvider(s.Model, () =>
        {
            entered.Signal();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        Task<KernelResult<RegionDamageDescriptorV1>>[] tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, provider))).ToArray();
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); }
        finally { resume.Set(); }
        KernelResult<RegionDamageDescriptorV1>[] results = await Task.WhenAll(tasks);
        Assert.Single(results, item => item.IsSuccess);
        Assert.Single(results, item => item.Error == KernelError.StaleGeneration);
        Assert.Single(s.Kernel.Regions.SnapshotDamage());
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public async Task PendingCloseRejectsOverlapOutsidePlacementLock()
    {
        CallbackMemoryProvider? memory = null;
        Scenario s = CreateScenario(memoryFactory: model => memory = new(model));
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        memory!.ReleaseHook = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        Task<KernelResult> close = Task.Run(() => s.Memory.Close(placement.PlacementId));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Memory.Close(placement.PlacementId).Error);
            Assert.Equal(CxlMemoryPlacementState.Draining, s.Memory.Refresh(placement.PlacementId).Value!.State);
            Assert.Equal(KernelError.InvalidTransition,
                s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model).Error);
        }
        finally { resume.Set(); }
        Assert.True((await close).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public void ClosingPlacementDuringHealthCallbackCannotResurrectReleasedPlacement()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        bool closed = false;
        KernelResult<RegionDamageDescriptorV1> result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner,
            new CallbackHealthProvider(s.Model, () => closed = s.Memory.Close(placement.PlacementId).IsSuccess));
        Assert.True(closed);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
    }

    private sealed class CallbackHealthProvider(ICxlType3HealthEvidenceProvider provider, Action callback)
        : ICxlType3HealthEvidenceProvider
    {
        public PlatformAuthorityResult<CxlType3HealthObservation> QueryHealth(CxlType3HealthObservationRequest request)
        {
            PlatformAuthorityResult<CxlType3HealthObservation> observation = provider.QueryHealth(request);
            callback();
            return observation;
        }
    }

    [Fact]
    public void ModelHealthEvidenceQuarantinesOnlyTheExactCxlBackedSubrange()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        RegionUseDescriptor overlapping = s.Kernel.AcquireRegionUse(s.Handle, s.Buffer.Handle,
            RegionUseMode.ReadOnly, new(8, 8)).Value!;
        RegionUseDescriptor disjoint = s.Kernel.AcquireRegionUse(s.Handle, s.Buffer.Handle,
            RegionUseMode.ReadOnly, new(40, 8)).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);

        KernelResult<RegionDamageDescriptorV1> damage = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model);

        Assert.True(damage.IsSuccess, damage.Message);
        Assert.Equal(new RegionUseRange(8, 8), damage.Value!.Range);
        Assert.Equal("cxl-type3-model", damage.Value.FailureDomain.ProviderId);
        Assert.Equal(CxlMemoryPlacementState.Quarantined,
            s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.StaleGeneration,
            s.Kernel.ValidateRegionUse(s.Handle, overlapping.Handle).Error);
        Assert.True(s.Kernel.ValidateRegionUse(s.Handle, disjoint.Handle).IsSuccess);
    }

    [Fact]
    public void DamagedRegionCannotBePlacedAgainAfterOldType3BackingCloses()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        Assert.True(s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model).IsSuccess);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);

        KernelResult<CxlMemoryPlacementSnapshot> replacement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.Quarantined, replacement.Error);
        Assert.Single(s.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void DeviceRebindRejectsPreviouslyInjectedCxlHealthObservation()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Failed, ProviderFaultClassV1.Omission, new(0, 16)).IsSuccess);
        Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);

        KernelResult<RegionDamageDescriptorV1> result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void DeviceRebindDuringHealthCallbackCannotCommitStaleRegionDamage()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Failed, ProviderFaultClassV1.Omission, new(0, 16)).IsSuccess);

        KernelResult<RegionDamageDescriptorV1> result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner,
            new RebindingHealthProvider(s.Model, s.Endpoint.EndpointId));

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void MalformedCxlHealthTupleCannotReachRegionAuthority()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        MalformedHealthProvider provider = new MalformedHealthProvider();

        KernelResult<RegionDamageDescriptorV1> result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, provider);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.Equal(CxlMemoryPlacementState.Active,
            s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public void CxlHealthObservationIsEvidenceOnly()
    {
        CxlType3HealthObservationRequest request = new CxlType3HealthObservationRequest(new("endpoint"), new(1), new(2), new(3), new(4), new(5));
        CxlType3HealthObservation observation = new CxlType3HealthObservation(request, new(1,
            new("provider", "domain", 1, 5), 1, ProviderHealthStateV1.Degraded,
            ProviderFaultClassV1.Omission, new(0, 1)));

        Assert.False(observation.AuthorizesRegionMutation);
        Assert.False(observation.AuthorizesReclaim);
    }

    [Fact]
    public void PlacementPolicyExplicitlyFallsBackOrFailsWhenCapacityIsMissing()
    {
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        CxlType3PlacementPlanner planner = new CxlType3PlacementPlanner(model, model);
        CxlMemoryPlacementIntent preferred = Intent(CxlMemoryPlacementPreference.CxlPreferred);
        CxlMemoryPlacementIntent required = Intent(CxlMemoryPlacementPreference.CxlRequired);

        KernelResult<CxlMemoryPlacementDecision> fallback = planner.Plan(preferred, [new("missing")]);
        KernelResult<CxlMemoryPlacementDecision> failure = planner.Plan(required, [new("missing")]);

        Assert.True(fallback.IsSuccess);
        Assert.Equal(CxlMemoryPlacementKind.Local, fallback.Value!.Kind);
        Assert.False(failure.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, failure.Error);
    }

    [Fact]
    public void ModelPlacesAnOrdinaryOwnedBufferAndCloseRestoresNormalReclaim()
    {
        Scenario s = CreateScenario();
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.DoesNotContain(typeof(CxlMemoryPlacementSnapshot).Assembly.GetExportedTypes(), type => type.Name == "CxlRegion");

        KernelResult blocked = s.Kernel.ReleaseRegion(s.Handle, s.Buffer);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, blocked.Error);
        Assert.True(s.Memory.Close(placed.Value!.PlacementId).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Handle, s.Buffer).IsSuccess);
    }

    [Fact]
    public void HotRemoveMarksControlledMigrationWithoutDanglingOrRevokingOwnership()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.HotRemove(s.Endpoint.EndpointId).IsSuccess);

        KernelResult<CxlMemoryPlacementSnapshot> refresh = s.Memory.Refresh(placed.PlacementId);
        Assert.False(refresh.IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired, s.Memory.Query(placed.PlacementId).Value!.State);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, s.Memory.Close(placed.PlacementId).Error);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placed.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
    }

    [Fact]
    public void HotRemoveInvalidatesOnlyAffectedEndpointBindings()
    {
        Scenario s = CreateScenario(twoEndpoints: true);
        CxlMemoryPlacementSnapshot first = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        OwnedBuffer<byte> secondBuffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 32).Value!;
        CxlEndpointSnapshot endpoint2 = s.Model.QueryEndpoint(new("type3-1")).Value!;
        CxlMemoryPlacementSnapshot second = s.Memory.Place(s.Owner, secondBuffer.Handle, s.Subject, s.Lease2!.Value, endpoint2, CxlMemoryPersistence.Volatile).Value!;

        Assert.True(s.Model.HotRemove(s.Endpoint.EndpointId).IsSuccess);
        Assert.False(s.Memory.Refresh(first.PlacementId).IsSuccess);
        Assert.True(s.Memory.Refresh(second.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Active, s.Memory.Query(second.PlacementId).Value!.State);
        Assert.True(s.Memory.Close(second.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease2!.Value).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(second.PlacementId).Value!.State);
    }

    [Fact]
    public void CapacityGenerationAndCoherenceClaimsAreExplicit()
    {
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("e"), new("device:e"), 128).IsSuccess);
        CxlEndpointSnapshot before = model.QueryEndpoint(new("e")).Value!;
        Assert.Equal(CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, before.Features);
        Assert.DoesNotContain(typeof(ICxlCoherentAccessProvider), model.GetType().GetInterfaces());
        Assert.True(model.Rebind(new("e")).IsSuccess);
        CxlEndpointSnapshot after = model.QueryEndpoint(new("e")).Value!;
        Assert.NotEqual(before.DeviceGeneration, after.DeviceGeneration);
    }

    [Fact]
    public void CxlBackedRegionStillUsesExistingPlatformMappingForDeviceAccess()
    {
        Scenario s = CreateScenario();
        KernelResult<CxlMemoryPlacementSnapshot> placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);

        KernelResult<PlatformRegionMapping> mapping = s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability,
            s.Buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.DoesNotContain(typeof(IPlatformDmaGrantProvider), s.Model.GetType().GetInterfaces());
    }

    [Fact]
    public void StaleDestructiveHandlesCannotDeleteCurrentFabricOrMemoryGeneration()
    {
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("exact"), new("device:exact"), 1024).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("exact")).Value!;
        CxlFabricBinding first = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        CxlFabricBinding second = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        RegionBackingLeaseDescriptor firstBacking = new RegionBackingLeaseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), 64);
        RegionBackingLeaseDescriptor secondBacking = new RegionBackingLeaseDescriptor(new(new(2), 1), new(new(2), new(1)), new(new(1), 1), 64);
        CxlMemoryBinding firstMemory = model.BindMemory(first, firstBacking).Value!;
        CxlMemoryBinding secondMemory = model.BindMemory(second, secondBacking).Value!;
        CxlFabricReconfigurationTicket ticket = model.BeginReconfiguration(first).Value!;
        CxlFabricBinding replacement = model.CompleteReconfiguration(ticket).Value!;

        Assert.Equal(PlatformAuthorityStatus.Stale, model.Unbind(first).Status);
        Assert.Equal(replacement, model.Query(first.BindingId).Value);
        Assert.Equal(new CxlFabricBindingGeneration(1), model.Query(second.BindingId).Value!.Generation);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            model.ReleaseMemory(firstMemory).Status);
        Assert.Equal(new CxlMemoryBindingGeneration(2), model.QueryMemory(firstMemory.BindingId).Value!.Generation);
        Assert.Equal(new CxlMemoryBindingGeneration(1), model.QueryMemory(secondMemory.BindingId).Value!.Generation);
        Assert.True(model.ReleaseMemory(model.QueryMemory(firstMemory.BindingId).Value!).IsSuccess);
        Assert.True(model.ReleaseMemory(secondMemory).IsSuccess);
    }

    [Fact]
    public void EffectCreatingModelRejectionsUseNotAcceptedOnlyBeforeEffect()
    {
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            model.Bind(new(new("missing"), new(1), 64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Status);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            model.AssignPoolCapacity(new("missing"), new("missing"), 64).Status);

        Assert.True(model.RegisterEndpoint(new("pre-effect"), new("device:pre-effect"), 128).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("pre-effect")).Value!;
        CxlFabricBinding fabric = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        CxlFabricBinding staleFabric = fabric with { Generation = new(fabric.Generation.Value + 1) };
        RegionBackingLeaseDescriptor backing = new RegionBackingLeaseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), 64);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, model.BindMemory(staleFabric, backing).Status);
        Assert.Equal(fabric, model.Query(fabric.BindingId).Value);
    }

    [Fact]
    public void ProcessTeardownClosesLiveType3PlacementBeforeRegionReclaim()
    {
        Scenario s = CreateScenario();
        CxlMemoryPlacementSnapshot placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;

        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);

        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.InvalidRegionState, s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).Error);
    }

    [Fact]
    public void Type3BackedInputAndOutputComposeWithPlannerAndType2StagedExecution()
    {
        Scenario s = CreateScenario();
        OwnedBuffer<byte> output = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        CxlMemoryPlacementSnapshot inputPlacement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryPlacementSnapshot outputPlacement = s.Memory.Place(s.Owner, output.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        ComputeProviderCandidate candidate = new ComputeProviderCandidate(new("type2-over-type3"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
            1024, 1, 1, true, false);
        ComputeIntent intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(s.Buffer.Handle, new(0, 64)), new(output.Handle, new(0, 64)),
            ComputePublicationPreference.StagedRequired, false, false);
        KernelResult<ComputePlan> plan = s.Kernel.PlanCompute(s.Handle, intent, new(true, true, true), [candidate]);
        Assert.True(plan.IsSuccess, plan.Message);

        OwnedBuffer<byte> control = s.Kernel.AllocateBuffer<byte>(s.Handle, 8).Value!;
        RegionUseDescriptor controlUse = s.Kernel.AcquireRegionUse(s.Handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 8)).Value!;
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(s.Kernel, s.Model, s.Model, s.Model, s.Model,
            new UnsupportedCoherent(), new EvidenceOnly());
        CxlFabricBinding fabric = bridge.BindFabric(s.Owner, controlUse.Handle, s.Subject, s.Lease,
            new(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, 8, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        CxlType2ModelAccelerator accelerator = new CxlType2ModelAccelerator();
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(s.Kernel, s.Model);
        Assert.True(manager.Register(fabric).IsSuccess);
        CxlType2AcceleratorService service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        KernelResult<CxlType2Execution> execution = service.Submit(s.Handle, plan.Value!, [candidate], s.Subject, s.Lease, s.Endpoint, fabric, 1);
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.True(service.CompleteVisiblePublish(s.Handle, execution.Value!, [candidate], () => s.Buffer.Span.CopyTo(output.Span)).IsSuccess);

        Assert.True(s.Memory.Close(inputPlacement.PlacementId).IsSuccess);
        Assert.True(s.Memory.Close(outputPlacement.PlacementId).IsSuccess);
    }

    [Fact]
    public void MalformedFabricSuccessfulReturnedObjectCleanupKeepsActualPlacementBackingQuarantined()
    {
        CallbackFabricProvider? fabric = null;
        Scenario s = CreateScenario(fabricFactory: model => fabric = new(model));
        fabric!.RewriteReceipt = receipt => receipt with { BindingId = new(receipt.BindingId.Value + 100) };
        fabric.ReturnedObjectCleanupSucceeds = true;
        KernelResult<CxlMemoryPlacementSnapshot> placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.Equal(1, fabric.BindCalls);
        Assert.Equal(1, fabric.UnbindCalls);
        Assert.True(s.Model.Query(fabric.OriginalReceipt!.BindingId).IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseFabric(fabric.ReturnedReceipt!).Error);
        Assert.Equal(1, fabric.UnbindCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MalformedFabricOrMemoryBinding_CompensationFailure_RemainsTrackedForTeardown(bool fabricFailure)
    {
        Scenario s = CreateScenario();
        if (fabricFailure)
        {
            s.Model.ReturnMalformedFabricBinding = true;
            s.Model.FabricUnbindFails = true;
        }
        else
        {
            s.Model.ReturnMalformedMemoryBinding = true;
            s.Model.MemoryReleaseFails = true;
        }

        KernelResult<CxlMemoryPlacementSnapshot> placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmbiguousFabricOrMemoryAcceptance_QuarantinesBackingAndBlocksReclaim(bool fabricAcceptance)
    {
        Scenario s = CreateScenario();
        if (fabricAcceptance)
            s.Model.FabricAcceptanceAmbiguous = true;
        else
            s.Model.MemoryAcceptanceAmbiguous = true;

        KernelResult<CxlMemoryPlacementSnapshot> placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderExceptionAfterFabricOrMemoryAcceptance_QuarantinesBacking(bool fabricAcceptance)
    {
        Scenario s = CreateScenario();
        if (fabricAcceptance)
            s.Model.FabricThrowsAfterAcceptance = true;
        else
            s.Model.MemoryThrowsAfterAcceptance = true;

        KernelResult<CxlMemoryPlacementSnapshot> placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    private static CxlMemoryPlacementIntent Intent(CxlMemoryPlacementPreference preference) =>
        new(64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive, preference, 10, 0);

    private static Scenario CreateScenario(bool twoEndpoints = false,
        Func<CxlType3ModelProvider, ICxlMemoryProvider>? memoryFactory = null,
        Func<CxlType3ModelProvider, ICxlFabricProvider>? fabricFactory = null,
        Func<CxlType3ModelProvider, ICxlDiscoveryProvider>? discoveryFactory = null,
        Action<RuntimeKernel, ProcessHandle>? beforeAllocation = null)
    {
        AuthorityProvider platform = new AuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(platform);
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 951, 952);
        SingProcess process = kernel.Processes.Resolve(handle).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, handle.Generation);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, handle);
        PlatformDomainBinding domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        PlatformDeviceLease Lease(string id)
        {
            CapabilityDescriptorV1 cap = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, id,
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
            return kernel.BindPlatformDevice(handle, domain, cap.CapabilityId,
                PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        }
        PlatformDeviceLease lease = Lease("device:type3-0");
        PlatformDeviceLease? lease2 = null;
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("type3-0"), lease.Device, 1024, latencyClass: 2, bandwidthClass: 5).IsSuccess);
        if (twoEndpoints)
        {
            lease2 = Lease("device:type3-1");
            Assert.True(model.RegisterEndpoint(new("type3-1"), lease2.Value.Device, 1024, latencyClass: 3, bandwidthClass: 4).IsSuccess);
        }
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(kernel, discoveryFactory?.Invoke(model) ?? model, model, fabricFactory?.Invoke(model) ?? model, memoryFactory?.Invoke(model) ?? model,
            new UnsupportedCoherent(), new EvidenceOnly());
        CxlType3MemoryAuthority memory = new CxlType3MemoryAuthority(kernel, bridge);
        beforeAllocation?.Invoke(kernel, handle);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 64).Value!;
        CapabilityId regionCapability = kernel.MintCapability(process.DomainId, handle, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Map).Value!.CapabilityId;
        return new(kernel, handle, owner, subject, domain, buffer, regionCapability, lease, lease2, model,
            model.QueryEndpoint(new("type3-0")).Value!, memory, bridge, platform);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle, RegionOwner Owner,
        PlatformDomainIdentity Subject, PlatformDomainBinding Domain, OwnedBuffer<byte> Buffer, CapabilityId RegionCapability, PlatformDeviceLease Lease,
        PlatformDeviceLease? Lease2, CxlType3ModelProvider Model, CxlEndpointSnapshot Endpoint,
        CxlType3MemoryAuthority Memory, CxlAuthorityBridge Bridge, AuthorityProvider Platform);

    private sealed class LateClosureDiscovery(ICxlDiscoveryProvider model) : ICxlDiscoveryProvider
    {
        internal int? NextFailure { get; set; }
        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpoint)
        {
            int? failure = NextFailure;
            NextFailure = null;
            if (failure == 0) return PlatformAuthorityResult<CxlEndpointSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "late closure query unavailable");
            if (failure == 1) throw new IOException("late closure query reply lost");
            return model.QueryEndpoint(endpoint);
        }
    }

    private sealed class CallbackMemoryProvider(CxlType3ModelProvider model) : ICxlMemoryProvider
    {
        internal Func<CxlMemoryBinding, CxlMemoryBinding>? RewriteReceipt { get; set; }
        internal bool ReturnedObjectCleanupSucceeds;
        internal CxlMemoryBinding? OriginalReceipt;
        internal CxlMemoryBinding? ReturnedReceipt;
        internal int BindCalls { get; private set; }
        internal Action? RejectBind { get; set; }
        internal Action? BindHook { get; set; }
        internal Action? QueryHook { get; set; }
        internal Action? ReleaseHook { get; set; }
        internal Action? AfterReleaseHook { get; set; }
        internal int ReleaseCalls { get; private set; }
        internal Action<CxlFabricBinding>? BeforeBindHook { get; set; }
        public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId endpoint) => model.QueryCapacity(endpoint);
        public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabric, RegionBackingLeaseDescriptor backing)
        {
            BindCalls++;
            BeforeBindHook?.Invoke(fabric);
            if (RejectBind is { } reject)
            {
                reject();
                return PlatformAuthorityResult<CxlMemoryBinding>.Fail(PlatformAuthorityStatus.NotAccepted, "zero-effect rejection");
            }
            PlatformAuthorityResult<CxlMemoryBinding> result = model.BindMemory(fabric, backing);
            if (result.IsSuccess) OriginalReceipt = result.Value!;
            BindHook?.Invoke();
            if (result.IsSuccess)
            {
                ReturnedReceipt = RewriteReceipt?.Invoke(result.Value!) ?? result.Value!;
                return PlatformAuthorityResult<CxlMemoryBinding>.Ok(ReturnedReceipt);
            }
            return result;
        }
        public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId binding)
        {
            QueryHook?.Invoke();
            return model.QueryMemory(binding);
        }
        public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding)
        {
            ReleaseCalls++;
            ReleaseHook?.Invoke();
            PlatformAuthorityResult result = ReturnedObjectCleanupSucceeds && binding != OriginalReceipt
                ? PlatformAuthorityResult.Ok() : model.ReleaseMemory(binding);
            if (result.IsSuccess) AfterReleaseHook?.Invoke();
            return result;
        }
    }

    private sealed class CallbackFabricProvider(CxlType3ModelProvider model) : ICxlFabricProvider
    {
        internal Func<CxlFabricBinding, CxlFabricBinding>? RewriteReceipt;
        internal bool ReturnedObjectCleanupSucceeds;
        internal CxlFabricBinding? OriginalReceipt;
        internal CxlFabricBinding? ReturnedReceipt;
        internal int BindCalls { get; private set; }
        internal Action? RejectBind { get; set; }
        internal Action? QueryHook { get; set; }
        internal Action? BindHook { get; set; }
        internal Action? UnbindHook { get; set; }
        internal Action? AfterUnbindHook { get; set; }
        internal int UnbindCalls { get; private set; }
        public PlatformAuthorityResult<CxlFabricBinding> Bind(CxlFabricBindingRequest request)
        {
            BindCalls++;
            if (RejectBind is { } reject)
            {
                reject();
                return PlatformAuthorityResult<CxlFabricBinding>.Fail(PlatformAuthorityStatus.NotAccepted, "zero-effect rejection");
            }
            PlatformAuthorityResult<CxlFabricBinding> result = model.Bind(request);
            BindHook?.Invoke();
            if (result.IsSuccess)
            {
                OriginalReceipt = result.Value!;
                ReturnedReceipt = RewriteReceipt?.Invoke(result.Value!) ?? result.Value!;
                return PlatformAuthorityResult<CxlFabricBinding>.Ok(ReturnedReceipt);
            }
            return result;
        }
        public PlatformAuthorityResult<CxlFabricBinding> Query(CxlFabricBindingId binding)
        {
            QueryHook?.Invoke();
            return model.Query(binding);
        }
        public PlatformAuthorityResult Unbind(CxlFabricBinding binding)
        {
            UnbindCalls++;
            UnbindHook?.Invoke();
            PlatformAuthorityResult result = ReturnedObjectCleanupSucceeds && binding != OriginalReceipt
                ? PlatformAuthorityResult.Ok() : model.Unbind(binding);
            if (result.IsSuccess) AfterUnbindHook?.Invoke();
            return result;
        }
    }

    private sealed class UnsupportedCoherent : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding f, RegionUseDescriptor u) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding b) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class EvidenceOnly : ICxlSecurityEvidenceProvider
    {
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId id, CxlDeviceGeneration generation) =>
            PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class MalformedHealthProvider : ICxlType3HealthEvidenceProvider
    {
        public PlatformAuthorityResult<CxlType3HealthObservation> QueryHealth(
            CxlType3HealthObservationRequest request) =>
            PlatformAuthorityResult<CxlType3HealthObservation>.Ok(new(
                request with { MemoryGeneration = new(request.MemoryGeneration.Value + 1) },
                new(1, new("malformed", "domain", request.DeviceGeneration.Value,
                        request.MemoryGeneration.Value), 1, ProviderHealthStateV1.Degraded,
                    ProviderFaultClassV1.Omission, new(0, 1))));
    }

    private sealed class RebindingHealthProvider(CxlType3ModelProvider model, CxlEndpointId endpointId)
        : ICxlType3HealthEvidenceProvider
    {
        public PlatformAuthorityResult<CxlType3HealthObservation> QueryHealth(
            CxlType3HealthObservationRequest request)
        {
            PlatformAuthorityResult<CxlType3HealthObservation> observation = model.QueryHealth(request);
            if (!model.Rebind(endpointId).IsSuccess)
                throw new InvalidOperationException("The model rebind fault injection failed.");
            return observation;
        }
    }

    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        internal int DeviceRevokeCalls { get; private set; }
        private ulong _nextDevice = 1;
        public PlatformProviderDescriptor Descriptor { get; } = new(new("type3-test"), 1,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), subject));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease d, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(_nextDevice++), new(1), d, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            DeviceRevokeCalls++;
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(new(new(1), new(1), d, r, a));
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => PlatformAuthorityResult.Ok();
    }
}
