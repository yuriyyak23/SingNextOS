using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlFabricManagerAuthorityTests
{
    [Fact]
    public void OriginalFabricBeginSurvivesLateRouteLossAndCompletesWithoutSecondBegin()
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", flags)!.GetValue(s.Manager)!;
        object original = routes[s.Binding.BindingId]!;
        hook!.AfterBegin = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(CxlFabricManagerAuthority).GetField("_gate", flags)!.GetValue(s.Manager)!));
            routes[s.Binding.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        };
        Assert.False(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        Assert.Equal(1, hook.BeginCalls);
        Assert.Equal(CxlFabricResourceState.Draining, s.Model.QueryResource(s.Binding.BindingId).Value!.State);
        routes[s.Binding.BindingId] = original; hook.AfterBegin = null;
        KernelResult<CxlFabricManagedBinding> completed = s.Manager.CompleteReconfiguration(s.Binding.BindingId);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(1, hook.BeginCalls); Assert.Equal(1, hook.Calls);
        Assert.True(s.Manager.ValidateAdmission(completed.Value!.Binding).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OriginalFabricCompletionCannotConsumeCopiedHistoryOrIndependentRevision(int fault)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", flags)!.GetValue(s.Manager)!;
        object original = routes[s.Binding.BindingId]!;
        hook!.QueryTransform = _ => PlatformAuthorityResult<CxlFabricResourceSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "late query lost");
        Assert.False(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        hook.QueryTransform = null;
        PropertyInfo property = original.GetType().GetProperty("Completion")!;
        object fact = property.GetValue(original)!;
        if (fault == 0) property.SetValue(original, typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(fact, null));
        if (fault == 1) fact.GetType().GetProperty("RawObservation")!.SetValue(fact, null);
        if (fault == 2) original.GetType().GetProperty("State")!.SetValue(original, CxlFabricResourceState.Faulted);
        if (fault == 3) original.GetType().GetProperty("Ticket")!.SetValue(original,
            ((CxlFabricReconfigurationTicket)original.GetType().GetProperty("Ticket")!.GetValue(original)!) with { });
        Assert.False(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.Equal(1, hook.Calls);
        Assert.False(s.Manager.ValidateAdmission(s.Model.Query(s.Binding.BindingId).Value!).IsSuccess);
        Assert.False((bool)original.GetType().GetProperty("CompletionInFlight")!.GetValue(original)!);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalFabricCompletionConsumesKnownReplacementAfterQueryLossWithoutRepeatingEffect(bool throws)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        hook!.QueryTransform = result => throws ? throw new IOException("original completion source reply lost")
            : PlatformAuthorityResult<CxlFabricResourceSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "source unavailable");
        Assert.False(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.Equal(1, hook.Calls);
        Assert.Equal(new CxlFabricBindingGeneration(s.Binding.Generation.Value + 1), s.Model.Query(s.Binding.BindingId).Value!.Generation);
        hook.QueryTransform = null;
        KernelResult<CxlFabricManagedBinding> result = s.Manager.CompleteReconfiguration(s.Binding.BindingId);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, hook.Calls);
        Assert.True(s.Manager.ValidateAdmission(result.Value!.Binding).IsSuccess);
        Assert.False(s.Manager.ValidateAdmission(s.Binding).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalFabricUnknownCompletionCannotRepeatAfterLocalStateRestoration(bool lostReply)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        if (lostReply) hook!.AfterComplete = () => throw new IOException("original Complete reply lost");
        else hook!.CompletionFailure = PlatformAuthorityStatus.Unavailable;
        Assert.False(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", flags)!.GetValue(s.Manager)!;
        object record = routes[s.Binding.BindingId]!;
        record.GetType().GetProperty("State")!.SetValue(record, CxlFabricResourceState.Draining);
        hook!.AfterComplete = null; hook.CompletionFailure = null;
        Assert.False(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.Equal(1, hook.Calls);
        Assert.False(s.Manager.ValidateAdmission(s.Binding).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalFabricAdmissionRefusesCopiedOrEqualWrittenOwnerAcrossProviderQuery(bool copied)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority).GetField("_bindings", flags)!.GetValue(s.Manager)!;
        object original = routes[s.Binding.BindingId]!;
        hook!.AfterQuery = () =>
        {
            if (copied) routes[s.Binding.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else original.GetType().GetProperty("State")!.SetValue(original, CxlFabricResourceState.Bound);
        };
        int effects = 0;
        KernelResult<int> result = s.Manager.ExecuteAdmission(s.Binding, () => { effects++; return KernelResult<int>.Ok(1); });
        Assert.False(result.IsSuccess); Assert.Equal(0, effects);
        hook.AfterQuery = null; routes[s.Binding.BindingId] = original;
        Assert.True(s.Manager.ValidateAdmission(s.Binding).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateUnavailableSourceRefusesActualFmReconfigurationEffectAndPreservesPendingFacts(bool completion)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        if (completion) Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        CxlFabricResourceSnapshot before = s.Model.QueryResource(s.Binding.BindingId).Value!;
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        Type type = typeof(CxlType3ModelProvider);
        IDictionary tickets = (global::System.Collections.IDictionary)type.GetField("_reconfigurations", flags)!.GetValue(s.Model)!;
        CxlFabricReconfigurationTicket[] saved = tickets.Values.Cast<CxlFabricReconfigurationTicket>().ToArray();
        FieldInfo counter = type.GetField("_nextReconfiguration", flags)!;
        ulong sequence = (ulong)counter.GetValue(s.Model)!;
        object gate = typeof(CxlFabricManagerAuthority).GetField("_gate", flags)!.GetValue(s.Manager)!;
        bool observed = false;
        void LoseSource()
        {
            Assert.False(Monitor.IsEntered(gate));
            IDictionary endpoints = (global::System.Collections.IDictionary)type.GetField("_endpoints", flags)!.GetValue(s.Model)!;
            object endpoint = endpoints[s.Endpoint.EndpointId]!;
            endpoint.GetType().GetProperty("DeviceGeneration")!.SetValue(endpoint, ulong.MaxValue);
            Assert.Equal(PlatformAuthorityStatus.Faulted, s.Model.HotRemove(s.Endpoint.EndpointId).Status);
            observed = true;
        }
        if (completion) hook!.BeforeComplete = LoseSource;
        else hook!.BeforeBegin = LoseSource;
        KernelResult<CxlFabricManagedBinding> result = completion ? s.Manager.CompleteReconfiguration(s.Binding.BindingId) : s.Manager.BeginReconfiguration(s.Binding);
        Assert.True(observed);
        Assert.False(result.IsSuccess);
        Assert.Equal(before with { State = CxlFabricResourceState.Faulted }, s.Model.QueryResource(s.Binding.BindingId).Value!);
        Assert.Equal(saved, tickets.Values.Cast<CxlFabricReconfigurationTicket>().ToArray());
        Assert.Equal(sequence, (ulong)counter.GetValue(s.Model)!);
        Assert.False(s.Manager.ValidateAdmission(s.Binding).IsSuccess);
        hook!.BeforeBegin = null;
        hook.BeforeComplete = null;
        Assert.False((completion ? s.Manager.CompleteReconfiguration(s.Binding.BindingId) : s.Manager.BeginReconfiguration(s.Binding)).IsSuccess);
        Assert.Equal(completion ? 1 : 0, hook.Calls);
        Assert.Equal(saved, tickets.Values.Cast<CxlFabricReconfigurationTicket>().ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ReconfigurationCapacityRefusalPreservesProviderSourceAndFmAdmission(int fault)
    {
        Scenario s = CreateScenario();
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        Type type = typeof(CxlType3ModelProvider);
        FieldInfo counter = type.GetField("_nextReconfiguration", flags)!;
        counter.SetValue(s.Model, fault == 0 ? 0UL : fault == 1 ? ulong.MaxValue : fault == 4 ? ulong.MaxValue - 1 : 1UL);
        CxlFabricBinding binding = fault == 2 ? s.Binding with { Generation = new(ulong.MaxValue) } : s.Binding;
        IDictionary fabric = (global::System.Collections.IDictionary)type.GetField("_fabric", flags)!.GetValue(s.Model)!;
        Type fabricRecordType = fabric[s.Binding.BindingId]!.GetType();
        fabric[s.Binding.BindingId] = Activator.CreateInstance(fabricRecordType, binding, 128L)!;
        IDictionary memory = (global::System.Collections.IDictionary)type.GetField("_memory", flags)!.GetValue(s.Model)!;
        Type memoryType = type.GetNestedType("MemoryRecord", global::System.Reflection.BindingFlags.NonPublic)!;
        CxlMemoryBinding first = new CxlMemoryBinding(new(900), new(1), binding, default);
        CxlMemoryBinding second = new CxlMemoryBinding(new(901), new(fault == 3 ? ulong.MaxValue : 1UL), binding, default);
        memory[first.BindingId] = Activator.CreateInstance(memoryType, first)!;
        memory[second.BindingId] = Activator.CreateInstance(memoryType, second)!;
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(s.Kernel, s.Model);
        Assert.True(manager.Register(binding).IsSuccess);
        CxlFabricResourceSnapshot before = s.Model.QueryResource(binding.BindingId).Value!;
        KernelResult<CxlFabricManagedBinding> result = manager.BeginReconfiguration(binding);
        CxlMemoryBinding ReadMemory(CxlMemoryBindingId id) => (CxlMemoryBinding)memory[id]!.GetType().GetProperty("Binding")!.GetValue(memory[id])!;
        if (fault == 4)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(ulong.MaxValue - 1, result.Value!.ActiveReconfiguration!.Value.Value);
            CxlFabricBinding replacement = manager.CompleteReconfiguration(binding.BindingId).Value!.Binding;
            Assert.Equal(KernelError.PlatformUnavailable, manager.BeginReconfiguration(replacement).Error);
            Assert.True(manager.ValidateAdmission(replacement).IsSuccess);
            Assert.Equal(2UL, ReadMemory(first.BindingId).Generation.Value);
        }
        else
        {
            Assert.Equal(KernelError.PlatformUnavailable, result.Error);
            Assert.True(manager.ValidateAdmission(binding).IsSuccess);
            Assert.Equal(before, s.Model.QueryResource(binding.BindingId).Value);
            Assert.Equal(first, ReadMemory(first.BindingId));
            Assert.Equal(second, ReadMemory(second.BindingId));
            Assert.Equal(PlatformAuthorityStatus.NotAccepted, s.Model.BeginReconfiguration(binding).Status);
        }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CompletionRefusalRetriesOnlyExactDrainingOriginalSource(int fault)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        KernelResult<CxlFabricManagedBinding> begun = s.Manager.BeginReconfiguration(s.Binding);
        Assert.True(begun.IsSuccess);
        hook!.CompletionFailure = fault == 5 ? PlatformAuthorityStatus.Unavailable : PlatformAuthorityStatus.NotAccepted;
        hook.QueryTransform = result => fault switch
        {
            1 => PlatformAuthorityResult<CxlFabricResourceSnapshot>.Ok(result.Value! with
                { Binding = result.Value.Binding with { Generation = new(result.Value.Binding.Generation.Value + 1) } }),
            2 => PlatformAuthorityResult<CxlFabricResourceSnapshot>.Ok(result.Value! with { State = CxlFabricResourceState.Bound }),
            3 => PlatformAuthorityResult<CxlFabricResourceSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "source lost"),
            4 => throw new InvalidOperationException("source query failed"),
            _ => result
        };
        hook.BeforeComplete = () => Assert.Equal(KernelError.PlatformBindingDraining,
            s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
        Assert.False(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.Equal(1, hook.Calls);
        Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.ValidateAdmission(s.Binding).Error);
        Assert.Equal(s.Binding, s.Model.QueryResource(s.Binding.BindingId).Value!.Binding);
        Assert.Equal(CxlFabricResourceState.Draining, s.Model.QueryResource(s.Binding.BindingId).Value!.State);
        hook.CompletionFailure = null;
        hook.QueryTransform = null;
        hook.BeforeComplete = null;
        KernelResult<CxlFabricManagedBinding> retry = s.Manager.CompleteReconfiguration(s.Binding.BindingId);
        if (fault == 0)
        {
            Assert.True(retry.IsSuccess, retry.Message);
            Assert.Equal(2, hook.Calls);
            Assert.Null(retry.Value!.ActiveReconfiguration);
            Assert.Equal(s.Binding.Generation.Value + 1, retry.Value.Binding.Generation.Value);
            Assert.True(s.Manager.ValidateAdmission(retry.Value.Binding).IsSuccess);
            Assert.Equal(KernelError.StaleGeneration, s.Manager.ValidateAdmission(s.Binding).Error);
        }
        else
        {
            Assert.Equal(fault == 5 ? KernelError.ExternalEffectUncontained : KernelError.PlatformUnavailable, retry.Error);
            Assert.Equal(1, hook.Calls);
            if (fault != 5)
            {
                KernelResult<CxlFabricManagedBinding> fresh = s.Manager.CompleteReconfiguration(s.Binding.BindingId);
                Assert.True(fresh.IsSuccess, fresh.Message);
                Assert.Equal(2, hook.Calls);
                Assert.True(s.Manager.ValidateAdmission(fresh.Value!.Binding).IsSuccess);
            }
        }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void UntrackingCannotForgetLiveOperationBeforeActualDrain(int stage)
    {
        Scenario s = CreateScenario();
        ExternalOperationHandle operation = PrepareOperation(s).Operation;
        OperationDependencySnapshot generations = new OperationDependencySnapshot(1, 1, s.Binding.Generation.Value);
        if (stage >= 1) Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, operation, generations).IsSuccess);
        OperationBinding? submitted = null;
        if (stage >= 2) submitted = s.Kernel.RecordExternalOperationSubmission(s.Handle, operation, generations).Value!;
        if (stage >= 3) Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Handle,
            new(submitted!.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        if (stage >= 4) Assert.True(s.Kernel.RecordExternalOperationVisibility(s.Handle,
            new(submitted!.Value, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        if (stage >= 5) Assert.True(s.Kernel.PublishExternalOperation(s.Handle, operation,
            generations, new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);
        int closures = 0;
        Assert.True(s.Manager.TrackOperation(s.Binding, s.Handle, operation,
            () => { closures++; return KernelResult.Ok(); }).IsSuccess);
        ExternalOperationSnapshot before = s.Kernel.QueryExternalOperation(s.Handle, operation).Value!;
        KernelResult<ExternalOperationResourceBinding> resourceBefore = s.Kernel.ExternalOperations.QueryResourceBinding(operation);
        RegionUseDescriptor[] usesBefore = s.Kernel.Regions.SnapshotUses().ToArray();
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Manager.UntrackOperation(s.Binding, s.Handle, operation).Error);
        AssertOperationFactsEqual(before, s.Kernel.QueryExternalOperation(s.Handle, operation).Value!);
        KernelResult<ExternalOperationResourceBinding> resourceAfter = s.Kernel.ExternalOperations.QueryResourceBinding(operation);
        Assert.Equal(resourceBefore.IsSuccess, resourceAfter.IsSuccess);
        Assert.Equal(resourceBefore.Error, resourceAfter.Error);
        Assert.Equal(resourceBefore.Value, resourceAfter.Value);
        Assert.Equal(usesBefore, s.Kernel.Regions.SnapshotUses().ToArray());
        Assert.Equal(0, closures);
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        Assert.Equal(1, closures);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.QueryExternalOperation(s.Handle, operation).Value!.State);
        Assert.True(s.Manager.UntrackOperation(s.Binding, s.Handle, operation).IsSuccess);
        Assert.True(s.Manager.UntrackOperation(s.Binding, s.Handle, operation).IsSuccess);
        Assert.True(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.Equal(1, closures);
    }
    [Fact]
    public async Task UntrackingDuringProviderClosureRetainsLiveCorrelationAndExcludesOverlappingDrain()
    {
        Scenario s = CreateScenario();
        ExternalOperationHandle operation = PrepareOperation(s).Operation;
        OperationDependencySnapshot dependencies = new OperationDependencySnapshot(1, 1, s.Binding.Generation.Value);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, operation, dependencies).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationSubmission(s.Handle, operation, dependencies).IsSuccess);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        int closures = 0;
        Assert.True(s.Manager.TrackOperation(s.Binding, s.Handle, operation, () =>
        {
            Interlocked.Increment(ref closures);
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("closure pause");
            return KernelResult.Ok();
        }).IsSuccess);
        Task<KernelResult<CxlFabricManagedBinding>> drain = Task.Run(() => s.Manager.BeginReconfiguration(s.Binding));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            ExternalOperationSnapshot before = s.Kernel.QueryExternalOperation(s.Handle, operation).Value!;
            Assert.Equal(KernelError.ExternalEffectUncontained,
                s.Manager.UntrackOperation(s.Binding, s.Handle, operation).Error);
            AssertOperationFactsEqual(before, s.Kernel.QueryExternalOperation(s.Handle, operation).Value!);
            Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.BeginReconfiguration(s.Binding).Error);
            Assert.Equal(1, Volatile.Read(ref closures));
        }
        finally { resume.Set(); }
        KernelResult<CxlFabricManagedBinding> result = await drain;
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.QueryExternalOperation(s.Handle, operation).Value!.State);
        Assert.True(s.Manager.UntrackOperation(s.Binding, s.Handle, operation).IsSuccess);
        Assert.True(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.Equal(1, closures);
    }

    [Fact]
    public void UntrackingRejectsWrongOwnerAndStaleOperationOrFabricGenerationWithoutForgettingCorrelation()
    {
        Scenario s = CreateScenario();
        ExternalOperationHandle operation = PrepareOperation(s).Operation;
        int closures = 0;
        Assert.True(s.Manager.TrackOperation(s.Binding, s.Handle, operation,
            () => { closures++; return KernelResult.Ok(); }).IsSuccess);
        Assert.True(s.Kernel.CancelExternalOperation(s.Handle, operation, false).IsSuccess);
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Handle, operation, new(false, false)).IsSuccess);
        (SingProcess _, ProcessHandle other) = TestFixtures.Create(s.Kernel, 993, 994);
        ExternalOperationSnapshot before = s.Kernel.QueryExternalOperation(s.Handle, operation).Value!;
        Assert.Equal(KernelError.WrongRegionOwner, s.Manager.UntrackOperation(s.Binding, other, operation).Error);
        Assert.False(s.Manager.UntrackOperation(s.Binding, s.Handle,
            operation with { Generation = new(operation.Generation.Value + 1) }).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Manager.UntrackOperation(
            s.Binding with { Generation = new(s.Binding.Generation.Value + 1) }, s.Handle, operation).Error);
        AssertOperationFactsEqual(before, s.Kernel.QueryExternalOperation(s.Handle, operation).Value!);
        Assert.Equal(0, closures);
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        Assert.Equal(1, closures); // Invalid identities did not remove the retained callback.
        Assert.True(s.Manager.UntrackOperation(s.Binding, s.Handle, operation).IsSuccess);
        Assert.True(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Manager.UntrackOperation(s.Binding, s.Handle, operation).Error);
    }

    private static void AssertOperationFactsEqual(ExternalOperationSnapshot expected, ExternalOperationSnapshot actual)
    {
        // Record equality covers every scalar, policy, generation and binding; lists
        // are compared by ordered content before normalizing their references.
        Assert.Equal(expected.Transitions.ToArray(), actual.Transitions.ToArray());
        Assert.Equal(expected.Admission is null, actual.Admission is null);
        if (expected.Admission is { } admission)
        {
            Assert.Equal(admission.RegionUses.ToArray(), actual.Admission!.RegionUses.ToArray());
            Assert.Equal(admission, actual.Admission with { RegionUses = admission.RegionUses });
        }
        Assert.Equal(expected, actual with { Admission = expected.Admission, Transitions = expected.Transitions });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DamagedCallbackRefusesPublicationAndSeparatesPoolClosureFromRegionReclaim(bool replaceBeforeCleanup)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Model.RegisterPool(new("damage-pool"), 256).IsSuccess);
        ProcessBudgetAdmission budget = s.Kernel.Budgets.CreateProcessHierarchy(s.Handle, "pool-damage", "pool-process",
            [new BudgetAmount(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value;
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        RegionDamageDescriptorV1? damage = null;
        hook!.AfterAssign = () => damage = s.Kernel.Regions.QuarantineSubrange(buffer.Handle, s.Owner,
            new ProviderHealthEvidenceV1(1, new("pool-provider", "pool-bank", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).Value!;
        KernelResult<CxlOwnedPoolAllocation> result = s.Manager.AssignPool(new("damage-pool"), s.Endpoint.EndpointId, buffer.Handle, s.Owner);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.NotNull(damage);
        IDictionary records = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority)
            .GetField("_poolAllocations", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Manager)!;
        object record = records[buffer.Handle]!;
        Assert.Equal(false, record.GetType().GetProperty("Published")!.GetValue(record));
        Assert.Equal(true, record.GetType().GetProperty("ReceiptValidated")!.GetValue(record));
        CxlOwnedPoolAllocation retained = (CxlOwnedPoolAllocation)record.GetType().GetProperty("Allocation")!.GetValue(record)!;
        RegionBackingReplacementEvidenceV1 replacement = new RegionBackingReplacementEvidenceV1(1, damage!.Handle,
            damage.FailureDomain with { ProviderGeneration = 2, FailureDomainGeneration = 2 },
            damage.EvidenceObservationSequence + 1, 2, damage.Range, new('a', 64), new('b', 64));
        if (replaceBeforeCleanup)
            Assert.Equal(KernelError.ExternalEffectUncontained,
                s.Kernel.Regions.RecordAuthoritativeBackingReplacement(s.Owner, replacement).Error);
        Assert.True(s.Manager.ReleasePool(retained).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.Contains(s.Kernel.QueryBudget(budget.ProcessBudget).Value!.Usage,
            amount => amount.Dimension == ServiceBudgetDimension.OwnedMemoryBytes && amount.Used == 64);
        if (!replaceBeforeCleanup)
        {
            Assert.Equal(damage, Assert.Single(s.Kernel.Regions.SnapshotDamage()));
            for (int retry = 0; retry < 2; retry++)
            {
                Assert.Equal(KernelError.Quarantined, s.Kernel.TerminateProcess(s.Handle).Error);
                Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
                Assert.Contains(s.Kernel.QueryBudget(budget.ProcessBudget).Value!.Usage,
                    amount => amount.Dimension == ServiceBudgetDimension.OwnedMemoryBytes && amount.Used == 64);
            }
        }
        Assert.True(s.Kernel.Regions.RecordAuthoritativeBackingReplacement(s.Owner, replacement).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.All(s.Kernel.QueryBudget(budget.ProcessBudget).Value!.Usage, amount => Assert.Equal(0UL, amount.Used));
        Assert.Equal(1, hook.AssignCalls);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.Equal(256, s.Model.QueryPool(new("damage-pool")).Value!.AvailableCapacityBytes);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PoolPublicationRequiresActiveUseAndRetainsKnownReceiptForCleanup(bool releaseUse)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Model.RegisterPool(new("publication-pool"), 256).IsSuccess);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        hook!.AfterAssign = () =>
        {
            if (releaseUse)
            {
                RegionUseDescriptor active = Assert.Single(s.Kernel.Regions.SnapshotUses(), use => use.Region == buffer.Handle && use.State == RegionUseState.Active);
                Assert.True(s.Kernel.Regions.ReleaseUse(active.Handle, s.Owner).IsSuccess);
            }
        };
        KernelResult<CxlOwnedPoolAllocation> result = s.Manager.AssignPool(new("publication-pool"), s.Endpoint.EndpointId, buffer.Handle, s.Owner);
        Assert.Equal(!releaseUse, result.IsSuccess);
        if (releaseUse) Assert.Equal(KernelError.InvalidRegionState, result.Error);
        IDictionary records = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority)
            .GetField("_poolAllocations", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Manager)!;
        object record = records[buffer.Handle]!;
        Assert.Equal(!releaseUse, record.GetType().GetProperty("Published")!.GetValue(record));
        Assert.Equal(true, record.GetType().GetProperty("ReceiptValidated")!.GetValue(record));
        Assert.Equal(1, hook.AssignCalls);
        Assert.Equal(0, hook.ReleaseCalls);
        CxlOwnedPoolAllocation retained = (CxlOwnedPoolAllocation)record.GetType().GetProperty("Allocation")!.GetValue(record)!;
        Assert.True(s.Manager.ReleasePool(retained).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(256, s.Model.QueryPool(new("publication-pool")).Value!.AvailableCapacityBytes);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedPoolReceiptIsRetainedWithoutPublicationOrOwnerOverwrite(int fault)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Model.RegisterPool(new("receipt-pool"), 256).IsSuccess);
        OwnedBuffer<byte> first = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        CxlOwnedPoolAllocation original = s.Manager.AssignPool(new("receipt-pool"), s.Endpoint.EndpointId, first.Handle, s.Owner).Value!;
        OwnedBuffer<byte> second = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        CxlPoolAssignment? returned = null;
        hook!.AssignmentTransform = receipt => returned = fault switch
        {
            0 => receipt with { AssignmentId = new(0) },
            1 => receipt with { Generation = 0 },
            2 => receipt with { PoolId = new("wrong-pool") },
            3 => receipt with { EndpointId = new("wrong-endpoint") },
            4 => receipt with { CapacityBytes = 63 },
            _ => receipt with { AssignmentId = original.Assignment.AssignmentId },
        };
        KernelResult<CxlOwnedPoolAllocation> result = s.Manager.AssignPool(new("receipt-pool"), s.Endpoint.EndpointId, second.Handle, s.Owner);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        IDictionary records = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority)
            .GetField("_poolAllocations", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Manager)!;
        object firstRecord = records[first.Handle]!;
        object badRecord = records[second.Handle]!;
        Assert.Equal(original, firstRecord.GetType().GetProperty("Allocation")!.GetValue(firstRecord));
        CxlOwnedPoolAllocation retained = (CxlOwnedPoolAllocation)badRecord.GetType().GetProperty("Allocation")!.GetValue(badRecord)!;
        Assert.Equal(returned, retained.Assignment);
        Assert.Equal(false, badRecord.GetType().GetProperty("ReceiptValidated")!.GetValue(badRecord));
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Manager.ReleasePool(retained).Error);
        Assert.Equal(0, hook.ReleaseCalls);
        Assert.True(s.Kernel.Regions.ValidateUse(retained.RegionUse, s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(0, hook.ReleaseCalls);
        Assert.True(s.Kernel.Regions.Validate(second.Handle, s.Owner).IsSuccess);
        Assert.True(s.Manager.ReleasePool(original).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
        Assert.Equal(192, s.Model.QueryPool(new("receipt-pool")).Value!.AvailableCapacityBytes);
    }
    [Fact]
    public async Task ExactPoolReleaseCannotOverlapProviderCallback()
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Model.RegisterPool(new("release-pool"), 256).IsSuccess);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        CxlOwnedPoolAllocation allocation = s.Manager.AssignPool(new("release-pool"), s.Endpoint.EndpointId, buffer.Handle, s.Owner).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        hook!.AfterRelease = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        Task<KernelResult> task = Task.Run(() => s.Manager.ReleasePool(allocation));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            KernelResult overlap = await Task.Run(() => s.Manager.ReleasePool(allocation)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.PlatformBindingDraining, overlap.Error);
            KernelResult close = await Task.Run(() => ((ICxlTeardownParticipant)s.Manager).CloseForProcess(s.Handle, s.Owner)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.PlatformBindingDraining, close.Error);
            Assert.Equal(1, hook.ReleaseCalls);
            Assert.True(s.Kernel.Regions.ValidateUse(allocation.RegionUse, s.Owner).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.True((await task.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(1, hook.ReleaseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PoolReleaseRetryRequiresReturnedClosureBeforeSkippingProvider(bool lostReply)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Model.RegisterPool(new("retry-pool"), 256).IsSuccess);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        CxlOwnedPoolAllocation allocation = s.Manager.AssignPool(new("retry-pool"), s.Endpoint.EndpointId, buffer.Handle, s.Owner).Value!;
        if (lostReply) hook!.AfterRelease = () => throw new IOException("lost release reply");
        else s.Manager.BeforePoolUseRelease = () => throw new IOException("local cleanup interrupted");
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Manager.ReleasePool(allocation).Error);
        Assert.True(s.Kernel.Regions.ValidateUse(allocation.RegionUse, s.Owner).IsSuccess);
        hook!.AfterRelease = null;
        s.Manager.BeforePoolUseRelease = null;
        KernelResult retry = s.Manager.ReleasePool(allocation);
        Assert.Equal(!lostReply, retry.IsSuccess);
        Assert.Equal(lostReply ? 2 : 1, hook.ReleaseCalls);
        Assert.Equal(!lostReply, s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        if (lostReply) Assert.True(s.Kernel.Regions.Validate(buffer.Handle, s.Owner).IsSuccess);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PoolAssignmentPendingSpansCallbackAndReceiptTracking(bool throwAfterEffect)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Model.RegisterPool(new("pending-pool"), 256).IsSuccess);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        hook!.AfterAssign = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            if (throwAfterEffect) throw new IOException("receipt lost after assignment");
        };
        Task<KernelResult<CxlOwnedPoolAllocation>> task = Task.Run(() => s.Manager.AssignPool(new("pending-pool"), s.Endpoint.EndpointId, buffer.Handle, s.Owner));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            KernelResult<CxlOwnedPoolAllocation> overlap = await Task.Run(() => s.Manager.AssignPool(new("pending-pool"), s.Endpoint.EndpointId, buffer.Handle, s.Owner)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.PlatformBindingActive, overlap.Error);
            KernelResult close = await Task.Run(() => ((ICxlTeardownParticipant)s.Manager).CloseForProcess(s.Handle, s.Owner)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.PlatformBindingDraining, close.Error);
            Assert.Equal(1, hook.AssignCalls);
            Assert.Equal(0, hook.ReleaseCalls);
        }
        finally { resume.Set(); }
        KernelResult<CxlOwnedPoolAllocation> result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        hook.AfterAssign = null;
        if (throwAfterEffect)
        {
            Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
            Assert.Equal(KernelError.ExternalEffectUncontained, ((ICxlTeardownParticipant)s.Manager).CloseForProcess(s.Handle, s.Owner).Error);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(0, hook.ReleaseCalls);
        }
        else
        {
            Assert.True(result.IsSuccess);
            Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.Equal(1, hook.ReleaseCalls);
        }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void NotAcceptedBeginReopensOnlyExactObservedSource(int fault)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        hook!.RejectBegin = true;
        hook.RejectedQueryFault = fault;
        KernelResult<CxlFabricManagedBinding> begin = s.Manager.BeginReconfiguration(s.Binding);
        Assert.Equal(fault == 0 ? KernelError.PlatformUnavailable : KernelError.ExternalEffectUncontained, begin.Error);
        Assert.Equal(fault == 0 ? CxlFabricResourceState.Bound : CxlFabricResourceState.Faulted,
            BindingFact(s.Manager, s.Binding.BindingId, "State"));
        Assert.Null(BindingFact(s.Manager, s.Binding.BindingId, "Ticket"));
        Assert.Equal(s.Binding, BindingFact(s.Manager, s.Binding.BindingId, "Binding"));
        hook.RejectedQueryFault = 0;
        Assert.Equal(fault == 0, s.Manager.ValidateAdmission(s.Binding).IsSuccess);
        Assert.Equal(0, hook.Calls);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MalformedBeginTicketCannotPermitCompletionOrReopenAdmission(int fault)
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        hook!.BeginTransform = ticket => fault switch
        {
            0 => ticket with { ReconfigurationId = new(0) },
            1 => ticket with { PreviousBinding = ticket.PreviousBinding with { EndpointId = new("wrong") } },
            2 => ticket with { ReplacementGeneration = ticket.PreviousBinding.Generation },
            _ => ticket with { ReplacementGeneration = new(0) },
        };
        KernelResult<CxlFabricManagedBinding> begin = s.Manager.BeginReconfiguration(s.Binding);
        Assert.Equal(KernelError.ExternalEffectUncontained, begin.Error);
        Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.ValidateAdmission(s.Binding).Error);
        Assert.Equal(KernelError.InvalidTransition, s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
        Assert.Equal(0, hook.Calls);
        Assert.NotNull(BindingFact(s.Manager, s.Binding.BindingId, "Ticket"));
        Assert.Equal(CxlFabricResourceState.Faulted, BindingFact(s.Manager, s.Binding.BindingId, "State"));
    }

    [Fact]
    public void ReturnedCompletionReceiptSurvivesFailedQueryWithoutPublicationPermission()
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        hook!.AfterQuery = () => throw new IOException("lost observation after returned completion");
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
        hook.AfterQuery = null;
        CxlFabricBinding receipt = s.Model.QueryResource(s.Binding.BindingId).Value!.Binding;
        Assert.Equal(receipt, BindingFact(s.Manager, s.Binding.BindingId, "LastCompletionReceipt"));
        Assert.Equal(s.Binding, BindingFact(s.Manager, s.Binding.BindingId, "Binding"));
        Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.ValidateAdmission(receipt).Error);
        Assert.True(s.Manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
        Assert.True(s.Manager.ValidateAdmission(receipt).IsSuccess);
        Assert.False(s.Manager.ValidateAdmission(s.Binding).IsSuccess);
        Assert.Equal(1, hook.Calls);
    }

    private static object? BindingFact(CxlFabricManagerAuthority manager, CxlFabricBindingId id, string name)
    {
        IDictionary records = (global::System.Collections.IDictionary)typeof(CxlFabricManagerAuthority)
            .GetField("_bindings", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        return records[id]!.GetType().GetProperty(name)!.GetValue(records[id]);
    }
    [Fact]
    public async Task ConcurrentRegisterHasOneOwnerAndDoesNotHoldGateDuringQuery()
    {
        Scenario s = CreateScenario();
        CompletionHook hook = new CompletionHook(s.Model);
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(s.Kernel, hook);
        using CountdownEvent entered = new CountdownEvent(2);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        hook.AfterQuery = () =>
        {
            entered.Signal();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        Task<KernelResult<CxlFabricManagedBinding>> first = Task.Run(() => manager.Register(s.Binding));
        Task<KernelResult<CxlFabricManagedBinding>> second = Task.Run(() => manager.Register(s.Binding));
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); }
        finally { resume.Set(); }
        KernelResult<CxlFabricManagedBinding>[] results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => !result.IsSuccess && result.Error == KernelError.PlatformDenied);
        hook.AfterQuery = null;
        Assert.True(manager.ValidateAdmission(s.Binding).IsSuccess);
        Assert.True(manager.BeginReconfiguration(s.Binding).IsSuccess);
    }

    [Fact]
    public void ReentrantRegisterKeepsFirstRecordAndRejectsOuterCommit()
    {
        Scenario s = CreateScenario();
        CompletionHook hook = new CompletionHook(s.Model);
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(s.Kernel, hook);
        hook.AfterQuery = () =>
        {
            hook.AfterQuery = null;
            Assert.True(manager.Register(s.Binding).IsSuccess);
            Assert.True(manager.BeginReconfiguration(s.Binding).IsSuccess);
        };
        Assert.Equal(KernelError.PlatformDenied, manager.Register(s.Binding).Error);
        Assert.Equal(KernelError.PlatformBindingDraining, manager.ValidateAdmission(s.Binding).Error);
        Assert.True(manager.CompleteReconfiguration(s.Binding.BindingId).IsSuccess);
    }

    [Fact]
    public async Task CompletionCallbackDoesNotHoldManagerGateAndCannotOverlap()
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        hook!.BeforeComplete = () =>
        {
            Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        Task<KernelResult<CxlFabricManagedBinding>> completion = Task.Run(() => s.Manager.CompleteReconfiguration(s.Binding.BindingId));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            KernelResult admission = await Task.Run(() => s.Manager.ValidateAdmission(s.Binding)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.PlatformBindingDraining, admission.Error);
            KernelResult<CxlFabricManagedBinding> overlap = await Task.Run(() => s.Manager.CompleteReconfiguration(s.Binding.BindingId)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.PlatformBindingDraining, overlap.Error);
            Assert.Equal(1, hook.Calls);
        }
        finally { resume.Set(); }
        KernelResult<CxlFabricManagedBinding> completed = await completion;
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(KernelError.InvalidTransition, s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
        Assert.True(s.Manager.ValidateAdmission(completed.Value!.Binding).IsSuccess);
    }

    [Fact]
    public void CompletionReceiptCannotSubstituteAnotherEndpoint()
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        hook!.ReplaceEndpoint = true;
        KernelResult<CxlFabricManagedBinding> completed = s.Manager.CompleteReconfiguration(s.Binding.BindingId);
        Assert.Equal(KernelError.PlatformFaulted, completed.Error);
        Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.ValidateAdmission(s.Binding).Error);
        Assert.Equal(KernelError.PlatformFaulted, s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
        Assert.Equal(1, hook.Calls);
    }

    [Fact]
    public void ResetAfterCompletionReceiptCannotReopenAdmission()
    {
        CompletionHook? hook = null;
        Scenario s = CreateScenario(providerFactory: model => hook = new(model));
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        hook!.AfterComplete = () => Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
        CxlFabricBinding rebound = s.Model.Query(s.Binding.BindingId).Value!;
        Assert.NotEqual(s.Binding, rebound);
        Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.ValidateAdmission(rebound).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Manager.CompleteReconfiguration(s.Binding.BindingId).Error);
        Assert.Equal(1, hook.Calls);
    }

    [Fact]
    public void LostPrincipalQueryDoesNotProveTrackedOperationDrain()
    {
        Scenario s = CreateScenario();
        OperationPreparation operation = PrepareOperation(s);
        Assert.True(s.Manager.TrackOperation(s.Binding, s.Handle, operation.Operation).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        KernelResult<ExternalOperationSnapshot> missing = s.Kernel.QueryExternalOperation(s.Handle, operation.Operation);
        Assert.False(missing.IsSuccess);

        KernelResult<CxlFabricManagedBinding> drained = s.Manager.BeginReconfiguration(s.Binding);

        Assert.False(drained.IsSuccess);
        Assert.Equal(missing.Error, drained.Error);
        Assert.Equal(s.Binding, s.Model.Query(s.Binding.BindingId).Value);
        Assert.Equal(CxlFabricResourceState.Bound, s.Model.QueryResource(s.Binding.BindingId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.ValidateAdmission(s.Binding).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ReconfigurationDrainsEveryTrackedLifecycleStage(int stage)
    {
        Scenario s = CreateScenario();
        OperationPreparation operation = PrepareOperation(s);
        OperationBinding? operationBinding = null;
        if (stage >= 1) Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).IsSuccess);
        if (stage >= 2) operationBinding = s.Kernel.RecordExternalOperationSubmission(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).Value!;
        if (stage >= 3) Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Handle, new(operationBinding!.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        if (stage >= 4) Assert.True(s.Kernel.RecordExternalOperationVisibility(s.Handle, new(operationBinding!.Value, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        if (stage >= 5) Assert.True(s.Kernel.PublishExternalOperation(s.Handle, operation.Operation,
            new(1, 1, s.Binding.Generation.Value), new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);
        bool closureInvoked = false;
        Func<KernelResult>? providerClosure = stage >= 2
            ? () => { closureInvoked = true; return KernelResult.Ok(); }
            : null;
        Assert.True(s.Manager.TrackOperation(s.Binding, s.Handle, operation.Operation, providerClosure).IsSuccess);

        KernelResult<CxlFabricManagedBinding> draining = s.Manager.BeginReconfiguration(s.Binding);
        Assert.True(draining.IsSuccess, draining.Message);
        Assert.Equal(CxlFabricResourceState.Draining, draining.Value!.State);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.QueryExternalOperation(s.Handle, operation.Operation).Value!.State);
        Assert.False(s.Manager.ValidateAdmission(s.Binding).IsSuccess);
        Assert.Equal(stage >= 2, closureInvoked);
    }

    [Fact]
    public void PostSubmitTrackingWithoutProviderClosureProofIsRejected()
    {
        Scenario s = CreateScenario();
        OperationPreparation operation = PrepareOperation(s);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationSubmission(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).IsSuccess);

        KernelResult tracked = s.Manager.TrackOperation(s.Binding, s.Handle, operation.Operation);

        Assert.False(tracked.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, tracked.Error);
    }

    [Fact]
    public void OldOpaqueBindingNeverReopensAfterReplacement()
    {
        Scenario s = CreateScenario();
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        KernelResult<CxlFabricManagedBinding> replacement = s.Manager.CompleteReconfiguration(s.Binding.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        Assert.NotEqual(s.Binding.Generation, replacement.Value!.Binding.Generation);
        Assert.Equal(KernelError.StaleGeneration, s.Manager.ValidateAdmission(s.Binding).Error);
        Assert.True(s.Manager.ValidateAdmission(replacement.Value.Binding).IsSuccess);
    }

    [Fact]
    public void UnrelatedEndpointGenerationRemainsValid()
    {
        Scenario s = CreateScenario(twoBindings: true);
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        Assert.True(s.Manager.ValidateAdmission(s.OtherBinding!).IsSuccess);
        Assert.Equal(new CxlFabricBindingGeneration(1), s.OtherBinding!.Generation);
    }

    [Fact]
    public void PoolAssignmentPinsNormalOwnershipUntilExplicitRelease()
    {
        Scenario s = CreateScenario();
        Assert.True(s.Model.RegisterPool(new("pool-a"), 1024).IsSuccess);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        KernelResult<CxlOwnedPoolAllocation> allocation = s.Manager.AssignPool(new("pool-a"), s.Endpoint.EndpointId, buffer.Handle, s.Owner);
        Assert.True(allocation.IsSuccess, allocation.Message);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(s.Kernel, 993, 994);
        Assert.Equal(KernelError.RegionUseConflict, s.Kernel.TransferRegion(s.Handle, target, buffer).Error);
        Assert.True(s.Manager.ReleasePool(allocation.Value!).IsSuccess);
        Assert.True(s.Kernel.TransferRegion(s.Handle, target, buffer).IsSuccess);
    }

    [Fact]
    public void StalePoolRelease_DoesNotDeleteCurrentAssignmentOrChangeCapacity()
    {
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("pool-endpoint"), new("device:pool-endpoint"), 1024).IsSuccess);
        Assert.True(model.RegisterPool(new("pool-exact"), 256).IsSuccess);
        CxlPoolAssignment assignment = model.AssignPoolCapacity(new("pool-exact"), new("pool-endpoint"), 64).Value!;
        CxlFabricPoolSnapshot before = model.QueryPool(new("pool-exact")).Value!;
        CxlPoolAssignment stale = assignment with { Generation = assignment.Generation + 1 };

        Assert.Equal(PlatformAuthorityStatus.Stale, model.ReleasePoolCapacity(stale).Status);
        Assert.Equal(before, model.QueryPool(new("pool-exact")).Value);
        Assert.True(model.ReleasePoolCapacity(assignment).IsSuccess);
        Assert.Equal(256, model.QueryPool(new("pool-exact")).Value!.AvailableCapacityBytes);
    }

    [Fact]
    public void AmbiguousPoolAssignmentAcceptanceKeepsRegionPinnedAndBlocksReclaim()
    {
        Scenario s = CreateScenario();
        Assert.True(s.Model.RegisterPool(new("pool-ambiguous"), 256).IsSuccess);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        s.Model.PoolAcceptanceAmbiguous = true;

        KernelResult<CxlOwnedPoolAllocation> allocation = s.Manager.AssignPool(new("pool-ambiguous"), s.Endpoint.EndpointId, buffer.Handle, s.Owner);

        Assert.False(allocation.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, allocation.Error);
        Assert.Contains(s.Kernel.Regions.SnapshotUses(), use => use.Region == buffer.Handle && use.State == RegionUseState.Active);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(buffer.Handle, s.Owner).IsSuccess);
    }

    [Fact]
    public void StaleReconfigurationTicket_DoesNotConsumeCurrentTicket()
    {
        Scenario s = CreateScenario();
        CxlFabricReconfigurationTicket ticket = s.Model.BeginReconfiguration(s.Binding).Value!;
        CxlFabricReconfigurationTicket stale = ticket with { ReplacementGeneration = new(ticket.ReplacementGeneration.Value + 1) };

        Assert.Equal(PlatformAuthorityStatus.Stale, s.Model.CompleteReconfiguration(stale).Status);
        PlatformAuthorityResult<CxlFabricBinding> completed = s.Model.CompleteReconfiguration(ticket);
        Assert.True(completed.IsSuccess);
        Assert.Equal(ticket.ReplacementGeneration, completed.Value!.Generation);
    }

    [Fact]
    public void EndpointGenerationChangeDuringReconfiguration_DoesNotRollbackGeneration()
    {
        Scenario s = CreateScenario();
        CxlFabricReconfigurationTicket ticket = s.Model.BeginReconfiguration(s.Binding).Value!;
        Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        CxlFabricBinding current = s.Model.Query(s.Binding.BindingId).Value!;

        Assert.Equal(PlatformAuthorityStatus.Stale, s.Model.CompleteReconfiguration(ticket).Status);
        Assert.Equal(current, s.Model.Query(s.Binding.BindingId).Value);
        Assert.True(current.Generation.Value > s.Binding.Generation.Value);
        Assert.Equal(CxlFabricResourceState.Bound, s.Model.QueryResource(s.Binding.BindingId).Value!.State);
    }

    [Fact]
    public void CompleteReconfigurationExceptionAfterProviderEffectFailsClosed()
    {
        Scenario s = CreateScenario();
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        s.Model.CompleteReconfigurationThrowsAfterEffect = true;

        KernelResult<CxlFabricManagedBinding> completed = s.Manager.CompleteReconfiguration(s.Binding.BindingId);

        Assert.False(completed.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, completed.Error);
        Assert.NotEqual(s.Binding, s.Model.Query(s.Binding.BindingId).Value);
        Assert.Equal(KernelError.PlatformBindingDraining, s.Manager.ValidateAdmission(s.Binding).Error);
    }

    [Fact]
    public void PeerAccessRequiresBothRouteSupportAndPlatformIsolation()
    {
        Scenario s = CreateScenario(twoBindings: true);
        s.Model.PeerAccessSupported = true;
        KernelResult<CxlPeerAccessEvidence> noIsolation = s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding!.EndpointId, 16, false), s.Binding, s.OtherBinding);
        Assert.Equal(KernelError.PlatformDenied, noIsolation.Error);
        s.Model.PeerAccessSupported = false;
        KernelResult<CxlPeerAccessEvidence> noRoute = s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding!.EndpointId, 16, true), s.Binding, s.OtherBinding);
        Assert.Equal(KernelError.PlatformUnsupported, noRoute.Error);
        s.Model.PeerAccessSupported = true;
        Assert.True(s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding!.EndpointId, 16, true), s.Binding, s.OtherBinding).IsSuccess);
        Assert.Equal(KernelError.PlatformDenied,
            s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding.EndpointId, 16, true)).Error);
    }

    private static OperationPreparation PrepareOperation(Scenario s) =>
        s.Kernel.PrepareExternalOperation(s.Handle,
            [new(s.OperationInput.Handle, RegionUseMode.ReadOnly, new(0, 8)),
             new(s.OperationOutput.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;

    private static Scenario CreateScenario(bool twoBindings = false, Func<CxlType3ModelProvider, ICxlFabricManagementProvider>? providerFactory = null)
    {
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("fm-0"), new("device:fm-0"), 1024).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("fm-0")).Value!;
        CxlFabricBinding binding = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 128,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        CxlFabricBinding? other = null;
        if (twoBindings)
        {
            Assert.True(model.RegisterEndpoint(new("fm-1"), new("device:fm-1"), 1024).IsSuccess);
            CxlEndpointSnapshot e = model.QueryEndpoint(new("fm-1")).Value!;
            other = model.Bind(new(e.EndpointId, e.DeviceGeneration, 128, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        }
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 991, 992);
        SingProcess process = kernel.Processes.Resolve(handle).Value!;
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(kernel, providerFactory?.Invoke(model) ?? model);
        Assert.True(manager.Register(binding).IsSuccess);
        if (other is not null) Assert.True(manager.Register(other).IsSuccess);
        return new(kernel, handle, new(process.DomainId, handle.Generation), model, manager, endpoint, binding, other,
            kernel.AllocateBuffer<byte>(handle, 8).Value!, kernel.AllocateBuffer<byte>(handle, 8).Value!);
    }

    private sealed class CompletionHook(ICxlFabricManagementProvider inner) : ICxlFabricManagementProvider
    {
        public PlatformAuthorityStatus? CompletionFailure { get; set; }
        public Func<PlatformAuthorityResult<CxlFabricResourceSnapshot>, PlatformAuthorityResult<CxlFabricResourceSnapshot>>? QueryTransform { get; set; }
        public Action? BeforeComplete { get; set; }
        public Action? BeforeBegin { get; set; }
        public Action? AfterBegin { get; set; }
        public int BeginCalls { get; private set; }
        public Action? AfterComplete { get; set; }
        public Action? AfterQuery { get; set; }
        public Action? AfterAssign { get; set; }
        public Action? AfterRelease { get; set; }
        public Func<CxlPoolAssignment, CxlPoolAssignment>? AssignmentTransform { get; set; }
        public int AssignCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public bool ReplaceEndpoint { get; set; }
        public bool RejectBegin { get; set; }
        public int RejectedQueryFault { get; set; }
        private bool _beginRejected;
        public Func<CxlFabricReconfigurationTicket, CxlFabricReconfigurationTicket>? BeginTransform { get; set; }
        public int Calls { get; private set; }
        public PlatformAuthorityResult<CxlFabricBinding> CompleteReconfiguration(CxlFabricReconfigurationTicket ticket)
        {
            Calls++;
            BeforeComplete?.Invoke();
            if (CompletionFailure is { } failure)
                return PlatformAuthorityResult<CxlFabricBinding>.Fail(failure, "completion refused");
            PlatformAuthorityResult<CxlFabricBinding> result = inner.CompleteReconfiguration(ticket);
            AfterComplete?.Invoke();
            return ReplaceEndpoint && result.IsSuccess
                ? PlatformAuthorityResult<CxlFabricBinding>.Ok(result.Value! with { EndpointId = new("substituted") }) : result;
        }
        public PlatformAuthorityResult<CxlFabricResourceSnapshot> QueryResource(CxlFabricBindingId id)
        {
            PlatformAuthorityResult<CxlFabricResourceSnapshot> result = inner.QueryResource(id);
            AfterQuery?.Invoke();
            if (QueryTransform is { } transform) return transform(result);
            if (_beginRejected && RejectedQueryFault != 0)
                return RejectedQueryFault switch
                {
                    1 => PlatformAuthorityResult<CxlFabricResourceSnapshot>.Ok(result.Value! with { Binding = result.Value!.Binding with { Generation = new(result.Value.Binding.Generation.Value + 1) } }),
                    2 => PlatformAuthorityResult<CxlFabricResourceSnapshot>.Ok(result.Value! with { State = CxlFabricResourceState.Draining }),
                    3 => PlatformAuthorityResult<CxlFabricResourceSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "source lost"),
                    _ => throw new IOException("query failed"),
                };
            return result;
        }
        public PlatformAuthorityResult<CxlFabricReconfigurationTicket> BeginReconfiguration(CxlFabricBinding binding)
        {
            BeginCalls++;
            BeforeBegin?.Invoke();
            if (RejectBegin)
            {
                _beginRejected = true;
                return PlatformAuthorityResult<CxlFabricReconfigurationTicket>.Fail(PlatformAuthorityStatus.NotAccepted, "zero effect rejection");
            }
            PlatformAuthorityResult<CxlFabricReconfigurationTicket> result = inner.BeginReconfiguration(binding);
            AfterBegin?.Invoke();
            return result.IsSuccess && BeginTransform is not null
                ? PlatformAuthorityResult<CxlFabricReconfigurationTicket>.Ok(BeginTransform(result.Value!)) : result;
        }
        public PlatformAuthorityResult<CxlFabricPoolSnapshot> QueryPool(CxlFabricPoolId id) => inner.QueryPool(id);
        public PlatformAuthorityResult<CxlPoolAssignment> AssignPoolCapacity(CxlFabricPoolId pool, CxlEndpointId endpoint, long bytes)
        {
            AssignCalls++;
            PlatformAuthorityResult<CxlPoolAssignment> result = inner.AssignPoolCapacity(pool, endpoint, bytes);
            AfterAssign?.Invoke();
            return result.IsSuccess && AssignmentTransform is not null
                ? PlatformAuthorityResult<CxlPoolAssignment>.Ok(AssignmentTransform(result.Value!)) : result;
        }
        public PlatformAuthorityResult ReleasePoolCapacity(CxlPoolAssignment assignment)
        {
            ReleaseCalls++;
            PlatformAuthorityResult result = inner.ReleasePoolCapacity(assignment);
            AfterRelease?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlPeerAccessEvidence> QueryPeerAccess(CxlPeerAccessRequest request) => inner.QueryPeerAccess(request);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle, RegionOwner Owner,
        CxlType3ModelProvider Model, CxlFabricManagerAuthority Manager, CxlEndpointSnapshot Endpoint,
        CxlFabricBinding Binding, CxlFabricBinding? OtherBinding,
        SingPlus.Sip.OwnedBuffer<byte> OperationInput, SingPlus.Sip.OwnedBuffer<byte> OperationOutput);
}
