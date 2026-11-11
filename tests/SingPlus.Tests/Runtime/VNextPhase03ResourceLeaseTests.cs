using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase03ResourceLeaseTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SplitCapturesOnceBeforeDebitAndRechecksCancelledParent(int fault)
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle _, BudgetAccountHandle account) = Create(10);
        BudgetReservationHandle parent = Reserve(authority, process, 10);
        ChangingSplitAmounts input = new ChangingSplitAmounts(() =>
        {
            Task<KernelResult<BudgetAccountSnapshot>> query = Task.Run(() => authority.Query(account));
            Assert.True(query.Wait(TimeSpan.FromSeconds(5)), "Split enumerated caller under owner lock.");
            Assert.True(query.Result.IsSuccess);
            if (fault == 1) Assert.True(authority.CancelLeasePreSubmit(process, parent).IsSuccess);
            if (fault == 2) throw new InvalidOperationException("Missing child amounts.");
        });
        BudgetReservationSnapshot? child = null;
        if (fault == 2)
            Assert.Throws<InvalidOperationException>(() => authority.SplitLease(process, parent, input));
        else
        {
            KernelResult<BudgetReservationSnapshot> split = authority.SplitLease(process, parent, input);
            Assert.Equal(fault == 0, split.IsSuccess);
            if (split.IsSuccess) child = split.Value!;
            else Assert.Equal(KernelError.InvalidTransition, split.Error);
        }
        Assert.Equal(1, input.Reads);
        if (fault == 1)
        {
            Assert.Equal(0UL, Used(authority.Query(account).Value!));
            parent = Reserve(authority, process, 10);
        }
        if (fault == 2)
        {
            Assert.Equal(10UL, Assert.Single(authority.Query(parent).Value!.Amounts).Amount);
            Assert.Equal(10UL, Used(authority.Query(account).Value!));
        }
        child ??= authority.SplitLease(process, parent, [Amount(4)]).Value!;
        Assert.Equal(4UL, Assert.Single(child.Amounts).Amount);
        Assert.Equal(6UL, Assert.Single(authority.Query(parent).Value!.Amounts).Amount);
        Assert.Equal(10UL, Used(authority.Query(account).Value!));
        Assert.True(authority.CancelLeasePreSubmit(process, parent).IsSuccess);
        Assert.Equal(4UL, Used(authority.Query(account).Value!));
        Assert.True(authority.CancelLeasePreSubmit(process, child.Reservation).IsSuccess);
        Assert.Equal(0UL, Used(authority.Query(account).Value!));
        Assert.True(authority.CancelLeasePreSubmit(process, child.Reservation).IsSuccess);
        Assert.True(authority.Reserve(process, [Amount(10)], BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).IsSuccess);
    }

    private sealed class ChangingSplitAmounts(Action callback) : IReadOnlyList<BudgetAmount>
    {
        public int Reads { get; private set; }
        public int Count => 1;
        public BudgetAmount this[int index] => Amount(4);
        public IEnumerator<BudgetAmount> GetEnumerator()
        {
            Reads++;
            callback();
            yield return Amount(Reads == 1 ? 4UL : 9UL);
        }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task LastUnitReservationHasExactlyOneWinner()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle _, BudgetAccountHandle _) = Create(10);
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<BudgetReservationSnapshot>>[] attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return authority.Reserve(process, [Amount(10)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None);
        })).ToArray();
        start.Set();
        KernelResult<BudgetReservationSnapshot>[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(31, results.Count(result => result.Error == KernelError.BudgetExceeded));
    }

    [Fact]
    public void ConsumptionCannotRefundUntilExactReconciliationAndSettlement()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle _, BudgetAccountHandle processAccount) = Create(10);
        BudgetReservationHandle lease = Reserve(authority, process, 10);
        Assert.Equal(BudgetReservationState.Bound, authority.BindLease(process, lease).Value!.State);
        Assert.Equal(BudgetReservationState.Consuming, authority.BeginConsumption(process, lease).Value!.State);
        Assert.Equal(KernelError.InvalidTransition, authority.Release(process, lease).Error);
        Assert.Equal(BudgetReservationState.Quarantined, authority.QuarantineLease(process, lease).Value!.State);
        Assert.Equal(KernelError.InvalidTransition, authority.Release(process, lease).Error);
        Assert.Equal(BudgetReservationState.Reconciled, authority.ReconcileLease(process, lease).Value!.State);

        KernelResult<BudgetReservationSnapshot> settled = authority.SettleLease(process, lease, [Amount(6)]);
        Assert.Equal(BudgetReservationState.Released, settled.Value!.State);
        Assert.Equal([Amount(6)], settled.Value.ChargedAmounts);
        Assert.Equal(6UL, Used(authority.Query(processAccount).Value!));
        BudgetSnapshotAssertions.Equal(settled.Value, authority.SettleLease(process, lease, [Amount(1)]).Value!);
        Assert.Equal(6UL, Used(authority.Query(processAccount).Value!));
        Assert.Equal(6UL, Used(authority.Release(process, lease).IsSuccess
            ? authority.Query(processAccount).Value! : throw new Xunit.Sdk.XunitException("terminal release must be idempotent")));
    }

    [Fact]
    public void PreSubmitCancellationAndSplitConserveCapacityExactlyOnce()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle _, BudgetAccountHandle processAccount) = Create(10);
        BudgetReservationHandle parent = Reserve(authority, process, 10);
        BudgetReservationSnapshot child = authority.SplitLease(process, parent, [Amount(4)]).Value!;

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
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle _, BudgetAccountHandle _) = Create(10, ulong.MaxValue);
        BudgetReservationHandle lease = Reserve(authority, process, 10);
        Assert.Equal(ulong.MaxValue, lease.ReservationId.Value);
        Assert.True(authority.CancelLeasePreSubmit(process, lease).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted,
            authority.Reserve(process, [Amount(1)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Error);
        Assert.Equal(KernelError.StaleGeneration,
            authority.BindLease(process, lease with { Generation = new(2) }).Error);

        (ResourceBudgetAuthority? settlementAuthority, ProcessHandle settlementProcess, BudgetAccountHandle _, BudgetAccountHandle _) = Create(10);
        BudgetReservationHandle settlementLease = Reserve(settlementAuthority, settlementProcess, 10);
        Assert.True(settlementAuthority.BindLease(settlementProcess, settlementLease).IsSuccess);
        Assert.True(settlementAuthority.BeginConsumption(settlementProcess, settlementLease).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded,
            settlementAuthority.SettleLease(settlementProcess, settlementLease, [Amount(11)]).Error);
    }

    [Fact]
    public void LiveAndQuarantinedLeasesBlockProcessRetirement()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle service, BudgetAccountHandle processAccount) = Create(10);
        BudgetReservationHandle lease = Reserve(authority, process, 10);
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
        for (int iteration = 0; iteration < 100; iteration++)
        {
            (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle _, BudgetAccountHandle processAccount) = Create(10);
            BudgetReservationHandle parent = Reserve(authority, process, 10);
            using ManualResetEventSlim start = new ManualResetEventSlim(false);
            Task<KernelResult<BudgetReservationSnapshot>> split = Task.Run(() => { start.Wait(); return authority.SplitLease(process, parent, [Amount(4)]); });
            Task<KernelResult<BudgetReservationSnapshot>> consume = Task.Run(() =>
            {
                start.Wait();
                KernelResult<BudgetReservationSnapshot> bound = authority.BindLease(process, parent);
                return bound.IsSuccess ? authority.BeginConsumption(process, parent) : bound;
            });
            start.Set();
            KernelResult<BudgetReservationSnapshot>[] results = await Task.WhenAll(split, consume);

            Assert.InRange(Used(authority.Query(processAccount).Value!), 0UL, 10UL);
            Assert.True(results[0].IsSuccess || results[0].Error == KernelError.InvalidTransition);
            Assert.True(results[1].IsSuccess || results[1].Error == KernelError.InvalidTransition);
        }
    }

    private static (ResourceBudgetAuthority Authority, ProcessHandle Process, BudgetAccountHandle Service, BudgetAccountHandle ProcessAccount)
        Create(ulong limit, ulong initialReservationId = 1)
    {
        ResourceBudgetAuthority authority = new ResourceBudgetAuthority(initialReservationId);
        Assert.True(authority.ConfigureSystem([Amount(limit)]).IsSuccess);
        BudgetAccountHandle service = authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service, "svc", [Amount(limit)]).Value!.Account;
        BudgetAccountHandle processAccount = authority.CreateChild(service, BudgetAccountLevel.ProcessDomain, "proc", [Amount(limit)]).Value!.Account;
        ProcessHandle process = new ProcessHandle(new(100), 7);
        Assert.True(authority.AttachProcess(process, processAccount).IsSuccess);
        return (authority, process, service, processAccount);
    }

    private static BudgetReservationHandle Reserve(ResourceBudgetAuthority authority, ProcessHandle process, ulong amount) =>
        authority.Reserve(process, [Amount(amount)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Value!.Reservation;

    private static BudgetAmount Amount(ulong amount) => new(ServiceBudgetDimension.ComputeTimeNanoseconds, amount);
    private static ulong Used(BudgetAccountSnapshot account) =>
        Assert.Single(account.Usage, usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;
}
