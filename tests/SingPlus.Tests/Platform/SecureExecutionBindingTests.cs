using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class SecureExecutionBindingTests
{
    private sealed class ObservedSecureProperties(Action observe) : IReadOnlyList<SecureProperty>
    {
        public int Count { get { observe(); return 1; } }
        public SecureProperty this[int index] { get { observe(); return index == 0 ? SecureProperty.PrivateMemory : throw new IndexOutOfRangeException(); } }
        public IEnumerator<SecureProperty> GetEnumerator() { observe(); yield return SecureProperty.PrivateMemory; }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void SecureCompositionDoesNotEnumerateProviderMetadataUnderOwnerCommitLocks()
    {
        var observations = 0;
        var s = Create(provider => provider.SecureProperties = new ObservedSecureProperties(() =>
        {
            observations++;
            provider.CheckLocks!.Invoke();
        }));
        Assert.True(observations > 0);
        var beforeBind = observations;
        var bound = Bind(s);
        Assert.True(bound.IsSuccess, bound.Message);
        Assert.Equal(beforeBind, observations);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.True(s.Kernel.CloseSecureExecution(bound.Value).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EqualTupleForeignRootCannotAdmitOrPublishSecureComposition(bool duringBind)
    {
        var s = Create();
        Assert.True(s.Kernel.AllocateRegion(s.Owner, 4096).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = s.Kernel.PlatformAuthority;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(bridge)!;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(bridge)!;
        var original = routes[s.Parent.BindingId]!;
        void Replace()
        {
            var foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            routes[s.Parent.BindingId] = foreign;
            originals[s.Parent.Subject] = foreign;
        }
        if (duringBind) s.Provider.DuringBind = Replace;
        else Replace();
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.CloseCalls);
        if (duringBind)
        {
            Assert.NotNull(s.Provider.LastCloseReceipt);
            Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseRequest);
            Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseReceipt!.Value.Request);
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
    }

    private sealed class ClosureTraceSink(Func<SemanticTraceEventV1, bool> observe) : ISemanticTraceSinkV1
    {
        public bool TryRecord(SemanticTraceEventV1 traceEvent) => observe(traceEvent);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ConcurrentActualSecureGuestBindRetainsOriginalCxlSourceAndPending(bool memoryRoute, bool foreign, bool stop)
    {
        var o = BackedOverlayScenario(); var s = o.S;
        var bridge = (CxlAuthorityBridge)typeof(CxlType3MemoryAuthority)
            .GetField("_bridge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o.Memory)!;
        var memory = bridge.QueryMemory(o.Placement.MemoryBindingId).Value!;
        var routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        object key = memoryRoute ? o.Placement.MemoryBindingId : memory.FabricBinding.BindingId;
        var original = routes[key]!;
        var gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Provider.SecureRegionClosureFault = true;
        s.Provider.DuringSecureRegionBind = () =>
        {
            s.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        };
        var binding = Task.Run(() => s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, s.Provider.SecureRegionBindCalls);
            Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
            Assert.False(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
            Assert.Equal(0, s.Provider.CloseCalls);
            Assert.False(o.Memory.Close(o.Placement.PlacementId).IsSuccess);
            Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
            Assert.Equal(1, s.Provider.SecureRegionBindCalls);
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            if (stop) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
        finally { resume.Set(); }
        var result = await binding.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        _ = RetainedOverlay(s);
        AssertOverlayPendingBalanced(s);
        Assert.Equal(memory, o.Model.QueryMemory(memory.BindingId).Value!);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ConcurrentActualComposedActionCannotCommitThroughLostOriginalCxlBacking(bool memoryRoute, bool foreign, bool stop)
    {
        var c = CreateComposedWorkload();
        var placement = c.OutputPlacement;
        var memory = c.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        var routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : memory.FabricBinding.BindingId;
        var original = routes[key]!;
        var gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = 0;
        var publishing = Task.Run(() => c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            c.Base.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            Interlocked.Increment(ref actions);
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var overlappingActions = 0;
            Assert.False(c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => overlappingActions++).IsSuccess);
            Assert.Equal(0, overlappingActions);
            Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
                c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
            Assert.False(c.Memory.Close(placement.PlacementId).IsSuccess);
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            if (stop) Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        }
        finally { resume.Set(); }
        var result = await publishing.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, actions);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.Contains(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        AssertType2OwnerTrace(c, operation, published: false);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.False(c.Base.Kernel.QueryProcessTeardown(c.Base.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(memory, c.Model.QueryMemory(memory.BindingId).Value!);
        AssertOverlayPendingBalanced(c.Base);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 3)]
    public void ActualSecureGuestCxlBackingLossRefusesEffectOrRetainsKnownOverlay(bool memoryRoute, bool foreign, int lossStage)
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var owner = new RegionOwner(process.DomainId, s.Owner.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-backing-source", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-backing-source"), device.Device, 4096).IsSuccess);
        var endpoint = model.QueryEndpoint(new("secure-backing-source")).Value!;
        var bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, model,
            new UnsupportedCoherentProvider(), new CxlSecurityModelProvider());
        var memory = new CxlType3MemoryAuthority(s.Kernel, bridge);
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var placement = memory.Place(owner, buffer.Handle, subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;
        var binding = bridge.QueryMemory(placement.MemoryBindingId).Value!;
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        var execution = Bind(s).Value;
        var routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : binding.FabricBinding.BindingId;
        var original = routes[key]!;
        var gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var lost = false;
        void Lose()
        {
            s.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            lost = true;
        }
        if (lossStage == 0) Lose();
        if (lossStage == 1) s.Kernel.BeforeSecureGuestRegionAdmission = Lose;
        if (lossStage == 2) s.Provider.DuringSecureRegionBind = Lose;
        if (lossStage == 3) s.Kernel.BeforeSecureGuestRegionPublication = Lose;
        if (lossStage >= 2) s.Provider.SecureRegionClosureFault = true;
        var result = s.Kernel.BindSecureGuestRegion(s.Owner, execution, guest.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(lost);
        Assert.False(result.IsSuccess);
        Assert.Equal(lossStage >= 2 ? 1 : 0, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
        SecureGuestRegionBinding? retained = null;
        if (lossStage >= 2)
        {
            retained = RetainedOverlay(s);
            Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
        }
        Assert.Equal(binding, model.QueryMemory(binding.BindingId).Value!);
        Assert.False(s.Kernel.ReleaseRegion(s.Owner, buffer).IsSuccess);
        Assert.False(s.Kernel.RevokePlatformDevice(s.Owner, device).IsSuccess);
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, execution, guest.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(lossStage >= 2 ? 1 : 0, s.Provider.SecureRegionBindCalls);
        s.Kernel.BeforeSecureGuestRegionAdmission = null;
        s.Kernel.BeforeSecureGuestRegionPublication = null;
        s.Provider.DuringSecureRegionBind = null;
        s.Provider.SecureRegionClosureFault = false;
        // Restore the exact original owner for cleanup positive; this is not durable routing.
        lock (gate) routes[key] = original;
        if (retained is { } overlay) Assert.True(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(execution).IsSuccess);
        Assert.True(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
        Assert.True(memory.Close(placement.PlacementId).IsSuccess);
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void ActualComposedPublicationRequiresOriginalCxlOperandBacking(bool memoryRoute, bool foreign, bool late, bool output)
    {
        var c = CreateComposedWorkload();
        var placement = output ? c.OutputPlacement : c.InputPlacement;
        var memory = c.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        var routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : memory.FabricBinding.BindingId;
        var original = routes[key]!;
        var gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        var lost = false;
        void Lose()
        {
            c.Base.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            lost = true;
        }
        if (!late) c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = Lose;
        var actions = 0;
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
            {
                actions++;
                if (late) Lose();
            });
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.True(lost);
        Assert.False(result.IsSuccess);
        Assert.Equal(late ? 1 : 0, actions);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.Equal(late, operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"));
        if (late) AssertType2OwnerTrace(c, operation, published: false);
        // Before action, exact provider closure may release the operation; it cannot
        // release the independent original CXL backing or guest source pins.
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.False(c.Base.Kernel.QueryProcessTeardown(c.Base.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(memory, c.Model.QueryMemory(memory.BindingId).Value!);
        AssertOverlayPendingBalanced(c.Base);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CompositionTraceRetainsDistinctMalformedReturnedClosureFacts(int fault)
    {
        SemanticTraceEventV1[] Observe(ulong value)
        {
            var s = Create(); var binding = Bind(s).Value;
            SecureExecutionClosure returned = default;
            s.Provider.ClosureTransform = closure =>
            {
                var receipt = closure.Receipt!.Value;
                returned = fault switch
                {
                    0 => closure with { Receipt = receipt with { Correlation = value } },
                    1 => closure with { Receipt = receipt with { Request = receipt.Request with { Context = receipt.Request.Context with { LocalPolicyGeneration = value } } } },
                    2 => closure with { Request = closure.Request with { Context = closure.Request.Context with { ProvenProperties = $"returned:{value}:雪" } } },
                    _ => closure with { Receipt = receipt with { ProtectionGeneration = value } },
                };
                return returned;
            };
            var trace = new List<SemanticTraceEventV1>();
            Assert.False(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
            Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
            Assert.Equal(SemanticTraceEventKindV1.Quarantined, trace[^1].Kind);
            var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            var record = records.Values.Cast<object>().Single();
            var fact = record.GetType().GetField("LastClosureReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!;
            Assert.Equal(returned, (SecureExecutionClosure)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!);
            Assert.Equal(0, s.Provider.SecureCloses); Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureTransform = null;
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(2, s.Provider.CloseCalls);
            return trace.ToArray();
        }
        var a = Observe(998); var b = Observe(999);
        Assert.Equal(a[0].GenerationVectorDigest, b[0].GenerationVectorDigest);
        Assert.Equal(a[0].EvidenceDigest, b[0].EvidenceDigest);
        Assert.Equal(a[1].EvidenceDigest, b[1].EvidenceDigest);
        Assert.NotEqual(a[^1].EvidenceDigest, b[^1].EvidenceDigest);
        Assert.All(a.Concat(b), e => Assert.False(e.AuthorizesEffect));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CompositionTraceDistinguishesNestedReceiptContext(int fault)
    {
        string Observe(bool mutate)
        {
            var s = Create(); var binding = Bind(s).Value;
            var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            var record = records.Values.Cast<object>().Single();
            var field = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var receipt = (SecureExecutionReceipt)field.GetValue(record)!;
            var c = receipt.Request.Context;
            if (mutate)
            {
                c = fault switch
                {
                    0 => c with { ProvenProperties = "é:雪|\"\n" },
                    1 => c with { Parent = c.Parent with { Generation = new(999) } },
                    2 => c with { Child = c.Child with { Owner = c.Child.Owner with { Generation = 999 } } },
                    3 => c with { ChildLease = c.ChildLease with { ParentDomainLease = c.ChildLease.ParentDomainLease with { Generation = new(999) } } },
                    4 => c with { SecureLease = c.SecureLease with { ProvenProperties = new[] { (SecureProperty)999 } } },
                    _ => c with { SecureBinding = c.SecureBinding with { Parent = c.SecureBinding.Parent with { Generation = new(999) } } },
                };
                field.SetValue(record, receipt with { Request = receipt.Request with { Context = c } });
            }
            var trace = new List<SemanticTraceEventV1>();
            Assert.True(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
            Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            return trace[0].GenerationVectorDigest;
        }
        Assert.NotEqual(Observe(false), Observe(true));
    }

    private sealed class BrokenTraceProperties(Action queried) : IReadOnlyList<SecureProperty>
    {
        public int Count => 1;
        public SecureProperty this[int index] => throw new IOException("property enumeration lost");
        public IEnumerator<SecureProperty> GetEnumerator() { queried(); throw new IOException("property enumeration lost"); }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompositionTraceSerializationFailureCannotChangeCleanup(bool returnedClosure)
    {
        var s = Create(); var binding = Bind(s).Value;
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = records.Values.Cast<object>().Single();
        var field = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var receipt = (SecureExecutionReceipt)field.GetValue(record)!;
        bool queried = false, locked = false;
        var properties = new BrokenTraceProperties(() =>
        {
            queried = true;
            foreach (var name in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                locked |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
        });
        if (returnedClosure)
            s.Provider.ClosureTransform = closure => closure with { Request = closure.Request with { Context = closure.Request.Context with {
                SecureLease = closure.Request.Context.SecureLease with { ProvenProperties = properties } } } };
        else
            field.SetValue(record, receipt with { Request = receipt.Request with { Context = receipt.Request.Context with {
                SecureLease = receipt.Request.Context.SecureLease with { ProvenProperties = properties } } } });
        var trace = new List<SemanticTraceEventV1>();
        Assert.Equal(!returnedClosure, s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
        Assert.True(queried); Assert.False(locked); Assert.Empty(trace);
        Assert.Equal(1, s.Provider.CloseCalls);
        s.Provider.ClosureTransform = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(returnedClosure ? 2 : 1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void CompositionTraceDistinguishesAdmittedReceiptMetadata(int fault)
    {
        string Observe(bool mutate)
        {
            var s = Create(); var binding = Bind(s).Value;
            var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            var record = records.Values.Cast<object>().Single();
            var field = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var receipt = (SecureExecutionReceipt)field.GetValue(record)!;
            // Fault injection into the admitted cleanup input, never production admission.
            if (mutate) field.SetValue(record, fault switch
            {
                0 => (SecureExecutionReceipt?)null,
                1 => receipt with { Correlation = receipt.Correlation + 1 },
                2 => receipt with { PolicyGeneration = receipt.PolicyGeneration + 1 },
                3 => receipt with { ProtectionGeneration = receipt.ProtectionGeneration + 1 },
                4 => receipt with { ContractVersion = receipt.ContractVersion + 1 },
                5 => receipt with { ProductionSecure = false },
                6 => receipt with { Request = receipt.Request with { Binding = new(999, 1) } },
                _ => receipt with { Request = receipt.Request with { Binding = binding with { Generation = 2 } } },
            });
            var trace = new List<SemanticTraceEventV1>();
            Assert.True(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
            Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
            Assert.All(trace, e => Assert.False(e.AuthorizesEffect));
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            return trace[0].GenerationVectorDigest;
        }
        Assert.NotEqual(Observe(false), Observe(true));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(5, false)] [InlineData(5, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    public void BindCompensationTracePreservesReceiptLossAndRetry(int admissionFault, bool refuseClosure)
    {
        var s = Create(); var trace = new List<SemanticTraceEventV1>();
        s.Provider.AdmissionFault = admissionFault;
        s.Provider.ClosureFault = refuseClosure ? 3 : -1;
        bool heldLock = false;
        var sink = new ClosureTraceSink(e =>
        {
            foreach (var field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                heldLock |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
            trace.Add(e); return true;
        });
        Assert.False(s.Kernel.BindSecureExecutionWithClosureTrace(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability,
            s.Secure.Domain, s.Secure.ConfigureCapability, sink).IsSuccess);
        Assert.False(heldLock);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(admissionFault == 0, s.Provider.LastCloseReceipt.HasValue);
        Assert.Equal(refuseClosure ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
        if (refuseClosure)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.SecureCloses);
            Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureFault = -1;
        }
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(refuseClosure ? 3 : 1, s.Provider.CloseCalls);
        Assert.Equal(refuseClosure ? 3 : 1, trace.Select(e => e.OperationCorrelation).Distinct().Count());
        foreach (var group in trace.GroupBy(e => e.OperationCorrelation))
        {
            Assert.Equal(new ulong[] { 1, 2, 3 }, group.Select(e => e.Sequence));
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
        }
        Assert.Equal(SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.All(trace, e => { Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void DomainTeardownPreservesRegisteredCompositionObserver(bool secure, bool refuse)
    {
        var s = Create(); var trace = new List<SemanticTraceEventV1>();
        bool heldLock = false;
        var sink = new ClosureTraceSink(e =>
        {
            foreach (var field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                heldLock |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
            heldLock |= Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!);
            trace.Add(e); return true;
        });
        Assert.True(s.Kernel.BindSecureExecutionWithClosureTrace(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability,
            s.Secure.Domain, s.Secure.ConfigureCapability, sink).IsSuccess);
        KernelResult Destroy() => secure
            ? s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability)
            : s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability);
        if (refuse) s.Provider.ClosureFault = 3;
        Assert.Equal(secure && !refuse, Destroy().IsSuccess);
        Assert.False(heldLock);
        Assert.Equal(1, s.Provider.CloseCalls);
        if (refuse)
        {
            Assert.Equal(SemanticTraceEventKindV1.Quarantined, trace[^1].Kind);
            Assert.Equal(0, s.Provider.SecureCloses);
            Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureFault = -1;
            Assert.Equal(secure, Destroy().IsSuccess);
            Assert.Equal(2, s.Provider.CloseCalls);
        }
        Assert.Equal(refuse ? 2 : 1, trace.Select(e => e.OperationCorrelation).Distinct().Count());
        foreach (var group in trace.GroupBy(e => e.OperationCorrelation))
        {
            Assert.Equal(new ulong[] { 1, 2, 3 }, group.Select(e => e.Sequence));
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
        }
        Assert.Equal(SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.All(trace, e => { Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        if (!secure)
            Assert.True(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(refuse ? 2 : 1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ProcessTeardownConsumesRegisteredCompositionTrace(int fault)
    {
        var s = Create(); var trace = new List<SemanticTraceEventV1>();
        var calls = 0;
        bool heldOwnerLock = false;
        var sink = new ClosureTraceSink(e =>
        {
            foreach (var field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                heldOwnerLock |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
            calls++;
            if (fault == 1) return false;
            if (fault == 2) throw new IOException("teardown observer lost");
            trace.Add(e); return true;
        });
        var binding = s.Kernel.BindSecureExecutionWithClosureTrace(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability,
            s.Secure.Domain, s.Secure.ConfigureCapability, sink).Value;
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = records.Values.Cast<object>().Single();
        var sinkField = record.GetType().GetField("ClosureTraceSink", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Same(sink, sinkField.GetValue(record));
        if (fault == 3) s.Provider.ClosureFault = 3;
        var terminated = s.Kernel.TerminateProcess(s.Owner);
        Assert.Equal(fault != 3, terminated.IsSuccess);
        Assert.False(heldOwnerLock);
        Assert.Equal(1, s.Provider.CloseCalls);
        if (fault == 3)
        {
            Assert.Same(sink, sinkField.GetValue(record));
            Assert.Equal(SemanticTraceEventKindV1.Quarantined, trace[^1].Kind);
            Assert.Equal(0, s.Provider.SecureCloses);
            Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureFault = -1;
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(2, s.Provider.CloseCalls);
            Assert.Equal(2, trace.Select(e => e.OperationCorrelation).Distinct().Count());
        }
        Assert.Null(sinkField.GetValue(record));
        Assert.Equal(fault is 1 or 2 ? 1 : fault == 3 ? 6 : 3, calls);
        foreach (var group in trace.GroupBy(e => e.OperationCorrelation))
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
        if (trace.Count != 0) Assert.Equal(SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.All(trace, e => Assert.False(e.AuthorizesEffect));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void CompositionTraceDeliveryLossNeverChangesClosure(int fault)
    {
        var s = Create(); var binding = Bind(s).Value;
        var trace = new List<SemanticTraceEventV1>();
        var calls = 0;
        if (fault == 4)
        {
            var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            var record = records.Values.Cast<object>().Single();
            record.GetType().GetField("ClosureObservationSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(record, ulong.MaxValue);
        }
        var sink = new ClosureTraceSink(e =>
        {
            s.Provider.CheckLocks?.Invoke();
            calls++;
            if (calls == (fault < 2 ? 1 : 2))
            {
                if (fault is 0 or 2) return false;
                throw new IOException("trace sink lost");
            }
            trace.Add(e); return true;
        });
        Assert.True(s.Kernel.CloseSecureExecution(binding, sink).IsSuccess);
        Assert.Equal(fault == 4 ? 0 : fault < 2 ? 1 : 2, calls);
        Assert.Equal(fault is 2 or 3 ? 1 : 0, trace.Count);
        Assert.Equal(trace.Count != 0, SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.True(s.Kernel.CloseSecureExecution(binding, sink).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompositionTraceReentrantRetryPreservesInvocationDecision(bool rejectOuter)
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.ClosureFault = 3;
        var outer = new List<SemanticTraceEventV1>();
        var inner = new List<SemanticTraceEventV1>();
        KernelResult retry = default;
        var result = s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e =>
        {
            s.Provider.CheckLocks?.Invoke();
            foreach (var field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!));
            if (outer.Count == 0)
            {
                s.Provider.ClosureFault = -1;
                retry = s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(n => { inner.Add(n); return true; }));
            }
            outer.Add(e);
            return !rejectOuter;
        }));
        Assert.False(result.IsSuccess);
        Assert.True(retry.IsSuccess);
        Assert.Equal(2, s.Provider.CloseCalls);
        Assert.Equal(rejectOuter ? 1 : 3, outer.Count);
        Assert.Equal(SemanticTraceEventKindV1.RetireOrComplete, inner[^1].Kind);
        if (!rejectOuter) Assert.Equal(SemanticTraceEventKindV1.Quarantined, outer[^1].Kind);
        Assert.True(SemanticTraceValidatorV1.Validate(outer).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(inner).IsValid);
        Assert.NotEqual(outer[0].OperationCorrelation, inner[0].OperationCorrelation);
        Assert.All(outer.Concat(inner), e => Assert.False(e.AuthorizesEffect));
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(2, s.Provider.CloseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompositionTraceOverlappingFlushKeepsIndependentDecisions(bool terminateDuringFlush)
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.ClosureFault = 3;
        var events = new global::System.Collections.Concurrent.ConcurrentQueue<SemanticTraceEventV1>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        string? firstCorrelation = null;
        bool released = false, heldOwnerLock = false;
        var sink = new ClosureTraceSink(e =>
        {
            foreach (var field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                if (Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!))
                    heldOwnerLock = true;
            events.Enqueue(e);
            if (Interlocked.CompareExchange(ref firstCorrelation, e.OperationCorrelation, null) is null)
            {
                entered.TrySetResult();
                released = release.Wait(TimeSpan.FromSeconds(10));
            }
            return true;
        });
        var first = Task.Run(() => s.Kernel.CloseSecureExecution(binding, sink));
        KernelResult second = default;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            s.Provider.ClosureFault = -1;
            second = await Task.Run(() => s.Kernel.CloseSecureExecution(binding, sink)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(second.IsSuccess);
            Assert.False(first.IsCompleted);
            if (terminateDuringFlush) Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
        finally { release.Set(); }
        Assert.False((await first.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.True(released);
        Assert.False(heldOwnerLock);
        Assert.Equal(2, s.Provider.CloseCalls);
        var stream = events.ToArray();
        var grouped = stream.GroupBy(e => e.OperationCorrelation).ToArray();
        Assert.Equal(2, grouped.Length);
        foreach (var group in grouped)
        {
            Assert.Equal(new ulong[] { 1, 2, 3 }, group.Select(e => e.Sequence));
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
            Assert.Equal(group.Key == firstCorrelation ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.RetireOrComplete, group.Last().Kind);
        }
        Assert.Equal(SemanticTraceValidationStatusV1.MixedOperation, SemanticTraceValidatorV1.Validate(stream).Status);
        Assert.All(stream, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        if (!terminateDuringFlush) Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectBridgeResetCannotPublishCompositionClosure(bool contained)
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.Contained = contained;
        s.Provider.DuringClose = () => Assert.True(s.Kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        var trace = new List<SemanticTraceEventV1>();
        Assert.False(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
        Assert.Equal(new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined }, trace.Select(e => e.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(1, s.Provider.CloseCalls);
        s.Provider.DuringClose = null;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void CompositionClosureRetainsRawFactsWithoutConflatingClosedAndContained(int fault)
    {
        var s = Create(); var binding = Bind(s).Value;
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = records.Values.Cast<object>().Single();
        var request = record.GetType().GetField("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
        var admitted = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
        var epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        var exactRequest = (SecureExecutionRequest)request!;
        var exactReceipt = (SecureExecutionReceipt?)admitted;
        var observations = new List<ProviderTraceEventV1>();
        string Hash(string value) => Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        // Qualification correlation only, never an admission digest or authenticated binding.
        string GenerationDigest()
        {
            var requestJson = global::System.Text.Json.JsonSerializer.Serialize(exactRequest);
            var receiptJson = global::System.Text.Json.JsonSerializer.Serialize(exactReceipt);
            var ownerJson = global::System.Text.Json.JsonSerializer.Serialize(s.Owner);
            var requestBytes = global::System.Text.Encoding.UTF8.GetByteCount(requestJson);
            var receiptBytes = global::System.Text.Encoding.UTF8.GetByteCount(receiptJson);
            var ownerBytes = global::System.Text.Encoding.UTF8.GetByteCount(ownerJson);
            return Hash(FormattableString.Invariant($"composition-close/3:{requestBytes}:{requestJson}:{receiptBytes}:{receiptJson}:{ownerBytes}:{ownerJson}:{s.Kernel.PlatformAuthority.BackendEpoch.Value}"));
        }
        void Observe(SemanticTraceEventKindV1 kind)
        {
            var known = record.GetType().GetField("LastClosureReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
            var raw = known is null ? (SecureExecutionClosure?)null : (SecureExecutionClosure)known.GetType().GetProperty("Receipt")!.GetValue(known)!;
            var state = record.GetType().GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!.ToString();
            observations.Add(new(1, "composition-close-invocation-1", (ulong)observations.Count + 1,
                ProviderTraceDispositionV1.SecurityRelevant, kind, "test.composition.close.qualifier", GenerationDigest(),
                Hash(FormattableString.Invariant($"closure-observation/1:{(byte)kind}:{s.Provider.CloseCalls}:{state}:{raw.HasValue}:{raw?.ProviderClosed == true}:{raw?.ProviderEffectContained == true}:{raw?.Request.Binding.Id ?? 0}:{raw?.Request.Binding.Generation ?? 0}"))));
        }
        s.Provider.Contained = fault is 1 or 3;
        s.Provider.ClosureFault = fault == 5 ? 0 : fault == 6 ? 4 : fault == 7 ? 3 : -1;
        s.Provider.DuringClose = () =>
        {
            var gate = typeof(RuntimeKernel).GetField("_secureExecutionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            Assert.False(Monitor.IsEntered(gate));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureExecution(binding).Error);
            Observe(SemanticTraceEventKindV1.Submit);
            Observe(SemanticTraceEventKindV1.EffectPossible);
            if (fault is 2 or 3 or 4)
            {
                Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
                Observe(SemanticTraceEventKindV1.GenerationChanged);
            }
            if (fault == 4) throw new IOException("closure reply lost");
        };
        var emitted = new List<SemanticTraceEventV1>();
        var close = s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e =>
        {
            s.Provider.CheckLocks?.Invoke();
            foreach (var (type, target, field) in new[] {
                (typeof(RuntimeKernel), (object)s.Kernel, "_secureExecutionGate"),
                (typeof(RuntimeKernel), (object)s.Kernel, "_platformMemoryUseGate"),
                (typeof(PlatformAuthorityBridge), (object)s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate") })
                Assert.False(Monitor.IsEntered(type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!));
            emitted.Add(e); return true;
        }));
        Assert.Equal(fault is 0 or 1, close.IsSuccess);
        Assert.Equal(close.IsSuccess ? "Closed" : "Quarantined",
            record.GetType().GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!.ToString());
        // Terminal close response is completion of this cleanup call, not resource release.
        Observe(close.IsSuccess ? SemanticTraceEventKindV1.RetireOrComplete : SemanticTraceEventKindV1.Quarantined);
        var trace = SemanticTraceProjectionV1.Project(observations);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(emitted).IsValid);
        Assert.Equal(trace.Select(e => e.Kind), emitted.Select(e => e.Kind));
        Assert.Equal(trace.Select(e => e.GenerationVectorDigest), emitted.Select(e => e.GenerationVectorDigest));
        Assert.All(emitted, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        Assert.All(trace, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        foreach (var forged in new[] { SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Released })
            Assert.False(SemanticTraceValidatorV1.Validate(trace.Concat([trace[^1] with { Sequence = (ulong)trace.Count + 1, Kind = forged }])).IsValid);
        var containment = SemanticRefinementEvaluatorV1.Evaluate(
            new SemanticRequirementV1<ContainmentClassV1>(1, SemanticRequirementStrengthV1.Mandatory, ContainmentClassV1.ExactOperationProviderGenerationClosure),
            new SemanticGuaranteeV1<ContainmentClassV1>(1, SemanticGuaranteeSupportV1.Unsupported, ContainmentClassV1.None),
            SemanticPartialOrdersV1.ContainmentRefines);
        Assert.Equal(SemanticRefinementStatusV1.MandatoryUnsupported, containment.Status);
        Assert.False(containment.AuthorizesEffect);
        Assert.Equal(1, s.Provider.CloseCalls);
        var fact = record.GetType().GetField("LastClosureReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
        if (fault is 4 or 5) Assert.Null(fact);
        else
        {
            Assert.NotNull(fact);
            Assert.Equal(request, fact.GetType().GetProperty("AdmittedRequest")!.GetValue(fact));
            Assert.Equal(admitted, fact.GetType().GetProperty("AdmittedReceipt")!.GetValue(fact));
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            var receipt = (SecureExecutionClosure)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.Equal(fault is 0 or 2 or 6, receipt.ProviderClosed);
            Assert.Equal(fault is 1 or 3, receipt.ProviderEffectContained);
            if (fault == 6) Assert.NotEqual(request, receipt.Request);
        }
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
        s.Provider.DuringClose = null;
        s.Provider.ClosureFault = -1;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(fault is 0 or 1 ? 1 : 2, s.Provider.CloseCalls);
        if (fault is 2 or 3 or 4) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        else Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void SecureGuestClosureSpansLocalLifetimeCompletion(int fault, bool teardown)
    {
        var o = OverlayScenario(); var s = o.S;
        var overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Value;
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureGuestRegions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = records.Values.Cast<object>().Single();
        var state = record.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        var domain = domains.Values.Cast<object>().Single();
        var epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        var gates = new[] {
            typeof(RuntimeKernel).GetField("_secureGuestRegionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!,
            typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!,
            typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)! };
        var calls = 0;
        s.Kernel.BeforeSecureGuestRegionClosureLocalCommit = () =>
        {
            calls++;
            foreach (var gate in gates) Assert.False(Monitor.IsEntered(gate));
            Assert.Equal("Closing", state.GetValue(record)!.ToString());
            Assert.Single(((global::System.Collections.IEnumerable)domain.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(domain)!).Cast<object>());
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureGuestRegion(overlay).Error);
            if (fault == 1) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 2) throw new IOException("local overlay closure lost");
            if (fault == 3) state.SetValue(record, Enum.Parse(state.PropertyType, "Quarantined"));
        };
        var result = teardown ? s.Kernel.TerminateProcess(s.Owner) : s.Kernel.CloseSecureGuestRegion(overlay);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.Equal(fault == 0 ? "Closed" : "Quarantined", state.GetValue(record)!.ToString());
        Assert.Empty(((global::System.Collections.IEnumerable)domain.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(domain)!).Cast<object>());
        AssertOverlayPendingBalanced(s);
        if (fault != 0)
        {
            var facts = (global::System.Collections.IDictionary)domain.GetType().GetProperty("RegionClosureReceipts")!.GetValue(domain)!;
            var fact = facts.Values.Cast<object>().Single();
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            var receipt = (PlatformSecureRegionClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.True(receipt.Closed);
            Assert.Equal(receipt.Binding, fact.GetType().GetProperty("AdmittedBinding")!.GetValue(fact));
            Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
    }

    private sealed class ExactReleaseProbe(ICxlType2AcceleratorProvider inner) : ICxlType2AcceleratorProvider
    {
        public List<CxlAcceleratorSubmission> SuccessfulReleases { get; } = [];
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request) => inner.Submit(request);
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission) => inner.ObserveCompletion(submission);
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement) => inner.AcquireVisibility(submission, requirement);
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission) => inner.Cancel(submission);
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission)
        {
            var result = inner.Release(submission);
            if (result.IsSuccess) SuccessfulReleases.Add(submission);
            return result;
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void Type2LateDiscoveryLossPreservesPublicationPinAfterExactAcceleratorClosure(int fault, bool releaseFails)
    {
        DiscoveryQueryHook? discovery = null;
        var model = new CxlType2ModelAccelerator { ReleaseFails = releaseFails };
        var releases = new ExactReleaseProbe(model);
        var c = CreateComposedWorkload(_ => releases, discoveryFactory: provider => discovery = new(provider));
        var actions = 0;
        var result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            actions++;
            c.Input.Span.CopyTo(c.Output.Span);
            discovery!.BeforeQuery = () => throw fault switch
            {
                0 => new TimeoutException("late discovery query timed out"),
                1 => new OperationCanceledException("late discovery query cancelled"),
                _ => new IOException("late discovery query response lost")
            };
        });
        Assert.False(result.IsSuccess);
        Assert.Equal(1, actions);
        Assert.Empty(releases.SuccessfulReleases);
        var operation = c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!;
        Assert.Contains(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        AssertType2OwnerTrace(c, operation, published: false);
        discovery!.BeforeQuery = null;
        var owner = new RegionOwner(c.Subject.DomainId, c.Base.Owner.Generation);
        if (releaseFails)
        {
            Assert.False(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
            Assert.Empty(releases.SuccessfulReleases);
        }
        model.ReleaseFails = false;
        Assert.False(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
        Assert.Equal(c.Execution.Submission, Assert.Single(releases.SuccessfulReleases));
        // Known accelerator closure does not settle the independent possible CPU publication.
        Assert.False(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
        Assert.Single(releases.SuccessfulReleases);
        Assert.NotEqual(ExternalOperationState.Released, c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!.State);
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Type2InitialSourceQueryLossRetainsExactReceiptUntilRealClosureRetry(int fault)
    {
        DiscoveryQueryHook? discovery = null;
        var c = CreateComposedWorkload(discoveryFactory: model => discovery = new(model));
        discovery!.BeforeQuery = () => throw fault switch
        {
            0 => new TimeoutException("initial discovery query timed out"),
            1 => new OperationCanceledException("initial discovery query cancelled"),
            _ => new IOException("initial discovery query reply lost")
        };
        var actions = 0;
        var result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actions++);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(0, actions);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.Contains(operation.Transitions, x => x.Event == "ProviderLost");
        Assert.DoesNotContain(operation.Transitions, x => x.Event is "Published" or "PublicationEffectAmbiguous");
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.True(c.Accelerator.ObserveCompletion(c.Execution.Submission).IsSuccess);
        discovery.BeforeQuery = null;
        var owner = new RegionOwner(c.Subject.DomainId, c.Base.Owner.Generation);
        Assert.True(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!.State);
        CloseComposedWorkload(c, c.Fabric);
    }
    private sealed class DiscoveryQueryHook(ICxlDiscoveryProvider inner) : ICxlDiscoveryProvider
    {
        public Action? BeforeQuery;
        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpoint)
        { BeforeQuery?.Invoke(); return inner.QueryEndpoint(endpoint); }
    }
    private static void AssertType2OwnerTrace(ComposedWorkload c, ExternalOperationSnapshot operation, bool published)
    {
        var d = c.Execution.Dependencies;
        // Evidence correlation of the exact tuple only, not a reusable admission token.
        var tuple = FormattableString.Invariant($"type2-owner-trace/1:{c.Execution.Operation.OperationId.Value}:{c.Execution.Operation.Generation.Value}:{d.PlatformGeneration}:{d.DeviceGeneration}:{d.BindingGeneration}:{d.BackingGeneration}");
        var digest = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(tuple))).ToLowerInvariant();
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(operation, digest);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.All(trace, x => { Assert.False(x.AuthorizesExecution); Assert.Equal(digest, x.GenerationVectorDigest); });
        Assert.Equal(published, trace.Any(x => x.Kind == SemanticTraceEventKindV1.Published));
        if (!published)
        {
            Assert.Equal(operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"),
                trace.Any(x => x.Kind == SemanticTraceEventKindV1.Quarantined));
            Assert.DoesNotContain(trace, x => x.Kind is SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
            var forgedRelease = trace.Concat([trace[^1] with { Sequence = trace[^1].Sequence + 1, Kind = SemanticTraceEventKindV1.Released }]).ToArray();
            Assert.False(SemanticTraceValidatorV1.Validate(forgedRelease).IsValid);
        }
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Type2ActualPublicationPlacementDriftSeparatesPreActionRefusalAndLateAmbiguity(bool output, bool late)
    {
        var c = CreateComposedWorkload();
        void Drift()
        {
            Assert.True(c.Model.HotRemove(c.Endpoint.EndpointId).IsSuccess);
            var placement = output ? c.OutputPlacement : c.InputPlacement;
            Assert.False(c.Memory.Refresh(placement.PlacementId).IsSuccess);
            Assert.Equal(CxlMemoryPlacementState.MigrationRequired, c.Memory.Query(placement.PlacementId).Value!.State);
        }
        if (!late) c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = Drift;
        var actions = 0;
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
            {
                actions++;
                c.Input.Span.CopyTo(c.Output.Span);
                if (late) Drift();
            });
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.Equal(late ? 1 : 0, actions);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.Equal(late, operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"));
        AssertType2OwnerTrace(c, operation, published: false);
        // Hot remove supplies no exact provider closure; even pre-action refusal keeps the submitted effect pinned.
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Type2PublicationPendingRejectsOverlappingDeliveryAndPinsExactGuestMapping(bool stop)
    {
        var c = CreateComposedWorkload();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var actions = 0;
        var pending = Task.Run(() => c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            Interlocked.Increment(ref actions);
            c.Input.Span.CopyTo(c.Output.Span);
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("publication test pause");
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var duplicate = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => Interlocked.Increment(ref actions));
            Assert.False(duplicate.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingDraining, duplicate.Error);
            Assert.Equal(1, Volatile.Read(ref actions));
            Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
                c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
            Assert.False(c.Manager.BeginReconfiguration(c.Fabric).IsSuccess);
            if (stop) Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        }
        finally { resume.Set(); }
        var result = await pending;
        Assert.Equal(!stop, result.IsSuccess);
        Assert.Equal(1, actions);
        var operation = c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!;
        Assert.Equal(stop, operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"));
        AssertType2OwnerTrace(c, operation, published: !stop);
        if (stop)
        {
            Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
            Assert.NotEqual(ExternalOperationState.Released, operation.State);
            Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        }
        else
        {
            Assert.Equal(ExternalOperationState.Released, operation.State);
            CloseComposedWorkload(c, c.Fabric);
        }
    }
    private sealed class BackingLockProbe(Action check) : ISecureGuestBackingAuthority
    {
        public KernelResult RevalidateForSecureGuest(RegionOwner owner, RegionHandle region) => KernelResult.Ok();
        public KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareForSecureGuest(
            RegionOwner owner, RegionHandle region, out RegionBackingLeaseHandle? backing)
        {
            backing = null; // Observes locking only; contributes no backing receipt or permission.
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(commit => { check(); return commit(); });
        }
    }

    [Fact]
    public void Type2MultiOperandBackingOwnersCommitBeforeAnyRegionGate()
    {
        var c = CreateComposedWorkload();
        var calls = 0;
        void Check()
        {
            calls++;
            var records = typeof(RegionAuthority).GetField("_regions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Base.Kernel.Regions)!;
            foreach (var record in ((global::System.Collections.IEnumerable)records.GetType().GetProperty("Values")!.GetValue(records)!).Cast<object>())
                Assert.False(Monitor.IsEntered(record.GetType().GetProperty("Gate")!.GetValue(record)!));
        }
        c.Base.Kernel.RegisterSecureGuestBackingAuthority(new BackingLockProbe(Check));
        c.Base.Kernel.RegisterSecureGuestBackingAuthority(new BackingLockProbe(Check));
        var prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        var mutations = 0;
        Assert.True(prepared.Value!(() => { mutations++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(4, calls);
        Assert.Equal(1, mutations);
        var actions = 0;
        Assert.True(c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actions++).IsSuccess);
        Assert.Equal(1, actions);
        Assert.True(calls > 4);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2PreparedPlacementDriftRefusesWithoutPartialOwnerMutation(bool output)
    {
        var c = CreateComposedWorkload();
        var prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        Assert.True(c.Model.HotRemove(c.Endpoint.EndpointId).IsSuccess);
        var placement = output ? c.OutputPlacement : c.InputPlacement;
        Assert.False(c.Memory.Refresh(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired, c.Memory.Query(placement.PlacementId).Value!.State);
        var mutations = 0;
        Assert.False(prepared.Value!(() => { mutations++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(0, mutations);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Type2LateBackingConsequencePreservesPublicationAmbiguityEvenAfterProviderClosure(bool output, bool closureFails)
    {
        var c = CreateComposedWorkload();
        var accelerator = Assert.IsType<CxlType2ModelAccelerator>(c.Accelerator);
        accelerator.ReleaseFails = closureFails;
        var actions = 0;
        var result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            actions++;
            c.Input.Span.CopyTo(c.Output.Span); // Actual modeled local publication before the fault.
            var operand = output ? c.Execution.Plan.Intent.Output : c.Execution.Plan.Intent.Input;
            Assert.True(c.Base.Kernel.Regions.QuarantineSubrange(operand.Region, c.Owner,
                new ProviderHealthEvidenceV1(1, new("late-type2-model", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, operand.Range)).IsSuccess);
        });
        Assert.False(result.IsSuccess);
        Assert.Equal(1, actions);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.Contains(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        accelerator.ReleaseFails = false;
        // Exact accelerator release cannot close an independently ambiguous CPU publication.
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.NotEqual(ExternalOperationState.Released,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PreparedType2BackingCommitRefusesFreshRegionDamageWithoutOwnerMutation(bool output)
    {
        var c = CreateComposedWorkload();
        var prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        var operand = output ? c.Execution.Plan.Intent.Output : c.Execution.Plan.Intent.Input;
        Assert.True(c.Base.Kernel.Regions.QuarantineSubrange(operand.Region, c.Owner,
            new ProviderHealthEvidenceV1(1, new("type2-backing-model", "bank", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, operand.Range)).IsSuccess);
        var calls = 0;
        Assert.False(prepared.Value!(() => { calls++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(0, calls);
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2StagedAdmissionRefusesBackingConsequenceBeforeAction(bool output)
    {
        var c = CreateComposedWorkload();
        var actions = 0;
        c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () =>
        {
            var operand = output ? c.Execution.Plan.Intent.Output : c.Execution.Plan.Intent.Input;
            Assert.True(c.Base.Kernel.Regions.QuarantineSubrange(operand.Region, c.Owner,
                new ProviderHealthEvidenceV1(1, new("type2-backing-model", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, operand.Range)).IsSuccess);
        };
        var result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actions++);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, actions);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
    }

    private static (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) OverlayScenario(bool unknownBacking = false)
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        if (unknownBacking) Assert.True(s.Kernel.Regions.ReserveBacking(buffer.Handle, new(process.DomainId, s.Owner.Generation)).IsSuccess);
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);
        return (s, mapping.Value!, region, Bind(s).Value);
    }

    private sealed class CheckedMemoryProvider(ICxlMemoryProvider inner, Action check) : ICxlMemoryProvider
    {
        public Action? BeforeQuery;
        public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId id) { check(); return inner.QueryCapacity(id); }
        public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabric, RegionBackingLeaseDescriptor backing) { check(); return inner.BindMemory(fabric, backing); }
        public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId id) { check(); BeforeQuery?.Invoke(); return inner.QueryMemory(id); }
        public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding) { check(); return inner.ReleaseMemory(binding); }
    }

    private static (Scenario S, CxlType3ModelProvider Model, CxlType3MemoryAuthority Memory, CheckedMemoryProvider Queries,
        CxlMemoryPlacementSnapshot Placement, GuestRegionMapping Mapping, SecureExecutionBinding Execution, RegionOwner Owner,
        CapabilityId DeviceCapability) BackedOverlayScenario()
    {
        var s = Create(); var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var owner = new RegionOwner(process.DomainId, s.Owner.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device, "device:secure-virtual-type2",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        var device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        var endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        CxlType3MemoryAuthority? memory = null;
        var existingCheck = s.Provider.CheckLocks!;
        s.Provider.CheckLocks = () =>
        {
            existingCheck();
            if (memory is not null) Assert.False(Monitor.IsEntered(typeof(CxlType3MemoryAuthority).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!));
            var records = typeof(RegionAuthority).GetField("_regions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.Regions)!;
            foreach (var record in ((global::System.Collections.IEnumerable)records.GetType().GetProperty("Values")!.GetValue(records)!).Cast<object>())
                Assert.False(Monitor.IsEntered(record.GetType().GetProperty("Gate")!.GetValue(record)!));
        };
        var queries = new CheckedMemoryProvider(model, () => s.Provider.CheckLocks!());
        var bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, queries, new UnsupportedCoherentProvider(), new CxlSecurityModelProvider());
        memory = new CxlType3MemoryAuthority(s.Kernel, bridge);
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var placement = memory.Place(owner, buffer.Handle, subject, device, endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placement.IsSuccess, placement.Message);
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);
        var execution = Bind(s).Value;
        s.Provider.DuringIncarnation = () => s.Provider.CheckLocks!();
        return (s, model, memory, queries, placement.Value!, mapping.Value!, execution, owner, cap);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestType3BackingDeviceSourceRevokeRefusesAtBothBoundaries(bool late)
    {
        var o = BackedOverlayScenario(); var s = o.S;
        void Revoke() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.CapabilityAuthority.Revoke(o.DeviceCapability).IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Revoke;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Revoke;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void SecureGuestFreshType3OwnerRefusesHealthOrMigrationAfterQueries(bool migration, bool late)
    {
        var o = BackedOverlayScenario(); var s = o.S;
        void Fault()
        {
            s.Provider.CheckLocks!();
            if (migration)
            {
                Assert.True(o.Model.HotRemove(o.Placement.EndpointId).IsSuccess);
                Assert.False(o.Memory.Refresh(o.Placement.PlacementId).IsSuccess);
            }
            else
            {
                Assert.True(o.Model.InjectHealthFault(o.Placement.MemoryBindingId, ProviderHealthStateV1.Degraded,
                    ProviderFaultClassV1.Omission, new(0, 8)).IsSuccess);
                Assert.True(o.Memory.ObserveAndQuarantine(o.Placement.PlacementId, o.Owner, o.Model).IsSuccess);
            }
        }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Fault;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Fault;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        AssertOverlayPendingBalanced(s);
        if (late) _ = RetainedOverlay(s);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestFreshRegionConsequenceRefusesOrdinaryBackingAtBothBoundaries(bool late)
    {
        var o = OverlayScenario(); var s = o.S;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        void Damage()
        {
            s.Provider.CheckLocks!();
            Assert.True(s.Kernel.Regions.QuarantineSubrange(o.Mapping.Region, new(process.DomainId, s.Owner.Generation),
                new ProviderHealthEvidenceV1(1, new("overlay-model", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
        }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Damage;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Damage;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Single(s.Kernel.Regions.SnapshotDamage()); AssertOverlayPendingBalanced(s);
    }

    [Fact]
    public void SecureGuestUnknownBackingOwnerCannotBeSubstitutedByLeaseIdentity()
    {
        var o = OverlayScenario(unknownBacking: true); var s = o.S;
        Assert.Equal(KernelError.PlatformDenied, s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Error);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls); AssertOverlayPendingBalanced(s);
    }

    [Fact]
    public void SecureGuestInitialBackingQueryExceptionMakesNoBindAndFreshRetryIsAllowed()
    {
        var o = BackedOverlayScenario(); var s = o.S;
        o.Queries.BeforeQuery = () => throw new IOException("backing query transport loss");
        Assert.Equal(KernelError.PlatformFaulted, s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Error);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls); Assert.Empty(s.Provider.OverlayCloseOrder);
        AssertOverlayPendingBalanced(s);
        o.Queries.BeforeQuery = null;
        var overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(overlay.IsSuccess, overlay.Message);
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay.Value).IsSuccess); AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(0, false, false)] [InlineData(0, true, false)]
    [InlineData(3, true, false)] [InlineData(3, true, true)]
    [InlineData(1, false, false)] [InlineData(1, true, false)]
    [InlineData(2, false, false)] [InlineData(2, true, false)]
    [InlineData(0, false, true)] [InlineData(0, true, true)]
    [InlineData(1, false, true)] [InlineData(1, true, true)]
    [InlineData(2, false, true)] [InlineData(2, true, true)]
    public void SecureGuestBindCompletesOriginalPendingOwnersAfterRouteLoss(int route, bool foreign, bool throws)
    {
        var o = OverlayScenario(); var s = o.S;
        var bridge = s.Kernel.PlatformAuthority;
        var domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var platform = domains.Resolve(s.Owner, s.Virtual.Domain).Value!.PlatformMappings[o.Mapping.Mapping.MappingId];
        var guests = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_guestBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var guest = guests[platform.Guest.MappingId]!;
        var child = children[platform.Guest.Child.BindingId]!;
        int foreignPending = route == 3 ? 1 : 7;
        int Pending(object record, string name) => (int)record.GetType().GetProperty(name)!.GetValue(record)!;
        object Clone(object record, string name)
        {
            var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(record, null)!;
            record.GetType().GetProperty(name)!.SetValue(clone, foreignPending);
            return clone;
        }
        object? foreignGuest = null, foreignChild = null;
        s.Provider.DuringSecureRegionBind = () =>
        {
            s.Provider.CheckLocks!();
            s.Provider.SecureRegionClosureFault = true; // Original cleanup stays unproven independently of pending completion.
            Assert.Equal(1, Pending(guest, "PendingSecureRegionEffects"));
            Assert.Equal(1, Pending(child, "PendingChildEffects"));
            if (route is 0 or 2 or 3)
            {
                if (foreign) guests[platform.Guest.MappingId] = foreignGuest = Clone(guest, "PendingSecureRegionEffects");
                else guests.Remove(platform.Guest.MappingId);
            }
            if (route is 1 or 2)
            {
                if (foreign) children[platform.Guest.Child.BindingId] = foreignChild = Clone(child, "PendingChildEffects");
                else children.Remove(platform.Guest.Child.BindingId);
            }
            if (throws) throw new IOException("Original secure guest route lost after possible effect");
        };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Equal(0, Pending(guest, "PendingSecureRegionEffects"));
        Assert.Equal(0, Pending(child, "PendingChildEffects"));
        if (foreignGuest is not null) Assert.Equal(foreignPending, Pending(foreignGuest, "PendingSecureRegionEffects"));
        if (foreignChild is not null) Assert.Equal(foreignPending, Pending(foreignChild, "PendingChildEffects"));
        _ = RetainedOverlay(s);
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        // Test restoration establishes neither original containment nor permission.
        guests[platform.Guest.MappingId] = guest; children[platform.Guest.Child.BindingId] = child;
        AssertOverlayPendingBalanced(s);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
    }

    private static void AssertOverlayPendingBalanced(Scenario s)
    {
        foreach (var (field, property) in new[] { ("_guestBindings", "PendingSecureRegionEffects"), ("_childBindings", "PendingChildEffects") })
        {
            var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
            foreach (var record in records.Values.Cast<object>())
                Assert.Equal(0, (int)record.GetType().GetProperty(property)!.GetValue(record)!);
        }
    }

    private static SecureGuestRegionBinding RetainedOverlay(Scenario s)
    {
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_secureGuestRegions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = Assert.Single(records.Values.Cast<object>());
        Assert.NotEqual("Active", record.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!.ToString());
        return (SecureGuestRegionBinding)record.GetType().GetProperty("Binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!;
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)]
    public void SecureGuestOverlayRequiresFreshConfigureAndMappingSources(int kind, bool late)
    {
        var o = OverlayScenario(); var s = o.S;
        var source = kind switch { 0 => s.Virtual.ConfigureCapability, 1 => s.Secure.ConfigureCapability, 2 => s.Virtual.MemoryCapability, _ => o.Region };
        void Revoke() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Revoke;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Revoke;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        AssertOverlayPendingBalanced(s);
        if (late) Assert.True(s.Kernel.CloseSecureGuestRegion(RetainedOverlay(s)).IsSuccess);
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
    }

    [Fact]
    public void SecureGuestLateRefusalPinsKnownReceiptUntilActualClosureRetry()
    {
        var o = OverlayScenario(); var s = o.S;
        s.Kernel.BeforeSecureGuestRegionPublication = () =>
        {
            Assert.True(s.Kernel.CapabilityAuthority.Revoke(o.Region).IsSuccess);
            s.Provider.SecureRegionClosureFault = true;
        };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var retained = RetainedOverlay(s);
        AssertOverlayPendingBalanced(s);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        Assert.Equal(0, s.Provider.CloseCalls);
        s.Provider.SecureRegionClosureFault = false;
        Assert.True(s.Kernel.CloseSecureGuestRegion(retained).IsSuccess);
        // Successful overlay closure does not clear the independently quarantined composition.
        Assert.True(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
        Assert.True(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        AssertOverlayPendingBalanced(s);
    }

    [Fact]
    public void SecureGuestCallbackPinsMappingAndCompositionAndRefusesOverlap()
    {
        var o = OverlayScenario(); var s = o.S;
        var domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var platform = domains.Resolve(s.Owner, s.Virtual.Domain).Value!.PlatformMappings[o.Mapping.Mapping.MappingId];
        s.Provider.DuringSecureRegionBind = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.PlatformAuthority.UnmapChildGuestRegion(platform.Guest).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureExecution(o.Execution).Error);
            Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        };
        var overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(overlay.IsSuccess, overlay.Message);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
        s.Provider.DuringSecureRegionBind = null;
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay.Value).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestStopRefusesInitialOrLateOwnerPublication(bool late)
    {
        var o = OverlayScenario(); var s = o.S;
        void Stop() { s.Provider.CheckLocks!(); Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Stop;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Stop;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestExceptionNeverPublishesOrInventsClosure(bool late)
    {
        var o = OverlayScenario(); var s = o.S;
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = () => throw new IOException("late overlay publication loss");
        else s.Provider.DuringSecureRegionBind = () => throw new IOException("bind outcome lost");
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        _ = RetainedOverlay(s); AssertOverlayPendingBalanced(s);
        if (!late) Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
    }

    [Fact]
    public void SecureGuestIdentityExhaustionRefusesBeforeProviderAndBalancesAdmission()
    {
        var o = OverlayScenario(); var s = o.S;
        typeof(RuntimeKernel).GetField("_nextSecureGuestRegionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s.Kernel, ulong.MaxValue);
        Assert.Equal(KernelError.CapacityExhausted, s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Error);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls); AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SecureGuestFreshCommitRejectsMappingSourceDriftGenerationAndRevokedAba(int fault)
    {
        var o = OverlayScenario(); var s = o.S;
        var domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var local = domains.Resolve(s.Owner, s.Virtual.Domain).Value!;
        var source = local.MappingSources[o.Mapping.Mapping.MappingId];
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var replacement = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(o.Mapping.Region.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        s.Kernel.BeforeSecureGuestRegionPublication = () =>
        {
            s.Provider.CheckLocks!();
            if (fault == 1) local.Mappings[o.Mapping.Mapping.MappingId] = o.Mapping with { Mapping = o.Mapping.Mapping with { Generation = new(2) } };
            else
            {
                local.MappingSources[o.Mapping.Mapping.MappingId] = (source.Memory, replacement);
                if (fault == 2)
                {
                    Assert.True(s.Kernel.CapabilityAuthority.Revoke(source.Region).IsSuccess);
                    local.MappingSources[o.Mapping.Mapping.MappingId] = source;
                }
            }
        };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        _ = RetainedOverlay(s); AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestBackendResetNeverSuppliesClosureAndBalancesPending(bool late)
    {
        var o = OverlayScenario(); var s = o.S;
        void Reset() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Reset;
        else s.Provider.DuringSecureRegionBind = Reset;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Empty(s.Provider.OverlayCloseOrder);
        _ = RetainedOverlay(s); AssertOverlayPendingBalanced(s);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void SecureRegionFreshSourcesRefuseBeforeAdmissionOrLatePublication(int sourceKind, bool late)
    {
        var s = Create();
        var binding = Bind(s).Value;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var regionCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, regionCapability, region.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value;
        var source = sourceKind switch { 0 => s.Secure.MemoryCapability, 1 => s.Virtual.ConfigureCapability, _ => s.Secure.ConfigureCapability };
        void Revoke() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess); }
        if (late) s.Kernel.BeforeSecureRegionLocalCommit = Revoke;
        else s.Kernel.BeforeSecureRegionAdmission = Revoke;
        var refused = s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapping,
            PlatformSecureRegionClass.Private);
        Assert.False(refused.IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Empty(s.Provider.OverlayCloseOrder);
        var bridgeRecords = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_secureDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        var bridgeRecord = Assert.Single(bridgeRecords.Values.Cast<object>());
        var receipts = (global::System.Collections.IDictionary)bridgeRecord.GetType().GetProperty("Regions")!.GetValue(bridgeRecord)!;
        Assert.Equal(late ? 1 : 0, receipts.Count);
        var localRecords = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var local = Assert.Single(localRecords.Values.Cast<object>());
        Assert.Empty(((global::System.Collections.IDictionary)local.GetType().GetProperty("Regions")!.GetValue(local)!).Values.Cast<object>());
        Assert.False((bool)local.GetType().GetProperty("RegionMutationInFlight")!.GetValue(local)!);
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapping,
            PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        if (late) Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureRegionProcessStopAndOverlappingBindCannotPublishOrStartAnotherEffect(bool late)
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, cap, region.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value;
        void Stop()
        {
            s.Provider.CheckLocks!();
            if (late)
                Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain,
                    s.Secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).Error);
            _ = s.Kernel.TerminateProcess(s.Owner);
        }
        if (late) s.Provider.DuringSecureRegionBind = Stop;
        else s.Kernel.BeforeSecureRegionAdmission = Stop;
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapping,
            PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        if (late) Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
    }

    [Fact]
    public void SharedVirtualIoDeliveryKeepsCompositionPinnedThroughProviderCallbackAndBalancesUse()
    {
        var s = Create();
        var binding = Bind(s).Value;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var calls = 0;
        s.Provider.DuringVirtualIoBind = () =>
        {
            calls++;
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureExecution(binding).Error);
            Assert.Equal(0, s.Provider.CloseCalls);
        };
        var io = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), binding);
        Assert.True(io.IsSuccess, io.Message);
        Assert.Equal(1, calls);
        s.Provider.DuringVirtualIoBind = null;
        Assert.True(s.Kernel.CloseVirtualIo(io.Value).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ComposedEventAdmissionRequiresBothConfigureSourcesAndRetainsLateReceipt(bool virtualSource, bool during)
    {
        var s = Create();
        var binding = Bind(s).Value;
        var source = virtualSource ? s.Virtual.ConfigureCapability : s.Secure.ConfigureCapability;
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        void Revoke() => Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        if (during) s.Provider.DuringEvent = Revoke;
        else Revoke();
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(during ? 1 : 0, s.Provider.EventEffects);
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_childBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        var child = Assert.Single(children.Values.Cast<object>());
        Assert.Equal(during ? 1ul : 0ul, (ulong)child.GetType().GetProperty("LastEventSequence")!.GetValue(child)!);
        Assert.Equal(0, (int)child.GetType().GetProperty("PendingChildEffects")!.GetValue(child)!);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(during ? 1 : 0, s.Provider.EventEffects);
        // Disposal balances the lifetime pin; revoke does not fabricate closure.
        Assert.Equal(0, s.Provider.CloseCalls);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Fact]
    public void InitialSecureProviderQueryExceptionRefusesWithoutBindOrClosureFactAndCanRetry()
    {
        var s = Create();
        s.Provider.DuringCapabilityQuery = () => throw new IOException("Controlled initial query loss.");
        Assert.Equal(KernelError.PlatformFaulted, Bind(s).Error);
        Assert.Equal(0, s.Provider.BindCalls);
        Assert.Equal(0, s.Provider.CloseCalls);
        s.Provider.DuringCapabilityQuery = null;
        var binding = Bind(s);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void SecureBindSourceRevokeRefusesInitialOrLateOwnerAdmission(bool virtualSource, bool duringBind)
    {
        var s = Create();
        var source = virtualSource ? s.Virtual.ConfigureCapability : s.Secure.ConfigureCapability;
        void Revoke() => Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        if (duringBind) s.Provider.DuringBind = Revoke;
        else s.Provider.DuringCapabilityQuery = () =>
        {
            s.Provider.DuringCapabilityQuery = null;
            Revoke();
        };
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.CloseCalls);
        if (duringBind)
            Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseReceipt!.Value.Request);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
    }

    [Fact]
    public void SecureBindIdentityExhaustionRefusesBeforeProviderEffect()
    {
        var s = Create();
        typeof(RuntimeKernel).GetField("_nextSecureExecution", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(s.Kernel, ulong.MaxValue);
        Assert.Equal(KernelError.CapacityExhausted, Bind(s).Error);
        Assert.Equal(0, s.Provider.BindCalls);
        Assert.Equal(0, s.Provider.CloseCalls);
    }

    [Fact]
    public void KnownSecureBindReceiptSurvivesLateProviderCapabilityQueryException()
    {
        var s = Create();
        s.Provider.DuringBind = () => s.Provider.DuringCapabilityQuery = () =>
        {
            s.Provider.DuringCapabilityQuery = null;
            throw new IOException("Controlled late provider contract query loss.");
        };
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.NotNull(s.Provider.LastCloseReceipt);
        Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseReceipt!.Value.Request);
        Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseRequest);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void InitialSecureProviderDriftRefusesType2ActionAndOldReceiptCannotRecover(int fault)
    {
        var c = CreateComposedWorkload();
        var actionCalls = 0;
        c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () => c.Base.Provider.RevalidationFault = fault;
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actionCalls++);
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.Equal(0, actionCalls);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        var operation = c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, item => item.Event is "Published" or "PublicationEffectAmbiguous");
        c.Base.Provider.RevalidationFault = -1;
        var calls = c.Base.Provider.RevalidateCalls;
        Assert.False(c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context).IsSuccess);
        Assert.Equal(calls, c.Base.Provider.RevalidateCalls);
        Assert.False(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
    }

    [Fact]
    public void Type2PublishedGuestEventKeepsProviderClosurePinnedUntilLocalDelivery()
    {
        var c = CreateComposedWorkload();
        var endpoint = c.Base.Kernel.CreateKernelEventEndpoint(c.Base.Owner).Value!;
        var checkedPending = false;
        c.Base.Provider.DuringEvent = () =>
        {
            var closure = ((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, c.Owner);
            Assert.Equal(KernelError.PlatformBindingDraining, closure.Error);
            Assert.Equal(ExternalOperationState.Published, c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!.State);
            Assert.Equal(KernelError.PlatformBindingActive, c.Bridge.ReleaseFabric(c.Fabric).Error);
            Assert.False(c.Memory.Close(c.InputPlacement.PlacementId).IsSuccess);
            Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
            checkedPending = true;
        };
        var completed = c.Service.CompleteVisiblePublishWithGuestEvent(c.Base.Owner, c.Execution,
            [c.Candidate], () => { }, c.VirtualIo, c.Base.Virtual.EventCapability, endpoint);
        Assert.True(checkedPending);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(ExternalOperationState.Released, completed.Value!.State);
        Assert.Equal(1, c.Base.Provider.EventEffects);
        c.Base.Provider.DuringEvent = null;
        CloseComposedWorkload(c, c.Fabric);
    }

    [Fact]
    public void ExactCompositionAndTerminalCloseAreIdempotent()
    {
        var s = Create(); var binding = Bind(s);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.True(s.Kernel.RevalidateSecureExecution(binding.Value).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.False(s.Kernel.CloseSecureExecution(binding.Value with { Generation = 2 }).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BothExactCapabilitiesAndCurrentDomainGenerationsAreRequired(int fault)
    {
        var s = Create();
        var result = s.Kernel.BindSecureExecution(s.Owner,
            fault == 2 ? s.Virtual.Domain with { Generation = new(2) } : s.Virtual.Domain,
            fault == 0 ? new(999999) : fault == 4 ? s.Secure.EvidenceCapability : s.Virtual.ConfigureCapability,
            fault == 3 ? s.Secure.Domain with { Generation = new(2) } : s.Secure.Domain,
            fault == 1 ? new(999999) : fault == 5 ? s.Secure.EvidenceCapability : s.Secure.ConfigureCapability);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Provider.BindCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MissingUnsupportedOrNonProductionCompositionContractCannotAdmit(int fault)
    {
        var s = Create();
        s.Provider.SecureExecutionContractVersion = fault == 0 ? 0u : fault == 1 ? 2u : 1u;
        s.Provider.ProductionSecureExecution = fault != 2;
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(0, s.Provider.BindCalls);
    }

    [Fact]
    public void ParentMismatchCannotCompose()
    {
        var s = Create();
        var other = TestFixtures.Create(s.Kernel, 991, 992).Handle;
        var parent = s.Kernel.BindPlatformAuthorityDomain(other).Value;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var create = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.SecureCompute,
            SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        // Provider bridge checks owner equality before materializing another-parent secure authority.
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, create, parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        var composed = Bind(s);
        Assert.True(composed.IsSuccess);
        var context = s.Provider.LastRequest.Context;
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(context.Virtual, context.Secure,
            context.Child with { ParentBinding = parent }, context.SecureBinding,
            context.LocalPolicyGeneration, context.LocalProtectionGeneration).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void ProviderChildAndSecureBindingStalenessAreRejectedBeforeNewAdmission()
    {
        var s = Create(); var binding = Bind(s).Value;
        var c = s.Provider.LastRequest.Context;
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(c.Virtual, c.Secure,
            c.Child with { Generation = new(2) }, c.SecureBinding, 1, 1).IsSuccess);
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(c.Virtual, c.Secure,
            c.Child, c.SecureBinding with { Generation = new(2) }, 1, 1).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.RevokeSecureDomain(c.SecureBinding).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void ClosedProviderChildCannotBeUsedForNewAdmission()
    {
        var s = Create(); var binding = Bind(s).Value;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(s.Provider.LastRequest.Context.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.CloseChildDomain(s.Provider.LastRequest.Context.Child).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void PublicationRevalidatesAfterProviderEventAndRollsBackStaging()
    {
        var s = Create(); var binding = Bind(s).Value;
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        s.Provider.DuringEvent = () => s.Provider.RevalidationFault = 0;
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        var terminated = s.Kernel.TerminateProcess(s.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
    }

    [Fact]
    public void InFlightProviderEffectPinsCompositionAgainstCloseAndTeardown()
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.DuringChildEffect = () => {
            Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.CloseCalls);
        };
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.NotEqual(VirtualDomainState.Running, s.Kernel.QueryVirtualDomain(s.Owner, s.Virtual.Domain).Value);
        Assert.Equal(1, s.Provider.ChildEffects);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void BackendResetQuarantinesCompositionWithoutNewProviderEffects()
    {
        var s = Create(); var binding = Bind(s).Value;
        Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(0, s.Provider.RevalidateCalls);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void MalformedOrPartialAdmissionCompensatesExactRequest(int fault)
    {
        var s = Create(); s.Provider.AdmissionFault = fault;
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseRequest);
        Assert.True(s.Kernel.CloseSecureExecution(s.Provider.LastRequest.Binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void PolicyProtectionProviderChildAndSecureLeaseChangesQuarantine(int fault)
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.RevalidationFault = fault;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        var calls = s.Provider.RevalidateCalls;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.RevalidateCalls);
        Assert.False(Bind(s).IsSuccess);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, s.Secure.Domain, s.Secure.ExecuteCapability,
            PlatformSecureDomainTransition.Start).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void AmbiguousClosurePinsProcessAndDomainsUntilExactRecovery(int fault)
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.ClosureFault = fault;
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.True(s.Kernel.Processes.Resolve(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
        s.Provider.ClosureFault = -1;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        var calls = s.Provider.CloseCalls;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.CloseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(new[] { "execution", "secure", "child" }, s.Provider.TerminalOrder);
    }

    [Fact]
    public void AmbiguousAdmissionAndLostResponseRemainRecoverableByExactRequest()
    {
        var s = Create(); s.Provider.AdmissionFault = 5; s.Provider.ClosureFault = 0;
        Assert.False(Bind(s).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.ClosureFault = -1;
        Assert.True(s.Kernel.CloseSecureExecution(s.Provider.LastRequest.Binding).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void ContainmentIsTerminalButUnavailableIsNot()
    {
        var s = Create(); var binding = Bind(s).Value;
        s.Provider.Contained = true;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
    }

    [Fact]
    public void ReplayedCorrelationAndCrossBindingReceiptCannotAdmit()
    {
        var s = Create(); var first = Bind(s).Value;
        Assert.True(s.Kernel.CloseSecureExecution(first).IsSuccess);
        s.Provider.ReplayCorrelation = true;
        Assert.False(Bind(s).IsSuccess);
        s.Provider.AdmissionFault = 0;
        Assert.False(Bind(s).IsSuccess);
    }

    [Fact]
    public void TeardownDuringProviderAdmissionStopsNewAdmissionsAndCompensates()
    {
        var s = Create();
        s.Provider.DuringBind = () => Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void PublicAndSipSurfacesDoNotExposeCompositionOrProviderIdentity()
    {
        Assert.False(typeof(SecureExecutionBinding).IsPublic);
        Assert.False(typeof(SecureExecutionContext).IsPublic);
        Assert.False(typeof(ISecureExecutionProvider).IsPublic);
        Assert.DoesNotContain(typeof(RuntimeKernel).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name.Contains("SecureExecution"));
        Assert.DoesNotContain(typeof(SecureDomainHandle).Assembly.GetExportedTypes(), type => type.Name.Contains("SecureExecution"));
    }

    [Fact]
    public void LocalProtectionChangeInvalidatesComposition()
    {
        var s = Create(); var binding = Bind(s).Value;
        var context = s.Provider.LastRequest.Context;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapped = s.Kernel.MapPlatformOwnedRegion(s.Owner, context.Parent, cap, region.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value,
            PlatformSecureRegionClass.Private).IsSuccess);
        var calls = s.Provider.RevalidateCalls;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.RevalidateCalls);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value,
            PlatformSecureRegionClass.Private).IsSuccess);
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, s.Secure.Domain, s.Secure.ExecuteCapability,
            PlatformSecureDomainTransition.Start).IsSuccess);
        Assert.True(s.Kernel.CloseSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RevokedEitherAuthorityBlocksEffectsBeforeProviderRevalidation(bool secure)
    {
        var s = Create(); var binding = Bind(s).Value;
        Assert.True(s.Kernel.RevokeCapability(secure ? s.Secure.ConfigureCapability : s.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(0, s.Provider.RevalidateCalls);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
    }

    [Fact]
    public void ProviderFeaturesAndDiagnosticObjectsCannotSupplyAuthorityOrBindImplicitly()
    {
        var s = Create();
        Assert.Equal(PlatformFeatureAvailability.ProductionSecure,
            s.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.SecureDomains).Availability);
        Assert.Equal(0, s.Provider.BindCalls);
        var operation = typeof(RuntimeKernel).GetMethod("BindSecureExecution", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(new[] { typeof(ProcessHandle), typeof(VirtualDomainHandle), typeof(CapabilityId),
            typeof(SecureDomainHandle), typeof(CapabilityId) }, operation.GetParameters().Select(x => x.ParameterType));
        Assert.False(typeof(ISecureExecutionProvider).IsAssignableFrom(typeof(HostPlatformAuthorityProvider)));
    }

    [Fact]
    public void SecureGuestOverlayUsesExistingGuestLineageAndClosesFirst()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read);
        Assert.True(guest.IsSuccess, guest.Message);
        var execution = Bind(s).Value;
        var overlay = s.Kernel.BindSecureGuestRegion(s.Owner, execution, guest.Value!.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(overlay.IsSuccess, overlay.Message);
        Assert.Equal(s.Provider.LastGuestParent, s.Provider.LastSecureParent);
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay.Value).IsSuccess);
        Assert.Equal(new[] { "secure" }, s.Provider.OverlayCloseOrder);
    }

    [Fact]
    public void StaleGuestMappingMakesZeroSecureOverlayProviderCalls()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read).Value!;
        var stale = guest.Mapping with { Generation = new(2) };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, Bind(s).Value, stale, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls);
    }

    [Fact]
    public void AmbiguousSecureOverlayCloseQuarantinesGuestMappingAndBlocksTeardown()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read).Value!;
        var overlay = s.Kernel.BindSecureGuestRegion(s.Owner, Bind(s).Value, guest.Mapping, PlatformSecureRegionClass.Private).Value;
        s.Provider.SecureRegionClosureFault = true;
        Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.SecureRegionClosureFault = false;
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        var terminalCloseCalls = s.Provider.OverlayCloseOrder.Count(item => item == "secure");
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.True(terminalCloseCalls >= 2);
        Assert.Equal(terminalCloseCalls, s.Provider.OverlayCloseOrder.Count(item => item == "secure"));
    }

    [Fact]
    public async Task SecureVirtualizedType3BackedType2WorkloadPublishesEventBeforeExactClosure()
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        var deviceCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;

        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        var endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        var security = new CxlSecurityModelProvider();
        var securityProperties = CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;
        Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, securityProperties).IsSuccess);
        var bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, model,
            new UnsupportedCoherentProvider(), security);
        var memory = new CxlType3MemoryAuthority(s.Kernel, bridge);

        var input = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var output = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        input.Span.Fill(0x5A);
        var inputPlacement = memory.Place(new(process.DomainId, s.Owner.Generation), input.Handle,
            subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;
        var outputPlacement = memory.Place(new(process.DomainId, s.Owner.Generation), output.Handle,
            subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;

        var inputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(input.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var outputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(output.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var inputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            inputCapability, input.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        var outputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            outputCapability, output.Handle, new(16, 16), GuestMemoryAccess.Read | GuestMemoryAccess.Write).Value!;
        var secureExecution = Bind(s).Value;
        var inputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, inputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var outputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, outputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var virtualIo = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), secureExecution).Value;

        var control = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var controlUse = s.Kernel.AcquireRegionUse(s.Owner, control.Handle, RegionUseMode.DevicePrivate,
            new(0, 16)).Value!;
        var fabric = bridge.BindFabric(new(process.DomainId, s.Owner.Generation), controlUse.Handle,
            subject, device, new(endpoint.EndpointId, endpoint.DeviceGeneration, 16,
                CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var candidate = new ComputeProviderCandidate(new("secure-virtual-type2"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication |
            ComputeProviderCapabilities.SecureComputeEvidence | ComputeProviderCapabilities.VirtualizedDomain,
            4096, 1, 1, true, false);
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 16)), new(output.Handle, new(0, 16)),
            ComputePublicationPreference.StagedRequired, RequiresSecureEvidence: true,
            RequiresVirtualizedDomain: true);
        var contextResult = s.Kernel.CreateVirtualComputeContext(s.Owner, s.Virtual.Domain,
            inputGuest.Mapping, outputGuest.Mapping, virtualIo, secureExecution);
        Assert.True(contextResult.IsSuccess, contextResult.Message);
        var context = contextResult.Value;
        var planResult = s.Kernel.PlanVirtualizedCompute(s.Owner, intent, new(true, true, true),
            [candidate], context);
        Assert.True(planResult.IsSuccess, planResult.Message);
        var plan = planResult.Value!;
        var accelerator = new CxlType2ModelAccelerator();
        var manager = new CxlFabricManagerAuthority(s.Kernel, model);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        var policy = new CxlSecurityPolicy(securityProperties, new(1), RequiredForOperation: true);
        var execution = service.SubmitVirtualized(s.Owner, plan, [candidate], context, subject, device,
            endpoint, fabric, 1, securityAuthority: new(bridge, security), securityPolicy: policy).Value!;
        var guestEvent = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;

        Assert.Equal(KernelError.InvalidTransition, s.Kernel.PublishVirtualIoEvent(s.Owner, virtualIo,
            execution.Operation, s.Virtual.EventCapability, guestEvent).Error);
        Assert.Equal(0, s.Provider.EventEffects);
        var completed = service.CompleteVisiblePublishWithGuestEvent(s.Owner, execution, [candidate],
            () => input.Span.CopyTo(output.Span), virtualIo, s.Virtual.EventCapability, guestEvent);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(ExternalOperationState.Released, completed.Value!.State);
        Assert.Equal(input.Span.ToArray(), output.Span.ToArray());
        Assert.True((await s.Kernel.WaitForKernelEventAsync(s.Owner, guestEvent)).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);

        Assert.True(s.Kernel.CloseVirtualIo(virtualIo).IsSuccess);
        Assert.True(s.Kernel.CloseSecureGuestRegion(inputSecure).IsSuccess);
        Assert.True(s.Kernel.CloseSecureGuestRegion(outputSecure).IsSuccess);
        var inputGuestClose = s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain,
            s.Virtual.MemoryCapability, inputGuest.Mapping);
        Assert.True(inputGuestClose.IsSuccess, inputGuestClose.Message);
        var outputGuestClose = s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain,
            s.Virtual.MemoryCapability, outputGuest.Mapping);
        Assert.True(outputGuestClose.IsSuccess, outputGuestClose.Message);
        Assert.True(s.Kernel.CloseSecureExecution(secureExecution).IsSuccess);
        Assert.True(memory.Close(inputPlacement.PlacementId).IsSuccess);
        Assert.True(memory.Close(outputPlacement.PlacementId).IsSuccess);
        Assert.True(bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegionUse(s.Owner, controlUse.Handle).IsSuccess);
        Assert.True(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain,
            s.Secure.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Owner, device).IsSuccess);
        var virtualClose = s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain,
            s.Virtual.ConfigureCapability);
        Assert.True(virtualClose.IsSuccess, virtualClose.Message);
        var finalTeardown = s.Kernel.TerminateProcess(s.Owner);
        Assert.True(finalTeardown.IsSuccess, finalTeardown.Message);
    }

    [Fact]
    public void FabricReconfigurationDuringSecureVirtualGuestWorkClosesEffectBeforeReclaim()
    {
        var c = CreateComposedWorkload();

        var draining = c.Manager.BeginReconfiguration(c.Fabric);

        Assert.True(draining.IsSuccess, draining.Message);
        Assert.Equal(ExternalOperationState.Released,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            c.Accelerator.ObserveCompletion(c.Execution.Submission).Status);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        Assert.True(c.Base.Kernel.Regions.Validate(c.Input.Handle, c.Owner).IsSuccess);
        var replacement = c.Manager.CompleteReconfiguration(c.Fabric.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        CloseComposedWorkload(c, replacement.Value!.Binding);
    }

    [Fact]
    public void VirtualComputeIoRequiresExactOperandDomainWithoutQuarantiningValidBinding()
    {
        var c = CreateComposedWorkload();
        var domain = c.Base.Virtual.Domain;
        Assert.True(c.Base.Kernel.RevalidateVirtualIo(c.Base.Owner, c.VirtualIo, domain).IsSuccess);
        Assert.Equal(KernelError.WrongPlatformDomain,
            c.Base.Kernel.RevalidateVirtualIo(c.Base.Owner, c.VirtualIo, default(VirtualDomainHandle)).Error);
        Assert.True(c.Base.Kernel.RevalidateVirtualComputeContext(c.Base.Owner, c.Context, c.Execution.Plan).IsSuccess);
        Assert.True(c.Base.Kernel.RevalidateVirtualIo(c.Base.Owner, c.VirtualIo).IsSuccess);
        var completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => c.Input.Span.CopyTo(c.Output.Span));
        Assert.True(completed.IsSuccess, completed.Message);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void VirtualType2FinalPublicationRequiresBothGuestMappingSources(int source)
    {
        var c = CreateComposedWorkload();
        var capability = source == 0 ? c.Base.Virtual.MemoryCapability
            : source == 1 ? c.InputCapability : c.OutputCapability;
        var prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        var actionRan = false;
        var result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            actionRan = true;
            c.Input.Span.CopyTo(c.Output.Span);
            Assert.True(c.Base.Kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            var committed = false;
            var refusal = prepared.Value!(() => { committed = true; return KernelResult.Ok(); });
            Assert.False(refusal.IsSuccess);
            Assert.False(committed);
        });
        Assert.True(actionRan);
        Assert.False(result.IsSuccess);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.NotEqual(ExternalOperationState.Published, operation.State);
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.Equal(0, c.Base.Provider.EventEffects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VirtualType2LateSubmitRegionRevokeRetainsReceiptForFabricCleanup(bool output)
    {
        var callbacks = 0;
        var c = CreateComposedWorkload(afterSubmit: (kernel, inputSource, outputSource) =>
        {
            callbacks++;
            Assert.True(kernel.CapabilityAuthority.Revoke(output ? outputSource : inputSource).IsSuccess);
        });
        Assert.Equal(1, callbacks);
        Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, c.SubmissionResult.Error);
        Assert.False(c.Bridge.ReleaseFabric(c.Fabric).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        var drain = c.Manager.BeginReconfiguration(c.Fabric);
        Assert.True(drain.IsSuccess, drain.Message);
        var replacement = c.Manager.CompleteReconfiguration(c.Fabric.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        CloseComposedWorkload(c, replacement.Value!.Binding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VirtualType2RegionRevokeInPreSubmitProviderQueryPreventsAcceleratorEffect(bool output)
    {
        var submits = 0;
        var queries = 0;
        var c = CreateComposedWorkload(afterSubmit: (_, _, _) => submits++,
            duringPreSubmitRevalidation: (kernel, inputSource, outputSource) =>
            {
                if (queries++ == 0)
                    Assert.True(kernel.CapabilityAuthority.Revoke(output ? outputSource : inputSource).IsSuccess);
            });
        Assert.True(queries > 0);
        Assert.Equal(0, submits);
        Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, c.SubmissionResult.Error);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VirtualType2LastDeviceSourceQueryRevokePreventsSubmit(bool output)
    {
        var submits = 0;
        var faults = 0;
        var c = CreateComposedWorkload(afterSubmit: (_, _, _) => submits++,
            duringIncarnation: (kernel, inputSource, outputSource) =>
            {
                var methods = new global::System.Diagnostics.StackTrace().GetFrames()
                    .Select(frame => frame.GetMethod()!.Name).ToArray();
                if (faults != 0 || !methods.Any(name => name.Contains("g__AdmitSubmit")) ||
                    !methods.Contains("CommitCxlDeviceEffect")) return;
                faults++;
                Assert.True(kernel.CapabilityAuthority.Revoke(output ? outputSource : inputSource).IsSuccess);
            });
        Assert.Equal(1, faults);
        Assert.Equal(0, submits);
        Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, c.SubmissionResult.Error);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSecurePublicationCannotAuthorizeRevokedCompositionSource(bool secureSource)
    {
        var c = CreateComposedWorkload();
        var prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        var source = secureSource ? c.Base.Secure.ConfigureCapability : c.Base.Virtual.ConfigureCapability;
        Assert.True(c.Base.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        var committed = false;
        var denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
            () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
        Assert.False(denied.IsSuccess);
        Assert.False(committed);
        var actionRan = false;
        var result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actionRan = true);
        Assert.False(result.IsSuccess);
        Assert.False(actionRan);
        Assert.NotEqual(ExternalOperationState.Published,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSecurePublicationRefusesLaterQuarantineOrProcessDrain(bool processDrain)
    {
        var c = CreateComposedWorkload();
        var prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        if (processDrain)
            _ = c.Base.Kernel.TerminateProcess(c.Base.Owner);
        else
        {
            c.Base.Provider.RevalidationFault = 4;
            Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        }
        var committed = false;
        var denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
            () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
        Assert.False(denied.IsSuccess);
        Assert.False(committed);
        Assert.False(c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context).IsSuccess);
    }

    [Fact]
    public void PreparedSecurePublicationCannotOverlapProviderRevalidation()
    {
        var c = CreateComposedWorkload();
        var prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        var callbacks = 0;
        c.Base.Provider.DuringRevalidation = () =>
        {
            callbacks++;
            var committed = false;
            var denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
                () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
            Assert.False(denied.IsSuccess);
            Assert.False(committed);
        };
        Assert.True(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        Assert.Equal(1, callbacks);
        c.Base.Provider.DuringRevalidation = null;
        var completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => c.Input.Span.CopyTo(c.Output.Span));
        Assert.True(completed.IsSuccess, completed.Message);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PreparedSecurePublicationDoesNotRecoverAfterProviderGenerationABA(int drift)
    {
        var c = CreateComposedWorkload();
        var prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        c.Base.Provider.RevalidationFault = drift;
        Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        c.Base.Provider.RevalidationFault = -1; // Old receipt returns; quarantine still owns admission refusal.
        var queries = c.Base.Provider.RevalidateCalls;
        Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        Assert.Equal(queries, c.Base.Provider.RevalidateCalls);
        var committed = false;
        Assert.False(c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
            () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); })).IsSuccess);
        Assert.False(committed);
        Assert.False(c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context).IsSuccess);
        Assert.False(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
    }

    [Fact]
    public async Task PreparedSecurePublicationCannotOverlapRevalidationOnAnotherThread()
    {
        var c = CreateComposedWorkload();
        var prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        c.Base.Provider.DuringRevalidation = () =>
        {
            entered.TrySetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        };
        var checking = Task.Run(() => c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var committed = false;
            var denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
                () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
            Assert.False(denied.IsSuccess);
            Assert.False(committed);
            Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
            Assert.False(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
        }
        finally
        {
            resume.Set();
            var settled = await checking.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(settled.IsSuccess, settled.Message);
            c.Base.Provider.DuringRevalidation = null;
        }
        var completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => c.Input.Span.CopyTo(c.Output.Span));
        Assert.True(completed.IsSuccess, completed.Message);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Fact]
    public void Type3HotRemoveWithLiveSecureGuestMappingsBlocksPublicationUntilExactClosure()
    {
        var c = CreateComposedWorkload();
        var published = false;

        Assert.True(c.Model.HotRemove(c.Endpoint.EndpointId).IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, c.Memory.Refresh(c.InputPlacement.PlacementId).Error);
        Assert.Equal(KernelError.PlatformUnavailable, c.Memory.Refresh(c.OutputPlacement.PlacementId).Error);
        var completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => published = true);

        Assert.False(completed.IsSuccess);
        Assert.False(published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            c.Memory.Query(c.InputPlacement.PlacementId).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.True(c.Base.Kernel.Regions.Validate(c.Input.Handle, c.Owner).IsSuccess);
        var currentFabric = c.Bridge.QueryFabric(c.Fabric.BindingId);
        Assert.True(currentFabric.IsSuccess, currentFabric.Message);
        Assert.False(c.Memory.Close(c.InputPlacement.PlacementId).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, c.Bridge.ReleaseFabric(currentFabric.Value!).Error);
        Assert.False(c.Base.Kernel.RevokePlatformDevice(c.Base.Owner, c.Device).IsSuccess);
        Assert.False(c.Base.Kernel.DestroySecureDomain(c.Base.Owner, c.Base.Secure.Domain,
            c.Base.Secure.ConfigureCapability).IsSuccess);
        Assert.False(c.Base.Kernel.DestroyVirtualDomain(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.False(c.Base.Kernel.QueryProcessTeardown(c.Base.Owner).Value.LocalReclaimCompleted);
        Assert.Equal(KernelError.PlatformBindingActive, c.Base.Kernel.Regions.ReserveBacking(c.Input.Handle, c.Owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, c.Base.Kernel.Regions.ReserveBacking(c.Output.Handle, c.Owner).Error);
    }

    [Fact]
    public void SecurityResetAfterDeviceCompleteBlocksVisibilityPublicationAndGuestEvent()
    {
        var c = CreateComposedWorkload(security => new CompletionHookAccelerator(
            new CxlType2ModelAccelerator(),
            () => Assert.True(security.ResetSecuritySession(new("secure-virtual-type2")).IsSuccess)));
        var published = false;
        var guestEvent = c.Base.Kernel.CreateKernelEventEndpoint(c.Base.Owner).Value!;

        var completed = c.Service.CompleteVisiblePublishWithGuestEvent(c.Base.Owner, c.Execution,
            [c.Candidate], () => published = true, c.VirtualIo,
            c.Base.Virtual.EventCapability, guestEvent);

        Assert.False(completed.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, completed.Error);
        Assert.False(published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, operation.State);
        Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void VirtualDeviceSourceRevokeAtCompletionVisibilityOrPublicationRefusesPublishedClaim(int phase)
    {
        ComposedWorkload? c = null;
        void Revoke()
        {
            var source = c!.Base.Kernel.CapabilityAuthority.InspectionSnapshot()
                .Single(x => x.Descriptor.ResourceKind == ResourceKind.Device &&
                    x.Descriptor.ResourceId == "device:secure-virtual-type2").Descriptor.CapabilityId;
            Assert.True(c.Base.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        }
        c = CreateComposedWorkload(_ => new CompletionHookAccelerator(new CxlType2ModelAccelerator(),
            () => { if (phase == 0) Revoke(); }, () => { if (phase == 1) Revoke(); }));
        var published = false;
        c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () => { if (phase == 3) Revoke(); };
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
            {
                published = true;
                if (phase == 2) Revoke();
            });
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.Equal(phase == 2, published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        var operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
        if (phase != 2)
        {
            Assert.DoesNotContain(operation.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
            Assert.Equal(ExternalOperationState.Released, operation.State);
            CloseComposedWorkload(c, c.Fabric);
        }
        else Assert.NotEqual(ExternalOperationState.Released, operation.State);
    }

    private static ComposedWorkload CreateComposedWorkload(
        Func<CxlSecurityModelProvider, ICxlType2AcceleratorProvider>? acceleratorFactory = null,
        Action<RuntimeKernel, CapabilityId, CapabilityId>? afterSubmit = null,
        Action<RuntimeKernel, CapabilityId, CapabilityId>? duringPreSubmitRevalidation = null,
        Action<RuntimeKernel, CapabilityId, CapabilityId>? duringIncarnation = null,
        Func<CxlType3ModelProvider, ICxlDiscoveryProvider>? discoveryFactory = null)
    {
        var s = Create();
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var owner = new RegionOwner(process.DomainId, s.Owner.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        var deviceCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write |
            CapabilityRights.Configure).Value!;
        var device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        var endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        var security = new CxlSecurityModelProvider();
        var securityProperties = CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;
        Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, securityProperties).IsSuccess);
        var bridge = new CxlAuthorityBridge(s.Kernel, discoveryFactory?.Invoke(model) ?? model, model, model, model,
            new UnsupportedCoherentProvider(), security);
        var memory = new CxlType3MemoryAuthority(s.Kernel, bridge);
        var input = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var output = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var inputPlacement = memory.Place(owner, input.Handle, subject, device, endpoint,
            CxlMemoryPersistence.Volatile).Value!;
        var outputPlacement = memory.Place(owner, output.Handle, subject, device, endpoint,
            CxlMemoryPersistence.Volatile).Value!;
        var inputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(input.Handle.RegionId), CapabilityRights.Map |
            CapabilityRights.Read).Value!.CapabilityId;
        var outputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(output.Handle.RegionId), CapabilityRights.Map |
            CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var inputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            inputCapability, input.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        var outputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            outputCapability, output.Handle, new(16, 16), GuestMemoryAccess.Read |
            GuestMemoryAccess.Write).Value!;
        var secureExecution = Bind(s).Value;
        var inputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, inputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var outputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, outputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        var virtualIo = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), secureExecution).Value;
        var control = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        var controlUse = s.Kernel.AcquireRegionUse(s.Owner, control.Handle, RegionUseMode.DevicePrivate,
            new(0, 16)).Value!;
        var fabric = bridge.BindFabric(owner, controlUse.Handle, subject, device,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile,
                CxlMemorySharing.Exclusive)).Value!;
        var candidate = new ComputeProviderCandidate(new("secure-virtual-type2"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication |
            ComputeProviderCapabilities.SecureComputeEvidence | ComputeProviderCapabilities.VirtualizedDomain,
            4096, 1, 1, true, false);
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 16)), new(output.Handle, new(0, 16)),
            ComputePublicationPreference.StagedRequired, RequiresSecureEvidence: true,
            RequiresVirtualizedDomain: true);
        var context = s.Kernel.CreateVirtualComputeContext(s.Owner, s.Virtual.Domain,
            inputGuest.Mapping, outputGuest.Mapping, virtualIo, secureExecution).Value;
        var plan = s.Kernel.PlanVirtualizedCompute(s.Owner, intent, new(true, true, true),
            [candidate], context).Value!;
        var accelerator = acceleratorFactory?.Invoke(security) ?? new CxlType2ModelAccelerator();
        if (afterSubmit is not null)
            accelerator = new CompletionHookAccelerator(accelerator, () => { },
                afterSubmit: () => afterSubmit(s.Kernel, inputCapability, outputCapability));
        var manager = new CxlFabricManagerAuthority(s.Kernel, model);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        var policy = new CxlSecurityPolicy(securityProperties, new(1), RequiredForOperation: true);
        if (duringPreSubmitRevalidation is not null)
            s.Provider.DuringRevalidation = () => duringPreSubmitRevalidation(s.Kernel, inputCapability, outputCapability);
        if (duringIncarnation is not null)
            s.Provider.DuringIncarnation = () => duringIncarnation(s.Kernel, inputCapability, outputCapability);
        var submissionResult = service.SubmitVirtualized(s.Owner, plan, [candidate], context, subject, device,
            endpoint, fabric, 1, securityAuthority: new(bridge, security), securityPolicy: policy);
        s.Provider.DuringRevalidation = null;
        s.Provider.DuringIncarnation = null;
        var execution = submissionResult.Value!;
        return new(s, owner, subject, device, model, endpoint, security, bridge, memory, input, output,
            inputPlacement, outputPlacement, inputGuest, outputGuest, inputSecure, outputSecure,
            secureExecution, virtualIo, controlUse, fabric, candidate, context, accelerator, manager,
            service, execution, inputCapability, outputCapability, submissionResult);
    }

    private static void CloseComposedWorkload(ComposedWorkload c, CxlFabricBinding currentFabric)
    {
        CloseComposedGuestAuthority(c);
        Assert.True(c.Memory.Close(c.InputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Bridge.ReleaseFabric(currentFabric).IsSuccess);
        Assert.True(c.Base.Kernel.ReleaseRegionUse(c.Base.Owner, c.ControlUse.Handle).IsSuccess);
        Assert.True(c.Base.Kernel.DestroySecureDomain(c.Base.Owner, c.Base.Secure.Domain,
            c.Base.Secure.ConfigureCapability).IsSuccess);
        Assert.True(c.Base.Kernel.RevokePlatformDevice(c.Base.Owner, c.Device).IsSuccess);
        Assert.True(c.Base.Kernel.DestroyVirtualDomain(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.ConfigureCapability).IsSuccess);
        var terminated = c.Base.Kernel.TerminateProcess(c.Base.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
    }

    private static void CloseComposedGuestAuthority(ComposedWorkload c)
    {
        Assert.True(c.Base.Kernel.CloseVirtualIo(c.VirtualIo).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureGuestRegion(c.InputSecure).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureGuestRegion(c.OutputSecure).IsSuccess);
        Assert.True(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.InputGuest.Mapping).IsSuccess);
        Assert.True(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
    }

    private sealed record ComposedWorkload(Scenario Base, RegionOwner Owner,
        PlatformDomainIdentity Subject, PlatformDeviceLease Device, CxlType3ModelProvider Model,
        CxlEndpointSnapshot Endpoint, CxlSecurityModelProvider Security, CxlAuthorityBridge Bridge,
        CxlType3MemoryAuthority Memory, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output,
        CxlMemoryPlacementSnapshot InputPlacement, CxlMemoryPlacementSnapshot OutputPlacement,
        GuestRegionMapping InputGuest, GuestRegionMapping OutputGuest,
        SecureGuestRegionBinding InputSecure, SecureGuestRegionBinding OutputSecure,
        SecureExecutionBinding SecureExecution, VirtualIoBinding VirtualIo,
        RegionUseDescriptor ControlUse, CxlFabricBinding Fabric, ComputeProviderCandidate Candidate,
        VirtualComputeContext Context, ICxlType2AcceleratorProvider Accelerator,
        CxlFabricManagerAuthority Manager, CxlType2AcceleratorService Service,
        CxlType2Execution Execution, CapabilityId InputCapability, CapabilityId OutputCapability,
        KernelResult<CxlType2Execution> SubmissionResult);

    private sealed class CompletionHookAccelerator(ICxlType2AcceleratorProvider inner,
        Action afterCompletion, Action? afterVisibility = null, Action? afterSubmit = null) : ICxlType2AcceleratorProvider
    {
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request)
        {
            var result = inner.Submit(request);
            afterSubmit?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(
            CxlAcceleratorSubmission submission)
        {
            var result = inner.ObserveCompletion(submission);
            if (result.IsSuccess) afterCompletion();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(
            CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement)
        {
            var result = inner.AcquireVisibility(submission, requirement);
            if (result.IsSuccess) afterVisibility?.Invoke();
            return result;
        }
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission) => inner.Cancel(submission);
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission) => inner.Release(submission);
    }

    private static KernelResult<SecureExecutionBinding> Bind(Scenario s) => s.Kernel.BindSecureExecution(s.Owner,
        s.Virtual.Domain, s.Virtual.ConfigureCapability, s.Secure.Domain, s.Secure.ConfigureCapability);

    private static Scenario Create(Action<FakeProvider>? configure = null)
    {
        var provider = new FakeProvider(); var kernel = new RuntimeKernel(provider);
        provider.CheckLocks = () => {
            foreach (var name in new[] { "_secureExecutionGate", "_platformMemoryUseGate", "_secureGuestRegionGate" })
                Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!));
            Assert.False(Monitor.IsEntered(typeof(CapabilityAuthority).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.CapabilityAuthority)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!));
        };
        configure?.Invoke(provider);
        var (process, owner) = TestFixtures.Create(kernel, 980, 981);
        var vc = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner, vc, new(1, 4096));
        Assert.True(vm.IsSuccess, vm.Message);
        Assert.True(kernel.ConfigureVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        // Reuse the actual VM parent; do not create a parallel domain authority.
        var parent = provider.Parent;
        var localParent = new PlatformDomainBinding(new(1), new(1), parent.Subject);
        var sc = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var secure = kernel.CreateSecureDomain(owner, sc, localParent, new([SecureProperty.PrivateMemory], 4096));
        Assert.True(secure.IsSuccess, secure.Message);
        return new(kernel, provider, owner, vm.Value, secure.Value!, localParent);
    }

    private sealed record Scenario(RuntimeKernel Kernel, FakeProvider Provider, ProcessHandle Owner,
        VirtualDomainAuthoritySet Virtual, SecureDomainAuthoritySet Secure, PlatformDomainBinding Parent);

    // Deterministic test contract only. This is not a production capability claim.
    private sealed class FakeProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformGuestMemoryProvider, IPlatformSecureComputeProvider,
        IPlatformVirtualEventProvider, IPlatformVirtualIoProvider, IPlatformDeviceLeaseProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider,
        ISecureExecutionProvider, IPlatformProviderIncarnationSource
    {
        private readonly HostPlatformAuthorityProvider _host = new();
        public PlatformProviderDescriptor Descriptor => _host.Descriptor;
        public Action? DuringIncarnation;
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get { DuringIncarnation?.Invoke(); return new(1); }
        }
        public PlatformProviderDomainLease Parent;
        private uint _secureExecutionContractVersion = 1;
        private bool _productionSecureExecution = true;
        public uint SecureExecutionContractVersion
        {
            get { CheckLocks?.Invoke(); DuringCapabilityQuery?.Invoke(); return _secureExecutionContractVersion; }
            set => _secureExecutionContractVersion = value;
        }
        public bool ProductionSecureExecution
        {
            get { CheckLocks?.Invoke(); DuringCapabilityQuery?.Invoke(); return _productionSecureExecution; }
            set => _productionSecureExecution = value;
        }
        public int AdmissionFault = -1, RevalidationFault = -1, ClosureFault = -1;
        public int BindCalls, CloseCalls, RevalidateCalls, SecureCloses, ChildCloses, ChildEffects, SecureRegionBindCalls;
        public bool Contained, ReplayCorrelation, SecureRegionClosureFault;
        public Action? DuringClose { get; set; }
        public Func<SecureExecutionClosure, SecureExecutionClosure>? ClosureTransform { get; set; }
        public Action? DuringBind, DuringEvent, DuringChildEffect, CheckLocks, DuringRevalidation;
        public Action? DuringCapabilityQuery;
        public Action? DuringVirtualIoBind;
        public Action? DuringSecureRegionBind;
        public SecureExecutionReceipt? LastCloseReceipt;
        public int EventEffects;
        public SecureExecutionRequest LastRequest, LastCloseRequest;
        public List<string> TerminalOrder = [];
        public List<string> OverlayCloseOrder = [];
        public PlatformProviderRegionMappingLease LastGuestParent, LastSecureParent;
        private ulong _nextGuest = 1;
        public PlatformFeatureManifest QueryFeatures() => new(_host.QueryFeatures().Features.Concat(new[] {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildEventDelivery, 1, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.BoundedVirtualIo, 1, PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.SecureDomains, 1, PlatformFeatureAvailability.ProductionSecure) }).ToArray());
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        { var result = _host.BindDomain(subject); if (result.IsSuccess) Parent = result.Value; return result; }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => _host.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease lease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            var result = _host.MapOwnedRegion(lease, region, access);
            if (result.IsSuccess) LastGuestParent = result.Value;
            return result;
        }
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => _host.RevokeRegionMapping(mapping, policy);
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            var revoked = _host.RevokeRegionMapping(mapping, policy);
            return revoked.IsSuccess
                ? PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId,
                    mapping.Generation, new(new(_nextGuest++), new(1), mapping.DomainLease)))
                : PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(revoked.Status,
                    revoked.Message ?? "mapping revocation failed");
        }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) =>
            PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId, operation.Generation,
                operation.DomainLease, PlatformCompletionState.Closed));
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domain,
            PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), domain, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(PlatformProviderDomainLease parent, PlatformChildDomainIntent intent) => PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(new(new(1), new(1), parent, intent));
        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease, PlatformChildDomainTransition transition) { if (transition != PlatformChildDomainTransition.BeginDrain) { ChildEffects++; DuringChildEffect?.Invoke(); } return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventEffects++; DuringEvent?.Invoke();
            var child = request.ChildLease;
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(new(child.LeaseId, child.Generation,
                child.ParentDomainLease.LeaseId, child.ParentDomainLease.Generation, (ulong)EventEffects, request.EventClass, request.SourceResourceId));
        }
        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(PlatformProviderChildDomainLease lease)
        { ChildCloses++; TerminalOrder.Add("child"); return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation, lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, PlatformChildDomainClosureDisposition.Closed)); }
        public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(PlatformVirtualIoRequest request)
        {
            CheckLocks?.Invoke(); DuringVirtualIoBind?.Invoke();
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(new(new(_nextGuest++), new(1),
                request.ChildLease, request.ParentDeviceLease, request.Profile));
        }
        public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(PlatformProviderVirtualIoLease lease) =>
            PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(PlatformGuestRegionMappingRequest request) =>
            PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(new(new(_nextGuest++), new(1), request.ChildLease, request.ParentMapping.Lease, request.GuestRange, request.Access));
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(PlatformProviderGuestRegionMappingLease lease)
        {
            OverlayCloseOrder.Add("guest");
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        }
        public IReadOnlyList<SecureProperty>? SecureProperties { get; set; }
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease> CreateSecureDomain(PlatformSecureDomainRequest request) => PlatformAuthorityResult<PlatformProviderSecureDomainLease>.Ok(new(new(1), new(1), request.Parent, SecureProperties ?? request.Profile.RequiredProperties.ToArray()));
        public PlatformAuthorityResult<PlatformProviderSecureRegionBinding> BindSecureRegion(PlatformProviderSecureDomainLease domain, PlatformProviderRegionMappingLease mapping, PlatformSecureRegionClass regionClass)
        { CheckLocks?.Invoke(); SecureRegionBindCalls++; LastSecureParent = mapping; DuringSecureRegionBind?.Invoke(); return PlatformAuthorityResult<PlatformProviderSecureRegionBinding>.Ok(new(new(1), new(1), domain, mapping, regionClass)); }
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> UnbindSecureRegion(PlatformProviderSecureRegionBinding binding)
        {
            CheckLocks?.Invoke();
            OverlayCloseOrder.Add("secure");
            return SecureRegionClosureFault
                ? PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Fail(PlatformAuthorityStatus.Unavailable, "secure overlay close outcome is ambiguous")
                : PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Ok(new(binding, true));
        }
        public PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> TransitionSecureDomain(PlatformProviderSecureDomainLease domain, PlatformSecureDomainTransition transition) => PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>.Ok(new(domain, transition, true));
        public PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> RevokeSecureDomain(PlatformProviderSecureDomainLease domain)
        { SecureCloses++; TerminalOrder.Add("secure"); return PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>.Ok(new(domain, true)); }
        public PlatformAuthorityResult<SecureExecutionReceipt> BindSecureExecution(SecureExecutionRequest request)
        {
            CheckLocks?.Invoke(); BindCalls++; LastRequest = request; DuringBind?.Invoke();
            if (AdmissionFault == 5) return PlatformAuthorityResult<SecureExecutionReceipt>.Fail(PlatformAuthorityStatus.Unavailable, "lost response after materialization");
            if (AdmissionFault == 6) throw new OperationCanceledException("lost admission response");
            var receipt = new SecureExecutionReceipt(request, ReplayCorrelation ? 1 : request.Binding.Id, 1, 1, 1, 1, true);
            receipt = AdmissionFault switch {
                0 => receipt with { Request = request with { Binding = new(999, 1) } },
                1 => receipt with { PolicyGeneration = 0 }, 2 => receipt with { ProtectionGeneration = 0 },
                3 => receipt with { ProductionSecure = false }, 4 => receipt with { ContractVersion = 2 }, _ => receipt };
            return PlatformAuthorityResult<SecureExecutionReceipt>.Ok(receipt);
        }
        public PlatformAuthorityResult<SecureExecutionReceipt> RevalidateSecureExecution(SecureExecutionReceipt receipt)
        {
            CheckLocks?.Invoke(); RevalidateCalls++;
            DuringRevalidation?.Invoke();
            if (RevalidationFault == 4) return PlatformAuthorityResult<SecureExecutionReceipt>.Fail(PlatformAuthorityStatus.Revoked, "secure lease missing");
            return PlatformAuthorityResult<SecureExecutionReceipt>.Ok(RevalidationFault switch {
                0 => receipt with { PolicyGeneration = 2 }, 1 => receipt with { ProtectionGeneration = 2 },
                2 => receipt with { ProviderGeneration = 2 }, 3 => receipt with { Request = receipt.Request with {
                    Context = receipt.Request.Context with { ChildLease = receipt.Request.Context.ChildLease with { Generation = new(2) } } } }, _ => receipt });
        }
        public PlatformAuthorityResult<SecureExecutionClosure> CloseSecureExecution(SecureExecutionRequest request, SecureExecutionReceipt? receipt)
        {
            CheckLocks?.Invoke(); CloseCalls++; LastCloseRequest = request; LastCloseReceipt = receipt;
            DuringClose?.Invoke();
            if (ClosureFault == 0) return PlatformAuthorityResult<SecureExecutionClosure>.Fail(PlatformAuthorityStatus.Unavailable, "disconnect");
            if (ClosureFault == 1) return PlatformAuthorityResult<SecureExecutionClosure>.Fail(PlatformAuthorityStatus.Revoked, "not found");
            if (ClosureFault == 2) throw new OperationCanceledException();
            if (ClosureFault == 3) return PlatformAuthorityResult<SecureExecutionClosure>.Ok(new(request, receipt, false, false));
            if (ClosureFault == 4) return PlatformAuthorityResult<SecureExecutionClosure>.Ok(new(request with { Binding = new(999, 1) }, receipt, true, false));
            if (ClosureFault == 5) throw new TimeoutException();
            if (ClosureFault == 6) return PlatformAuthorityResult<SecureExecutionClosure>.Fail((PlatformAuthorityStatus)999, "unknown outcome");
            TerminalOrder.Add("execution");
            var closure = new SecureExecutionClosure(request, receipt, !Contained, Contained);
            return PlatformAuthorityResult<SecureExecutionClosure>.Ok(ClosureTransform?.Invoke(closure) ?? closure);
        }
    }

    private sealed class UnsupportedCoherentProvider : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding binding,
            RegionUseDescriptor use) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) =>
            Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding) =>
            PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() =>
            PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
}
