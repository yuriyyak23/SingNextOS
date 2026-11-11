using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6StatefulResumeAccountingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CapturedStorageRequiresExistingResumeDiscardOrReconciliationConsumer(int path)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(64);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt captured = accounting.AdmitCapturedState(owner, Binding(), 64, () => 7, () => 11).Value!;
        Assert.Equal(KernelError.InvalidTransition, budgets.ReleaseExplicit(owner, captured.StorageReservation).Error);
        Assert.Equal(64UL, Used(budgets.Query(account).Value!));
        Assert.Equal(KernelError.BudgetExceeded, budgets.Reserve(owner, [Amount(1)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Error);
        if (path == 0) Assert.True(accounting.Discard(owner, captured.Handle).IsSuccess);
        else if (path == 1)
            Assert.True(accounting.Resume(owner, captured.Handle, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
                () => 7, () => 11, KernelResult.Ok).IsSuccess);
        else
        {
            Assert.False(accounting.DiscardWithProvider(owner, captured.Handle,
                () => KernelResult.Fail(KernelError.PlatformUnavailable, "injected discard loss")).IsSuccess);
            Assert.Equal(KernelError.InvalidTransition, budgets.ReleaseExplicit(owner, captured.StorageReservation).Error);
            Assert.True(accounting.ReconcileQuarantinedDiscardWithProvider(owner, captured.Handle, KernelResult.Ok).IsSuccess);
        }
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        Assert.Equal(BudgetReservationState.Released, budgets.Query(captured.StorageReservation).Value!.State);
        Assert.True(budgets.ReleaseExplicit(owner, captured.StorageReservation).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BindFailureRemovesRecordOnlyAfterExactBudgetOwnerRelease(int mutation)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        accounting.BeforeNextStorageBindForTest(reservation =>
        {
            if (mutation == 0) Assert.True(budgets.QuarantineLease(owner, reservation).IsSuccess);
            else if (mutation == 1)
            {
                Assert.True(budgets.BindLease(owner, reservation).IsSuccess);
                Assert.True(budgets.BeginConsumption(owner, reservation).IsSuccess);
            }
            else Assert.True(budgets.Release(owner, reservation).IsSuccess);
        });
        Assert.False(accounting.AdmitCapturedState(owner, Binding(), 64, () => 7, () => 11).IsSuccess);
        KernelResult<V6StatefulSuspensionReceipt> recovery = accounting.FindRecoverySuspension(owner, Binding());
        if (mutation != 2)
        {
            Assert.True(recovery.IsSuccess, recovery.Message);
            Assert.Equal(BudgetReservationState.Quarantined, budgets.Query(recovery.Value!.StorageReservation).Value!.State);
            Assert.Equal(64UL, Used(budgets.Query(account).Value!));
            Assert.Equal(KernelError.DuplicateIdentity,
                accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Error);
            Assert.True(accounting.ReconcileQuarantinedDiscardWithProvider(owner, recovery.Value.Handle,
                KernelResult.Ok).IsSuccess);
        }
        else Assert.False(recovery.IsSuccess);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).IsSuccess);
    }

    [Fact]
    public void PostBindFaultHookExceptionRetainsExactRecoveryEscrow()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        accounting.AfterNextStorageBindForTest(() => throw new InvalidOperationException("injected boundary loss"));
        Assert.Equal(KernelError.PlatformUnavailable,
            accounting.AdmitCapturedState(owner, Binding(), 64, () => 7, () => 11).Error);
        V6StatefulSuspensionReceipt recovery = accounting.FindRecoverySuspension(owner, Binding()).Value!;
        Assert.Equal(V6StatefulSuspensionState.Quarantined, recovery.State);
        Assert.Equal(64UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.ReconcileQuarantinedDiscardWithProvider(owner, recovery.Handle,
            KernelResult.Ok).IsSuccess);
        Assert.True(accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AdmissionGenerationLossRetainsEscrowUntilExactProviderDiscard(int fault)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        int providerReads = 0;
        int runtimeReads = 0;
        ulong ProviderGeneration()
        {
            if (++providerReads == 2 && fault == 2) throw new InvalidOperationException("provider loss");
            return providerReads == 2 && fault == 0 ? 8UL : 7UL;
        }
        ulong RuntimeGeneration() => ++runtimeReads == 2 && fault == 1 ? 12UL : 11UL;
        KernelResult<V6StatefulSuspensionReceipt> denied = accounting.AdmitCapturedState(owner, Binding(), 64, ProviderGeneration, RuntimeGeneration);
        Assert.Equal(fault == 2 ? KernelError.PlatformUnavailable : KernelError.StaleGeneration, denied.Error);
        KernelResult<V6StatefulSuspensionReceipt> recovery = accounting.FindRecoverySuspension(owner, Binding());
        Assert.True(recovery.IsSuccess, recovery.Message);
        Assert.Equal(V6StatefulSuspensionState.Quarantined, recovery.Value!.State);
        Assert.Equal(BudgetReservationState.Quarantined,
            budgets.Query(recovery.Value.StorageReservation).Value!.State);
        Assert.Equal(64UL, Used(budgets.Query(account).Value!));
        Assert.Equal(KernelError.DuplicateIdentity,
            accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Error);
        int closures = 0;
        Assert.True(accounting.ReconcileQuarantinedDiscardWithProvider(owner, recovery.Value.Handle,
            () => { closures++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(1, closures);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnclosedCorrelationCannotAcquireSecondStorageEscrow(bool settlementPending)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 64, () => 7, () => 11).Value!;
        Assert.False(accounting.DiscardWithProvider(owner, admitted.Handle,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "lost closure")).IsSuccess);
        if (settlementPending)
        {
            accounting.FailNextSettlementForTest();
            Assert.False(accounting.ReconcileQuarantinedDiscardWithProvider(owner, admitted.Handle,
                KernelResult.Ok).IsSuccess);
        }
        Assert.Equal(KernelError.DuplicateIdentity,
            accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Error);
        Assert.Equal(64UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.ReconcileQuarantinedDiscardWithProvider(owner, admitted.Handle,
            KernelResult.Ok).IsSuccess);
        Assert.True(accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).IsSuccess);
        Assert.Equal(32UL, Used(budgets.Query(account).Value!));
    }

    [Fact]
    public void DiscardCallbackCannotReadmitItsUnclosedCorrelation()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 64, () => 7, () => 11).Value!;
        KernelResult<V6StatefulSuspensionReceipt> discarded = accounting.DiscardWithProvider(owner, admitted.Handle, () =>
        {
            Assert.Equal(KernelError.DuplicateIdentity,
                accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Error);
            Assert.Equal(64UL, Used(budgets.Query(account).Value!));
            return KernelResult.Ok();
        });
        Assert.True(discarded.IsSuccess, discarded.Message);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).IsSuccess);
    }

    [Fact]
    public void CapturedStatePinsCheckpointStorageUntilExplicitDiscard()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 64, () => 7, () => 11).Value!;

        Assert.Equal(V6StatefulSuspensionState.Suspended, admitted.State);
        Assert.False(admitted.AuthorizesResume);
        Assert.False(admitted.PreservesCapability);
        Assert.Equal(64UL, Used(budgets.Query(account).Value!));
        Assert.Equal(BudgetReservationState.Bound, budgets.Query(admitted.StorageReservation).Value!.State);

        KernelResult<V6StatefulSuspensionReceipt> discarded = accounting.Discard(owner, admitted.Handle);
        Assert.True(discarded.IsSuccess, discarded.Message);
        Assert.Equal(V6StatefulSuspensionState.Discarded, discarded.Value!.State);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Discard(owner, admitted.Handle).IsSuccess);
    }

    [Fact]
    public void FreshThreeOwnerResumeReleasesStorageOnlyAfterProviderSuccess()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 80, () => 7, () => 11).Value!;
        int gates = 0;
        int providerCalls = 0;
        KernelResult Gate() { gates++; return KernelResult.Ok(); }
        KernelResult Provider() { providerCalls++; Assert.Equal(80UL, Used(budgets.Query(account).Value!)); return KernelResult.Ok(); }

        KernelResult<V6StatefulSuspensionReceipt> resumed = accounting.Resume(owner, admitted.Handle, Gate, Gate, Gate,
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FinalResumeCallbacksCannotDonateQuarantinedStorageToRestore(int mutationPoint)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 48, () => 7, () => 11).Value!;
        int[] gateCalls = new int[3];
        int generationCalls = 0;
        int restores = 0;
        KernelResult Gate(int index)
        {
            if (++gateCalls[index] == 2 && mutationPoint == index)
                Assert.True(budgets.QuarantineLease(owner, admitted.StorageReservation).IsSuccess);
            return KernelResult.Ok();
        }
        ulong ProviderGeneration()
        {
            if (++generationCalls == 2 && mutationPoint == 3)
                Assert.True(budgets.QuarantineLease(owner, admitted.StorageReservation).IsSuccess);
            return 7;
        }
        KernelResult<V6StatefulSuspensionReceipt> denied = accounting.Resume(owner, admitted.Handle,
            () => Gate(0), () => Gate(1), () => Gate(2), ProviderGeneration, () => 11,
            () => { restores++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.StaleGeneration, denied.Error);
        Assert.Equal(0, restores);
        Assert.Equal(BudgetReservationState.Quarantined, budgets.Query(admitted.StorageReservation).Value!.State);
        Assert.Equal(48UL, Used(budgets.Query(account).Value!));
    }

    [Fact]
    public void WrongSuspensionOwnerCannotInvokeResumeGatesOrProviderRestore()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 48, () => 7, () => 11).Value!;
        int callbacks = 0;
        KernelResult Callback() { callbacks++; return KernelResult.Ok(); }

        ProcessHandle wrongGeneration = new ProcessHandle(owner.ProcessId, owner.Generation + 1);
        KernelResult<V6StatefulSuspensionReceipt> denied = accounting.Resume(wrongGeneration, admitted.Handle,
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
        foreach (bool denyGate in new[] { true, false })
        {
            (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
            V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
            V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 40, () => 7, () => 11).Value!;
            int providerCalls = 0;
            KernelResult<V6StatefulSuspensionReceipt> result = accounting.Resume(owner, admitted.Handle,
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
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 40, () => 7, () => 11).Value!;
        bool revoked = false;
        int restoreCalls = 0;
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

        KernelResult<V6StatefulSuspensionReceipt> result = accounting.Resume(owner, admitted.Handle, SingNext, Provider,
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
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 40, () => 7, () => 11).Value!;
        ulong providerGeneration = 7;
        int legalityCalls = 0;
        int restoreCalls = 0;
        KernelResult Legality()
        {
            if (++legalityCalls == 2) providerGeneration++;
            return KernelResult.Ok();
        }

        KernelResult<V6StatefulSuspensionReceipt> result = accounting.Resume(owner, admitted.Handle, KernelResult.Ok,
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
        foreach (bool drift in new[] { false, true })
        {
            (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
            V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
            V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 48, () => 7, () => 11).Value!;
            ulong providerGeneration = 7;
            KernelResult<V6StatefulSuspensionReceipt> result = accounting.Resume(owner, admitted.Handle,
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
            KernelResult<V6StatefulSuspensionReceipt> reconciled = accounting.ReconcileQuarantinedDiscard(owner, admitted.Handle);
            Assert.True(reconciled.IsSuccess, reconciled.Message);
            Assert.Equal(V6StatefulSuspensionState.Discarded, reconciled.Value!.State);
            Assert.Equal(0UL, Used(budgets.Query(account).Value!));
        }
    }

    [Fact]
    public void StaleAdmissionAndDuplicateCorrelationDoNotLeakStorage()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        Assert.Equal(KernelError.StaleGeneration,
            accounting.AdmitCapturedState(owner, Binding(), 32, () => 8, () => 11).Error);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));

        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Value!;
        Assert.Equal(KernelError.DuplicateIdentity,
            accounting.AdmitCapturedState(owner, Binding(), 32, () => 7, () => 11).Error);
        Assert.Equal(32UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Discard(owner, admitted.Handle).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentResumeHasOneProviderCallbackAndOneTerminalRelease()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 72, () => 7, () => 11).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim proceed = new ManualResetEventSlim();
        int providerCalls = 0;
        Task<KernelResult<V6StatefulSuspensionReceipt>> first = Task.Run(() => accounting.Resume(owner, admitted.Handle,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 7, () => 11,
            () =>
            {
                Interlocked.Increment(ref providerCalls);
                entered.Set();
                proceed.Wait(TimeSpan.FromSeconds(5));
                return KernelResult.Ok();
            }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        KernelResult<V6StatefulSuspensionReceipt> second = accounting.Resume(owner, admitted.Handle,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 7, () => 11,
            () => { Interlocked.Increment(ref providerCalls); return KernelResult.Ok(); });
        proceed.Set();
        KernelResult<V6StatefulSuspensionReceipt> completed = await first;

        Assert.Equal(KernelError.InvalidTransition, second.Error);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(1, providerCalls);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
    }

    [Fact]
    public async Task ConcurrentDiscardRetainsEscrowUntilProviderClosureAndHasOneWinner()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6StatefulSuspensionReceipt admitted = accounting.AdmitCapturedState(owner, Binding(), 72, () => 7, () => 11).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim proceed = new ManualResetEventSlim();
        int providerCalls = 0;
        Task<KernelResult<V6StatefulSuspensionReceipt>> first = Task.Run(() => accounting.DiscardWithProvider(owner, admitted.Handle, () =>
        {
            Interlocked.Increment(ref providerCalls);
            entered.Set();
            proceed.Wait(TimeSpan.FromSeconds(5));
            return KernelResult.Ok();
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        KernelResult<V6StatefulSuspensionReceipt> duplicate = accounting.DiscardWithProvider(owner, admitted.Handle, () =>
        {
            Interlocked.Increment(ref providerCalls);
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.InvalidTransition, duplicate.Error);
        Assert.Equal(72UL, Used(budgets.Query(account).Value!));
        proceed.Set();
        KernelResult<V6StatefulSuspensionReceipt> completed = await first;

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(1, providerCalls);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
    }

    [Fact]
    public async Task ConcurrentAdmissionOfOneCorrelationCreatesOneStorageEscrow()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Create(256);
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        using Barrier barrier = new Barrier(2);
        int generationReads = 0;
        ulong ProviderGeneration()
        {
            if (Interlocked.Increment(ref generationReads) <= 2)
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
            return 7;
        }
        Task<KernelResult<V6StatefulSuspensionReceipt>> first = Task.Run(() => accounting.AdmitCapturedState(
            owner, Binding(), 32, ProviderGeneration, () => 11));
        Task<KernelResult<V6StatefulSuspensionReceipt>> second = Task.Run(() => accounting.AdmitCapturedState(
            owner, Binding(), 32, ProviderGeneration, () => 11));
        KernelResult<V6StatefulSuspensionReceipt>[] results = await Task.WhenAll(first, second);

        V6StatefulSuspensionReceipt accepted = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Equal(KernelError.DuplicateIdentity,
            Assert.Single(results, result => !result.IsSuccess).Error);
        Assert.Equal(32UL, Used(budgets.Query(account).Value!));
        Assert.True(accounting.Discard(owner, accepted.Handle).IsSuccess);
        Assert.Equal(0UL, Used(budgets.Query(account).Value!));
    }

    private static ResumeBindingV1 Binding() => new(1, "operation:stateful:1", D('a'), D('b'), 3, 5, 7, 11);

    private static (ResourceBudgetAuthority Budgets, ProcessHandle Owner, BudgetAccountHandle Account) Create(ulong limit)
    {
        ResourceBudgetAuthority budgets = new ResourceBudgetAuthority();
        Assert.True(budgets.ConfigureSystem([Amount(limit)]).IsSuccess);
        BudgetAccountHandle service = budgets.CreateChild(budgets.SystemBudget, BudgetAccountLevel.Service,
            "stateful-service", [Amount(limit)]).Value!.Account;
        BudgetAccountHandle account = budgets.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "stateful-process", [Amount(limit)]).Value!.Account;
        ProcessHandle owner = new ProcessHandle(new(888), 4);
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
