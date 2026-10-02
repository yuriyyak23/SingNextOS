using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class TraceClockPublicationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClockFailureCannotMutateSequenceBufferOrDropFacts(bool full)
    {
        var s = Create(full ? 1 : 4);
        if (full) Assert.True(Record(s).IsSuccess);
        var before = s.Kernel.Traces.Snapshot(s.Session).Value!;
        s.Clock.Next = () => throw new InvalidOperationException("injected clock loss");
        Assert.Equal(KernelError.PlatformFaulted, Record(s).Error);
        var after = s.Kernel.Traces.Snapshot(s.Session).Value!;
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
        var s = Create(4);
        s.Clock.Next = () => Assert.True(exit ? s.Kernel.TerminateProcess(s.Owner).IsSuccess
            : s.Kernel.StopTraceSession(s.Owner, s.Session).IsSuccess);
        Assert.Equal(KernelError.TraceStopped, Record(s).Error);
        var snapshot = s.Kernel.Traces.Snapshot(s.Session).Value!;
        Assert.Equal(TraceSessionState.Stopped, snapshot.Session.State);
        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public async Task BlockedClockHoldsNoOwnerProducerLockAndCannotPublishAfterStop()
    {
        var s = Create(4);
        using var entered = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        s.Clock.Next = () => { entered.Set(); Assert.True(proceed.Wait(TimeSpan.FromSeconds(5))); };
        var recording = Task.Run(() => Record(s));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var snapshot = await Task.Run(() => s.Kernel.Traces.Snapshot(s.Session)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(snapshot.Value!.Events);
            var stopped = await Task.Run(() => s.Kernel.StopTraceSession(s.Owner, s.Session)).WaitAsync(TimeSpan.FromSeconds(5));
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
        var s = Create(8);
        s.Clock.Next = () => throw new InvalidOperationException("observational clock unavailable");
        if (telemetry)
        {
            var admitted = s.Kernel.StartTelemetrySubscription(s.Owner, s.Owner, TelemetryProjectionClass.SelfOperational,
                1, TelemetrySubscriptionOverflowPolicy.RejectSample);
            Assert.True(admitted.IsSuccess, admitted.Message);
            Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, admitted.Value!.Subscription).IsSuccess);
        }
        else
        {
            var admitted = s.Kernel.StartTraceSession(s.Owner, 1);
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
        var s = Create(1);
        if (dropped) Assert.True(Record(s).IsSuccess);
        var producer = Producer(s);
        producer.GetType().GetProperty(dropped ? "Dropped" : "NextSequence")!.SetValue(producer, dropped ? ulong.MaxValue : 0UL);
        var before = s.Kernel.Traces.Snapshot(s.Session).Value!;
        Assert.Equal(KernelError.CapacityExhausted, Record(s).Error);
        var after = s.Kernel.Traces.Snapshot(s.Session).Value!;
        Assert.Equal(before.Events, after.Events);
        Assert.Equal(before.DroppedEventCount, after.DroppedEventCount);
        Assert.False(after.Complete);
    }

    [Fact]
    public void FinalSequencePublishesOnceAndCannotWrap()
    {
        var s = Create(4);
        var producer = Producer(s);
        producer.GetType().GetProperty("NextSequence")!.SetValue(producer, ulong.MaxValue);
        Assert.True(Record(s).IsSuccess);
        Assert.Equal(ulong.MaxValue, Assert.Single(s.Kernel.Traces.Snapshot(s.Session).Value!.Events).Sequence.Value);
        Assert.Equal(KernelError.CapacityExhausted, Record(s).Error);
        Assert.Single(s.Kernel.Traces.Snapshot(s.Session).Value!.Events);
    }

    private static object Producer(Scenario s)
    {
        var records = (IDictionary)typeof(DeterministicTraceAuthority).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.Traces)!;
        return records[s.Session.SessionId]!.GetType().GetProperty("Producer")!.GetValue(records[s.Session.SessionId])!;
    }
    private static KernelResult Record(Scenario s) => s.Kernel.RecordTraceSemanticEvent(s.Owner, TraceEventKind.SupervisorObservation,
        new(new("clock-boundary")), new("clock", "boundary", "active", "observed"));
    private static Scenario Create(int capacity)
    {
        var clock = new CallbackClock();
        var kernel = new RuntimeKernel(null, clock);
        byte[] image = [0x10, 0x12];
        var manifest = new ServiceManifestV1(new("trace-clock"), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(771, 7701), budgetRequests: [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        var component = kernel.AdmitComponent(new(manifest, image)).Value!;
        var trace = kernel.StartTraceSession(component.Process, capacity).Value!;
        return new(kernel, component.Process, component.ProcessBudget, trace.Session, clock);
    }
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Account, TraceSessionHandle Session, CallbackClock Clock);
    private sealed class CallbackClock : TimeProvider
    {
        public Action? Next { get; set; }
        public override long GetTimestamp() { var callback = Next; Next = null; callback?.Invoke(); return base.GetTimestamp(); }
    }
}
