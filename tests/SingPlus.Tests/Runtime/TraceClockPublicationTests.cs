using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class TraceClockPublicationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MappingBudgetObservationSeesRetiredRoutingAndAllowsWorkerRead(bool teardown)
    {
        CallbackClock clock = new CallbackClock();
        RuntimeKernel kernel = new RuntimeKernel(new SingPlus.Platform.Host.HostPlatformAuthorityProvider(), clock);
        ProcessHandle owner = TestFixtures.Create(kernel, 772, 7702).Handle;
        ProcessHandle admin = TestFixtures.Create(kernel, 773, 7703).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(7703), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<ProcessBudgetAdmission> budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "mapping-observation",
            [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 128), new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]);
        Assert.True(budget.IsSuccess, budget.Message);
        KernelResult<TraceSessionAdmission> trace = kernel.StartTraceSession(owner, 32);
        Assert.True(trace.IsSuccess, trace.Message);
        Scenario s = new Scenario(kernel, owner, budget.Value!.ProcessBudget, trace.Value!.Session, clock);
        DomainId domain = s.Kernel.Processes.Resolve(s.Owner).Value!.DomainId;
        KernelResult<PlatformDomainBinding> bound = s.Kernel.BindPlatformDomain(s.Owner);
        Assert.True(bound.IsSuccess, bound.Message);
        PlatformDomainBinding binding = bound.Value!;
        KernelResult<OwnedBuffer<byte>> allocated = s.Kernel.AllocateBuffer<byte>(s.Owner, 64);
        Assert.True(allocated.IsSuccess, allocated.Message);
        OwnedBuffer<byte> buffer = allocated.Value!;
        CapabilityId cap = s.Kernel.MintCapability(domain, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<PlatformRegionMapping> mapped = s.Kernel.MapPlatformOwnedRegion(s.Owner, binding, cap, buffer.Handle, SingPlus.Platform.PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        PlatformRegionMapping mapping = mapped.Value!;
        Assert.Equal(64UL, s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        bool observed = false;
        Action? observation = null;
        observation = () =>
        {
            if (s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used != 0)
            {
                s.Clock.Next = observation;
                return;
            }
            IDictionary routing = (IDictionary)typeof(RuntimeKernel).GetField("_mappingBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            Assert.Empty(routing);
            Task<KernelResult<ProcessTeardownSnapshot>> worker = Task.Run(() => s.Kernel.QueryProcessTeardown(s.Owner));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            observed = true;
        };
        s.Clock.Next = observation;
        KernelResult result = teardown ? s.Kernel.TerminateProcess(s.Owner) : s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(!teardown, observed); // Teardown closes its trace session before mapping progression.
        Assert.Equal(0UL, s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Empty((IDictionary)typeof(RuntimeKernel).GetField("_mappingBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
    }

    [Fact]
    public void VirtualMintObservationSeesCompleteCleanupFactsBeforeRevoke()
    {
        Scenario s = Create(32, new SingPlus.Platform.Host.HostPlatformAuthorityProvider());
        DomainId domain = s.Kernel.Processes.Resolve(s.Owner).Value!.DomainId;
        CapabilityId create = s.Kernel.MintCapability(domain, s.Owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        CapabilityId[] captured = [];
        bool observed = false;
        s.Clock.Next = () =>
        {
            object authority = typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            IDictionary records = (IDictionary)authority.GetType().GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(authority)!;
            object record = records.Values.Cast<object>().Single();
            captured = (CapabilityId[])record.GetType().GetProperty("Capabilities")!.GetValue(record)!;
            Assert.Equal(5, captured.Length);
            Assert.True((bool)record.GetType().GetProperty("AuthorityPublished")!.GetValue(record)!);
            VirtualDomainHandle handle = (VirtualDomainHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
            Task<KernelResult> close = Task.Run(() => s.Kernel.DestroyVirtualDomain(s.Owner, handle, captured[0]));
            Assert.True(close.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(close.Result.IsSuccess, close.Result.Message);
            observed = true;
        };
        Assert.True(s.Kernel.CreateVirtualDomain(s.Owner, create, new(1, 4096)).IsSuccess);
        Assert.True(observed);
        Assert.All(captured, id => Assert.Equal(KernelError.CapabilityRevoked,
            s.Kernel.CapabilityAuthority.Validate(id, domain, s.Owner.Generation, CapabilityRights.None).Error));
    }

    [Fact]
    public void RevokeObservationAllowsWorkerAdmissionAfterPermissionCommit()
    {
        Scenario s = Create(8);
        DomainId domain = s.Kernel.Processes.Resolve(s.Owner).Value!.DomainId;
        CapabilityId cap = s.Kernel.MintCapability(domain, s.Owner, ResourceKind.Device, "trace-revoke", CapabilityRights.Read).Value!.CapabilityId;
        bool observed = false;
        s.Clock.Next = () =>
        {
            Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CapabilityAuthority.Validate(cap, domain, s.Owner.Generation, CapabilityRights.Read).Error);
            Task<KernelResult<CapabilityDescriptorV1>> worker = Task.Run(() => s.Kernel.MintCapability(domain, s.Owner, ResourceKind.Device, "trace-after-revoke", CapabilityRights.Read));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
            observed = true;
        };
        Assert.True(s.Kernel.RevokeCapability(cap).IsSuccess);
        Assert.True(observed);
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CapabilityAuthority.Validate(cap, domain, s.Owner.Generation, CapabilityRights.Read).Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClockFailureCannotMutateSequenceBufferOrDropFacts(bool full)
    {
        Scenario s = Create(full ? 1 : 4);
        if (full) Assert.True(Record(s).IsSuccess);
        TraceSnapshot before = s.Kernel.Traces.Snapshot(s.Session).Value!;
        s.Clock.Next = () => throw new InvalidOperationException("injected clock loss");
        Assert.Equal(KernelError.PlatformFaulted, Record(s).Error);
        TraceSnapshot after = s.Kernel.Traces.Snapshot(s.Session).Value!;
        Assert.Equal(before.Events, after.Events);
        Assert.Equal(before.DroppedEventCount, after.DroppedEventCount);
        Assert.Equal(before.Session, after.Session);
        Assert.False(after.Complete);
        Assert.Contains(TraceReplayEngine.ReplayDiagnostic(after).Divergences,
            item => item.Kind == TraceDivergenceKind.IncompleteTrace);
        Assert.True(Record(s).IsSuccess);
        Assert.Equal(full ? 2UL : 1UL, s.Kernel.Traces.Snapshot(s.Session).Value!.Events.Last().Sequence.Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClockReentrantStopOrExitCannotPublishAfterLocalClosure(bool exit)
    {
        Scenario s = Create(4);
        s.Clock.Next = () => Assert.True(exit ? s.Kernel.TerminateProcess(s.Owner).IsSuccess
            : s.Kernel.StopTraceSession(s.Owner, s.Session).IsSuccess);
        Assert.Equal(KernelError.TraceStopped, Record(s).Error);
        TraceSnapshot snapshot = s.Kernel.Traces.Snapshot(s.Session).Value!;
        Assert.Equal(TraceSessionState.Stopped, snapshot.Session.State);
        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public async Task BlockedClockHoldsNoOwnerProducerLockAndCannotPublishAfterStop()
    {
        Scenario s = Create(4);
        using ManualResetEventSlim entered = new ManualResetEventSlim(false);
        using ManualResetEventSlim proceed = new ManualResetEventSlim(false);
        s.Clock.Next = () => { entered.Set(); Assert.True(proceed.Wait(TimeSpan.FromSeconds(5))); };
        Task<KernelResult> recording = Task.Run(() => Record(s));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            KernelResult<TraceSnapshot> snapshot = await Task.Run(() => s.Kernel.Traces.Snapshot(s.Session)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(snapshot.Value!.Events);
            KernelResult<TraceSessionAdmission> stopped = await Task.Run(() => s.Kernel.StopTraceSession(s.Owner, s.Session)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stopped.IsSuccess);
        }
        finally { proceed.Set(); }
        Assert.Equal(KernelError.TraceStopped, (await recording).Error);
        Assert.Empty(s.Kernel.Traces.Snapshot(s.Session).Value!.Events);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ObservationalClockFailureCannotOrphanResourceAdmissionBudget(bool telemetry)
    {
        Scenario s = Create(8);
        s.Clock.Next = () => throw new InvalidOperationException("observational clock unavailable");
        if (telemetry)
        {
            KernelResult<TelemetrySubscriptionAdmission> admitted = s.Kernel.StartTelemetrySubscription(s.Owner, s.Owner, TelemetryProjectionClass.SelfOperational,
                1, TelemetrySubscriptionOverflowPolicy.RejectSample);
            Assert.True(admitted.IsSuccess, admitted.Message);
            Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, admitted.Value!.Subscription).IsSuccess);
        }
        else
        {
            KernelResult<TraceSessionAdmission> admitted = s.Kernel.StartTraceSession(s.Owner, 1);
            Assert.True(admitted.IsSuccess, admitted.Message);
            Assert.True(s.Kernel.StopTraceSession(s.Owner, admitted.Value!.Session).IsSuccess);
        }
        Assert.Equal(2048UL, s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.TraceTelemetryBufferBytes).Used);
        Assert.True(s.Kernel.StopTraceSession(s.Owner, s.Session).IsSuccess);
        Assert.Equal(0UL, s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.TraceTelemetryBufferBytes).Used);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExhaustedCountersRefuseBeforeBufferMutationAndMarkIncomplete(bool dropped)
    {
        Scenario s = Create(1);
        if (dropped) Assert.True(Record(s).IsSuccess);
        object producer = Producer(s);
        producer.GetType().GetProperty(dropped ? "Dropped" : "NextSequence")!.SetValue(producer, dropped ? ulong.MaxValue : 0UL);
        TraceSnapshot before = s.Kernel.Traces.Snapshot(s.Session).Value!;
        Assert.Equal(KernelError.CapacityExhausted, Record(s).Error);
        TraceSnapshot after = s.Kernel.Traces.Snapshot(s.Session).Value!;
        Assert.Equal(before.Events, after.Events);
        Assert.Equal(before.DroppedEventCount, after.DroppedEventCount);
        Assert.False(after.Complete);
    }

    [Fact]
    public void FinalSequencePublishesOnceAndCannotWrap()
    {
        Scenario s = Create(4);
        object producer = Producer(s);
        producer.GetType().GetProperty("NextSequence")!.SetValue(producer, ulong.MaxValue);
        Assert.True(Record(s).IsSuccess);
        Assert.Equal(ulong.MaxValue, Assert.Single(s.Kernel.Traces.Snapshot(s.Session).Value!.Events).Sequence.Value);
        Assert.Equal(KernelError.CapacityExhausted, Record(s).Error);
        Assert.Single(s.Kernel.Traces.Snapshot(s.Session).Value!.Events);
    }

    private static object Producer(Scenario s)
    {
        IDictionary records = (IDictionary)typeof(DeterministicTraceAuthority).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.Traces)!;
        return records[s.Session.SessionId]!.GetType().GetProperty("Producer")!.GetValue(records[s.Session.SessionId])!;
    }
    private static KernelResult Record(Scenario s) => s.Kernel.RecordTraceSemanticEvent(s.Owner, TraceEventKind.SupervisorObservation,
        new(new("clock-boundary")), new("clock", "boundary", "active", "observed"));
    private static Scenario Create(int capacity, SingPlus.Platform.IPlatformAuthorityProvider? provider = null)
    {
        CallbackClock clock = new CallbackClock();
        RuntimeKernel kernel = new RuntimeKernel(provider, clock);
        byte[] image = [0x10, 0x12];
        ServiceManifestV1 manifest = new ServiceManifestV1(new("trace-clock"), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(771, 7701), budgetRequests: [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        ComponentLifecycleSnapshot component = kernel.AdmitComponent(new(manifest, image)).Value!;
        TraceSessionAdmission trace = kernel.StartTraceSession(component.Process, capacity).Value!;
        return new(kernel, component.Process, component.ProcessBudget, trace.Session, clock);
    }
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Account, TraceSessionHandle Session, CallbackClock Clock);
    private sealed class CallbackClock : TimeProvider
    {
        public Action? Next { get; set; }
        public override long GetTimestamp() { Action? callback = Next; Next = null; callback?.Invoke(); return base.GetTimestamp(); }
    }
}
