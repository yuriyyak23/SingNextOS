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
        var correlation = new string(surrogate == 0 ? '\uD800' : '\uDC00', 1);
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        Assert.Equal(KernelError.InvalidMessage,
            contour.CaptureAndSuspend(owner, correlation, 3, D('b'), Payload).Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(0UL, Used(budgets, account));
    }

    [Fact]
    public void ValidSupplementaryCorrelationAtCharacterLimitCanCaptureAndDiscard()
    {
        var provider = Provider();
        var correlation = string.Concat(Enumerable.Repeat("\U0001F680", 128));
        var captured = provider.Capture(correlation, 3, D('b'), Payload);
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var accounting = new V6StatefulResumeAccounting(budgets);
        var contour = new V6ManagedStatefulResumeContour(accounting, provider);
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
        var captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(discardLoss ? V6ManagedCapturedStateStatus.Quarantined : V6ManagedCapturedStateStatus.Discarded,
            captured.Status);
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, Used(budgets, account));
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, provider.RetainedPayloadBytes);
        if (discardLoss)
        {
            var recovery = accounting.FindRecoverySuspension(owner, captured.Binding).Value!;
            Assert.Equal(BudgetReservationState.Quarantined, budgets.Query(recovery.StorageReservation).Value!.State);
            var queries = provider.CorrelationQueries;
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var accounting = new V6StatefulResumeAccounting(budgets);
        var contour = new V6ManagedStatefulResumeContour(accounting, provider);
        var afterBind = 0;
        accounting.AfterNextStorageBindForTest(() => afterBind++);
        accounting.FailNextStorageRecordAllocationForTest();
        if (discardLoss) provider.FailNextDiscardForTest();
        var denied = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);
        Assert.Equal(discardLoss ? KernelError.PlatformFaulted : KernelError.CapacityExhausted, denied.Error);
        Assert.Equal(0, afterBind);
        Assert.Equal(0UL, Used(budgets, account));
        var captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(discardLoss ? V6ManagedCapturedStateStatus.Quarantined : V6ManagedCapturedStateStatus.Discarded,
            captured.Status);
        if (discardLoss) Assert.True(provider.Discard(captured.Handle, captured.Binding).IsSuccess);
        Assert.True(contour.CaptureAndSuspend(owner, Correlation, 4, D('c'), new byte[] { 1 }).IsSuccess);
    }

    [Fact]
    public void CaptureRejectsNonCanonicalSemanticDigestBeforeRetainingState()
    {
        var provider = Provider();

        var rejected = provider.Capture(Correlation, 3, D('b').ToUpperInvariant(), Payload);

        Assert.Equal(KernelError.InvalidMessage, rejected.Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(KernelError.StaleGeneration,
            provider.QueryByCorrelation(Correlation).Error);
        Assert.True(provider.Capture(Correlation, 3, D('b'), Payload).IsSuccess);
    }

    [Fact]
    public void CaptureProducesOpaqueGenerationBoundNonAuthorityBindingAndStorageEscrow()
    {
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);

        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.True(suspended.IsSuccess, suspended.Message);
        Assert.Equal(V6StatefulSuspensionState.Suspended, suspended.Value!.State);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
        var captured = provider.QueryByCorrelation(Correlation).Value!;
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var wrongGeneration = new ProcessHandle(owner.ProcessId, owner.Generation + 1);

        var denied = contour.CaptureAndSuspend(wrongGeneration, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.BudgetNotConfigured, denied.Error);
        Assert.Equal(KernelError.StaleGeneration, provider.QueryByCorrelation(Correlation).Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(0UL, Used(budgets, account));
        Assert.True(contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).IsSuccess);
    }

    [Fact]
    public void FreshThreeOwnerResumeRestoresManagedStateAndReleasesEscrow()
    {
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        var singNextCalls = 0;
        var runtimeCalls = 0;

        var resumed = contour.Resume(owner, suspended.Handle,
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;

        var denied = contour.Resume(owner, suspended.Handle,
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;

        provider.ResetProvider();
        var stale = contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);

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
        var provider = new V6ManagedStatefulResumeProvider("managed-stateful-model-v1",
            providerReset ? ulong.MaxValue : 7,
            providerReset ? 11 : ulong.MaxValue);
        var capture = provider.Capture(Correlation, 3, D('b'), Payload).Value!;

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
        var (budgets, owner, account) = Budget(256);
        var provider = new V6ManagedStatefulResumeProvider("managed-stateful-model-v1",
            providerReset ? ulong.MaxValue : 7,
            providerReset ? 11 : ulong.MaxValue);
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;

        if (providerReset) provider.ResetProvider();
        else provider.ResetRuntime();
        var resumed = contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);

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
        var provider = Provider();
        typeof(V6ManagedStatefulResumeProvider).GetField("_nextCaptureGeneration",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, ulong.MaxValue);

        var capture = provider.Capture(Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.CapacityExhausted, capture.Error);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(KernelError.StaleGeneration,
            provider.QueryByCorrelation(Correlation).Error);
    }

    [Fact]
    public void RestoreLossQuarantinesBothProviderStateAndStorageUntilReconciliation()
    {
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        provider.FailNextRestoreForTest();

        var lost = contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);

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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        provider.FailNextDiscardForTest();

        var failed = contour.Discard(owner, suspended.Handle);

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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        var wrongOwner = new ProcessHandle(new(890), 2);

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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var accounting = new V6StatefulResumeAccounting(budgets);
        var contour = new V6ManagedStatefulResumeContour(accounting, provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
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
        var settled = contour.ReconcileQuarantinedDiscard(owner, suspended.Handle);

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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var accounting = new V6StatefulResumeAccounting(budgets);
        var contour = new V6ManagedStatefulResumeContour(accounting, provider);
        accounting.AfterNextStorageBindForTest(() =>
        {
            Assert.Equal((ulong)Payload.Length, Used(budgets, account));
            if (runtimeReset) provider.ResetRuntime(); else provider.ResetProvider();
        });
        if (discardLoss) provider.FailNextDiscardForTest();

        var denied = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(discardLoss ? KernelError.PlatformFaulted : KernelError.StaleGeneration, denied.Error);
        var captured = provider.QueryByCorrelation(Correlation).Value!;
        Assert.Equal(discardLoss ? V6ManagedCapturedStateStatus.Quarantined : V6ManagedCapturedStateStatus.Discarded,
            captured.Status);
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, provider.RetainedPayloadBytes);
        Assert.Equal(discardLoss ? (ulong)Payload.Length : 0UL, Used(budgets, account));
        if (discardLoss)
        {
            var recovery = accounting.FindRecoverySuspension(owner, captured.Binding).Value!;
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        var suspended = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Value!;
        using var start = new ManualResetEventSlim(false);
        Task<KernelResult<V6StatefulSuspensionReceipt>> Run() => Task.Run(() =>
        {
            start.Wait();
            return contour.Resume(owner, suspended.Handle, KernelResult.Ok, KernelResult.Ok);
        });
        var first = Run();
        var second = Run();
        start.Set();

        var results = await Task.WhenAll(first, second);

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
        var (budgets, owner, account) = Budget(4);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);

        var denied = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.BudgetExceeded, denied.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal(0UL, Used(budgets, account));
        var retry = provider.Capture(Correlation, 4, D('c'), new byte[] { 1 });
        Assert.True(retry.IsSuccess, retry.Message);
    }

    [Fact]
    public void StorageAdmissionFailureWithAmbiguousProviderDiscardKeepsCaptureQuarantined()
    {
        var (budgets, owner, account) = Budget(4);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        provider.FailNextDiscardForTest();

        var failed = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.PlatformFaulted, failed.Error);
        var captured = provider.QueryByCorrelation(Correlation).Value!;
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
        var (budgets, owner, account) = Budget(4);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        provider.FailNextDiscardForTest();

        Assert.Equal(KernelError.PlatformFaulted,
            contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Error);
        var captured = provider.QueryByCorrelation(Correlation).Value!;
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var contour = new V6ManagedStatefulResumeContour(new(budgets), provider);
        contour.FailNextBindingPublicationForTest();
        provider.FailNextDiscardForTest();

        var failed = contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload);

        Assert.Equal(KernelError.PlatformFaulted, failed.Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Quarantined,
            provider.QueryByCorrelation(Correlation).Value!.Status);
        Assert.Equal((ulong)Payload.Length, provider.RetainedPayloadBytes);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));
        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Capture(Correlation, 4, D('c'), new byte[] { 1 }).Error);
        var queriesBeforeRecovery = provider.CorrelationQueries;
        var otherOwner = new ProcessHandle(new(890), 2);
        Assert.True(budgets.AttachProcess(otherOwner, account).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            contour.ReconcileUnpublishedCapture(otherOwner, Correlation).Error);
        Assert.Equal(queriesBeforeRecovery, provider.CorrelationQueries);
        Assert.Equal((ulong)Payload.Length, Used(budgets, account));

        var reconciled = contour.ReconcileUnpublishedCapture(owner, Correlation);
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
        var (budgets, owner, account) = Budget(256);
        var provider = Provider();
        var accounting = new V6StatefulResumeAccounting(budgets);
        var contour = new V6ManagedStatefulResumeContour(accounting, provider);
        contour.FailNextBindingPublicationForTest();
        provider.FailNextDiscardForTest();
        Assert.Equal(KernelError.PlatformFaulted,
            contour.CaptureAndSuspend(owner, Correlation, 3, D('b'), Payload).Error);
        var oldCapture = provider.QueryByCorrelation(Correlation).Value!;
        accounting.FailNextSettlementForTest();

        Assert.Equal(KernelError.PlatformFaulted,
            contour.ReconcileUnpublishedCapture(owner, Correlation).Error);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.Query(oldCapture.Handle).Value!.Status);
        var newer = contour.CaptureAndSuspend(owner, Correlation, 4, D('c'), new byte[] { 1 });
        Assert.True(newer.IsSuccess, newer.Message);

        var settled = contour.ReconcileUnpublishedCapture(owner, Correlation);

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(V6StatefulSuspensionState.Discarded, settled.Value!.State);
        Assert.Equal(1UL, Used(budgets, account));
        Assert.Equal(V6ManagedCapturedStateStatus.Captured,
            provider.QueryByCorrelation(Correlation).Value!.Status);
    }

    [Fact]
    public void TerminalCaptureKeepsReceiptButDoesNotRetainLargePayload()
    {
        var provider = Provider();
        byte[] largeState = new byte[64 * 1024];
        largeState.AsSpan().Fill(0xa5);
        var first = provider.Capture(Correlation, 3, D('b'), largeState).Value!;
        Assert.Equal((ulong)largeState.Length, provider.RetainedPayloadBytes);

        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Capture(Correlation, 3, D('b'), largeState).Error);
        Assert.Equal((ulong)largeState.Length, provider.RetainedPayloadBytes);
        Assert.True(provider.Discard(first.Handle, first.Binding).IsSuccess);
        Assert.Equal(0UL, provider.RetainedPayloadBytes);
        Assert.Equal(V6ManagedCapturedStateStatus.Discarded,
            provider.Query(first.Handle).Value!.Status);

        var second = provider.Capture(Correlation, 4, D('c'), largeState).Value!;
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
        var budgets = new ResourceBudgetAuthority();
        Assert.True(budgets.ConfigureSystem([Amount(limit)]).IsSuccess);
        var service = budgets.CreateChild(budgets.SystemBudget, BudgetAccountLevel.Service,
            "managed-stateful", [Amount(limit)]).Value!.Account;
        var account = budgets.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "managed-stateful-process", [Amount(limit)]).Value!.Account;
        var owner = new ProcessHandle(new(889), 2);
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
