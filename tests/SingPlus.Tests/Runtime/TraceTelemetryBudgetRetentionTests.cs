using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class TraceTelemetryBudgetRetentionTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 2)]
    public void LocalStopCloseOrTeardownCannotEraseRefusedQuantitativeCharge(int path, int fault)
    {
        var s = Create(path >= 3);
        if (fault == 0) Assert.True(s.Kernel.Budgets.QuarantineLease(s.Owner, s.Reservation).IsSuccess);
        else if (fault == 1)
        {
            Assert.True(s.Kernel.Budgets.BindLease(s.Owner, s.Reservation).IsSuccess);
            Assert.True(s.Kernel.Budgets.BeginConsumption(s.Owner, s.Reservation).IsSuccess);
        }
        else
        {
            Assert.True(s.Kernel.Budgets.QuarantineLease(s.Owner, s.Reservation).IsSuccess);
            Assert.True(s.Kernel.Budgets.ReconcileLease(s.Owner, s.Reservation).IsSuccess);
        }
        var before = s.Kernel.QueryBudget(s.Reservation).Value!;
        if (path is 0 or 2)
        {
            Assert.True(s.Kernel.StopTraceSession(s.Owner, s.Trace!.Value).IsSuccess);
            Assert.True(s.Kernel.StopTraceSession(s.Owner, s.Trace.Value).IsSuccess);
        }
        if (path == 3)
        {
            Assert.Equal(KernelError.InvalidTransition, s.Kernel.CloseTelemetrySubscription(s.Owner, s.Telemetry!.Value).Error);
            Assert.Equal(KernelError.InvalidTransition, s.Kernel.CloseTelemetrySubscription(s.Owner, s.Telemetry.Value).Error);
        }
        if (path is 1 or 2 or 4) Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(before.State, s.Kernel.QueryBudget(s.Reservation).Value!.State);
        Assert.Equal(s.Amount, Used(s));
        Assert.False(s.Kernel.ReleaseBudget(s.Owner, s.Reservation).IsSuccess);
        if (s.Trace is { } trace)
        {
            Assert.Equal(TraceSessionState.Stopped, s.Kernel.Traces.Snapshot(trace).Value!.Session.State);
            Assert.Equal((s.Owner, s.Reservation), TraceCharges(s.Kernel)[trace]);
        }
        else
        {
            var records = (IDictionary)typeof(RuntimeKernel).GetField("_telemetrySubscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            var record = Assert.Single(records.Values.Cast<object>());
            Assert.Equal(s.Reservation, record.GetType().GetProperty("BudgetReservation")!.GetValue(record));
            Assert.Equal(TelemetrySubscriptionState.Closed, ((TelemetrySubscriptionAdmission)record.GetType().GetProperty("Admission")!.GetValue(record)!).State);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ActualNormalStopCloseAndTeardownRefundOnce(bool telemetry, bool teardown)
    {
        var s = Create(telemetry);
        if (teardown) Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        else if (telemetry)
        {
            Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, s.Telemetry!.Value).IsSuccess);
            Assert.True(s.Kernel.CloseTelemetrySubscription(s.Owner, s.Telemetry.Value).IsSuccess);
        }
        else
        {
            Assert.True(s.Kernel.StopTraceSession(s.Owner, s.Trace!.Value).IsSuccess);
            Assert.True(s.Kernel.StopTraceSession(s.Owner, s.Trace.Value).IsSuccess);
        }
        Assert.Equal(BudgetReservationState.Released, s.Kernel.QueryBudget(s.Reservation).Value!.State);
        Assert.Equal(0UL, Used(s));
        if (!telemetry) Assert.Empty(TraceCharges(s.Kernel));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OverlappingTraceStopsPreserveRefusalOrRefundExactlyOnce(bool quarantine)
    {
        var s = Create(false);
        if (quarantine) Assert.True(s.Kernel.Budgets.QuarantineLease(s.Owner, s.Reservation).IsSuccess);
        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        { start.Wait(); return s.Kernel.StopTraceSession(s.Owner, s.Trace!.Value); })).ToArray();
        start.Set();
        Assert.All(await Task.WhenAll(attempts), result => Assert.True(result.IsSuccess));
        Assert.Equal(quarantine ? s.Amount : 0UL, Used(s));
        Assert.Equal(quarantine, TraceCharges(s.Kernel).ContainsKey(s.Trace!.Value));
    }

    [Fact]
    public void NewTraceChargeCannotClearQuarantinedStoppedSession()
    {
        var s = Create(false);
        Assert.True(s.Kernel.Budgets.QuarantineLease(s.Owner, s.Reservation).IsSuccess);
        Assert.True(s.Kernel.StopTraceSession(s.Owner, s.Trace!.Value).IsSuccess);
        var next = s.Kernel.StartTraceSession(s.Owner, 1).Value!;
        Assert.NotEqual(s.Trace.Value, next.Session);
        var stale = next.Session with { Generation = new(next.Session.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.StopTraceSession(s.Owner, stale).Error);
        var other = TestFixtures.Create(s.Kernel, 752, 7502).Handle;
        Assert.Equal(KernelError.ProjectionDenied, s.Kernel.StopTraceSession(other, next.Session).Error);
        Assert.Equal(2, TraceCharges(s.Kernel).Count);
        Assert.Equal(s.Amount * 2, Used(s));
        Assert.True(s.Kernel.StopTraceSession(s.Owner, next.Session).IsSuccess);
        Assert.Equal((s.Owner, s.Reservation), TraceCharges(s.Kernel)[s.Trace.Value]);
        Assert.Single(TraceCharges(s.Kernel));
        Assert.Equal(s.Amount, Used(s));
    }

    private static Scenario Create(bool telemetry)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [0x10, 0x09];
        var manifest = new ServiceManifestV1(new("trace-retention"), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(751, 7501), budgetRequests: [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 2048)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 2048));
        var component = kernel.AdmitComponent(new(manifest, image)).Value!;
        TraceSessionHandle? trace = null;
        TelemetrySubscriptionHandle? subscription = null;
        if (telemetry) subscription = kernel.StartTelemetrySubscription(component.Process, component.Process,
            TelemetryProjectionClass.SelfOperational, 1, TelemetrySubscriptionOverflowPolicy.RejectSample).Value!.Subscription;
        else trace = kernel.StartTraceSession(component.Process, 1).Value!.Session;
        var charge = Assert.Single(kernel.Budgets.InspectionSnapshot());
        return new(kernel, component.Process, component.ProcessBudget, charge.Reservation, charge.Amounts.Single().Amount, trace, subscription);
    }

    private static Dictionary<TraceSessionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> TraceCharges(RuntimeKernel kernel) =>
        (Dictionary<TraceSessionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>)typeof(RuntimeKernel).GetField("_traceBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
    private static ulong Used(Scenario s) => s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.TraceTelemetryBufferBytes).Used;
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Account, BudgetReservationHandle Reservation,
        ulong Amount, TraceSessionHandle? Trace, TelemetrySubscriptionHandle? Telemetry);
}
