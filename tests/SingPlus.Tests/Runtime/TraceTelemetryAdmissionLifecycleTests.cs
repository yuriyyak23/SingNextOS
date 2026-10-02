using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class TraceTelemetryAdmissionLifecycleTests
{
    [Fact]
    public void DirectProjectionClockReplacementCannotPublishOldSubjectGeneration()
    {
        var clock = new CallbackClock();
        var s = Create(2, clock);
        ProcessHandle replacement = default;
        clock.Next = () =>
        {
            var identity = new ComponentIdentity("admission-subject");
            Assert.True(s.Kernel.DrainComponent(identity).Value!.Reclaimable);
            Assert.True(s.Kernel.RetireReclaimableComponent(identity, s.Subject).IsSuccess);
            var fresh = s.Kernel.AdmitComponent(Plan("admission-subject", 762, 7602, 2));
            Assert.True(fresh.IsSuccess, fresh.Message);
            replacement = fresh.Value!.Process;
        };
        var old = s.Kernel.ProjectTelemetry(s.Owner, s.Subject, TelemetryProjectionClass.SelfOperational, s.Inspection);
        Assert.False(old.IsSuccess);
        Assert.Null(old.Value);
        Assert.Equal(s.Subject.ProcessId, replacement.ProcessId);
        Assert.NotEqual(s.Subject.Generation, replacement.Generation);
        Assert.True(s.Kernel.ProjectTelemetry(s.Owner, replacement, TelemetryProjectionClass.SelfOperational, s.Inspection).IsSuccess);
        Assert.Equal(0UL, Used(s));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectProjectionClockRevokeCannotPublishStalePermission(bool privilegedSelf)
    {
        var clock = new CallbackClock();
        var s = Create(2, clock);
        var subject = privilegedSelf ? s.Owner : s.Subject;
        var projection = privilegedSelf ? TelemetryProjectionClass.PrivilegedSystemDiagnostics : TelemetryProjectionClass.SelfOperational;
        Assert.True(s.Kernel.ProjectTelemetry(s.Owner, subject, projection, s.Inspection).IsSuccess);
        clock.Next = () => Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.Inspection!.Value).IsSuccess);
        var refused = s.Kernel.ProjectTelemetry(s.Owner, subject, projection, s.Inspection);
        Assert.False(refused.IsSuccess);
        Assert.Null(refused.Value);
        Assert.Null(clock.Next);
        Assert.Equal(0UL, Used(s));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectProjectionClockExitCannotPublishOldProcessSnapshot(bool targetExit)
    {
        var clock = new CallbackClock();
        var s = Create(2, clock);
        clock.Next = () => Assert.True(s.Kernel.TerminateProcess(targetExit ? s.Subject : s.Owner).IsSuccess);
        var refused = s.Kernel.ProjectTelemetry(s.Owner, s.Subject, TelemetryProjectionClass.SelfOperational, s.Inspection);
        Assert.False(refused.IsSuccess);
        Assert.Null(refused.Value);
        Assert.Null(clock.Next);
        Assert.Equal(0UL, Used(s));
    }

    [Fact]
    public async Task DirectProjectionBlockedClockDoesNotPreventRevokeAndCannotPublishAfterIt()
    {
        var clock = new CallbackClock();
        var s = Create(2, clock);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        clock.Next = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var projection = Task.Run(() => s.Kernel.ProjectTelemetry(s.Owner, s.Subject,
            TelemetryProjectionClass.SelfOperational, s.Inspection));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => s.Kernel.CapabilityAuthority.Revoke(s.Inspection!.Value))
                .WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally { release.Set(); }
        var refused = await projection.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(refused.IsSuccess);
        Assert.Null(refused.Value);
        Assert.Equal(0UL, Used(s));
    }

    [Theory]
    [InlineData(TelemetrySubscriptionOverflowPolicy.DropOldestWithMarker)]
    [InlineData(TelemetrySubscriptionOverflowPolicy.RejectSample)]
    [InlineData(TelemetrySubscriptionOverflowPolicy.StopSubscription)]
    public async Task RevokeDuringBlockedSnapshotCannotPublishOrMutateOverflow(TelemetrySubscriptionOverflowPolicy policy)
    {
        var clock = new CallbackClock();
        var s = Create(2, clock);
        var subscription = s.Kernel.StartTelemetrySubscription(s.Owner, s.Subject,
            TelemetryProjectionClass.SelfOperational, 1, policy, s.Inspection).Value!;
        Assert.True(s.Kernel.SampleTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        clock.Next = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var sample = Task.Run(() => s.Kernel.SampleTelemetrySubscription(s.Owner, subscription.Subscription));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var revoke = await Task.Run(() => s.Kernel.CapabilityAuthority.Revoke(s.Inspection!.Value))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(revoke.IsSuccess);
        }
        finally { release.Set(); }
        Assert.False((await sample.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(1, Queued(s));
        var subscriptions = (IDictionary)typeof(RuntimeKernel).GetField("_telemetrySubscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = subscriptions.Values.Cast<object>().Single();
        Assert.Equal(0UL, record.GetType().GetProperty("Dropped")!.GetValue(record));
        Assert.Equal(TelemetrySubscriptionState.Active,
            ((TelemetrySubscriptionAdmission)record.GetType().GetProperty("Admission")!.GetValue(record)!).State);
        Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.Equal(0UL, Used(s));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RevokedInspectionCannotSampleOrDrainBufferedTelemetry(bool privilegedSelf)
    {
        var s = Create(2, new CallbackClock());
        var subject = privilegedSelf ? s.Owner : s.Subject;
        var projection = privilegedSelf ? TelemetryProjectionClass.PrivilegedSystemDiagnostics : TelemetryProjectionClass.SelfOperational;
        var subscription = s.Kernel.StartTelemetrySubscription(s.Owner, subject, projection, 2,
            TelemetrySubscriptionOverflowPolicy.DropOldestWithMarker, s.Inspection).Value!;
        Assert.True(s.Kernel.SampleTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.Equal(1, Queued(s));
        Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.Inspection!.Value).IsSuccess);
        Assert.False(s.Kernel.SampleTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.False(s.Kernel.ReadTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.Equal(1, Queued(s));
        Assert.Equal(2048UL, Used(s));
        Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.Equal(0UL, Used(s));
        Assert.False(s.Kernel.ReadTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.Equal(1, Queued(s));
    }

    [Fact]
    public void SnapshotClockRevocationRefusesLateEnqueueWithoutQueueMutation()
    {
        var clock = new CallbackClock();
        var s = Create(2, clock);
        var subscription = s.Kernel.StartTelemetrySubscription(s.Owner, s.Subject,
            TelemetryProjectionClass.SelfOperational, 1, TelemetrySubscriptionOverflowPolicy.DropOldestWithMarker, s.Inspection).Value!;
        Assert.True(s.Kernel.SampleTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        clock.Next = () => Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.Inspection!.Value).IsSuccess);
        Assert.False(s.Kernel.SampleTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.Null(clock.Next);
        Assert.Equal(1, Queued(s));
        Assert.Equal(1024UL, Used(s));
        Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
        Assert.Equal(0UL, Used(s));
    }

    [Fact]
    public void AuthorizedReadAndSelfReadAfterCloseRetainV1DrainSemantics()
    {
        foreach (var path in new[] { 1, 2 })
        {
            var s = Create(path, new CallbackClock());
            var subscription = s.Kernel.StartTelemetrySubscription(s.Owner, s.Subject,
                TelemetryProjectionClass.SelfOperational, 1, TelemetrySubscriptionOverflowPolicy.RejectSample, s.Inspection).Value!;
            Assert.True(s.Kernel.SampleTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
            Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
            Assert.True(s.Kernel.ReadTelemetrySubscription(s.Owner, subscription.Subscription).IsSuccess);
            Assert.Equal(0, Queued(s));
            Assert.Equal(0UL, Used(s));
        }
    }

    private static int Queued(Scenario s)
    {
        var subscriptions = (IDictionary)typeof(RuntimeKernel).GetField("_telemetrySubscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = subscriptions.Values.Cast<object>().Single();
        var queue = record.GetType().GetProperty("Queue")!.GetValue(record)!;
        return (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InspectionRevocationBeforeOrDuringReservationRefusesPublication(bool duringReservation)
    {
        var clock = new CallbackClock();
        var s = Create(2, clock);
        var trace = s.Kernel.StartTraceSession(s.Owner, 8);
        Assert.True(trace.IsSuccess, trace.Message);
        Action revoke = () => Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.Inspection!.Value).IsSuccess);
        if (duringReservation) clock.Next = revoke; else revoke();
        Assert.False(Admit(s));
        Assert.Equal(2048UL, Used(s));
        var subscriptions = (IDictionary)typeof(RuntimeKernel).GetField("_telemetrySubscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        Assert.Empty(subscriptions);
        Assert.True(s.Kernel.StopTraceSession(s.Owner, trace.Value!.Session).IsSuccess);
        Assert.Equal(0UL, Used(s));
    }

    [Fact]
    public async Task InspectionPublicationCommitSerializesWithRevocation()
    {
        var s = Create(2, new CallbackClock());
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var commit = Task.Run(() => s.Kernel.CapabilityAuthority.CommitTelemetryPublication(
            s.Inspection!.Value, new(7601), s.Owner.Generation, () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return KernelResult.Ok();
            }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var revoke = Task.Run(() => s.Kernel.CapabilityAuthority.Revoke(s.Inspection!.Value));
        release.Set();
        Assert.True((await commit).IsSuccess);
        Assert.True((await revoke).IsSuccess);
        var called = false;
        var refused = s.Kernel.CapabilityAuthority.CommitTelemetryPublication(
            s.Inspection!.Value, new(7601), s.Owner.Generation, () => { called = true; return KernelResult.Ok(); });
        Assert.False(refused.IsSuccess);
        Assert.False(called);
        Assert.False(Admit(s));
        Assert.Equal(0UL, Used(s));
    }

    [Fact]
    public void InspectionCommitRejectsStaleGenerationAndWrongDomainWithoutMutation()
    {
        var s = Create(2, new CallbackClock());
        var called = false;
        KernelResult Publish() { called = true; return KernelResult.Ok(); }
        Assert.False(s.Kernel.CapabilityAuthority.CommitTelemetryPublication(s.Inspection!.Value,
            new(7601), s.Owner.Generation + 1, Publish).IsSuccess);
        Assert.False(s.Kernel.CapabilityAuthority.CommitTelemetryPublication(s.Inspection!.Value,
            new(7602), s.Owner.Generation, Publish).IsSuccess);
        Assert.False(called);
        Assert.True(Admit(s));
        Assert.Equal(1024UL, Used(s));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void BudgetTraceClockReentrantExitCannotPublishNewAdmission(int path)
    {
        var clock = new CallbackClock();
        var s = Create(path, clock);
        var initial = s.Kernel.StartTraceSession(s.Owner, 8);
        Assert.True(initial.IsSuccess, initial.Message);
        clock.Next = () => Assert.True(s.Kernel.TerminateProcess(s.Exit).IsSuccess);
        Assert.False(Admit(s));
        Assert.Null(clock.Next);
        Assert.Equal(path == 3 ? 2048UL : 0UL, Used(s));
        AssertNoActiveAdmissionForExit(s);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task AdmissionOverlappingTeardownCannotLeaveLateActiveRecord(int path)
    {
        var s = Create(path, new CallbackClock());
        using var start = new ManualResetEventSlim(false);
        var admission = Task.Run(() => { start.Wait(); return Admit(s); });
        var exit = Task.Run(() => { start.Wait(); return s.Kernel.TerminateProcess(s.Exit); });
        start.Set();
        _ = await admission;
        Assert.True((await exit).IsSuccess);
        Assert.Equal(0UL, Used(s));
        AssertNoActiveAdmissionForExit(s);
        Assert.False(Admit(s));
        Assert.Equal(0UL, Used(s));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void LiveAdmissionAndStaleProcessRefusalPreserveExactBudget(int path)
    {
        var s = Create(path, new CallbackClock());
        Assert.False(Admit(s with { Owner = s.Owner with { Generation = s.Owner.Generation + 1 } }));
        Assert.Equal(0UL, Used(s));
        Assert.True(Admit(s));
        Assert.Equal(path == 0 ? 256UL : 1024UL, Used(s));
        Assert.True(s.Kernel.TerminateProcess(s.Exit).IsSuccess);
        Assert.Equal(0UL, Used(s));
        AssertNoActiveAdmissionForExit(s);
    }

    private static bool Admit(Scenario s) => s.Path == 0
        ? s.Kernel.StartTraceSession(s.Owner, 1).IsSuccess
        : s.Kernel.StartTelemetrySubscription(s.Owner, s.Subject, TelemetryProjectionClass.SelfOperational,
            1, TelemetrySubscriptionOverflowPolicy.RejectSample, s.Inspection).IsSuccess;

    private static Scenario Create(int path, CallbackClock clock)
    {
        var kernel = new RuntimeKernel(null, clock);
        var owner = kernel.AdmitComponent(Plan("admission-owner", 761, 7601)).Value!;
        var subject = path >= 2 ? kernel.AdmitComponent(Plan("admission-subject", 762, 7602)).Value!.Process : owner.Process;
        CapabilityId? inspection = path >= 2 ? kernel.MintCapability(new(7601), owner.Process, ResourceKind.KernelService,
            CapabilityResourceIds.TelemetryInspection, CapabilityRights.Read).Value!.CapabilityId : null;
        return new(kernel, owner.Process, subject, path == 3 ? subject : owner.Process, owner.ProcessBudget, inspection, path);
    }

    private static ComponentAdmissionPlan Plan(string name, ulong pid, ulong domain, ulong generation = 1)
    {
        byte[] image = [0x10, 0x11];
        var manifest = new ServiceManifestV1(new(name), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(pid, domain, generation), budgetRequests: [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        return new(manifest, image);
    }

    private static void AssertNoActiveAdmissionForExit(Scenario s)
    {
        var subscriptions = (IDictionary)typeof(RuntimeKernel).GetField("_telemetrySubscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        foreach (var record in subscriptions.Values)
        {
            var admission = (TelemetrySubscriptionAdmission)record!.GetType().GetProperty("Admission")!.GetValue(record)!;
            if (admission.Owner == s.Exit || admission.Subject == s.Exit) Assert.Equal(TelemetrySubscriptionState.Closed, admission.State);
        }
        var traces = (IDictionary)typeof(DeterministicTraceAuthority).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.Traces)!;
        foreach (var record in traces.Values)
        {
            var admission = (TraceSessionAdmission)record!.GetType().GetProperty("Admission")!.GetValue(record)!;
            if (admission.Owner == s.Exit) Assert.Equal(TraceSessionState.Stopped, admission.State);
        }
    }

    private static ulong Used(Scenario s) => s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.TraceTelemetryBufferBytes).Used;
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, ProcessHandle Subject, ProcessHandle Exit,
        BudgetAccountHandle Account, CapabilityId? Inspection, int Path);
    private sealed class CallbackClock : TimeProvider
    {
        public Action? Next { get; set; }
        public override long GetTimestamp()
        {
            var callback = Next;
            Next = null;
            callback?.Invoke();
            return base.GetTimestamp();
        }
    }
}
