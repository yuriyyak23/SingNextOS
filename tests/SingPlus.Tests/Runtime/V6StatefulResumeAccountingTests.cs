using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6StatefulResumeAccountingTests
{
    [Fact]
    public void CapturedStatePinsCheckpointStorageUntilExplicitDiscard()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        var admitted = accounting.AdmitCapturedState(owner, Binding(), 64, () => 7, () => 11).Value!;

        Assert.Equal(V6StatefulSuspensionState.Suspended, admitted.State);
        Assert.False(admitted.AuthorizesResume);
        Assert.False(admitted.PreservesCapability);
        Assert.Equal(64UL, Used(budgets.Query(account).Value!));
        Assert.Equal(BudgetReservationState.Bound, budgets.Query(admitted.StorageReservation).Value!.State);

        var discarded = accounting.Discard(owner, admitted.Handle);
        Assert.True(discarded.IsSuccess, discarded.Message);
        Assert.Equal(V6StatefulSuspensionState.Discarded, discarded.Value!.State);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Discard(owner, admitted.Handle).IsSuccess);
    }

    [Fact]
    public void FreshThreeOwnerResumeReleasesStorageOnlyAfterProviderSuccess()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        var admitted = accounting.AdmitCapturedState(owner, Binding(), 80, () => 7, () => 11).Value!;
        var gates = 0;
        var providerCalls = 0;
        KernelResult Gate() { gates++; return KernelResult.Ok(); }
        KernelResult Provider() { providerCalls++; Assert.Equal(80UL, Used(budgets.Query(account).Value!)); return KernelResult.Ok(); }

        var resumed = accounting.Resume(owner, admitted.Handle, Gate, Gate, Gate,
            () => 7, () => 11, Provider);

        Assert.True(resumed.IsSuccess, resumed.Message);
        Assert.Equal(V6StatefulSuspensionState.Resumed, resumed.Value!.State);
        Assert.Equal(6, gates);
        Assert.Equal(1, providerCalls);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        Assert.Equal(KernelError.InvalidTransition,
            accounting.Resume(owner, admitted.Handle, Gate, Gate, Gate,
                () => 7, () => 11, Provider).Error);
    }

    [Fact]
    public void WrongSuspensionOwnerCannotInvokeResumeGatesOrProviderRestore()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        var admitted = accounting.AdmitCapturedState(owner, Binding(), 48, () => 7, () => 11).Value!;
        var callbacks = 0;
        KernelResult Callback() { callbacks++; return KernelResult.Ok(); }

        var wrongGeneration = new ProcessHandle(owner.ProcessId, owner.Generation + 1);
        var denied = accounting.Resume(wrongGeneration, admitted.Handle,
            Callback, Callback, Callback, () => 7, () => 11, Callback);

        Assert.Equal(KernelError.StaleGeneration, denied.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Bound,
            budgets.Query(admitted.StorageReservation).Value!.State);
        Assert.Equal(48UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Resume(owner, admitted.Handle,
            Callback, Callback, Callback, () => 7, () => 11, Callback).IsSuccess);
        Assert.Equal(7, callbacks);
    }

    [Fact]
    public void AdmissionDenialOrPreSubmitGenerationDriftLeavesStateSuspendedAndPinned()
    {
        foreach (var denyGate in new[] { true, false })
        {
            var (budgets, owner, account) = Create(256);
            var accounting = new V6StatefulResumeAccounting(budgets);
            var admitted = accounting.AdmitCapturedState(owner, Binding(), 40, () => 7, () => 11).Value!;
            var providerCalls = 0;
            var result = accounting.Resume(owner, admitted.Handle,
                denyGate ? () => KernelResult.Fail(KernelError.CapabilityRevoked, "revoked") : KernelResult.Ok,
                KernelResult.Ok, KernelResult.Ok, () => denyGate ? 7UL : 8UL, () => 11,
                () => { providerCalls++; return KernelResult.Ok(); });

            Assert.Equal(denyGate ? KernelError.CapabilityRevoked : KernelError.StaleGeneration, result.Error);
            Assert.Equal(0, providerCalls);
            Assert.Equal(40UL, Used(budgets.Query(account).Value!));
            Assert.Equal(BudgetReservationState.Bound, budgets.Query(admitted.StorageReservation).Value!.State);
        }
    }

    [Theory]
    [InlineData("singnext")]
    [InlineData("provider")]
    [InlineData("legality")]
    public void FinalResumeAdmissionRejectsPostCheckRevocationBeforeRestore(string revokedGate)
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        var admitted = accounting.AdmitCapturedState(owner, Binding(), 40, () => 7, () => 11).Value!;
        var revoked = false;
        var restoreCalls = 0;
        KernelResult SingNext() => revoked && revokedGate == "singnext"
            ? KernelResult.Fail(KernelError.CapabilityRevoked, "revoked") : KernelResult.Ok();
        KernelResult Provider() => revoked && revokedGate == "provider"
            ? KernelResult.Fail(KernelError.PlatformDenied, "revoked") : KernelResult.Ok();
        KernelResult Legality()
        {
            if (!revoked) { revoked = true; return KernelResult.Ok(); }
            return revokedGate == "legality"
                ? KernelResult.Fail(KernelError.PlatformDenied, "revoked") : KernelResult.Ok();
        }

        var result = accounting.Resume(owner, admitted.Handle, SingNext, Provider,
            Legality, () => 7, () => 11,
            () => { restoreCalls++; return KernelResult.Ok(); });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, restoreCalls);
        Assert.Equal(BudgetReservationState.Bound,
            budgets.Query(admitted.StorageReservation).Value!.State);
        Assert.Equal(40UL, Used(budgets.Query(account).Value!));
        Assert.Equal(V6StatefulSuspensionState.Discarded,
            accounting.Discard(owner, admitted.Handle).Value!.State);
    }

    [Fact]
    public void FinalResumeProviderDriftBeforeRestoreKeepsStorageEscrowBound()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        var admitted = accounting.AdmitCapturedState(owner, Binding(), 40, () => 7, () => 11).Value!;
        ulong providerGeneration = 7;
        var legalityCalls = 0;
        var restoreCalls = 0;
        KernelResult Legality()
        {
            if (++legalityCalls == 2) providerGeneration++;
            return KernelResult.Ok();
        }

        var result = accounting.Resume(owner, admitted.Handle, KernelResult.Ok,
            KernelResult.Ok, Legality, () => providerGeneration, () => 11,
            () => { restoreCalls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(2, legalityCalls);
        Assert.Equal(0, restoreCalls);
        Assert.Equal(BudgetReservationState.Bound,
            budgets.Query(admitted.StorageReservation).Value!.State);
        Assert.Equal(40UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Discard(owner, admitted.Handle).IsSuccess);
    }

    [Fact]
    public void ProviderFailureOrGenerationChangeDuringCallbackQuarantinesStorage()
    {
        foreach (var drift in new[] { false, true })
        {
            var (budgets, owner, account) = Create(256);
            var accounting = new V6StatefulResumeAccounting(budgets);
            var admitted = accounting.AdmitCapturedState(owner, Binding(), 48, () => 7, () => 11).Value!;
            ulong providerGeneration = 7;
            var result = accounting.Resume(owner, admitted.Handle,
                KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
                () => providerGeneration, () => 11,
                () =>
                {
                    if (drift) providerGeneration++;
                    return drift ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformUnavailable, "lost");
                });

            Assert.Equal(drift ? KernelError.StaleGeneration : KernelError.PlatformUnavailable, result.Error);
            Assert.Equal(BudgetReservationState.Quarantined,
                budgets.Query(admitted.StorageReservation).Value!.State);
            Assert.Equal(48UL, Used(budgets.Query(account).Value!));
            var reconciled = accounting.ReconcileQuarantinedDiscard(owner, admitted.Handle);
            Assert.True(reconciled.IsSuccess, reconciled.Message);
            Assert.Equal(V6StatefulSuspensionState.Discarded, reconciled.Value!.State);
            Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        }
    }

    [Fact]
    public void StaleAdmissionAndDuplicateCorrelationDoNotLeakStorage()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        Assert.Equal(KernelError.StaleGeneration,
            accounting.AdmitCapturedState(owner, Binding(), 32, () => 8, () => 11).Error);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));

        var admitted = accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Value!;
        Assert.Equal(KernelError.DuplicateIdentity,
            accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Error);
        Assert.Equal(32UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Discard(owner, admitted.Handle).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentResumeHasOneProviderCallbackAndOneTerminalRelease()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        var admitted = accounting.AdmitCapturedState(owner, Binding(), 72, () => 7, () => 11).Value!;
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var providerCalls = 0;
        var first = Task.Run(() => accounting.Resume(owner, admitted.Handle,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 7, () => 11,
            () =>
            {
                Interlocked.Increment(ref providerCalls);
                entered.Set();
                proceed.Wait(TimeSpan.FromSeconds(5));
                return KernelResult.Ok();
            }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var second = accounting.Resume(owner, admitted.Handle,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 7, () => 11,
            () => { Interlocked.Increment(ref providerCalls); return KernelResult.Ok(); });
        proceed.Set();
        var completed = await first;

        Assert.Equal(KernelError.InvalidTransition, second.Error);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(1, providerCalls);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
    }

    [Fact]
    public async Task ConcurrentDiscardRetainsEscrowUntilProviderClosureAndHasOneWinner()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        var admitted = accounting.AdmitCapturedState(owner, Binding(), 72, () => 7, () => 11).Value!;
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var providerCalls = 0;
        var first = Task.Run(() => accounting.DiscardWithProvider(owner, admitted.Handle, () =>
        {
            Interlocked.Increment(ref providerCalls);
            entered.Set();
            proceed.Wait(TimeSpan.FromSeconds(5));
            return KernelResult.Ok();
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var duplicate = accounting.DiscardWithProvider(owner, admitted.Handle, () =>
        {
            Interlocked.Increment(ref providerCalls);
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.InvalidTransition, duplicate.Error);
        Assert.Equal(72UL, Used(budgets.Query(account).Value!));
        proceed.Set();
        var completed = await first;

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(1, providerCalls);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
    }

    [Fact]
    public async Task ConcurrentAdmissionOfOneCorrelationCreatesOneStorageEscrow()
    {
        var (budgets, owner, account) = Create(256);
        var accounting = new V6StatefulResumeAccounting(budgets);
        using var barrier = new Barrier(2);
        ulong ProviderGeneration()
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
            return 7;
        }
        var first = Task.Run(() => accounting.AdmitCapturedState(
            owner, Binding(), 32, ProviderGeneration, () => 11));
        var second = Task.Run(() => accounting.AdmitCapturedState(
            owner, Binding(), 32, ProviderGeneration, () => 11));
        var results = await Task.WhenAll(first, second);

        var accepted = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Equal(KernelError.DuplicateIdentity,
            Assert.Single(results, result => !result.IsSuccess).Error);
        Assert.Equal(32UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Discard(owner, accepted.Handle).IsSuccess);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
    }

    private static ResumeBindingV1 Binding() => new(1, "operation:stateful:1", D('a'), D('b'), 3, 5, 7, 11);

    private static (ResourceBudgetAuthority Budgets, ProcessHandle Owner, BudgetAccountHandle Account) Create(ulong limit)
    {
        var budgets = new ResourceBudgetAuthority();
        Assert.True(budgets.ConfigureSystem([Amount(limit)]).IsSuccess);
        var service = budgets.CreateChild(budgets.SystemBudget, BudgetAccountLevel.Service,
            "stateful-service", [Amount(limit)]).Value!.Account;
        var account = budgets.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "stateful-process", [Amount(limit)]).Value!.Account;
        var owner = new ProcessHandle(new(888), 4);
        Assert.True(budgets.AttachProcess(owner, account).IsSuccess);
        return (budgets, owner, account);
    }

    private static BudgetAmount Amount(ulong amount) =>
        new(ServiceBudgetDimension.CheckpointStorageBytes, amount);

    private static ulong Used(BudgetAccountSnapshot snapshot) =>
        Assert.Single(snapshot.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.CheckpointStorageBytes).Used;

    private static string D(char value) => new(value, 64);
}
