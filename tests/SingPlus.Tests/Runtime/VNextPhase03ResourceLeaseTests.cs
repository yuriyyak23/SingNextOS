using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase03ResourceLeaseTests
{
    [Fact]
    public async Task LastUnitReservationHasExactlyOneWinner()
    {
        var (authority, process, _, _) = Create(10);
        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return authority.Reserve(process, [Amount(10)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(31, results.Count(result => result.Error == KernelError.BudgetExceeded));
    }

    [Fact]
    public void ConsumptionCannotRefundUntilExactReconciliationAndSettlement()
    {
        var (authority, process, _, processAccount) = Create(10);
        var lease = Reserve(authority, process, 10);
        Assert.Equal(BudgetReservationState.Bound, authority.BindLease(process, lease).Value!.State);
        Assert.Equal(BudgetReservationState.Consuming, authority.BeginConsumption(process, lease).Value!.State);
        Assert.Equal(KernelError.InvalidTransition, authority.Release(process, lease).Error);
        Assert.Equal(BudgetReservationState.Quarantined, authority.QuarantineLease(process, lease).Value!.State);
        Assert.Equal(KernelError.InvalidTransition, authority.Release(process, lease).Error);
        Assert.Equal(BudgetReservationState.Reconciled, authority.ReconcileLease(process, lease).Value!.State);

        var settled = authority.SettleLease(process, lease, [Amount(6)]);
        Assert.Equal(BudgetReservationState.Released, settled.Value!.State);
        Assert.Equal([Amount(6)], settled.Value.ChargedAmounts);
        Assert.Equal(6UL, Used(authority.Query(processAccount).Value!));
        Assert.Equal(settled.Value, authority.SettleLease(process, lease, [Amount(1)]).Value);
        Assert.Equal(6UL, Used(authority.Query(processAccount).Value!));
        Assert.Equal(6UL, Used(authority.Release(process, lease).IsSuccess
            ? authority.Query(processAccount).Value! : throw new Xunit.Sdk.XunitException("terminal release must be idempotent")));
    }

    [Fact]
    public void PreSubmitCancellationAndSplitConserveCapacityExactlyOnce()
    {
        var (authority, process, _, processAccount) = Create(10);
        var parent = Reserve(authority, process, 10);
        var child = authority.SplitLease(process, parent, [Amount(4)]).Value!;

        Assert.Equal(10UL, Used(authority.Query(processAccount).Value!));
        Assert.Equal(6UL, Assert.Single(authority.Query(parent).Value!.Amounts).Amount);
        Assert.Equal(4UL, Assert.Single(child.Amounts).Amount);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            authority.CancelLeasePreSubmit(process, parent).Value!.State);
        Assert.Equal(4UL, Used(authority.Query(processAccount).Value!));
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            authority.CancelLeasePreSubmit(process, child.Reservation).Value!.State);
        Assert.Equal(0UL, Used(authority.Query(processAccount).Value!));
        Assert.Equal(0UL, Used(authority.Query(processAccount).Value!));
    }

    [Fact]
    public void OversettlementStaleGenerationAndIdentityExhaustionFailClosed()
    {
        var (authority, process, _, _) = Create(10, ulong.MaxValue);
        var lease = Reserve(authority, process, 10);
        Assert.Equal(ulong.MaxValue, lease.ReservationId.Value);
        Assert.True(authority.CancelLeasePreSubmit(process, lease).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted,
            authority.Reserve(process, [Amount(1)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Error);
        Assert.Equal(KernelError.StaleGeneration,
            authority.BindLease(process, lease with { Generation = new(2) }).Error);

        var (settlementAuthority, settlementProcess, _, _) = Create(10);
        var settlementLease = Reserve(settlementAuthority, settlementProcess, 10);
        Assert.True(settlementAuthority.BindLease(settlementProcess, settlementLease).IsSuccess);
        Assert.True(settlementAuthority.BeginConsumption(settlementProcess, settlementLease).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded,
            settlementAuthority.SettleLease(settlementProcess, settlementLease, [Amount(11)]).Error);
    }

    [Fact]
    public void LiveAndQuarantinedLeasesBlockProcessRetirement()
    {
        var (authority, process, service, processAccount) = Create(10);
        var lease = Reserve(authority, process, 10);
        Assert.True(authority.BindLease(process, lease).IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked,
            authority.RetireProcessHierarchy(process, processAccount, service).Error);
        Assert.True(authority.BeginConsumption(process, lease).IsSuccess);
        Assert.True(authority.QuarantineLease(process, lease).IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked,
            authority.RetireProcessHierarchy(process, processAccount, service).Error);
    }

    [Fact]
    public async Task SplitVersusConsumptionRaceNeverCreatesCapacity()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var (authority, process, _, processAccount) = Create(10);
            var parent = Reserve(authority, process, 10);
            using var start = new ManualResetEventSlim(false);
            var split = Task.Run(() => { start.Wait(); return authority.SplitLease(process, parent, [Amount(4)]); });
            var consume = Task.Run(() =>
            {
                start.Wait();
                var bound = authority.BindLease(process, parent);
                return bound.IsSuccess ? authority.BeginConsumption(process, parent) : bound;
            });
            start.Set();
            var results = await Task.WhenAll(split, consume);

            Assert.InRange(Used(authority.Query(processAccount).Value!), 0UL, 10UL);
            Assert.True(results[0].IsSuccess || results[0].Error == KernelError.InvalidTransition);
            Assert.True(results[1].IsSuccess || results[1].Error == KernelError.InvalidTransition);
        }
    }

    private static (ResourceBudgetAuthority Authority, ProcessHandle Process, BudgetAccountHandle Service, BudgetAccountHandle ProcessAccount)
        Create(ulong limit, ulong initialReservationId = 1)
    {
        var authority = new ResourceBudgetAuthority(initialReservationId);
        Assert.True(authority.ConfigureSystem([Amount(limit)]).IsSuccess);
        var service = authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service, "svc", [Amount(limit)]).Value!.Account;
        var processAccount = authority.CreateChild(service, BudgetAccountLevel.ProcessDomain, "proc", [Amount(limit)]).Value!.Account;
        var process = new ProcessHandle(new(100), 7);
        Assert.True(authority.AttachProcess(process, processAccount).IsSuccess);
        return (authority, process, service, processAccount);
    }

    private static BudgetReservationHandle Reserve(ResourceBudgetAuthority authority, ProcessHandle process, ulong amount) =>
        authority.Reserve(process, [Amount(amount)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Value!.Reservation;

    private static BudgetAmount Amount(ulong amount) => new(ServiceBudgetDimension.ComputeTimeNanoseconds, amount);
    private static ulong Used(BudgetAccountSnapshot account) =>
        Assert.Single(account.Usage, usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;
}
