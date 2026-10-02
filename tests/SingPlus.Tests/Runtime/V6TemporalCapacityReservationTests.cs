using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6TemporalCapacityReservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignPreSubmitCancellationCannotRefundClaimedTemporalCapacity(bool bound)
    {
        var c = BoundCancellationContext(admitBinding: false);
        var binding = (bound
            ? c.Coordinator.AdmitBoundToExternalOperation(c.Owner, c.Budget, c.Operation, Temporal(40))
            : c.Coordinator.Admit(c.Owner, c.Budget, "foreign-cancel-guard", Temporal(40))).Value!;
        Assert.Equal(KernelError.InvalidTransition, c.Kernel.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget).Error);
        Assert.Equal(KernelError.InvalidTransition, c.Kernel.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget, Guid.NewGuid()).Error);
        Assert.Equal(BudgetReservationState.Bound, c.Kernel.QueryBudget(c.Budget).Value!.State);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.True(c.Coordinator.CancelAdmitted(binding).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.QueryBudget(c.Budget).Value!.State);
        Assert.Equal(KernelError.InvalidTransition, c.Kernel.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget).Error);
        Assert.True(c.Kernel.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget, binding.Id).IsSuccess);
    }

    [Fact]
    public void ProvisionalTemporalClaimCannotBeCancelledBeforeCommitOrExactAbandonment()
    {
        var c = Create(100, 100, 40);
        var composition = Guid.NewGuid();
        Assert.True(c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, composition).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget).Error);
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget, composition).Error);
        Assert.Equal(40UL, Used(c));
        c.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, composition);
        Assert.True(c.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget).IsSuccess);
        Assert.Equal(0UL, Used(c));
    }

    [Fact]
    public async Task ForeignCancellationOverlappingActualCoordinatorCannotRefundTwice()
    {
        var c = BoundCancellationContext();
        using var start = new ManualResetEventSlim(false);
        var foreign = Task.Run(() => { start.Wait(); return c.Kernel.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget); });
        var actual = Task.Run(() => { start.Wait(); return c.Coordinator.CancelAdmitted(c.Binding); });
        start.Set();
        Assert.Equal(KernelError.InvalidTransition, (await foreign).Error);
        Assert.True((await actual).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.QueryBudget(c.Budget).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericReleaseCannotDetachCommittedTemporalCapacity(bool bound)
    {
        var c = BoundCancellationContext(admitBinding: false);
        var binding = (bound
            ? c.Coordinator.AdmitBoundToExternalOperation(c.Owner, c.Budget, c.Operation, Temporal(40))
            : c.Coordinator.Admit(c.Owner, c.Budget, "generic-release-guard", Temporal(40))).Value!;
        Assert.Equal(KernelError.InvalidTransition, c.Kernel.ReleaseBudget(c.Owner, c.Budget).Error);
        Assert.Equal(KernelError.InvalidTransition, c.Kernel.Budgets.Release(c.Owner, c.Budget).Error);
        Assert.Equal(BudgetReservationState.Bound, c.Kernel.QueryBudget(c.Budget).Value!.State);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        var stale = c.Budget with { Generation = new(c.Budget.Generation.Value + 1) };
        Assert.Equal(BudgetReservationState.Stale, c.Kernel.ReleaseBudget(c.Owner, stale).Value!.State);
        Assert.Equal(KernelError.WrongRegionOwner, c.Kernel.ReleaseBudget(c.Owner with { Generation = c.Owner.Generation + 1 }, c.Budget).Error);
        Assert.True(c.Coordinator.CancelAdmitted(binding).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.QueryBudget(c.Budget).Value!.State);
        Assert.True(c.Kernel.ReleaseBudget(c.Owner, c.Budget).IsSuccess);
    }

    [Fact]
    public void UnpublishedTemporalClaimPinsReleaseUntilActualAbandonment()
    {
        var c = Create(100, 100, 40);
        var composition = Guid.NewGuid();
        Assert.True(c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, composition).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.ReleaseExplicit(c.Owner, c.Budget).Error);
        Assert.Equal(40UL, Used(c));
        c.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, Guid.NewGuid());
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.ReleaseExplicit(c.Owner, c.Budget).Error);
        c.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, composition);
        Assert.True(c.Budgets.ReleaseExplicit(c.Owner, c.Budget).IsSuccess);
        Assert.Equal(0UL, Used(c));
    }

    [Fact]
    public void FailedModelCapacityAdmissionAbandonsClaimBeforeExplicitRelease()
    {
        var c = Create(100, 1, 40);
        Assert.False(c.Coordinator.Admit(c.Owner, c.Budget, "capacity-refusal-release", Temporal(40)).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        Assert.True(c.Budgets.ReleaseExplicit(c.Owner, c.Budget).IsSuccess);
        Assert.Equal(0UL, Used(c));
    }

    [Fact]
    public async Task GenericReleaseOverlappingTemporalAdmissionCannotDetachAdmittedCapacity()
    {
        var c = BoundCancellationContext(admitBinding: false);
        using var start = new ManualResetEventSlim(false);
        var admission = Task.Run(() => { start.Wait(); return c.Coordinator.AdmitBoundToExternalOperation(c.Owner, c.Budget, c.Operation, Temporal(40)); });
        var release = Task.Run(() => { start.Wait(); return c.Kernel.ReleaseBudget(c.Owner, c.Budget); });
        start.Set();
        var admitted = await admission;
        var released = await release;
        if (admitted.IsSuccess)
        {
            Assert.Equal(KernelError.InvalidTransition, released.Error);
            Assert.Equal(BudgetReservationState.Bound, c.Kernel.QueryBudget(c.Budget).Value!.State);
            Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
            Assert.True(c.Coordinator.CancelAdmitted(admitted.Value!).IsSuccess);
        }
        else
        {
            Assert.True(released.IsSuccess, released.Message);
            Assert.Equal(BudgetReservationState.Released, c.Kernel.QueryBudget(c.Budget).Value!.State);
            Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        }
    }

    [Fact]
    public void TemporalSettlementCannotFabricateExternalResourceSettlementTrace()
    {
        var c = BoundCancellationContext();
        var result = c.Coordinator.SubmitBoundExternalOperation(c.Binding, new(1, 1),
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () =>
            {
                Assert.True(c.Kernel.RecordExternalOperationProviderLoss(c.Owner, c.Operation).IsSuccess);
                return KernelResult.Fail(KernelError.PlatformFaulted, "provider lost");
            });
        Assert.False(result.IsSuccess);
        var beforeRelease = c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!;
        var generationDigest = new string('a', 64); // Offline observation fixture; never admission evidence.
        var prefix = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(beforeRelease, generationDigest);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined], prefix.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        Assert.All(prefix, item => Assert.False(item.AuthorizesEffect));
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.True(c.Kernel.ReleaseExternalOperation(c.Owner, c.Operation,
            new(ProviderResourcesClosed: true, ProviderUnavailable: true)).IsSuccess);
        var released = c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!;
        Assert.DoesNotContain(released.Transitions, item => item.Event == "ResourceSettled");
        var localReleased = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released, generationDigest);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding],
            localReleased.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(localReleased).IsValid);
        Assert.DoesNotContain(localReleased, item => item.Kind is SemanticTraceEventKindV1.Settled or
            SemanticTraceEventKindV1.EffectClosedWithoutPublication or SemanticTraceEventKindV1.Released);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.True(c.Coordinator.ReconcileQuarantinedBoundExternalOperation(c.Binding).IsSuccess);
        Assert.Equal(BudgetReservationState.Released, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        var afterSettlement = c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!;
        Assert.DoesNotContain(afterSettlement.Transitions, item => item.Event == "ResourceSettled");
        Assert.Equal(localReleased,
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(afterSettlement, generationDigest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedExactProviderRecordAllowsKnownReceiptReconciliation(bool bound)
    {
        var c = BoundCancellationContext(admitBinding: false);
        var binding = (bound
            ? c.Coordinator.AdmitBoundToExternalOperation(c.Owner, c.Budget, c.Operation, Temporal(40))
            : c.Coordinator.Admit(c.Owner, c.Budget, "released-provider-retry", Temporal(40))).Value!;
        KernelResult Callback()
        {
            Assert.True(c.Provider.Release(binding.ProviderReservation.Handle).IsSuccess);
            if (bound) Assert.True(c.Kernel.RecordExternalOperationProviderLoss(c.Owner, c.Operation).IsSuccess);
            return KernelResult.Fail(KernelError.PlatformFaulted, "model capacity closed; response lost");
        }
        var submitted = bound
            ? c.Coordinator.SubmitBoundExternalOperation(binding, new(1, 1),
                KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, Callback)
            : c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, Callback);
        Assert.False(submitted.IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Released,
            c.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        var denied = bound
            ? c.Coordinator.ReconcileQuarantinedBoundExternalOperation(binding)
            : c.Coordinator.ReconcileQuarantined(binding,
                () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "closure denied"));
        Assert.False(denied.IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        if (bound)
            Assert.True(c.Kernel.ReleaseExternalOperation(c.Owner, c.Operation,
                new(ProviderResourcesClosed: true, ProviderUnavailable: true)).IsSuccess);
        var reconciled = bound
            ? c.Coordinator.ReconcileQuarantinedBoundExternalOperation(binding)
            : c.Coordinator.ReconcileQuarantined(binding, KernelResult.Ok);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(BudgetReservationState.Released, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        var fresh = c.Provider.Reserve("after-reconciliation", Temporal(40).ComputeEnvelope).Value!;
        Assert.True(c.Provider.ReconcileAndRelease(binding.ProviderReservation.Handle).IsSuccess);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(KernelError.StaleGeneration,
            c.Provider.ReconcileAndRelease(binding.ProviderReservation.Handle with { Generation = 2 }).Error);
        Assert.Equal(V6TemporalProviderReservationState.Reserved, c.Provider.Query(fresh.Handle).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundOwnerDenialReleaseResetRetainsBudgetUntilExactOwnerClosure(bool terminal)
    {
        var c = BoundCancellationContext(terminalProvider: terminal);
        c.Provider.InjectResetBeforeNextRelease();
        var callbacks = 0;
        var denied = c.Coordinator.SubmitBoundExternalOperation(c.Binding, new(2, 2),
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.ExternalEffectUncontained, denied.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            c.Provider.Query(c.Binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        var owner = c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!;
        Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition);
        Assert.DoesNotContain(owner.Transitions, transition => transition.Event == "Submitted");
        Assert.Equal(KernelError.ExternalEffectUncontained,
            c.Coordinator.ReconcileQuarantinedBoundExternalOperation(c.Binding).Error);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);

        Assert.True(c.Kernel.ReleaseExternalOperation(c.Owner, c.Operation,
            new(ProviderResourcesClosed: false, ProviderUnavailable: false)).IsSuccess);
        var reconciled = c.Coordinator.ReconcileQuarantinedBoundExternalOperation(c.Binding);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(BudgetReservationState.Released, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public async Task ConcurrentProviderUseAndCancellationHaveOneClosureOrder()
    {
        for (var repeat = 0; repeat < 12; repeat++)
        {
            var c = Create(100, 100, 40);
            var binding = c.Coordinator.Admit(c.Owner, c.Budget, "cancel-use-race", Temporal(40)).Value!;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var use = Task.Run(async () => { await start.Task; return c.Provider.BeginUse(binding.ProviderReservation.Handle); });
            var cancel = Task.Run(async () => { await start.Task; return c.Coordinator.CancelAdmitted(binding); });
            start.SetResult();
            await Task.WhenAll(use, cancel);
            if (use.Result.IsSuccess)
            {
                Assert.False(cancel.Result.IsSuccess);
                Assert.Equal(BudgetReservationState.Quarantined, c.Budgets.Query(c.Budget).Value!.State);
                Assert.Equal(V6TemporalProviderReservationState.Quarantined,
                    c.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
                Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
            }
            else
            {
                Assert.True(cancel.Result.IsSuccess, cancel.Result.Message);
                Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Budgets.Query(c.Budget).Value!.State);
                Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedProviderUseBeforeSubmitCannotTurnIntoBudgetCancellation(bool quarantined)
    {
        var c = Create(100, 100, 40);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "submit-closure-fault", Temporal(40)).Value!;
        var callbacks = 0;
        var result = c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, () =>
        {
            Assert.True((quarantined ? c.Provider.Quarantine(binding.ProviderReservation.Handle)
                : c.Provider.BeginUse(binding.ProviderReservation.Handle)).IsSuccess);
            return KernelResult.Ok();
        }, () => { callbacks++; return KernelResult.Ok(); });
        Assert.False(result.IsSuccess);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined, c.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            c.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CancellationWithoutExactUnusedProviderClosureCannotRefundBudget(int fault)
    {
        var c = Create(100, 100, 40);
        if (fault == 2)
            typeof(V6ManagedTemporalCapacityProvider).GetField("_providerGeneration",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
                .SetValue(c.Provider, ulong.MaxValue);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "cancel-closure-fault", Temporal(40)).Value!;
        if (fault == 0) Assert.True(c.Provider.BeginUse(binding.ProviderReservation.Handle).IsSuccess);
        if (fault == 1) Assert.True(c.Provider.Quarantine(binding.ProviderReservation.Handle).IsSuccess);
        if (fault == 2) c.Provider.InjectResetBeforeNextRelease();

        Assert.False(c.Coordinator.CancelAdmitted(binding).IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, c.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            c.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(KernelError.BudgetExceeded, c.Budgets.Reserve(c.Owner, [Amount(100)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Error);
        Assert.False(c.Coordinator.CancelAdmitted(binding).IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, c.Budgets.Query(c.Budget).Value!.State);
    }

    private static void AssertBudgetSnapshotEqual(BudgetReservationSnapshot expected, BudgetReservationSnapshot actual)
    {
        Assert.Equal(expected with { Amounts = actual.Amounts, ChargedAmounts = actual.ChargedAmounts }, actual);
        Assert.Equal<BudgetAmount>(expected.Amounts, actual.Amounts);
        Assert.Equal<BudgetAmount>(expected.ChargedAmounts!, actual.ChargedAmounts!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservationSnapshotCollectionsCannotMutateOwnerFacts(bool charged)
    {
        var c = Create(100, 100, 40);
        if (charged)
        {
            Assert.True(c.Budgets.BeginConsumption(c.Owner, c.Budget).IsSuccess);
            Assert.True(c.Budgets.SettleLease(c.Owner, c.Budget, [Amount(5)]).IsSuccess);
        }
        var snapshot = c.Budgets.Query(c.Budget).Value!;
        var values = charged ? snapshot.ChargedAmounts! : snapshot.Amounts;
        var mutable = Assert.IsAssignableFrom<IList<BudgetAmount>>(values);
        Assert.True(mutable.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutable[0] = Amount(0));
        var fresh = c.Budgets.Query(c.Budget).Value!;
        Assert.NotSame(values, charged ? fresh.ChargedAmounts : fresh.Amounts);
        Assert.Equal(charged ? 5UL : 40UL, (charged ? fresh.ChargedAmounts! : fresh.Amounts).Single().Amount);
        if (charged) Assert.True(c.Budgets.IsExactSettledExternalLease(c.Owner, c.Budget, 5));
        else
        {
            Assert.True(c.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget).IsSuccess);
            _ = BoundBudget(c.Budgets, c.Owner, 100);
        }
    }

    [Fact]
    public async Task ConcurrentSnapshotMutationCannotChangeReservationOrRefund()
    {
        var c = Create(100, 100, 40);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var snapshot = c.Budgets.Query(c.Budget).Value!;
            var list = Assert.IsAssignableFrom<IList<BudgetAmount>>(snapshot.Amounts);
            Assert.Throws<NotSupportedException>(() => list[0] = Amount(0));
        })));
        Assert.Equal(40UL, c.Budgets.Query(c.Budget).Value!.Amounts.Single().Amount);
        Assert.True(c.Budgets.CancelLeasePreSubmit(c.Owner, c.Budget).IsSuccess);
        _ = BoundBudget(c.Budgets, c.Owner, 100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ForeignSettlementConsumerCannotMutateClaimedBudget(int method)
    {
        var c = Create(100, 100, 40);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "foreign-settlement", Temporal(40)).Value!;
        _ = c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => method == 0 ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformUnavailable, "lost"));
        var before = c.Budgets.Query(c.Budget).Value!;
        var foreign = Guid.NewGuid();
        var result = method switch
        {
            0 => c.Budgets.SettleLease(c.Owner, c.Budget, [], foreign),
            1 => c.Budgets.ReconcileLease(c.Owner, c.Budget, foreign),
            _ => c.Budgets.SettleReportedComputeOverrun(c.Owner, c.Budget, 45, foreign),
        };
        Assert.Equal(KernelError.InvalidTransition, result.Error);
        AssertBudgetSnapshotEqual(before, c.Budgets.Query(c.Budget).Value!);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public async Task GenericSettlementCannotOvertakeTemporalSettlementOrReuseTerminalResponse()
    {
        var c = Create(100, 100, 40);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "settlement-race", Temporal(40)).Value!;
        var submitted = c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generic = Task.Run(async () => { await start.Task; return c.Budgets.SettleLease(c.Owner, c.Budget, []); });
        var exact = Task.Run(async () => { await start.Task; return c.Coordinator.Settle(submitted, 5); });
        start.SetResult();
        Assert.Equal(KernelError.InvalidTransition, (await generic).Error);
        Assert.True((await exact).IsSuccess);
        var terminal = c.Budgets.Query(c.Budget).Value!;
        Assert.Equal(5UL, terminal.ChargedAmounts!.Single().Amount);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.SettleLease(c.Owner, c.Budget, []).Error);
        AssertBudgetSnapshotEqual(terminal, c.Budgets.Query(c.Budget).Value!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericSettlementOrReconciliationCannotCloseTemporalBudget(bool quarantined)
    {
        var c = Create(100, 100, 40);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "settlement-owner", Temporal(40)).Value!;
        var submitted = c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => quarantined ? KernelResult.Fail(KernelError.PlatformUnavailable, "lost receipt") : KernelResult.Ok());
        Assert.Equal(!quarantined, submitted.IsSuccess);
        var before = c.Budgets.Query(c.Budget).Value!;
        var generic = quarantined ? c.Budgets.ReconcileLease(c.Owner, c.Budget) : c.Budgets.SettleLease(c.Owner, c.Budget, []);
        Assert.Equal(KernelError.InvalidTransition, generic.Error);
        AssertBudgetSnapshotEqual(before, c.Budgets.Query(c.Budget).Value!);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void GenericCorrectiveSettlementCannotCloseClaimedOverrunBudget()
    {
        var c = Create(100, 100, 40);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "corrective-owner", Temporal(40)).Value!;
        var submitted = c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.Equal(KernelError.BudgetExceeded, c.Coordinator.Settle(submitted, 45).Error);
        var before = c.Budgets.Query(c.Budget).Value!;
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.SettleReportedComputeOverrun(c.Owner, c.Budget, 45).Error);
        AssertBudgetSnapshotEqual(before, c.Budgets.Query(c.Budget).Value!);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.True(c.Coordinator.ReconcileReportedOverrun(binding, KernelResult.Ok).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(45UL, c.Budgets.Query(c.Budget).Value!.ChargedAmounts!.Single().Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvisionalOrForeignCompositionCannotBeginConsumption(bool provisional)
    {
        var c = Create(100, 100, 40);
        var id = Guid.NewGuid();
        Assert.True(c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, id).IsSuccess);
        if (!provisional) Assert.True(c.Budgets.CommitTemporalComposition(c.Owner, c.Budget, id).IsSuccess);
        var before = c.Budgets.Query(c.Budget).Value!;
        Assert.Equal(KernelError.InvalidTransition,
            c.Budgets.BeginConsumption(c.Owner, c.Budget, provisional ? id : Guid.NewGuid()).Error);
        AssertBudgetSnapshotEqual(before, c.Budgets.Query(c.Budget).Value!);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void UnclaimedLegacyConsumptionRemainsCompatible()
    {
        var c = Create(100, 100, 40);
        Assert.True(c.Budgets.BeginConsumption(c.Owner, c.Budget).IsSuccess);
        Assert.Equal(BudgetReservationState.Consuming, c.Budgets.Query(c.Budget).Value!.State);
        Assert.True(c.Budgets.SettleLease(c.Owner, c.Budget, [Amount(5)]).IsSuccess);
        Assert.Equal(BudgetReservationState.Released, c.Budgets.Query(c.Budget).Value!.State);
    }

    [Fact]
    public void AbandonedProvisionalClaimCannotBeUsedAsConsumptionIdentity()
    {
        var c = Create(100, 100, 40);
        var id = Guid.NewGuid();
        Assert.True(c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, id).IsSuccess);
        c.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, id);
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.BeginConsumption(c.Owner, c.Budget, id).Error);
        Assert.Equal(BudgetReservationState.Bound, c.Budgets.Query(c.Budget).Value!.State);
        Assert.True(c.Budgets.BeginConsumption(c.Owner, c.Budget).IsSuccess);
    }

    [Fact]
    public async Task GenericConsumerCannotOvertakeTemporalSubmit()
    {
        var c = Create(100, 100, 40);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "consumer-race", Temporal(40)).Value!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var generic = Task.Run(async () => { await start.Task; return c.Budgets.BeginConsumption(c.Owner, c.Budget); });
        var submit = Task.Run(async () =>
        {
            await start.Task;
            return c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
                () => { calls++; return KernelResult.Ok(); });
        });
        start.SetResult();
        Assert.Equal(KernelError.InvalidTransition, (await generic).Error);
        var submitted = await submit;
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, calls);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.True(c.Coordinator.Settle(submitted.Value!, 5).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void GenericConsumptionCannotTakeClaimedTemporalLease()
    {
        var c = Create(100, 100, 40);
        var binding = c.Coordinator.Admit(c.Owner, c.Budget, "claimed-consumer", Temporal(40)).Value!;
        var before = c.Budgets.Query(c.Budget).Value!;
        Assert.Equal(KernelError.InvalidTransition, c.Budgets.BeginConsumption(c.Owner, c.Budget).Error);
        AssertBudgetSnapshotEqual(before, c.Budgets.Query(c.Budget).Value!);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        var callbacks = 0;
        var submitted = c.Coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, callbacks);
        Assert.True(c.Coordinator.Settle(submitted.Value!, 5).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void StaleOrForeignCompositionCommitCannotMutateExactClaim(int mismatch)
    {
        var c = Create(100, 100, 40);
        var id = Guid.NewGuid();
        Assert.True(c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, id).IsSuccess);
        var owner = mismatch == 0 ? c.Owner with { Generation = c.Owner.Generation + 1 } : c.Owner;
        var budget = mismatch == 1 ? c.Budget with { Generation = new(c.Budget.Generation.Value + 1) } : c.Budget;
        Assert.False(c.Budgets.CommitTemporalComposition(owner, budget, mismatch == 2 ? Guid.NewGuid() : id).IsSuccess);
        Assert.Equal(BudgetReservationState.Bound, c.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(KernelError.DuplicateIdentity,
            c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, Guid.NewGuid()).Error);
        Assert.True(c.Budgets.CommitTemporalComposition(c.Owner, c.Budget, id).IsSuccess);
        c.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, id);
        Assert.Equal(KernelError.DuplicateIdentity,
            c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, Guid.NewGuid()).Error);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public async Task CompositionCommitAndAbandonHaveOneOwnerBoundary()
    {
        var c = Create(100, 100, 40);
        var id = Guid.NewGuid();
        Assert.True(c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, id).IsSuccess);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = Task.Run(async () =>
        {
            await start.Task;
            return c.Budgets.CommitTemporalComposition(c.Owner, c.Budget, id);
        });
        var abandon = Task.Run(async () =>
        {
            await start.Task;
            c.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, id);
        });
        start.SetResult();
        var committed = await commit;
        await abandon;
        var next = c.Budgets.ClaimTemporalComposition(c.Owner, c.Budget, Guid.NewGuid());
        if (committed.IsSuccess) Assert.Equal(KernelError.DuplicateIdentity, next.Error);
        else Assert.True(next.IsSuccess, next.Message);
        Assert.Equal(BudgetReservationState.Bound, c.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishedCompositionCannotBeAbandonedEvenWithExactId(bool bound)
    {
        var c = BoundCancellationContext(admitBinding: bound);
        var first = bound ? c.Binding : c.Coordinator.Admit(c.Owner, c.Budget, "published-claim", Temporal(40)).Value!;
        c.Kernel.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, first.Id);
        var other = new V6TemporalCapacityCoordinator(c.Kernel, c.Provider);
        Assert.Equal(KernelError.DuplicateIdentity,
            other.Admit(c.Owner, c.Budget, "after-exact-rollback", Temporal(40)).Error);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Bound, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.True(c.Coordinator.CancelAdmitted(first).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnusedCompensationCannotReleaseInUseOrQuarantinedCapacity(bool quarantined)
    {
        var provider = new V6ManagedTemporalCapacityProvider(100);
        var reservation = provider.Reserve("unused-only-compensation", Temporal(40).ComputeEnvelope).Value!;
        Assert.True(provider.BeginUse(reservation.Handle).IsSuccess);
        if (quarantined) Assert.True(provider.Quarantine(reservation.Handle).IsSuccess);
        var before = provider.Query(reservation.Handle).Value!;
        Assert.Equal(KernelError.InvalidTransition, provider.Release(reservation.Handle, unusedOnly: true).Error);
        Assert.Equal(before, provider.Query(reservation.Handle).Value!);
        Assert.Equal(40UL, provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BoundAdmissionRevalidatesOwnerAfterInitialRead(int transition)
    {
        var c = BoundCancellationContext(terminalProvider: transition == 3, admitBinding: false);
        if (transition >= 3) c.Provider.InjectResetBeforeNextRelease();
        var gate = typeof(V6TemporalCapacityCoordinator).GetField("_sync",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.GetValue(c.Coordinator)!;
        KernelResult<V6TemporalCapacityBinding> result = default;
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try { result = c.Coordinator.AdmitBoundToExternalOperation(c.Owner, c.Budget, c.Operation, Temporal(40)); }
            catch (Exception exception) { workerFailure = exception; }
        });
        Monitor.Enter(gate);
        try
        {
            worker.Start();
            // The fresh worker has no other contended lock: waiting here means
            // it completed the initial owner query and reached coordinator admission.
            Assert.True(SpinWait.SpinUntil(() =>
                (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            if (transition == 1)
                Assert.True(c.Kernel.RecordExternalOperationSubmission(c.Owner, c.Operation, new(1, 1)).IsSuccess);
            else
            {
                Assert.True(c.Kernel.CancelExternalOperation(c.Owner, c.Operation, false).IsSuccess);
                if (transition == 2)
                    Assert.True(c.Kernel.ReleaseExternalOperation(c.Owner, c.Operation,
                        new(ProviderResourcesClosed: false, ProviderUnavailable: false)).IsSuccess);
            }
        }
        finally
        {
            Monitor.Exit(gate);
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        }
        Assert.Null(workerFailure);
        Assert.Equal(transition == 3 ? KernelError.ExternalEffectUncontained : KernelError.InvalidTransition, result.Error);
        Assert.Null(result.Value);
        Assert.Equal(transition == 3 ? 40UL : 0UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(transition == 3 ? BudgetReservationState.Quarantined : BudgetReservationState.Bound,
            c.Kernel.Budgets.Query(c.Budget).Value!.State);
        if (transition is 0 or 2 or 4)
        {
            var retry = new V6TemporalCapacityCoordinator(c.Kernel, c.Provider)
                .Admit(c.Owner, c.Budget, "fresh-after-unpublished-reserve", Temporal(40));
            Assert.True(retry.IsSuccess, retry.Message);
            if (transition == 4) Assert.Equal(2UL, c.Provider.ProviderGeneration);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleTemporalCompositionAdmissionCannotConsumeExactBudgetClaim(bool staleOwner)
    {
        var c = Create(100, 100, 40);
        var other = new V6TemporalCapacityCoordinator(c.Budgets, c.Provider);
        var owner = staleOwner ? c.Owner with { Generation = c.Owner.Generation + 1 } : c.Owner;
        var budget = staleOwner ? c.Budget : c.Budget with
        { Generation = new(c.Budget.Generation.Value + 1) };
        Assert.False(other.Admit(owner, budget, "stale-composition", Temporal(40)).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        var exact = c.Coordinator.Admit(c.Owner, c.Budget, "exact-composition", Temporal(40));
        Assert.True(exact.IsSuccess, exact.Message);
        Assert.True(c.Coordinator.CancelAdmitted(exact.Value!).IsSuccess);
    }

    [Fact]
    public void ForeignCompositionRollbackCannotRemoveWinnerClaim()
    {
        var c = Create(100, 100, 40);
        var first = c.Coordinator.Admit(c.Owner, c.Budget, "winner-claim", Temporal(40)).Value!;
        c.Budgets.AbandonTemporalComposition(c.Owner, c.Budget, Guid.NewGuid());
        var other = new V6TemporalCapacityCoordinator(c.Budgets, c.Provider);
        Assert.Equal(KernelError.DuplicateIdentity,
            other.Admit(c.Owner, c.Budget, "foreign-rollback", Temporal(40)).Error);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.True(c.Coordinator.CancelAdmitted(first).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentCoordinatorsCannotComposeOneBudgetTwice(bool differentProvider)
    {
        var c = Create(100, 100, 40);
        var first = c.Coordinator.Admit(c.Owner, c.Budget, "first-coordinator", Temporal(40)).Value!;
        var otherProvider = differentProvider ? new V6ManagedTemporalCapacityProvider(100) : c.Provider;
        var other = new V6TemporalCapacityCoordinator(c.Budgets, otherProvider);
        Assert.Equal(KernelError.DuplicateIdentity,
            other.Admit(c.Owner, c.Budget, "second-coordinator", Temporal(40)).Error);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        if (differentProvider) Assert.Equal(0UL, otherProvider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Bound, c.Budgets.Query(c.Budget).Value!.State);
        Assert.True(c.Coordinator.CancelAdmitted(first).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void FailedProviderReserveDoesNotRetainBudgetCompositionClaim()
    {
        var c = Create(100, 100, 40);
        var small = new V6ManagedTemporalCapacityProvider(20);
        var rejected = new V6TemporalCapacityCoordinator(c.Budgets, small);
        Assert.Equal(KernelError.BudgetExceeded,
            rejected.Admit(c.Owner, c.Budget, "too-large", Temporal(40)).Error);
        Assert.Equal(0UL, small.ReservedNanoseconds);
        var accepted = c.Coordinator.Admit(c.Owner, c.Budget, "retry-other-provider", Temporal(40));
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.True(c.Coordinator.CancelAdmitted(accepted.Value!).IsSuccess);
    }

    [Fact]
    public async Task DifferentCoordinatorsHaveOneBudgetCompositionWinner()
    {
        var c = Create(400, 400, 40);
        var coordinators = Enumerable.Range(0, 8)
            .Select(_ => new V6TemporalCapacityCoordinator(c.Budgets, c.Provider)).ToArray();
        var results = await Task.WhenAll(coordinators.Select((coordinator, index) => Task.Run(() =>
            coordinator.Admit(c.Owner, c.Budget, $"cross-coordinator:{index}", Temporal(40)))));
        var winner = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.All(results.Where(result => !result.IsSuccess),
            result => Assert.Equal(KernelError.DuplicateIdentity, result.Error));
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.True(coordinators[Array.FindIndex(results, result => result.IsSuccess)].CancelAdmitted(winner).IsSuccess);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void TerminalResetInsideBoundSubmitRetainsBothReservationsDespiteUnchangedGeneration()
    {
        var c = BoundCancellationContext(terminalProvider: true);
        var callbacks = 0;
        var result = c.Coordinator.SubmitBoundExternalOperation(c.Binding, new(1, 1),
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () =>
            {
                callbacks++;
                Assert.Equal(KernelError.CapacityExhausted, c.Provider.Reset().Error);
                return KernelResult.Ok();
            });
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(1, callbacks);
        Assert.Equal(ulong.MaxValue, c.Provider.ProviderGeneration);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            c.Provider.Query(c.Binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted,
            c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!.State);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            c.Coordinator.ReconcileQuarantinedBoundExternalOperation(c.Binding).Error);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedProviderResetCannotLeaveOldGenerationUsable(bool inUse)
    {
        var provider = new V6ManagedTemporalCapacityProvider(100);
        typeof(V6ManagedTemporalCapacityProvider).GetField("_providerGeneration",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, ulong.MaxValue);
        var reservation = provider.Reserve("terminal-generation", Temporal(40).ComputeEnvelope).Value!;
        if (inUse) Assert.True(provider.BeginUse(reservation.Handle).IsSuccess);

        Assert.Equal(KernelError.CapacityExhausted, provider.Reset().Error);
        Assert.Equal(ulong.MaxValue, provider.ProviderGeneration);
        Assert.Equal(40UL, provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            provider.Query(reservation.Handle).Value!.State);
        Assert.False(provider.BeginUse(reservation.Handle).IsSuccess);
        Assert.False(provider.Release(reservation.Handle).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted,
            provider.Reserve("fresh-after-terminal-reset", Temporal(40).ComputeEnvelope).Error);
        Assert.Equal(KernelError.CapacityExhausted, provider.Reset().Error);
        Assert.Equal(40UL, provider.ReservedNanoseconds);
        Assert.True(provider.ReconcileAndRelease(reservation.Handle).IsSuccess);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.Equal(KernelError.CapacityExhausted,
            provider.Reserve("after-explicit-model-reconciliation", Temporal(40).ComputeEnvelope).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundCancellationResetCannotTurnSubmittedEffectIntoNoEffectRelease(bool submitted)
    {
        var c = BoundCancellationContext();
        var generation = c.Provider.ProviderGeneration;
        if (submitted)
            Assert.True(c.Kernel.RecordExternalOperationSubmission(c.Owner, c.Operation, new(1, 1)).IsSuccess);
        c.Provider.InjectResetBeforeNextRelease();
        var cancellation = c.Coordinator.CancelAdmitted(c.Binding);
        if (!submitted)
        {
            Assert.True(cancellation.IsSuccess, cancellation.Message);
            Assert.Equal(generation + 1, c.Provider.ProviderGeneration);
            Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
            Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.Budgets.Query(c.Budget).Value!.State);
            Assert.Equal(ExternalOperationDisposition.Cancelled,
                c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!.Disposition);
            Assert.Equal(KernelError.InvalidTransition,
                c.Kernel.RecordExternalOperationSubmission(c.Owner, c.Operation, new(1, 1)).Error);
        }
        else
        {
            Assert.Equal(KernelError.ExternalEffectUncontained, cancellation.Error);
            Assert.Equal(generation, c.Provider.ProviderGeneration);
            Assert.Equal(KernelError.InvalidTransition, c.Provider.Release(c.Binding.ProviderReservation.Handle).Error);
            Assert.Equal(generation, c.Provider.ProviderGeneration);
            Assert.True(c.Provider.Reset().IsSuccess);
            Assert.Equal(generation + 1, c.Provider.ProviderGeneration);
            Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
            Assert.Equal(V6TemporalProviderReservationState.Quarantined,
                c.Provider.Query(c.Binding.ProviderReservation.Handle).Value!.State);
            Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
            Assert.Equal(KernelError.ExternalEffectUncontained,
                c.Coordinator.ReconcileQuarantinedBoundExternalOperation(c.Binding).Error);
            Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundCancelAndSubmitOverlapPreservesTheWinningOwnerBoundary(bool callbackInFlight)
    {
        var c = BoundCancellationContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim(false);
        var callbacks = 0;
        KernelResult Pause()
        {
            entered.TrySetResult();
            if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Temporal overlap rendezvous.");
            return KernelResult.Ok();
        }
        var submit = Task.Run(() => c.Coordinator.SubmitBoundExternalOperation(c.Binding, new(1, 1),
            callbackInFlight ? KernelResult.Ok : Pause, KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return callbackInFlight ? Pause() : KernelResult.Ok(); }));
        KernelResult<V6TemporalCapacityBinding> result;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancellation = c.Coordinator.CancelAdmitted(c.Binding);
            if (callbackInFlight)
            {
                Assert.Equal(KernelError.InvalidTransition, cancellation.Error);
                Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
                Assert.Equal(BudgetReservationState.Consuming, c.Kernel.Budgets.Query(c.Budget).Value!.State);
            }
            else
            {
                Assert.True(cancellation.IsSuccess, cancellation.Message);
                Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
                Assert.Equal(ExternalOperationDisposition.Cancelled,
                    c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!.Disposition);
            }
        }
        finally { proceed.Set(); result = await submit; }
        Assert.Equal(callbackInFlight ? 1 : 0, callbacks);
        Assert.Equal(callbackInFlight, result.IsSuccess);
        Assert.Equal(callbackInFlight ? BudgetReservationState.Consuming : BudgetReservationState.CancelledPreSubmit,
            c.Kernel.Budgets.Query(c.Budget).Value!.State);
    }

    [Fact]
    public void FailedBoundBudgetCancellationClearsInterlockWithoutInventingEffectClosure()
    {
        var c = BoundCancellationContext();
        Assert.True(c.Kernel.Budgets.QuarantineLease(c.Owner, c.Budget).IsSuccess);
        Assert.False(c.Coordinator.CancelAdmitted(c.Binding).IsSuccess);
        Assert.Equal(40UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(ExternalOperationDisposition.Cancelled,
            c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!.Disposition);
        var callbacks = 0;
        Assert.False(c.Coordinator.SubmitBoundExternalOperation(c.Binding, new(1, 1),
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(0, callbacks);
        Assert.Equal(0UL, c.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Budget).Value!.State);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Owner, BudgetReservationHandle Budget,
        ExternalOperationHandle Operation, V6ManagedTemporalCapacityProvider Provider,
        V6TemporalCapacityCoordinator Coordinator, V6TemporalCapacityBinding Binding) BoundCancellationContext(bool terminalProvider = false, bool admitBinding = true)
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9970, 9971).Handle;
        var owner = TestFixtures.Create(kernel, 9972, 9973).Handle;
        var authority = kernel.MintCapability(new(9971), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, authority, owner, "temporal-overlap", [Amount(100)]).IsSuccess);
        var budget = BoundBudget(kernel.Budgets, owner, 40);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        Assert.True(kernel.AdmitExternalOperation(owner, operation, new(1, 1), new("temporal-overlap-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(100);
        if (terminalProvider)
            typeof(V6ManagedTemporalCapacityProvider).GetField("_providerGeneration",
                global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, ulong.MaxValue);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var binding = admitBinding ? coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(40)).Value! : null!;
        return (kernel, owner, budget, operation, provider, coordinator, binding);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundCancellationRequiresOwnerPreSubmitClosureAndPreservesSubmittedPins(bool submitted)
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9960, 9961).Handle;
        var owner = TestFixtures.Create(kernel, 9962, 9963).Handle;
        var authority = kernel.MintCapability(new(9961), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, authority, owner, "temporal-bound-cancel", [Amount(100)]).IsSuccess);
        var budget = BoundBudget(kernel.Budgets, owner, 40);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        Assert.True(kernel.AdmitExternalOperation(owner, operation, new(1, 1), new("temporal-bound-cancel-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(100);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(40)).Value!;
        Assert.Equal(KernelError.InvalidTransition,
            coordinator.CancelAdmitted(bound with { Owner = owner with { Generation = owner.Generation + 1 } }).Error);
        Assert.Equal(ExternalOperationDisposition.Active, kernel.QueryExternalOperation(owner, operation).Value!.Disposition);
        if (submitted) Assert.True(kernel.RecordExternalOperationSubmission(owner, operation, new(1, 1)).IsSuccess);
        var cancelled = coordinator.CancelAdmitted(bound);
        var effect = kernel.QueryExternalOperation(owner, operation).Value!;
        if (submitted)
        {
            Assert.Equal(KernelError.ExternalEffectUncontained, cancelled.Error);
            Assert.Equal(40UL, provider.ReservedNanoseconds);
            Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
            Assert.Equal(V6TemporalProviderReservationState.Quarantined, provider.Query(bound.ProviderReservation.Handle).Value!.State);
            Assert.Equal(ExternalOperationDisposition.CancellationPending, effect.Disposition);
        }
        else
        {
            Assert.True(cancelled.IsSuccess, cancelled.Message);
            Assert.Equal(0UL, provider.ReservedNanoseconds);
            Assert.Equal(BudgetReservationState.CancelledPreSubmit, kernel.Budgets.Query(budget).Value!.State);
            Assert.Equal(ExternalOperationDisposition.Cancelled, effect.Disposition);
            Assert.Equal(KernelError.InvalidTransition, kernel.RecordExternalOperationSubmission(owner, operation, new(1, 1)).Error);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledPreSubmitOwnerCannotAcquireTemporalCapacityBinding(bool admitted)
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9950, 9951).Handle;
        var owner = TestFixtures.Create(kernel, 9952, 9953).Handle;
        var authority = kernel.MintCapability(new(9951), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, authority, owner, "temporal-cancelled",
            [Amount(100)]).IsSuccess);
        var budget = BoundBudget(kernel.Budgets, owner, 40);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        if (admitted) Assert.True(kernel.AdmitExternalOperation(owner, operation,
            new(1, 1), new("temporal-cancelled-provider")).IsSuccess);
        Assert.True(kernel.CancelExternalOperation(owner, operation, false).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(100);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        Assert.Equal(KernelError.InvalidTransition,
            coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(40)).Error);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Bound, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled,
            kernel.QueryExternalOperation(owner, operation).Value!.Disposition);
    }
    [Fact]
    public void CancellingDistinctBudgetBindingPreservesOtherAdmissionAndCapacity()
    {
        var context = Create(100, 100, 40);
        var otherBudget = BoundBudget(context.Budgets, context.Owner, 40);
        var first = context.Coordinator.Admit(context.Owner, context.Budget, "operation:isolated:first", Temporal(40)).Value!;
        var second = context.Coordinator.Admit(context.Owner, otherBudget, "operation:isolated:second", Temporal(40)).Value!;
        Assert.Equal(80UL, context.Provider.ReservedNanoseconds);
        Assert.True(context.Coordinator.CancelAdmitted(first).IsSuccess);
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(otherBudget).Value!.State);
        var submitted = context.Coordinator.Submit(second, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok);
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.True(context.Coordinator.Settle(submitted.Value!, 10).IsSuccess);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }
    [Fact]
    public void OneBudgetCannotAcquireSecondTemporalBindingOrCancelFirstAdmission()
    {
        var context = Create(100, 100, 40);
        var first = context.Coordinator.Admit(context.Owner, context.Budget, "operation:first", Temporal(40)).Value!;
        var second = context.Coordinator.Admit(context.Owner, context.Budget, "operation:second", Temporal(40));
        Assert.Equal(KernelError.DuplicateIdentity, second.Error);
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(context.Budget).Value!.State);
        var calls = 0;
        var submitted = context.Coordinator.Submit(first, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { calls++; return KernelResult.Ok(); });
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, calls);
        Assert.True(context.Coordinator.Settle(submitted.Value!, 10).IsSuccess);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public async Task ConcurrentBudgetBindingAdmissionHasOneCapacityWinner()
    {
        var context = Create(100, 100, 40);
        var admissions = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            context.Coordinator.Admit(context.Owner, context.Budget, $"operation:race:{index}", Temporal(40)))));
        var winner = Assert.Single(admissions, result => result.IsSuccess).Value!;
        Assert.All(admissions.Where(result => !result.IsSuccess), result => Assert.Equal(KernelError.DuplicateIdentity, result.Error));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(context.Budget).Value!.State);
        Assert.True(context.Coordinator.CancelAdmitted(winner).IsSuccess);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUnicodeCorrelationCannotReserveCapacityOrConsumeBudget(bool lowSurrogate)
    {
        var context = Create(100, 100, 40);
        var malformed = "operation:" + (lowSurrogate ? (char)0xDC00 : (char)0xD800);
        var result = context.Coordinator.Admit(context.Owner, context.Budget, malformed, Temporal(40));
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(context.Budget).Value!.State);
        var valid = context.Coordinator.Admit(context.Owner, context.Budget, "operation:😀", Temporal(40));
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.True(context.Coordinator.CancelAdmitted(valid.Value!).IsSuccess);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }
    [Fact]
    public void BudgetQuarantineDuringSuccessfulSubmitCannotPublishTemporalSuccess()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:budget-loss:during-submit", Temporal(40)).Value!;
        var result = context.Coordinator.Submit(binding, KernelResult.Ok,
            KernelResult.Ok, KernelResult.Ok, () =>
            {
                Assert.True(context.Budgets.QuarantineLease(context.Owner, context.Budget).IsSuccess);
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void StaleReleaseCannotTriggerInjectedResetOfLiveReservation()
    {
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var reserved = provider.Reserve("operation:capacity:exact-release", Temporal(40).ComputeEnvelope).Value!;
        Assert.True(provider.BeginUse(reserved.Handle).IsSuccess);
        provider.InjectResetBeforeNextRelease();

        Assert.Equal(KernelError.StaleGeneration,
            provider.Release(reserved.Handle with { Generation = reserved.Handle.Generation + 1 }).Error);
        Assert.Equal(1UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.InUse,
            provider.Query(reserved.Handle).Value!.State);
        Assert.Equal(40UL, provider.ReservedNanoseconds);

        Assert.Equal(KernelError.InvalidTransition, provider.Release(reserved.Handle).Error);
        Assert.Equal(2UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            provider.Query(reserved.Handle).Value!.State);
        Assert.Equal(40UL, provider.ReservedNanoseconds);
    }

    [Fact]
    public void RepeatedOrQuarantinedReleaseCannotTriggerInjectedReset()
    {
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var released = provider.Reserve("operation:capacity:released", Temporal(10).ComputeEnvelope).Value!;
        Assert.True(provider.Release(released.Handle).IsSuccess);
        var live = provider.Reserve("operation:capacity:live", Temporal(40).ComputeEnvelope).Value!;
        Assert.True(provider.BeginUse(live.Handle).IsSuccess);
        provider.InjectResetBeforeNextRelease();

        Assert.True(provider.Release(released.Handle).IsSuccess);
        Assert.Equal(1UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.InUse, provider.Query(live.Handle).Value!.State);
        Assert.Equal(40UL, provider.ReservedNanoseconds);

        Assert.Equal(KernelError.InvalidTransition, provider.Release(live.Handle).Error);
        Assert.Equal(2UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined, provider.Query(live.Handle).Value!.State);
        provider.InjectResetBeforeNextRelease();
        Assert.Equal(KernelError.InvalidTransition, provider.Release(live.Handle).Error);
        Assert.Equal(2UL, provider.ProviderGeneration);
    }

    [Fact]
    public void AdmissionBindsExistingBudgetAndNamedProviderWithoutDeadlinePromotion()
    {
        var context = Create(100, 100, 60);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:1", Temporal(60)).Value!;

        Assert.Equal(60UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalCapacityOperationState.Admitted, binding.State);
        Assert.Equal(V6ManagedTemporalCapacityProvider.Identity, binding.ProviderReservation.ProviderIdentity);
        Assert.False(binding.AuthorizesExecution);
        Assert.False(binding.GuaranteesDeadline);
        Assert.False(binding.GuaranteesMinimumService);

        var cancelled = context.Coordinator.CancelAdmitted(binding);
        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(0UL, Used(context));
    }

    [Fact]
    public void SuccessfulSubmitConsumesBudgetThenExactSettlementReleasesCapacity()
    {
        var context = Create(100, 100, 80);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:submit", Temporal(80)).Value!;
        var callbacks = 0;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () =>
            {
                callbacks++;
                Assert.Equal(BudgetReservationState.Consuming,
                    context.Budgets.Query(context.Budget).Value!.State);
                Assert.Equal(V6TemporalProviderReservationState.InUse,
                    context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
                return KernelResult.Ok();
            });

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, callbacks);
        var settled = context.Coordinator.Settle(submitted.Value!, 45);
        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(V6TemporalCapacityOperationState.Settled, settled.Value!.State);
        Assert.Equal(45UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void FreshAdmissionDenialLeavesBothReservationsReusable()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:denied", Temporal(50)).Value!;
        var callbacks = 0;

        var result = context.Coordinator.Submit(binding,
            () => KernelResult.Fail(KernelError.CapabilityRevoked, "revoked"),
            KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Reserved,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.True(context.Coordinator.CancelAdmitted(binding).IsSuccess);
    }

    [Fact]
    public void BoundTemporalQuarantineRequiresExactExternalOperationOwnerRelease()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9910, 9911).Handle;
        var owner = TestFixtures.Create(kernel, 9912, 9913).Handle;
        var administration = kernel.MintCapability(new(9911), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-bound",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-bound-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        var unboundCallbacks = 0;
        Assert.Equal(KernelError.PlatformDenied,
            coordinator.Submit(bound, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
                () => { unboundCallbacks++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, unboundCallbacks);

        var ambiguous = coordinator.SubmitBoundExternalOperation(bound, dependencies,
            KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, () =>
            {
                Assert.Equal(ExternalOperationState.Submitted,
                    kernel.QueryExternalOperation(owner, operation).Value!.State);
                return KernelResult.Fail(KernelError.PlatformUnavailable, "provider outcome unknown");
            });
        Assert.Equal(KernelError.PlatformUnavailable, ambiguous.Error);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            coordinator.ReconcileQuarantinedBoundExternalOperation(bound).Error);
        Assert.Equal(KernelError.PlatformDenied,
            coordinator.ReconcileQuarantined(bound, KernelResult.Ok).Error);
        Assert.Equal(KernelError.InvalidTransition,
            coordinator.ReconcileQuarantined(bound with { ExternalOperation = null }, KernelResult.Ok).Error);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(50UL, provider.ReservedNanoseconds);

        Assert.True(kernel.RecordExternalOperationProviderLoss(owner, operation).IsSuccess);
        Assert.True(kernel.ReleaseExternalOperation(owner, operation,
            new(ProviderResourcesClosed: true, ProviderUnavailable: true)).IsSuccess);
        var reconciled = coordinator.ReconcileQuarantinedBoundExternalOperation(bound);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(BudgetReservationState.Released, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
    }

    [Fact]
    public void BoundTemporalSettlementWaitsForOwnerPublication()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9920, 9921).Handle;
        var owner = TestFixtures.Create(kernel, 9922, 9923).Handle;
        var administration = kernel.MintCapability(new(9921), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-publication",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-publication-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        OperationBinding? ownerBinding = null;
        var submitted = coordinator.SubmitBoundExternalOperation(bound, dependencies,
            KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, () =>
            {
                var effect = kernel.QueryExternalOperation(owner, operation);
                Assert.Equal(ExternalOperationState.Submitted, effect.Value!.State);
                ownerBinding = effect.Value.Binding;
                return KernelResult.Ok();
            }).Value!;

        Assert.Equal(KernelError.InvalidTransition,
            coordinator.Settle(submitted with { ExternalOperation = null }, 20).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, coordinator.Settle(submitted, 20).Error);
        Assert.Equal(BudgetReservationState.Consuming, kernel.Budgets.Query(budget).Value!.State);
        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(ownerBinding!.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(ownerBinding.Value, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(kernel.PublishExternalOperation(owner, operation, dependencies,
            new(ExternalPublicationPolicy.Staged), static () => { }).IsSuccess);
        var settled = coordinator.Settle(submitted, 20);

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Released, kernel.Budgets.Query(budget).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundOwnerSubmitDenialClosesCapacityBeforeProviderCallback(bool competingOwnerSubmit)
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9930, 9931).Handle;
        var owner = TestFixtures.Create(kernel, 9932, 9933).Handle;
        var administration = kernel.MintCapability(new(9931), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-owner-denial",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        Assert.True(kernel.AdmitExternalOperation(owner, operation,
            new OperationDependencySnapshot(1, 1), new("temporal-denial-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        var callbacks = 0;

        var denied = coordinator.SubmitBoundExternalOperation(bound,
            new OperationDependencySnapshot(2, 2), KernelResult.Ok, KernelResult.Ok,
            () =>
            {
                if (competingOwnerSubmit)
                    Assert.True(kernel.RecordExternalOperationSubmission(owner, operation, new(1, 1)).IsSuccess);
                return KernelResult.Ok();
            }, () => { callbacks++; return KernelResult.Ok(); });

        Assert.False(denied.IsSuccess);
        Assert.Equal(0, callbacks);
        if (competingOwnerSubmit)
        {
            Assert.Equal(KernelError.StaleGeneration, denied.Error);
            Assert.Equal(50UL, provider.ReservedNanoseconds);
            Assert.Equal(BudgetReservationState.Bound, kernel.Budgets.Query(budget).Value!.State);
            var effect = kernel.QueryExternalOperation(owner, operation).Value!;
            Assert.Equal(ExternalOperationState.Submitted, effect.State);
            Assert.Equal(ExternalOperationDisposition.Active, effect.Disposition);
            return;
        }
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Released, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(ExternalOperationState.Admitted,
            kernel.QueryExternalOperation(owner, operation).Value!.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled,
            kernel.QueryExternalOperation(owner, operation).Value!.Disposition);
        Assert.Equal(KernelError.InvalidTransition,
            kernel.RecordExternalOperationSubmission(owner, operation, new(1, 1)).Error);
        Assert.Equal(KernelError.InvalidTransition,
            coordinator.ReconcileQuarantinedBoundExternalOperation(bound).Error);
    }

    [Fact]
    public async Task ConcurrentBoundSubmitHasOneOwnerTransitionAndOneProviderCallback()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9940, 9941).Handle;
        var owner = TestFixtures.Create(kernel, 9942, 9943).Handle;
        var administration = kernel.MintCapability(new(9941), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-race",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-race-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        using var barrier = new Barrier(2);
        var callbacks = 0;
        KernelResult<V6TemporalCapacityBinding> Invoke() => coordinator.SubmitBoundExternalOperation(
            bound, dependencies, KernelResult.Ok, KernelResult.Ok,
            () => { barrier.SignalAndWait(TimeSpan.FromSeconds(5)); return KernelResult.Ok(); },
            () => { Interlocked.Increment(ref callbacks); return KernelResult.Ok(); });

        var results = await Task.WhenAll(Task.Run(Invoke), Task.Run(Invoke)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error == KernelError.StaleGeneration);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, kernel.QueryExternalOperation(owner, operation).Value!.Transitions.Count(
            transition => transition.Event == "Submitted"));
        Assert.Equal(BudgetReservationState.Consuming, kernel.Budgets.Query(budget).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundOwnerLossDuringSuccessfulProviderCallbackStillQuarantinesCapacity(bool loseBudget)
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9950, 9951).Handle;
        var owner = TestFixtures.Create(kernel, 9952, 9953).Handle;
        var administration = kernel.MintCapability(new(9951), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-owner-loss",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-loss-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;

        var result = coordinator.SubmitBoundExternalOperation(bound, dependencies,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () =>
            {
                Assert.True(loseBudget
                    ? kernel.Budgets.QuarantineLease(owner, budget).IsSuccess
                    : kernel.RecordExternalOperationProviderLoss(owner, operation).IsSuccess);
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            provider.Query(bound.ProviderReservation.Handle).Value!.State);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            coordinator.ReconcileQuarantinedBoundExternalOperation(bound).Error);
        Assert.Equal(50UL, provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
        if (loseBudget)
            Assert.True(kernel.RecordExternalOperationProviderLoss(owner, operation).IsSuccess);
        Assert.True(kernel.ReleaseExternalOperation(owner, operation,
            new(ProviderResourcesClosed: true, ProviderUnavailable: true)).IsSuccess);
        var reconciled = coordinator.ReconcileQuarantinedBoundExternalOperation(bound);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(BudgetReservationState.Released, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegalityTriggeredAdmissionRevocationIsCaughtBeforeCapacityUse(bool revokeSingNext)
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:legality-revoke", Temporal(50)).Value!;
        var revoked = false;
        var singNextReads = 0;
        var providerReads = 0;
        var effects = 0;

        var result = context.Coordinator.Submit(binding,
            () =>
            {
                singNextReads++;
                return revoked && revokeSingNext
                    ? KernelResult.Fail(KernelError.CapabilityRevoked, "SingNext admission revoked")
                    : KernelResult.Ok();
            },
            () =>
            {
                providerReads++;
                return revoked && !revokeSingNext
                    ? KernelResult.Fail(KernelError.PlatformDenied, "provider admission revoked")
                    : KernelResult.Ok();
            },
            () => { revoked = true; return KernelResult.Ok(); },
            () => { effects++; return KernelResult.Ok(); });

        Assert.Equal(revokeSingNext ? KernelError.CapabilityRevoked : KernelError.PlatformDenied,
            result.Error);
        Assert.Equal(2, singNextReads);
        Assert.Equal(revokeSingNext ? 1 : 2, providerReads);
        Assert.Equal(0, effects);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Reserved,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
    }

    [Fact]
    public void BudgetCancelledAfterAdmissionClosesUnusedProviderCapacityBeforeSubmit()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:budget-cancelled", Temporal(50)).Value!;
        // Fault injection through the exact existing quantitative consumer;
        // generic cancellation cannot detach another composition's charge.
        Assert.True(context.Budgets.CancelLeasePreSubmit(context.Owner, context.Budget, binding.Id).IsSuccess);
        var effects = 0;

        var stale = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { effects++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, effects);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Released,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(KernelError.StaleGeneration, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { effects++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, effects);
    }

    [Fact]
    public void ProviderFailureQuarantinesBothOwnersUntilExplicitReconciliation()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:failure", Temporal(50)).Value!;
        var result = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "lost"));

        Assert.Equal(KernelError.PlatformUnavailable, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(50UL, Used(context));
        var reconciled = context.Coordinator.ReconcileQuarantined(
            binding with { State = V6TemporalCapacityOperationState.Quarantined }, KernelResult.Ok);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void DeniedEffectClosureCannotReleaseQuarantinedCapacityOrBudget()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:closure:denied", Temporal(50)).Value!;
        Assert.Equal(KernelError.PlatformUnavailable, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "ambiguous effect")).Error);

        var denied = context.Coordinator.ReconcileQuarantined(binding,
            () => KernelResult.Fail(KernelError.PlatformDenied, "effect not closed"));

        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(50UL, Used(context));
        Assert.Equal(50UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
    }

    [Fact]
    public void EffectClosureCallbackRunsOutsideCoordinatorLock()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:closure:outside-lock", Temporal(50)).Value!;
        Assert.Equal(KernelError.PlatformUnavailable, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "ambiguous effect")).Error);

        var reconciled = context.Coordinator.ReconcileQuarantined(binding, () =>
        {
            var concurrent = Task.Run(() => context.Coordinator.CancelAdmitted(binding));
            Assert.True(concurrent.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.InvalidTransition, concurrent.Result.Error);
            return KernelResult.Ok();
        });

        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ConcurrentReconciliationConsumesQuarantineOnce()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:closure:one-winner", Temporal(50)).Value!;
        Assert.Equal(KernelError.PlatformUnavailable, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "ambiguous effect")).Error);

        var outer = context.Coordinator.ReconcileQuarantined(binding, () =>
        {
            var inner = context.Coordinator.ReconcileQuarantined(binding, KernelResult.Ok);
            Assert.True(inner.IsSuccess, inner.Message);
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.StaleGeneration, outer.Error);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ProviderResetBeforeOrDuringSubmitNeverRunsOrPublishesStaleCapacity()
    {
        var before = Create(100, 100, 40);
        var beforeBinding = before.Coordinator.Admit(before.Owner, before.Budget,
            "operation:capacity:reset-before", Temporal(40)).Value!;
        Assert.True(before.Provider.Reset().IsSuccess);
        var callbacks = 0;
        var denied = before.Coordinator.Submit(beforeBinding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.StaleGeneration, denied.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            before.Budgets.Query(before.Budget).Value!.State);
        Assert.Equal(0UL, before.Provider.ReservedNanoseconds);

        var during = Create(100, 100, 40);
        var duringBinding = during.Coordinator.Admit(during.Owner, during.Budget,
            "operation:capacity:reset-during", Temporal(40)).Value!;
        var ambiguous = during.Coordinator.Submit(duringBinding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { Assert.True(during.Provider.Reset().IsSuccess); return KernelResult.Ok(); });
        Assert.Equal(KernelError.StaleGeneration, ambiguous.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            during.Budgets.Query(during.Budget).Value!.State);
        Assert.Equal(40UL, during.Provider.ReservedNanoseconds);
        var reconciled = during.Coordinator.ReconcileQuarantined(
            duringBinding with { State = V6TemporalCapacityOperationState.Quarantined }, KernelResult.Ok);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(0UL, Used(during));
        Assert.Equal(0UL, during.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ResetRetainsAmbiguousCapacityAndCorrelationUntilReconciliation()
    {
        var provider = new V6ManagedTemporalCapacityProvider(100);
        var first = provider.Reserve("operation:reset:ambiguous", Temporal(80).ComputeEnvelope).Value!;
        Assert.True(provider.BeginUse(first.Handle).IsSuccess);
        Assert.True(provider.Reset().IsSuccess);

        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            provider.Query(first.Handle).Value!.State);
        Assert.Equal(80UL, provider.ReservedNanoseconds);
        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Reserve("operation:reset:ambiguous", Temporal(1).ComputeEnvelope).Error);
        Assert.Equal(KernelError.BudgetExceeded,
            provider.Reserve("operation:reset:new", Temporal(21).ComputeEnvelope).Error);

        Assert.True(provider.ReconcileAndRelease(first.Handle).IsSuccess);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.True(provider.Reserve("operation:reset:ambiguous", Temporal(80).ComputeEnvelope).IsSuccess);
    }

    [Fact]
    public void ResetBeforeSubmitAllowsExactCancellationOfStaleReservation()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:reset:pre-submit", Temporal(40)).Value!;
        Assert.True(context.Provider.Reset().IsSuccess);

        var cancelled = context.Coordinator.CancelAdmitted(binding);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(V6TemporalCapacityOperationState.Cancelled, cancelled.Value!.State);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(0UL, Used(context));
    }

    [Fact]
    public void ResetAfterSubmitQuarantinesBudgetBeforeSettlement()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:reset:after-submit", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.True(context.Provider.Reset().IsSuccess);

        var stale = context.Coordinator.Settle(submitted, 20);

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(40UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.True(context.Coordinator.ReconcileQuarantined(submitted, KernelResult.Ok).IsSuccess);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void SettlementFailureQuarantinesProviderAndKeepsReconciliationReachable()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:settlement:failure", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.True(context.Budgets.QuarantineLease(context.Owner, context.Budget).IsSuccess);

        var failed = context.Coordinator.Settle(submitted, 20);

        Assert.Equal(KernelError.InvalidTransition, failed.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.True(context.Coordinator.ReconcileQuarantined(submitted, KernelResult.Ok).IsSuccess);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ResetBetweenBudgetSettlementAndProviderReleaseRetainsCapacityForReconciliation()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:settlement:reset-race", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        context.Provider.InjectResetBeforeNextRelease();

        var interrupted = context.Coordinator.Settle(submitted, 20);

        Assert.Equal(KernelError.PlatformFaulted, interrupted.Error);
        Assert.Equal(BudgetReservationState.Released,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(20UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.True(context.Coordinator.ReconcileQuarantined(submitted, KernelResult.Ok).IsSuccess);
        Assert.Equal(20UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public async Task LastProtectedCapacityUnitHasExactlyOneWinner()
    {
        var budgets = BudgetAuthority(200, out var owner, out var account);
        var firstBudget = BoundBudget(budgets, owner, 60);
        var secondBudget = BoundBudget(budgets, owner, 60);
        var provider = new V6ManagedTemporalCapacityProvider(60);
        var coordinator = new V6TemporalCapacityCoordinator(budgets, provider);
        using var barrier = new Barrier(2);
        Task<KernelResult<V6TemporalCapacityBinding>> Admit(string correlation, BudgetReservationHandle budget) =>
            Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                return coordinator.Admit(owner, budget, correlation, Temporal(60));
            });
        var results = await Task.WhenAll(Admit("operation:capacity:a", firstBudget),
            Admit("operation:capacity:b", secondBudget));

        var winner = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Equal(KernelError.BudgetExceeded, Assert.Single(results, result => !result.IsSuccess).Error);
        Assert.Equal(60UL, provider.ReservedNanoseconds);
        Assert.Equal(120UL, Used(budgets.Query(account).Value!));
        Assert.True(coordinator.CancelAdmitted(winner).IsSuccess);
    }

    [Fact]
    public void GuaranteedCompletionDeadlineIsStillRejectedByCapacityContour()
    {
        var context = Create(100, 100, 40);
        var deadline = Temporal(40) with
        {
            DeadlineSemantics = TemporalDeadlineSemanticsV1.GuaranteedCompletion,
            DeadlineTimestamp = 1000,
        };

        Assert.Equal(KernelError.PlatformUnsupported,
            context.Coordinator.Admit(context.Owner, context.Budget,
                "operation:capacity:deadline", deadline).Error);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void MeasuredOverrunQuarantinesBothOwnersAndCannotBeRewrittenByLowerSettlement()
    {
        var context = Create(100, 50, 40);
        var admitted = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:overrun", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(admitted, KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, KernelResult.Ok).Value!;

        Assert.Equal(KernelError.BudgetExceeded, context.Coordinator.Settle(submitted, 41).Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(submitted.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(KernelError.InvalidTransition, context.Coordinator.Settle(submitted, 20).Error);
        var closureCalls = 0;
        var reconciliation = context.Coordinator.ReconcileQuarantined(submitted, () =>
        {
            closureCalls++;
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.BudgetExceeded, reconciliation.Error);
        Assert.Equal(0, closureCalls);
        var budget = context.Budgets.Query(context.Budget).Value!;
        Assert.Equal(BudgetReservationState.Quarantined, budget.State);
        Assert.Empty(budget.ChargedAmounts ?? []);
        Assert.Equal(40UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(100UL, true)]
    [InlineData(40UL, false)]
    public void CorrectiveOverrunSettlementUsesBudgetOwnerCapacityOrRetainsQuarantine(
        ulong budgetLimit, bool chargeFits)
    {
        var context = Create(budgetLimit, 40, 40);
        var admitted = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:corrective", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(admitted, KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.Equal(KernelError.BudgetExceeded, context.Coordinator.Settle(submitted, 41).Error);
        var closureCalls = 0;
        var corrected = context.Coordinator.ReconcileReportedOverrun(submitted, () =>
        {
            closureCalls++;
            return KernelResult.Ok();
        });

        Assert.Equal(1, closureCalls);
        if (chargeFits)
        {
            Assert.True(corrected.IsSuccess, corrected.Message);
            Assert.Equal(BudgetReservationState.Released, context.Budgets.Query(context.Budget).Value!.State);
            Assert.Equal(41UL, Assert.Single(context.Budgets.Query(context.Budget).Value!.ChargedAmounts!).Amount);
            Assert.Equal(41UL, Used(context));
            Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
            Assert.Equal(KernelError.InvalidTransition,
                context.Coordinator.ReconcileReportedOverrun(submitted, KernelResult.Ok).Error);
        }
        else
        {
            Assert.Equal(KernelError.BudgetExceeded, corrected.Error);
            Assert.Equal(BudgetReservationState.Quarantined, context.Budgets.Query(context.Budget).Value!.State);
            Assert.Equal(40UL, Used(context));
            Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        }
    }

    [Fact]
    public void CorrectiveOverrunChargeRequiresEffectClosureBeforeBudgetMutation()
    {
        var context = Create(100, 40, 40);
        var admitted = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:closure-before-charge", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(admitted, KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.Equal(KernelError.BudgetExceeded, context.Coordinator.Settle(submitted, 41).Error);

        var denied = context.Coordinator.ReconcileReportedOverrun(submitted,
            () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "effect remains open"));

        Assert.Equal(KernelError.ExternalEffectUncontained, denied.Error);
        Assert.Equal(BudgetReservationState.Quarantined, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Empty(context.Budgets.Query(context.Budget).Value!.ChargedAmounts ?? []);
        Assert.Equal(40UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void BoundCorrectiveOverrunWaitsForExactExternalOwnerRelease()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9960, 9961).Handle;
        var owner = TestFixtures.Create(kernel, 9962, 9963).Handle;
        var administration = kernel.MintCapability(new(9961), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-bound-overrun",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 40)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-bound-overrun-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(40);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var admitted = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(40)).Value!;
        var submitted = coordinator.SubmitBoundExternalOperation(admitted, dependencies,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        var ownerBinding = kernel.QueryExternalOperation(owner, operation).Value!.Binding!.Value;
        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(ownerBinding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(ownerBinding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(kernel.PublishExternalOperation(owner, operation, dependencies,
            new(ExternalPublicationPolicy.Staged), static () => { }).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded, coordinator.Settle(submitted, 41).Error);

        Assert.Equal(KernelError.ExternalEffectUncontained,
            coordinator.ReconcileReportedOverrunBoundExternalOperation(submitted).Error);
        Assert.Equal(KernelError.PlatformDenied,
            coordinator.ReconcileReportedOverrun(submitted, KernelResult.Ok).Error);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
        Assert.True(kernel.ReleaseExternalOperation(owner, operation,
            new(ProviderResourcesClosed: true, ProviderUnavailable: false)).IsSuccess);

        var corrected = coordinator.ReconcileReportedOverrunBoundExternalOperation(submitted);
        Assert.True(corrected.IsSuccess, corrected.Message);
        Assert.Equal(41UL, Assert.Single(kernel.Budgets.Query(budget).Value!.ChargedAmounts!).Amount);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
    }

    private static Context Create(ulong budgetLimit, ulong providerCapacity, ulong reservation)
    {
        var budgets = BudgetAuthority(budgetLimit, out var owner, out var account);
        var lease = BoundBudget(budgets, owner, reservation);
        var provider = new V6ManagedTemporalCapacityProvider(providerCapacity);
        return new(budgets, owner, account, lease, provider,
            new V6TemporalCapacityCoordinator(budgets, provider));
    }

    private static ResourceBudgetAuthority BudgetAuthority(
        ulong limit, out ProcessHandle owner, out BudgetAccountHandle account)
    {
        var budgets = new ResourceBudgetAuthority();
        Assert.True(budgets.ConfigureSystem([Amount(limit)]).IsSuccess);
        var service = budgets.CreateChild(budgets.SystemBudget, BudgetAccountLevel.Service,
            "temporal-service", [Amount(limit)]).Value!.Account;
        account = budgets.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "temporal-process", [Amount(limit)]).Value!.Account;
        owner = new ProcessHandle(new(990), 5);
        Assert.True(budgets.AttachProcess(owner, account).IsSuccess);
        return budgets;
    }

    private static BudgetReservationHandle BoundBudget(
        ResourceBudgetAuthority budgets, ProcessHandle owner, ulong amount)
    {
        var reservation = budgets.Reserve(owner, [Amount(amount)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.BoundedInteractive).Value!.Reservation;
        Assert.True(budgets.BindLease(owner, reservation).IsSuccess);
        return reservation;
    }

    private static TemporalSemanticsV1 Temporal(ulong amount) => new(1,
        new ResourceEnvelopeV1(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, amount, 0, "managed:protected-capacity"),
        ResourceAssuranceV1.GuaranteedReservation, TemporalDeadlineSemanticsV1.None,
        DeadlineClockClass.MonotonicRuntime, 0);

    private static BudgetAmount Amount(ulong amount) =>
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, amount);

    private static ulong Used(Context context) => Used(context.Budgets.Query(context.Account).Value!);
    private static ulong Used(BudgetAccountSnapshot snapshot) =>
        Assert.Single(snapshot.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record Context(ResourceBudgetAuthority Budgets, ProcessHandle Owner,
        BudgetAccountHandle Account, BudgetReservationHandle Budget,
        V6ManagedTemporalCapacityProvider Provider, V6TemporalCapacityCoordinator Coordinator);
}
