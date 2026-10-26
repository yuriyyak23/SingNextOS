using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6ManagedStatefulResumeProviderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MalformedCorrelationCannotRetainCaptureOrStorage(int surrogate)
    {
        string correlation = new string(surrogate == 0 ? '\uD800' : '\uDC00', 1);
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        Assert.Equal(KernelError.InvalidMessage,
            contour.CaptureAndSuspend(owner, correlation, 3, D('b'), Payload).Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void ValidSupplementaryCorrelationAtCharacterLimitCanCaptureAndDiscard()
    {
        V6ManagedStatefulResumeProvider provider = Provider();
        string correlation = string.Concat(Enumerable.Repeat("\U0001F680", 128));
        KernelResult<V6ManagedCapturedStateReceipt> captured = provider.Capture(correlation, 3, D('b'), Payload);
        Assert.True(captured.IsSuccess, captured.Message);
        Assert.Equal(correlation, captured.Value!.Binding.OperationCorrelation);
        Assert.True(provider.Discard(captured.Value.Handle, captured.Value.Binding).IsSuccess);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void BindFailureCleanupUsesExactProviderClosureAndRetainsDiscardLoss(int mutation, bool discardLoss)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(accounting, provider);
        accounting.BeforeNextStorageBindForTest(reservation =>
        {
            if (mutation == 0) Assert.True(budgets.QuarantineLease(owner, reservation).IsSuccess);
            else
            {
                Assert.True(budgets.BindLease(owner, reservation).IsSuccess);
                Assert.True(budgets.BeginConsumption(owner, reservation).IsSuccess);
                if (mutation == 2) throw new InvalidOperationException("loss after consumption");
            }
        });
        if (discardLoss) provider.FailNextDiscardForTest();
        Assert.Equal(KernelError.PlatformFaulted,
            contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Error);
        V6ManagedCapturedStateReceipt captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(discardLoss ? V6ManagedCapturedStateStatus.Quarantined : V6ManagedCapturedStateStatus.Discarded,
            captured.Status);
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, Used(budgets, account));
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, provider.RetainedPayloadBytes);
        if (discardLoss)
        {
            V6StatefulSuspensionReceipt recovery = accounting.FindRecoverySuspension(owner, captured.Binding).Value!;
            Assert.Equal(BudgetReservationState.Quarantined, budgets.Query(recovery.StorageReservation).Value!.State);
            int queries = provider.CorrelationQueries;
            Assert.Equal(KernelError.StaleGeneration,
                contour.ReconcileUnpublishedCapture(new(owner.ProcessId, owner.Generation + 1), Correlation).Error);
            Assert.Equal(queries, provider.CorrelationQueries);
            Assert.Equal((ulong)Payload.Length, Used(budgets, account));
            Assert.True(contour.ReconcileUnpublishedCapture(owner, Correlation).IsSuccess);
            Assert.Equal(0UL, Used(budgets, account));
            Assert.Equal(0UL, provider.RetainedPayloadBytes);
        }
        Assert.True(contour.CaptureAndSuspend(owner, Correlation, 4, D('c'), new byte[] { 1 }).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageRecordAllocationFailurePrecedesChargeAndUsesProviderCleanup(bool discardLoss)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(accounting, provider);
        int afterBind = 0;
        accounting.AfterNextStorageBindForTest(() => afterBind++);
        accounting.FailNextStorageRecordAllocationForTest();
        if (discardLoss) provider.FailNextDiscardForTest();
        KernelResult<V6StatefulSuspensionReceipt> denied = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);
        Assert.Equal(discardLoss ? KernelError.PlatformFaulted : KernelError.CapacityExhausted, denied.Error);
        Assert.Equal(0, afterBind);
        Assert.Equal(0UL, Used(budgets, account));
        V6ManagedCapturedStateReceipt captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(discardLoss ? V6ManagedCapturedStateStatus.Quarantined : V6ManagedCapturedStateStatus.Discarded,
            captured.Status);
        if (discardLoss) Assert.True(provider.Discard(captured.Handle, captured.Binding).IsSuccess);
        Assert.True(contour.CaptureAndSuspend(owner, Correlation, 4, D('c'), new byte[] { 1 }).IsSuccess);
    }

    [Fact]
    public void CaptureRejectsNonCanonicalSemanticDigestBeforeRetainingState()
    {
        V6ManagedStatefulResumeProvider provider = Provider();

        KernelResult<V6ManagedCapturedStateReceipt> rejected = provider.Capture(Correlation, 3, D('b').ToUpperInvariant(), Payload);

        Assert.Equal(KernelError.InvalidMessage, rejected.Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(KernelError.StaleGeneration,
            provider.QueryByCorrelation(Correlation).Error);
        Assert.True(provider.Capture(Correlation, 3, D('b'), Payload).IsSuccess);
    }

    [Fact]
    public void CaptureProducesOpaqueGenerationBoundNonAuthorityBindingAndStorageEscrow()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);

        KernelResult<V6StatefulSuspensionReceipt> suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.True(suspended.IsSuccess, suspended.Message);
        Assert.Equal(V6StatefulSuspensionState.Suspended, suspended.Value!.State);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
        V6ManagedCapturedStateReceipt captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(V6ManagedCapturedStateStatus.Captured, captured.Status);
        Assert.Equal(provider.ProviderGeneration, captured.Binding.ProviderGeneration);
        Assert.Equal(provider.RuntimeGeneration, captured.Binding.RuntimeGeneration);
        Assert.False(captured.AuthorizesResume);
        Assert.False(captured.PreservesCapability);
        Assert.False(captured.Binding.AuthorizesExecution);
        Assert.False(captured.Binding.AuthorizesRegionAccess);
    }

    [Fact]
    public void UnattachedCaptureOwnerCannotReachProviderOrCreateEscrow()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        ProcessHandle wrongGeneration = new ProcessHandle(owner.ProcessId, owner.Generation + 1);

        KernelResult<V6StatefulSuspensionReceipt> denied = contour.CaptureAndSuspend(wrongGeneration, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.BudgetNotConfigured, denied.Error);
        Assert.Equal(KernelError.StaleGeneration, provider.QueryByCorrelation(Correlation).Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(0UL, Used(budgets, account));
        Assert.True(contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).IsSuccess);
    }

    [Fact]
    public void FreshThreeOwnerResumeRestoresManagedStateAndReleasesEscrow()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        int singNextCalls = 0;
        int runtimeCalls = 0;

        KernelResult<V6StatefulSuspensionReceipt> resumed = contour.Resume(owner, suspended.Handle,
            () => { singNextCalls++; return KernelResult.Ok(); },
            () => { runtimeCalls++; return KernelResult.Ok(); });

        Assert.True(resumed.IsSuccess, resumed.Message);
        Assert.Equal(V6StatefulSuspensionState.Resumed, resumed.Value!.State);
        Assert.Equal(V6ManagedCapturedStateStatus.Resumed,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(2, singNextCalls);
        Assert.Equal(2, runtimeCalls);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void FreshAdmissionDenialLeavesProviderStateAndEscrowSuspended()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;

        KernelResult<V6StatefulSuspensionReceipt> denied = contour.Resume(owner, suspended.Handle,
            () => KernelResult.Fail(KernelError.CapabilityRevoked, "revoked"), KernelResult.Ok);

        Assert.Equal(KernelError.CapabilityRevoked, denied.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Captured,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
        Assert.True(contour.Discard(owner, suspended.Handle).IsSuccess);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void ProviderResetInvalidatesCapturedGenerationWithoutResumeCallback()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;

        provider.ResetProvider();
        KernelResult<V6StatefulSuspensionReceipt> stale = contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
        Assert.True(contour.Discard(owner, suspended.Handle).IsSuccess);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalGenerationResetQuarantinesCaptureAndDeniesRestore(bool providerReset)
    {
        V6ManagedStatefulResumeProvider provider = new V6ManagedStatefulResumeProvider("managed-stateful-model-v1",
            providerReset ? ulong.MaxValue : 7,
            providerReset ? 11 : ulong.MaxValue);
        V6ManagedCapturedStateReceipt capture = provider.Capture(Correlation, 3, D('b'), Payload).Value!;

        if (providerReset) provider.ResetProvider();
        else provider.ResetRuntime();

        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.Query(capture.Handle).Value!.Status);
        Assert.Equal(KernelError.StaleGeneration,
            provider.AdmitRestore(capture.Handle, capture.Binding).Error);
        Assert.Equal(KernelError.StaleGeneration,
            provider.Restore(capture.Handle, capture.Binding).Error);
        Assert.Equal(KernelError.StaleGeneration,
            provider.Capture("managed-stateful:new", 4, D('c'), Payload).Error);
        Assert.Equal((ulong)Payload.Length, provider.RetainedPayloadBytes);
        Assert.True(provider.Discard(capture.Handle, capture.Binding).IsSuccess);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalResetCannotUseUnchangedGenerationToReleaseStorageEscrow(bool providerReset)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = new V6ManagedStatefulResumeProvider("managed-stateful-model-v1",
            providerReset ? ulong.MaxValue : 7,
            providerReset ? 11 : ulong.MaxValue);
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;

        if (providerReset) provider.ResetProvider();
        else provider.ResetRuntime();
        KernelResult<V6StatefulSuspensionReceipt> resumed = contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);

        Assert.Equal(KernelError.StaleGeneration, resumed.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
        Assert.True(contour.Discard(owner, suspended.Handle).IsSuccess);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void ExhaustedCaptureGenerationDeniesBeforeRetainingPayload()
    {
        V6ManagedStatefulResumeProvider provider = Provider();
        typeof(V6ManagedStatefulResumeProvider).GetField("_nextCaptureGeneration",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, ulong.MaxValue);

        KernelResult<V6ManagedCapturedStateReceipt> capture = provider.Capture(Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.CapacityExhausted, capture.Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(KernelError.StaleGeneration,
            provider.QueryByCorrelation(Correlation).Error);
    }

    [Fact]
    public void RestoreLossQuarantinesBothProviderStateAndStorageUntilReconciliation()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        provider.FailNextRestoreForTest();

        KernelResult<V6StatefulSuspensionReceipt> lost = contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);

        Assert.Equal(KernelError.PlatformUnavailable, lost.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(BudgetReservationState.Quarantined,
            budgets.Query(suspended.StorageReservation).Value!.State);
        Assert.True(contour.ReconcileQuarantinedDiscard(owner, suspended.Handle).IsSuccess);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void ProviderDiscardFailureQuarantinesStorageUntilExactReconciliation()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        provider.FailNextDiscardForTest();

        KernelResult<V6StatefulSuspensionReceipt> failed = contour.Discard(owner, suspended.Handle);

        Assert.Equal(KernelError.PlatformUnavailable, failed.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(BudgetReservationState.Quarantined,
            budgets.Query(suspended.StorageReservation).Value!.State);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));

        Assert.Equal(KernelError.InvalidTransition,
            contour.Discard(owner, suspended.Handle).Error);
        Assert.Equal(KernelError.StaleGeneration,
            contour.ReconcileQuarantinedDiscard(new ProcessHandle(new(890), 2),
                suspended.Handle).Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.True(contour.ReconcileQuarantinedDiscard(owner, suspended.Handle).IsSuccess);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void WrongOwnerCannotDiscardProviderPayloadBeforeEscrowAdmission()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        ProcessHandle wrongOwner = new ProcessHandle(new(890), 2);

        Assert.Equal(KernelError.StaleGeneration,
            contour.Discard(wrongOwner, suspended.Handle).Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Captured,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(BudgetReservationState.Bound,
            budgets.Query(suspended.StorageReservation).Value!.State);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
    }

    [Fact]
    public void SettlementInterruptionRetriesWithoutRepeatingProviderDiscard()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(accounting, provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        provider.FailNextRestoreForTest();
        Assert.Equal(KernelError.PlatformUnavailable,
            contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok).Error);
        accounting.FailNextSettlementForTest();

        Assert.Equal(KernelError.PlatformFaulted,
            contour.ReconcileQuarantinedDiscard(owner, suspended.Handle).Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(BudgetReservationState.Reconciled,
            budgets.Query(suspended.StorageReservation).Value!.State);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));

        provider.FailNextDiscardForTest();
        KernelResult<V6StatefulSuspensionReceipt> settled = contour.ReconcileQuarantinedDiscard(owner, suspended.Handle);

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResetAfterStorageBindUsesExactCleanupAndKeepsFailedDiscardPinned(bool runtimeReset, bool discardLoss)
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(accounting, provider);
        accounting.AfterNextStorageBindForTest(() =>
        {
            Assert.Equal((ulong)Payload.Length, Used(budgets, account));
            if (runtimeReset) provider.ResetRuntime(); else provider.ResetProvider();
        });
        if (discardLoss) provider.FailNextDiscardForTest();

        KernelResult<V6StatefulSuspensionReceipt> denied = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(discardLoss ? KernelError.PlatformFaulted : KernelError.StaleGeneration, denied.Error);
        V6ManagedCapturedStateReceipt captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(discardLoss ? V6ManagedCapturedStateStatus.Quarantined : V6ManagedCapturedStateStatus.Discarded,
            captured.Status);
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, provider.RetainedPayloadBytes);
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, Used(budgets, account));
        if (discardLoss)
        {
            V6StatefulSuspensionReceipt recovery = accounting.FindRecoverySuspension(owner, captured.Binding).Value!;
            Assert.Equal(BudgetReservationState.Quarantined, budgets.Query(recovery.StorageReservation).Value!.State);
            Assert.True(contour.ReconcileUnpublishedCapture(owner, Correlation).IsSuccess);
            Assert.Equal(0UL, provider.RetainedPayloadBytes);
            Assert.Equal(0UL, Used(budgets, account));
        }
        Assert.True(contour.CaptureAndSuspend(owner, Correlation, 4, D('c'), new byte[] { 1 }).IsSuccess);
        Assert.Equal(1UL, Used(budgets, account));
    }

    [Fact]
    public async Task ConcurrentResumeHasOneRestoreWinner()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        V6StatefulSuspensionReceipt suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<V6StatefulSuspensionReceipt>> Run() => Task.Run(() =>
        {
            start.Wait();
            return contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);
        });
        Task<KernelResult<V6StatefulSuspensionReceipt>> first = Run();
        Task<KernelResult<V6StatefulSuspensionReceipt>> second = Run();
        start.Set();

        KernelResult<V6StatefulSuspensionReceipt>[] results = await Task.WhenAll(first, second);

        Assert.Single(results, static result => result.IsSuccess);
        // The losing caller can fail at provider admission after the winner has
        // restored the payload, or at the accounting owner's final state check.
        Assert.Contains(Assert.Single(results, static result => !result.IsSuccess).Error,
            new[] { KernelError.StaleGeneration, KernelError.InvalidTransition });
        Assert.Equal(V6ManagedCapturedStateStatus.Resumed,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void StorageAdmissionFailureDiscardsProviderCaptureAndAllowsFreshRetry()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(4);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);

        KernelResult<V6StatefulSuspensionReceipt> denied = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.BudgetExceeded, denied.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(0UL, Used(budgets, account));
        KernelResult<V6ManagedCapturedStateReceipt> retry = provider.Capture(Correlation, 4, D('c'), new byte[] { 1 });
        Assert.True(retry.IsSuccess, retry.Message);
    }

    [Fact]
    public void StorageAdmissionFailureWithAmbiguousProviderDiscardKeepsCaptureQuarantined()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(4);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        provider.FailNextDiscardForTest();

        KernelResult<V6StatefulSuspensionReceipt> failed = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.PlatformFaulted, failed.Error);
        V6ManagedCapturedStateReceipt captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined, captured.Status);
        Assert.Equal((ulong)Payload.Length, provider.RetainedPayloadBytes);
        Assert.Equal(0UL, Used(budgets, account));
        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Capture(Correlation, 4, D('c'), new byte[] { 1 }).Error);

        Assert.True(provider.Discard(captured.Handle, captured.Binding).IsSuccess);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
    }

    [Fact]
    public void UnadmittedCaptureResetDoesNotImplyProviderPayloadClosure()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(4);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        provider.FailNextDiscardForTest();

        Assert.Equal(KernelError.PlatformFaulted,
            contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Error);
        V6ManagedCapturedStateReceipt captured = provider.QueryByCorrelation(Correlation).Value!;
        provider.ResetProvider();

        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.Query(captured.Handle).Value!.Status);
        Assert.Equal((ulong)Payload.Length, provider.RetainedPayloadBytes);
        Assert.Equal(0UL, Used(budgets, account));
        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Capture(Correlation, 4, D('c'), new byte[] { 1 }).Error);
        Assert.True(provider.Discard(captured.Handle, captured.Binding).IsSuccess);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
    }

    [Fact]
    public void BindingPublicationFailureWithAmbiguousCleanupKeepsProviderAndStoragePinned()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        contour.FailNextBindingPublicationForTest();
        provider.FailNextDiscardForTest();

        KernelResult<V6StatefulSuspensionReceipt> failed = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.PlatformFaulted, failed.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal((ulong)Payload.Length, provider.RetainedPayloadBytes);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Capture(Correlation, 4, D('c'), new byte[] { 1 }).Error);
        int queriesBeforeRecovery = provider.CorrelationQueries;
        ProcessHandle otherOwner = new ProcessHandle(new(890), 2);
        Assert.True(budgets.AttachProcess(otherOwner, account).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            contour.ReconcileUnpublishedCapture(otherOwner, Correlation).Error);
        Assert.Equal(queriesBeforeRecovery, provider.CorrelationQueries);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));

        KernelResult<V6StatefulSuspensionReceipt> reconciled = contour.ReconcileUnpublishedCapture(owner, Correlation);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(queriesBeforeRecovery + 1, provider.CorrelationQueries);
        Assert.Equal(V6StatefulSuspensionState.Discarded, reconciled.Value!.State);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(0UL, Used(budgets, account));
        Assert.Equal(KernelError.StaleGeneration,
            contour.ReconcileUnpublishedCapture(owner, Correlation).Error);
    }

    [Fact]
    public void UnpublishedSettlementRetryUsesOwnerBindingAfterNewerCapture()
    {
        (ResourceBudgetAuthority? budgets, ProcessHandle owner, BudgetAccountHandle account) = Budget(256);
        V6ManagedStatefulResumeProvider provider = Provider();
        V6StatefulResumeAccounting accounting = new V6StatefulResumeAccounting(budgets);
        V6ManagedStatefulResumeContour contour = new V6ManagedStatefulResumeContour(accounting, provider);
        contour.FailNextBindingPublicationForTest();
        provider.FailNextDiscardForTest();
        Assert.Equal(KernelError.PlatformFaulted,
            contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Error);
        V6ManagedCapturedStateReceipt oldCapture = provider.QueryByCorrelation(Correlation).Value!;
        accounting.FailNextSettlementForTest();

        Assert.Equal(KernelError.PlatformFaulted,
            contour.ReconcileUnpublishedCapture(owner, Correlation).Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.Query(oldCapture.Handle).Value!.Status);
        KernelResult<V6StatefulSuspensionReceipt> newer = contour.CaptureAndSuspend(owner, Correlation, 4, D('c'), new byte[] { 1 });
        Assert.True(newer.IsSuccess, newer.Message);

        KernelResult<V6StatefulSuspensionReceipt> settled = contour.ReconcileUnpublishedCapture(owner, Correlation);

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(V6StatefulSuspensionState.Discarded, settled.Value!.State);
        Assert.Equal(1UL, Used(budgets, account));
        Assert.Equal(V6ManagedCapturedStateStatus.Captured,
            provider.QueryByCorrelation(Correlation).Value!.Status);
    }

    [Fact]
    public void TerminalCaptureKeepsReceiptButDoesNotRetainLargePayload()
    {
        V6ManagedStatefulResumeProvider provider = Provider();
        byte[] largeState = new byte[64 * 1024];
        largeState.AsSpan().Fill(0xa5);
        V6ManagedCapturedStateReceipt first = provider.Capture(Correlation, 3, D('b'), largeState).Value!;
        Assert.Equal((ulong)largeState.Length, provider.RetainedPayloadBytes);

        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Capture(Correlation, 3, D('b'), largeState).Error);
        Assert.Equal((ulong)largeState.Length, provider.RetainedPayloadBytes);
        Assert.True(provider.Discard(first.Handle, first.Binding).IsSuccess);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.Query(first.Handle).Value!.Status);

        V6ManagedCapturedStateReceipt second = provider.Capture(Correlation, 4, D('c'), largeState).Value!;
        Assert.Equal((ulong)largeState.Length, provider.RetainedPayloadBytes);
        Assert.True(provider.Restore(second.Handle, second.Binding).IsSuccess);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(V6ManagedCapturedStateStatus.Resumed,
            provider.Query(second.Handle).Value!.Status);
    }

    private const string Correlation = "managed-stateful:operation:1";
    private static readonly byte[] Payload = [1, 3, 3, 7, 9, 11, 15, 21];

    private static V6ManagedStatefulResumeProvider Provider() =>
        new("managed-stateful-model-v1", 7, 11);

    private static (ResourceBudgetAuthority Budgets, ProcessHandle Owner, BudgetAccountHandle Account) Budget(ulong limit)
    {
        ResourceBudgetAuthority budgets = new ResourceBudgetAuthority();
        Assert.True(budgets.ConfigureSystem([Amount(limit)]).IsSuccess);
        BudgetAccountHandle service = budgets.CreateChild(budgets.SystemBudget, BudgetAccountLevel.Service,
            "managed-stateful", [Amount(limit)]).Value!.Account;
        BudgetAccountHandle account = budgets.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "managed-stateful-process", [Amount(limit)]).Value!.Account;
        ProcessHandle owner = new ProcessHandle(new(889), 2);
        Assert.True(budgets.AttachProcess(owner, account).IsSuccess);
        return (budgets, owner, account);
    }

    private static BudgetAmount Amount(ulong value) =>
        new(ServiceBudgetDimension.CheckpointStorageBytes, value);

    private static ulong Used(ResourceBudgetAuthority budgets, BudgetAccountHandle account) =>
        Assert.Single(budgets.Query(account).Value!.Usage,
            static usage => usage.Dimension == ServiceBudgetDimension.CheckpointStorageBytes).Used;

    private static string D(char value) => new(value, 64);
}
