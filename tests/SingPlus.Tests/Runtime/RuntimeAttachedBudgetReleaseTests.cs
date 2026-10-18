using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class RuntimeAttachedBudgetReleaseTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void ObservationCannotReleaseLiveResourceChargeButActualConsumerCan(int path, bool bound)
    {
        var s = Create(path);
        if (bound) Assert.True(s.Kernel.Budgets.BindLease(s.Owner, s.Reservation).IsSuccess);
        var before = s.Kernel.QueryBudget(s.Reservation).Value!;
        var denied = s.Kernel.ReleaseBudget(s.Owner, s.Reservation);
        Assert.Equal(KernelError.InvalidTransition, denied.Error);
        Assert.Equal(before.State, s.Kernel.QueryBudget(s.Reservation).Value!.State);
        Assert.Equal(s.Amount, Used(s));
        Assert.Equal(KernelError.BudgetExceeded, s.Kernel.ReserveBudget(s.Owner,
            [new(s.Dimension, 1)], BudgetReservationLifetime.LocalResource).Error);
        Assert.True(s.Close().IsSuccess);
        Assert.Equal(BudgetReservationState.Released, s.Kernel.QueryBudget(s.Reservation).Value!.State);
        Assert.Equal(0UL, Used(s));
        Assert.True(s.Kernel.ReleaseBudget(s.Owner, s.Reservation).IsSuccess);
        Assert.Equal(0UL, Used(s));
    }

    [Theory]
    [InlineData(BudgetReservationLifetime.LocalResource)]
    [InlineData(BudgetReservationLifetime.ExternalEffect)]
    [InlineData(BudgetReservationLifetime.TraceTelemetryBuffer)]
    [InlineData(BudgetReservationLifetime.CheckpointImage)]
    public void ExplicitReservationReleaseRemainsCompatibleRegardlessOfLifetime(BudgetReservationLifetime lifetime)
    {
        var s = Setup();
        var reservation = s.Kernel.ReserveBudget(s.Owner,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 16)], lifetime).Value!;
        Assert.True(s.Kernel.ReleaseBudget(s.Owner, reservation.Reservation).IsSuccess);
        Assert.Equal(0UL, s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void WrongProcessAndStaleReservationCannotChangeAttachedCharge()
    {
        var s = Create(0);
        var other = TestFixtures.Create(s.Kernel, 703, 7003).Handle;
        Assert.Equal(KernelError.WrongRegionOwner, s.Kernel.ReleaseBudget(other, s.Reservation).Error);
        var stale = s.Reservation with { Generation = new(s.Reservation.Generation.Value + 1) };
        Assert.Equal(BudgetReservationState.Stale, s.Kernel.ReleaseBudget(s.Owner, stale).Value!.State);
        Assert.Equal(s.Amount, Used(s));
        Assert.True(s.Close().IsSuccess);
    }

    [Fact]
    public async Task PublicReleaseAndActualResourceReleaseCannotRefundTwice()
    {
        var s = Create(0);
        using var start = new ManualResetEventSlim(false);
        var publicRelease = Task.Run(() => { start.Wait(); return s.Kernel.ReleaseBudget(s.Owner, s.Reservation); });
        var actualRelease = Task.Run(() => { start.Wait(); return s.Close(); });
        start.Set();
        var publicResult = await publicRelease;
        Assert.True((await actualRelease).IsSuccess);
        Assert.True(publicResult.IsSuccess || publicResult.Error == KernelError.InvalidTransition);
        Assert.Equal(0UL, Used(s));
        Assert.Equal(BudgetReservationState.Released, s.Kernel.QueryBudget(s.Reservation).Value!.State);
    }

    private static Scenario Create(int path)
    {
        var (kernel, owner, account) = Setup(path == 1 ? 8UL : 16UL);
        Func<KernelResult> close;
        ServiceBudgetDimension dimension;
        ulong amount;
        if (path == 0)
        {
            var buffer = kernel.AllocateBuffer<byte>(owner, 16).Value!;
            close = () => kernel.ReleaseRegion(owner, buffer);
            dimension = ServiceBudgetDimension.OwnedMemoryBytes; amount = 16;
        }
        else if (path == 1)
        {
            var scalar = kernel.AllocateRegion(owner, 0UL).Value!;
            close = () => kernel.ReleaseRegion(owner, scalar);
            dimension = ServiceBudgetDimension.OwnedMemoryBytes; amount = 8;
        }
        else if (path == 2)
        {
            var receiver = TestFixtures.Create(kernel, 702, 7002).Handle;
            var protocol = new ProtocolDefinitionV1("Charge", "charge", "Ready", null,
                [new(1, "Ping")], [new(1, "Ready", "Ready")]);
            var channel = kernel.CreateChannel(owner, receiver, protocol, 1).Value;
            Assert.True(kernel.Send(owner, receiver, channel.Left, 1).IsSuccess);
            close = () => { var result = kernel.Receive(receiver, channel.Right); return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!); };
            dimension = ServiceBudgetDimension.IpcMessages; amount = 1;
        }
        else
        {
            var trace = kernel.StartTraceSession(owner, 1).Value!;
            close = () => { var result = kernel.StopTraceSession(owner, trace.Session); return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!); };
            dimension = ServiceBudgetDimension.TraceTelemetryBufferBytes; amount = 256;
        }
        var records = (IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Budgets)!;
        var record = Assert.Single(records.Values.Cast<object>());
        var reservation = (BudgetReservationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
        return new(kernel, owner, account, reservation, dimension, amount, close);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Account) Setup(ulong memoryBytes = 16)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 701, 7001).Handle;
        var cap = kernel.MintCapability(new(7001), owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var account = kernel.AdmitProcessBudget(owner, cap, owner, "attached-release",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, memoryBytes), new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 256)]).Value!.ProcessBudget;
        return (kernel, owner, account);
    }

    private static ulong Used(Scenario s) => s.Kernel.QueryBudget(s.Account).Value!.Usage.Single(u => u.Dimension == s.Dimension).Used;
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Account,
        BudgetReservationHandle Reservation, ServiceBudgetDimension Dimension, ulong Amount, Func<KernelResult> Close);
}
