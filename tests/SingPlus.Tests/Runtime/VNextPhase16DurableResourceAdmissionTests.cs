using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase16DurableResourceAdmissionTests
{
    private static readonly byte[] Key = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void PostJournalAdmissionSeesLiveOwnersBeforeCreatingPossibleEffect(int fault)
    {
        WithJournalStore((path, context, store) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            int calls = 0, observations = 0;
            bool mutationAccepted = false, outsideLocks = true, reentryBlocked = false;
            store.AfterFrameWrite = () =>
            {
                store.AfterFrameWrite = null;
                foreach (object owner in new object[] { context.Kernel.CapabilityAuthority, context.Kernel.ExternalOperations, context.Kernel.Budgets })
                {
                    FieldInfo? field = owner.GetType().GetField("_gate",
                        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic);
                    outsideLocks &= field is not null && !Monitor.IsEntered(field.GetValue(owner)!);
                }
                reentryBlocked = !context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
                    () => { calls++; return KernelResult.Ok(); }).IsSuccess;
                mutationAccepted = fault switch
                {
                    1 => context.Kernel.CapabilityAuthority.Revoke(commit.EffectCapability).IsSuccess,
                    2 => context.Kernel.CapabilityAuthority.Revoke(commit.ResourceGrant).IsSuccess,
                    3 => context.Kernel.Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease).IsSuccess,
                    4 => context.Kernel.ExternalOperations.RecordSubmission(context.Operation, context.Dependencies).IsSuccess,
                    _ => true
                };
            };
            KernelResult<OperationBinding> result = context.Kernel.SubmitResourceExternalAdmissionWithBinding(commit, context.Dependencies,
                _ => { calls++; return KernelResult.Ok(); }, preSubmitFailureObservation: () => observations++);
            Assert.True(mutationAccepted); Assert.True(outsideLocks); Assert.True(reentryBlocked);
            ExternalOperationSnapshot owner = context.Kernel.ExternalOperations.Query(context.Operation).Value!;
            ExternalOperationResourceBinding resource = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            BudgetReservationSnapshot budget = context.Kernel.QueryBudget(commit.Lease).Value!;
            ResourceBudgetRecoverySnapshot cold = Kernel(path).ResourceBudgetRecovery!;
            if (fault == 0)
            {
                Assert.True(result.IsSuccess, result.Message);
                Assert.Equal(1, calls); Assert.Equal(0, observations);
                Assert.Equal(ExternalOperationState.Submitted, owner.State);
                Assert.Equal(BudgetReservationState.Consuming, budget.State);
                Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit, Assert.Single(cold.Items).LastPayload.Transition);
            }
            else
            {
                Assert.False(result.IsSuccess); Assert.Equal(0, calls); Assert.Equal(1, observations);
                if (fault is 1 or 2)
                {
                    Assert.Null(owner.Binding);
                    Assert.Equal(ExternalEffectBoundaryState.NotCrossed, owner.EffectBoundary);
                    Assert.DoesNotContain(owner.Transitions, item => item.To == ExternalOperationState.Submitted);
                    Assert.Equal(BudgetReservationState.CancelledPreSubmit, budget.State);
                    Assert.Equal(ExternalResourceBindingState.CancelledPreSubmit, resource.State);
                    Assert.Equal(ResourceBudgetRecoveryTransition.SettledContained, Assert.Single(cold.Items).LastPayload.Transition);
                    Assert.Empty(cold.ConservativeRecoveryCharge);
                }
                else
                {
                    if (fault == 3) Assert.Null(owner.Binding);
                    else Assert.NotNull(owner.Binding);
                    Assert.Equal(BudgetReservationState.Quarantined, budget.State);
                    Assert.Equal(ExternalResourceBindingState.Quarantined, resource.State);
                    Assert.Equal(10UL, Assert.Single(cold.ConservativeRecoveryCharge).Amount);
                }
            }
            Assert.Equal(fault == 0 ? 2UL : 3UL, cold.LastSequence);
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void LostCancellationFrameCannotLowerColdChargeWithoutExactOriginalOwnerTuple(int drift)
    {
        WithJournalStore((path, context, store) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            store.ThrowUnexpectedException = true;
            store.ThrowAfterNextWrite = true;
            store.BeforeAppendFailure = () => store.ThrowBeforeNextWrite = true;
            int calls = 0;
            Assert.Equal(KernelError.Quarantined, context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, () => { calls++; return KernelResult.Ok(); }).Error);
            Assert.Equal(BudgetReservationState.CancelledPreSubmit, context.Kernel.QueryBudget(commit.Lease).Value!.State);
            BudgetReservationHandle lease = drift == 0 ? new BudgetReservationHandle(commit.Lease.ReservationId,
                new(commit.Lease.Generation.Value + 1)) : commit.Lease;
            ProcessHandle owner = drift == 1 ? commit.BudgetOwner with { Generation = commit.BudgetOwner.Generation + 1 } : commit.BudgetOwner;
            PlatformResourceCorrelation correlation = drift == 2 ? commit.ProviderCorrelation with
            {
                CorrelationId = new(commit.ProviderCorrelation.CorrelationId.Value + 1)
            } : drift == 3 ? commit.ProviderCorrelation with
            {
                Generation = new(commit.ProviderCorrelation.Generation.Value + 1)
            } : commit.ProviderCorrelation;
            ResourceEnvelopeV1 envelope = drift == 4 ? commit.Envelope with { Amount = commit.Envelope.Amount + 1 } : commit.Envelope;
            IReadOnlyList<BudgetAmount> charge = drift == 5 ? [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 1)] : [];
            MethodInfo writer = typeof(RuntimeKernel).GetMethod("AppendResourceRecovery",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
            KernelResult refused = (KernelResult)writer.Invoke(context.Kernel,
                [lease, owner, envelope, correlation, ResourceBudgetRecoveryTransition.CancelledPreSubmit, charge])!;
            Assert.Equal(KernelError.Quarantined, refused.Error);
            ResourceBudgetRecoverySnapshot pinned = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(2UL, pinned.LastSequence);
            Assert.True(pinned.BlocksFreshAdmission);
            Assert.Equal(10UL, Assert.Single(pinned.ConservativeRecoveryCharge).Amount);
            Assert.True(context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).IsSuccess);
            Assert.True(context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).IsSuccess);
            ResourceBudgetRecoverySnapshot recovered = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(3UL, recovered.LastSequence);
            Assert.False(recovered.BlocksFreshAdmission);
            Assert.Empty(recovered.ConservativeRecoveryCharge);
            Assert.Equal(ResourceBudgetRecoveryTransition.SettledContained, Assert.Single(recovered.Items).LastPayload.Transition);
            Assert.Equal(0, calls);
        });
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void UnexpectedPossibleSubmitAppendFailureCompensatesOnlyOriginalPreEffectOwners(bool frameWritten, bool ownerSubmitted)
    {
        WithJournalStore((path, context, store) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            int calls = 0, observations = 0;
            bool otherSubmitAccepted = false;
            store.ThrowUnexpectedException = true;
            store.ThrowBeforeNextWrite = !frameWritten;
            store.ThrowAfterNextWrite = frameWritten;
            if (ownerSubmitted) store.BeforeAppendFailure = () =>
                otherSubmitAccepted = context.Kernel.ExternalOperations.RecordSubmission(
                    context.Operation, context.Dependencies).IsSuccess;
            KernelResult<OperationBinding> result = context.Kernel.SubmitResourceExternalAdmissionWithBinding(commit,
                context.Dependencies, _ => { calls++; return KernelResult.Ok(); },
                preSubmitFailureObservation: () => observations++);
            Assert.Equal(KernelError.Quarantined, result.Error);
            Assert.Equal(0, calls);
            Assert.Equal(1, observations);
            if (ownerSubmitted) Assert.True(otherSubmitAccepted);
            ExternalOperationSnapshot owner = context.Kernel.QueryExternalOperation(context.Process, context.Operation).Value!;
            ExternalOperationResourceBinding resource = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            BudgetReservationSnapshot budget = context.Kernel.QueryBudget(commit.Lease).Value!;
            Assert.Equal(ownerSubmitted ? BudgetReservationState.Quarantined : BudgetReservationState.CancelledPreSubmit, budget.State);
            Assert.Equal(ownerSubmitted ? ExternalResourceBindingState.Quarantined : ExternalResourceBindingState.CancelledPreSubmit, resource.State);
            if (ownerSubmitted) Assert.NotNull(owner.Binding);
            else { Assert.Null(owner.Binding); Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition); }
            ResourceBudgetRecoverySnapshot cold = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(frameWritten ? 3UL : 2UL, cold.LastSequence);
            Assert.Equal(ownerSubmitted ? ResourceBudgetRecoveryTransition.Quarantined : frameWritten
                    ? ResourceBudgetRecoveryTransition.SettledContained : ResourceBudgetRecoveryTransition.CancelledPreSubmit,
                Assert.Single(cold.Items).LastPayload.Transition);
            if (ownerSubmitted) Assert.Equal(10UL, Assert.Single(cold.ConservativeRecoveryCharge).Amount);
            else Assert.Empty(cold.ConservativeRecoveryCharge);
            Assert.Equal(KernelError.InvalidTransition, context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, () => { calls++; return KernelResult.Ok(); }).Error);
            commit.Dispose();
            Assert.Equal(0, calls);
            Assert.Equal(cold.LastSequence, Kernel(path).ResourceBudgetRecovery!.LastSequence);
            Assert.Equal(ownerSubmitted ? 10UL : 0UL, context.Kernel.Budgets.Query(context.ProcessBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        });
    }

    [Theory]
    [InlineData(0UL, 0)] [InlineData(0UL, 1)] [InlineData(0UL, 2)] [InlineData(0UL, 3)] [InlineData(0UL, 4)]
    [InlineData(4UL, 0)] [InlineData(4UL, 1)] [InlineData(4UL, 2)] [InlineData(4UL, 3)] [InlineData(4UL, 4)]
    public void ExceptionalSettlementRetainsOriginalFactsAndBalancesOnlyItsAttempt(ulong consumed, int boundary)
    {
        WithJournalStore((path, context, store) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            OperationBinding binding = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            ExternalResourceUsageEvidence evidence = Evidence(binding, consumed);
            bool outsideLocks = true, reentryBlocked = false;
            void Interrupt()
            {
                foreach (object owner in new object[] { context.Kernel.ExternalOperations, context.Kernel.Budgets })
                    outsideLocks &= !Monitor.IsEntered(owner.GetType().GetField("_gate",
                        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!);
                reentryBlocked = !context.Kernel.SettleResourceExternalOperation(context.Process, evidence).IsSuccess;
                throw new InvalidOperationException("Settlement callback interrupted");
            }
            if (boundary == 0) context.Kernel.ResourceSettlementAfterBeginQualificationHook = Interrupt;
            if (boundary == 1) context.Kernel.ResourceSettlementAfterBudgetQualificationHook = Interrupt;
            if (boundary == 2) context.Kernel.ResourceSettlementAfterJournalQualificationHook = Interrupt;
            store.ThrowUnexpectedException = boundary >= 3;
            store.ThrowBeforeNextWrite = boundary == 3;
            store.ThrowAfterNextWrite = boundary == 4;
            Assert.Equal(KernelError.Quarantined,
                context.Kernel.SettleResourceExternalOperation(context.Process, evidence).Error);
            if (boundary <= 2) { Assert.True(outsideLocks); Assert.True(reentryBlocked); }
            ExternalOperationResourceBinding original = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            Assert.Equal(ExternalResourceBindingState.Quarantined, original.State);
            Assert.False(original.SettlementInFlight);
            Assert.Equal(evidence.Usage, original.TerminalUsage);
            Assert.Equal(evidence.EvidenceId, original.TerminalEvidenceId);
            Assert.Equal(boundary == 0 ? BudgetReservationState.Consuming : BudgetReservationState.Released,
                context.Kernel.QueryBudget(commit.Lease).Value!.State);
            ResourceBudgetRecoverySnapshot cold = Kernel(path).ResourceBudgetRecovery!;
            ulong expectedCharge = boundary is 2 or 4 ? consumed : 10UL;
            if (expectedCharge == 0) Assert.Empty(cold.ConservativeRecoveryCharge);
            else Assert.Equal(expectedCharge, Assert.Single(cold.ConservativeRecoveryCharge).Amount);
            Assert.False(context.Kernel.ReleaseExternalOperation(context.Process, context.Operation, new(true, false)).IsSuccess);
            context.Kernel.ResourceSettlementAfterBeginQualificationHook = null;
            context.Kernel.ResourceSettlementAfterBudgetQualificationHook = null;
            context.Kernel.ResourceSettlementAfterJournalQualificationHook = null;
            Assert.Equal(KernelError.InvalidTransition,
                context.Kernel.SettleResourceExternalOperation(context.Process, evidence with { ReceiptSequence = 2 }).Error);
            Assert.True(context.Kernel.SettleResourceExternalOperation(context.Process, evidence).IsSuccess);
            Assert.True(context.Kernel.SettleResourceExternalOperation(context.Process, evidence).IsSuccess);
            Assert.Equal(3UL, Kernel(path).ResourceBudgetRecovery!.LastSequence);
            Assert.Equal(consumed, context.Kernel.Budgets.Query(context.ProcessBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateSettlementExitCannotCompleteNestedRetryAttempt(bool throws)
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            OperationBinding submitted = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(submitted, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            ExternalResourceUsageEvidence evidence = Evidence(submitted, 4);
            ExternalOperationResourceBinding? nested = null;
            context.Kernel.ResourceSettlementAfterJournalQualificationHook = () =>
            {
                ExternalOperationResourceBinding outer = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
                Assert.True(context.Kernel.ExternalOperations.CompleteResourceSettlement(context.Operation,
                    commit.Lease, false, outer.SettlementAttempt).IsSuccess);
                nested = context.Kernel.ExternalOperations.BeginResourceSettlement(evidence).Value!;
                Assert.NotSame(outer.SettlementAttempt, nested.SettlementAttempt);
                if (throws) throw new InvalidOperationException("Late outer callback");
            };
            Assert.Equal(throws ? KernelError.Quarantined : KernelError.StaleGeneration,
                context.Kernel.SettleResourceExternalOperation(context.Process, evidence).Error);
            ExternalOperationResourceBinding current = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            Assert.Same(nested!.SettlementAttempt, current.SettlementAttempt);
            Assert.True(current.SettlementInFlight);
            Assert.Equal(ExternalResourceBindingState.Settling, current.State);
            Assert.Equal(3UL, Kernel(path).ResourceBudgetRecovery!.LastSequence);
            Assert.False(context.Kernel.SettleResourceExternalOperation(context.Process, evidence).IsSuccess);
            Assert.True(context.Kernel.ExternalOperations.CompleteResourceSettlement(context.Operation,
                commit.Lease, false, nested.SettlementAttempt).IsSuccess);
            context.Kernel.ResourceSettlementAfterJournalQualificationHook = null;
            Assert.True(context.Kernel.SettleResourceExternalOperation(context.Process, evidence).IsSuccess);
            Assert.Equal(3UL, Kernel(path).ResourceBudgetRecovery!.LastSequence);
            Assert.Equal(4UL, context.Kernel.Budgets.Query(context.ProcessBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        });
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    public void JournalFailurePinsOriginalUsageReceiptDespiteEqualNormalizedCharge(bool frameWritten, int conflict)
    {
        WithJournalStore((path, context, store) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            OperationBinding binding = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            ExternalResourceUsageEvidence evidence = Evidence(binding, 4) with
            {
                Usage = new(1, 0, 0, 1, 1, 1, 37, 1, 0, 41)
            };
            store.ThrowBeforeNextWrite = !frameWritten;
            store.ThrowAfterNextWrite = frameWritten;
            Assert.Equal(KernelError.Quarantined,
                context.Kernel.SettleResourceExternalOperation(context.Process, evidence).Error);
            ExternalOperationResourceBinding original = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            Assert.Equal(ExternalResourceBindingState.Quarantined, original.State);
            Assert.False(original.SettlementInFlight);
            Assert.Equal(BudgetReservationState.Released, context.Kernel.QueryBudget(commit.Lease).Value!.State);
            ResourceBudgetRecoverySnapshot before = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(frameWritten ? 4UL : 10UL, Assert.Single(before.ConservativeRecoveryCharge).Amount);
            ExternalResourceUsageEvidence changed = conflict switch
            {
                0 => evidence with { ReceiptSequence = evidence.ReceiptSequence + 1 },
                1 => evidence with { EvidenceId = Guid.NewGuid() },
                2 => evidence with { Usage = evidence.Usage with { Executed = 0, Squashed = 2 } },
                _ => evidence with { Usage = evidence.Usage with { Retired = 38, StalledOrBlocked = 42 } }
            };
            Assert.Equal(4UL, ResourceChargeabilityMatrixV1.For(ResourceClassV1.ComputeTime).Normalize(changed.Usage));
            Assert.Equal(KernelError.InvalidTransition,
                context.Kernel.SettleResourceExternalOperation(context.Process, changed).Error);
            Assert.Equal(original, context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!);
            Assert.Equal(before.LastSequence, Kernel(path).ResourceBudgetRecovery!.LastSequence);
            Assert.False(context.Kernel.ReleaseExternalOperation(context.Process, context.Operation, new(true, false)).IsSuccess);
            Assert.True(context.Kernel.SettleResourceExternalOperation(context.Process, evidence).IsSuccess);
            ResourceBudgetRecoverySnapshot after = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(3UL, after.LastSequence);
            Assert.Equal(4UL, Assert.Single(after.ConservativeRecoveryCharge).Amount);
            ExternalOperationResourceBinding settled = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            Assert.Equal(ExternalResourceBindingState.Settled, settled.State);
            Assert.Equal(evidence.Usage, settled.TerminalUsage);
            Assert.Equal(4UL, context.Kernel.Budgets.Query(context.ProcessBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
            Assert.Equal(KernelError.InvalidTransition,
                context.Kernel.SettleResourceExternalOperation(context.Process, changed).Error);
            Assert.True(context.Kernel.SettleResourceExternalOperation(context.Process, evidence).IsSuccess);
            Assert.Equal(3UL, Kernel(path).ResourceBudgetRecovery!.LastSequence);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CancellationTerminalReplayRejectsConflictingExactTuple(int conflict)
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            Assert.True(context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).IsSuccess);
            MethodInfo method = typeof(RuntimeKernel).GetMethod("AppendResourceRecovery",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
            ProcessHandle owner = conflict == 0 ? commit.BudgetOwner with { Generation = commit.BudgetOwner.Generation + 1 } : commit.BudgetOwner;
            PlatformResourceCorrelation correlation = conflict == 1 ? commit.ProviderCorrelation with
            {
                Generation = new(commit.ProviderCorrelation.Generation.Value + 1)
            } : commit.ProviderCorrelation;
            ResourceEnvelopeV1 envelope = conflict == 2 ? commit.Envelope with { Amount = commit.Envelope.Amount + 1 } : commit.Envelope;
            IReadOnlyList<BudgetAmount> charged = conflict == 3 ? [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 1)] : [];
            KernelResult result = (KernelResult)method.Invoke(context.Kernel,
                [commit.Lease, owner, envelope, correlation, ResourceBudgetRecoveryTransition.CancelledPreSubmit, charged])!;
            Assert.Equal(KernelError.Quarantined, result.Error);
            Assert.Equal(2UL, Kernel(path).ResourceBudgetRecovery!.LastSequence);
            Assert.Empty(Kernel(path).ResourceBudgetRecovery!.ConservativeRecoveryCharge);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAppendFailureRetriesOnlyExactVerifiedClosure(bool frameWritten)
    {
        WithJournalStore((path, context, store) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            store.ThrowBeforeNextWrite = !frameWritten;
            store.ThrowAfterNextWrite = frameWritten;
            Assert.Equal(KernelError.Quarantined,
                context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).Error);
            Assert.Equal(BudgetReservationState.CancelledPreSubmit,
                context.Kernel.Budgets.Query(commit.Lease).Value!.State);
            ResourceBudgetRecoverySnapshot before = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(frameWritten ? ResourceBudgetRecoveryTransition.CancelledPreSubmit : ResourceBudgetRecoveryTransition.Prepared,
                Assert.Single(before.Items).LastPayload.Transition);
            if (frameWritten) Assert.Empty(before.ConservativeRecoveryCharge);
            else Assert.Equal(10UL, Assert.Single(before.ConservativeRecoveryCharge).Amount);
            Assert.True(context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).IsSuccess);
            ResourceBudgetRecoverySnapshot after = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(2UL, after.LastSequence);
            Assert.Empty(after.ConservativeRecoveryCharge);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedAppendFailureHasNoSubmitAndUsesVerifiedCleanupHistory(bool frameWritten)
    {
        WithJournalStore((path, context, store) =>
        {
            store.ThrowBeforeNextWrite = !frameWritten;
            store.ThrowAfterNextWrite = frameWritten;
            Assert.Equal(KernelError.Quarantined, Prepare(context).Error);
            ExternalOperationSnapshot owner = context.Kernel.QueryExternalOperation(context.Process, context.Operation).Value!;
            Assert.Null(owner.Binding);
            Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition);
            ExternalOperationResourceBinding binding = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            Assert.Equal(BudgetReservationState.CancelledPreSubmit, context.Kernel.Budgets.Query(binding.Lease).Value!.State);
            ResourceBudgetRecoverySnapshot recovered = Kernel(path).ResourceBudgetRecovery!;
            Assert.Empty(recovered.ConservativeRecoveryCharge);
            if (frameWritten)
                Assert.Equal(ResourceBudgetRecoveryTransition.CancelledPreSubmit, Assert.Single(recovered.Items).LastPayload.Transition);
            else Assert.Empty(recovered.Items);
        });
    }

    private static void WithJournalStore(Action<string, Context, CompleteFrameThenThrowStore> action)
        => WithJournal((path, _) =>
        {
            CompleteFrameThenThrowStore store = new CompleteFrameThenThrowStore(new FileResourceBudgetJournalStore(path));
            action(path, Create(new RuntimeKernel(null, null, new RuntimeKernelRecoveryOptions(path, Key), store)), store);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareFailureCleanupJournalsOnlyOwnerConfirmedCancellation(bool denyCancellation)
    {
        WithJournal((path, context) =>
        {
            BudgetReservationHandle lease = default;
            context.Kernel.ResourceAdmissionQualificationHook = new AdmissionHook(point =>
            {
                if (point != ResourceAdmissionQualificationPoint.AfterLocalCommit) return;
                lease = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.Lease;
                if (denyCancellation)
                    Assert.True(context.Kernel.Budgets.QuarantineLease(context.Process, lease).IsSuccess);
                throw new IOException("failure after prepared journal");
            });
            Assert.Equal(KernelError.PlatformFaulted, Prepare(context).Error);
            Assert.Equal(denyCancellation ? BudgetReservationState.Quarantined : BudgetReservationState.CancelledPreSubmit,
                context.Kernel.Budgets.Query(lease).Value!.State);
            Assert.Equal(denyCancellation ? ExternalResourceBindingState.Quarantined : ExternalResourceBindingState.CancelledPreSubmit,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            ResourceBudgetRecoverySnapshot recovery = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(denyCancellation ? ResourceBudgetRecoveryTransition.Quarantined : ResourceBudgetRecoveryTransition.CancelledPreSubmit,
                Assert.Single(recovery.Items).LastPayload.Transition);
            if (denyCancellation) Assert.Equal(10UL, Assert.Single(recovery.ConservativeRecoveryCharge).Amount);
            else Assert.Empty(recovery.ConservativeRecoveryCharge);
        });
    }

    private sealed class AdmissionHook(Action<ResourceAdmissionQualificationPoint> action) : IResourceAdmissionQualificationHook
    {
        public void At(ResourceAdmissionQualificationPoint point) => action(point);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeniedCompensationCannotJournalPreSubmitClosure(bool dispose)
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            Assert.True(context.Kernel.Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease).IsSuccess);
            if (dispose) commit.Dispose();
            else Assert.Equal(KernelError.Quarantined,
                context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
                    KernelResult.Ok, () => KernelResult.Fail(KernelError.PlatformDenied, "sentry denial")).Error);
            Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.Budgets.Query(commit.Lease).Value!.State);
            Assert.Equal(ExternalResourceBindingState.Quarantined,
                context.Kernel.ExternalOperations.QueryResourceBinding(commit.Operation).Value!.State);
            ResourceBudgetRecoveryItem backlog = Assert.Single(Kernel(path).ResourceBudgetRecovery!.ReconciliationBacklog);
            Assert.Equal(ResourceBudgetRecoveryTransition.Quarantined, backlog.LastPayload.Transition);
            Assert.Equal(10UL, Assert.Single(Kernel(path).ResourceBudgetRecovery!.ConservativeRecoveryCharge).Amount);
        });
    }

    [Fact]
    public void PossibleSubmitIsDurableBeforeCallbackAndColdRestartDoesNotResurrectLease()
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            bool callbackObserved = false;
            KernelResult<OperationBinding> submitted = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () =>
            {
                callbackObserved = true;
                ResourceBudgetRecoveryItem pending = Assert.Single(context.Kernel.ResourceBudgetRecovery!.ReconciliationBacklog);
                Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit, pending.LastPayload.Transition);
                return KernelResult.Fail(KernelError.PlatformFaulted, "ambiguous provider disconnect");
            });

            Assert.True(callbackObserved);
            Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
            Assert.Equal(BudgetReservationState.Quarantined,
                context.Kernel.Budgets.Query(commit.Lease).Value!.State);

            RuntimeKernel restarted = Kernel(path);
            ResourceBudgetRecoveryItem backlog = Assert.Single(restarted.ResourceBudgetRecovery!.ReconciliationBacklog);
            Assert.Equal(ResourceBudgetRecoveryTransition.Quarantined, backlog.LastPayload.Transition);
            Assert.Equal(KernelError.BudgetReservationNotFound, restarted.Budgets.Query(commit.Lease).Error);
            Assert.True(restarted.ResourceBudgetRecovery.BlocksFreshAdmission);
            Assert.False(restarted.Budgets.RecoveryImportComplete);
            Assert.True(restarted.Budgets.ConfigureSystem([
                new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
            Assert.True(restarted.Budgets.RecoveryImportComplete);
            Assert.Equal(10UL, restarted.Budgets.Query(restarted.Budgets.SystemBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        });
    }

    [Fact]
    public void ConservativeColdChargeCannotBeRefundedOrMultipliedByOldLeaseCopies()
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
                    () => KernelResult.Fail(KernelError.PlatformFaulted, "ambiguous")).Error);

            foreach (int restart in Enumerable.Range(0, 2))
            {
                RuntimeKernel kernel = Kernel(path);
                Assert.True(kernel.Budgets.ConfigureSystem([
                    new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
                BudgetAccountSnapshot root = kernel.Budgets.Query(kernel.Budgets.SystemBudget).Value!;
                Assert.Equal(10UL, root.Usage.Single(usage =>
                    usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
                Assert.Equal(KernelError.BudgetReservationNotFound, kernel.Budgets.Query(commit.Lease).Error);
            }

            RuntimeKernel insufficient = Kernel(path);
            Assert.Equal(KernelError.BudgetExceeded, insufficient.Budgets.ConfigureSystem([
                new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 9)]).Error);
            Assert.False(insufficient.Budgets.RecoveryImportComplete);
        });
    }

    [Fact]
    public void AbandonedPreparedAdmissionPersistsPreSubmitCancellationWithoutRecoveryBacklog()
    {
        WithJournal((path, context) =>
        {
            ResourceAdmissionCommit commit = Prepare(context).Value!;
            commit.Dispose();

            RuntimeKernel restarted = Kernel(path);
            ResourceBudgetRecoveryItem item = Assert.Single(restarted.ResourceBudgetRecovery!.Items);
            Assert.Equal(ResourceBudgetRecoveryTransition.CancelledPreSubmit, item.LastPayload.Transition);
            Assert.True(item.IsTerminal);
            Assert.Empty(restarted.ResourceBudgetRecovery.ReconciliationBacklog);
            Assert.False(restarted.ResourceBudgetRecovery.BlocksFreshAdmission);
        });
    }

    [Fact]
    public void CorruptColdJournalFailsKernelConstructionClosed()
    {
        WithJournal((path, context) =>
        {
            ResourceAdmissionCommit commit = Prepare(context).Value!;
            commit.Dispose();
            byte[] bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0x80;
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => Kernel(path));
        });
    }

    [Fact]
    public void JournalFailureAfterBudgetSettlementAndProviderLossKeepsColdChargeConservative()
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            OperationBinding binding = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            FileStream? blockedJournal = null;
            context.Kernel.ResourceSettlementAfterBudgetQualificationHook = () =>
            {
                Assert.True(context.Kernel.RecordExternalOperationProviderLoss(context.Process,
                    context.Operation).IsSuccess);
                blockedJournal = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None);
            };
            KernelResult<BudgetReservationSnapshot> settled;
            try
            {
                settled = context.Kernel.SettleResourceExternalOperation(context.Process,
                    Evidence(binding, 4));
            }
            finally
            {
                blockedJournal?.Dispose();
            }

            Assert.Equal(KernelError.Quarantined, settled.Error);
            Assert.Equal(BudgetReservationState.Released,
                context.Kernel.Budgets.Query(commit.Lease).Value!.State);
            Assert.Equal(ExternalResourceBindingState.Quarantined,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            Assert.Equal(ExternalOperationDisposition.ProviderLost,
                context.Kernel.QueryExternalOperation(context.Process, context.Operation).Value!.Disposition);
            RuntimeKernel restarted = Kernel(path);
            ResourceBudgetRecoveryItem item = Assert.Single(restarted.ResourceBudgetRecovery!.ReconciliationBacklog);
            Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit, item.LastPayload.Transition);
            Assert.Equal(10UL, Assert.Single(restarted.ResourceBudgetRecovery.ConservativeRecoveryCharge).Amount);
            Assert.True(restarted.ResourceBudgetRecovery.BlocksFreshAdmission);

            Assert.False(context.Kernel.ExternalOperations.QueryResourceBinding(
                context.Operation).Value!.SettlementInFlight);
            context.Kernel.ResourceSettlementAfterBudgetQualificationHook = null;
            Assert.Equal(KernelError.InvalidTransition,
                context.Kernel.SettleResourceExternalOperation(context.Process,
                    Evidence(binding, 5)).Error);
            KernelResult<BudgetReservationSnapshot> retried = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));
            Assert.True(retried.IsSuccess, retried.Message);
            Assert.Equal(ExternalResourceBindingState.Settled,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            Assert.Equal(ExternalOperationDisposition.ProviderLost,
                context.Kernel.QueryExternalOperation(context.Process, context.Operation).Value!.Disposition);
            Assert.Equal(4UL, context.Kernel.Budgets.Query(context.ProcessBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
            RuntimeKernel afterRetry = Kernel(path);
            ResourceBudgetRecoveryItem exact = Assert.Single(afterRetry.ResourceBudgetRecovery!.Items);
            Assert.Equal(ResourceBudgetRecoveryTransition.SettledExact, exact.LastPayload.Transition);
            Assert.Equal(4UL, Assert.Single(afterRetry.ResourceBudgetRecovery.ConservativeRecoveryCharge).Amount);
        });
    }

    [Fact]
    public void ExactRecoveryRecordFollowsBudgetOwnerCommit()
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            OperationBinding binding = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            context.Kernel.ResourceSettlementAfterBudgetQualificationHook = () =>
            {
                Assert.Equal(BudgetReservationState.Released,
                    context.Kernel.Budgets.Query(commit.Lease).Value!.State);
                Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit,
                    Assert.Single(context.Kernel.ResourceBudgetRecovery!.Items).LastPayload.Transition);
            };

            KernelResult<BudgetReservationSnapshot> settled = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));

            Assert.True(settled.IsSuccess, settled.Message);
            RuntimeKernel restarted = Kernel(path);
            ResourceBudgetRecoveryItem item = Assert.Single(restarted.ResourceBudgetRecovery!.Items);
            Assert.Equal(ResourceBudgetRecoveryTransition.SettledExact, item.LastPayload.Transition);
            Assert.Equal(4UL, Assert.Single(item.LastPayload.ChargedAmounts).Amount);
            Assert.Equal(4UL, Assert.Single(restarted.ResourceBudgetRecovery.ConservativeRecoveryCharge).Amount);
        });
    }

    [Fact]
    public void RetryAfterDurableTerminalRecordDoesNotAppendSecondReceipt()
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            OperationBinding binding = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            context.Kernel.ResourceSettlementAfterJournalQualificationHook = () =>
            {
                Assert.True(context.Kernel.ExternalOperations.CompleteResourceSettlement(
                    context.Operation, commit.Lease, success: false).IsSuccess);
                Assert.True(context.Kernel.RecordExternalOperationProviderLoss(context.Process,
                    context.Operation).IsSuccess);
                context.Kernel.ResourceSettlementAfterJournalQualificationHook = null;
            };

            KernelResult<BudgetReservationSnapshot> first = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));

            Assert.Equal(KernelError.StaleGeneration, first.Error);
            ResourceBudgetRecoverySnapshot before = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(ResourceBudgetRecoveryTransition.SettledExact,
                Assert.Single(before.Items).LastPayload.Transition);
            Assert.Equal(ExternalResourceBindingState.Quarantined,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            KernelResult<BudgetReservationSnapshot> second = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));
            Assert.True(second.IsSuccess, second.Message);
            ResourceBudgetRecoverySnapshot after = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(before.LastSequence, after.LastSequence);
            Assert.Equal(4UL, Assert.Single(after.ConservativeRecoveryCharge).Amount);
            Assert.Equal(ExternalResourceBindingState.Settled,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            Assert.Equal(ExternalOperationDisposition.ProviderLost,
                context.Kernel.QueryExternalOperation(context.Process, context.Operation).Value!.Disposition);
        });
    }

    [Fact]
    public void CompleteFileFrameWithLostAppendAcknowledgementRetriesExactReceipt()
    {
        string root = Path.Combine(Path.GetTempPath(), $"singnext-p16-ack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "budget.recovery");
            CompleteFrameThenThrowStore store = new CompleteFrameThenThrowStore(new FileResourceBudgetJournalStore(path));
            Context context = Create(new RuntimeKernel(null, null,
                new RuntimeKernelRecoveryOptions(path, Key), store));
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            OperationBinding binding = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            store.ThrowAfterNextWrite = true;

            KernelResult<BudgetReservationSnapshot> first = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));

            Assert.Equal(KernelError.Quarantined, first.Error);
            Assert.Equal(3UL, context.Kernel.ResourceBudgetRecovery!.LastSequence);
            Assert.Equal(ExternalResourceBindingState.Quarantined,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            Assert.False(context.Kernel.ExternalOperations.QueryResourceBinding(
                context.Operation).Value!.SettlementInFlight);
            KernelResult<BudgetReservationSnapshot> retry = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));
            Assert.True(retry.IsSuccess, retry.Message);
            Assert.Equal(3UL, context.Kernel.ResourceBudgetRecovery!.LastSequence);
            Assert.Equal(4UL, Assert.Single(Kernel(path).ResourceBudgetRecovery!
                .ConservativeRecoveryCharge).Amount);
            Assert.Equal(ExternalResourceBindingState.Settled,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LiveJournalRollbackCannotLowerObservedRecoveryCharge()
    {
        WithJournal((path, context) =>
        {
            using ResourceAdmissionCommit commit = Prepare(context).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
                    () => KernelResult.Fail(KernelError.PlatformFaulted, "ambiguous provider loss")).Error);
            Assert.Equal(10UL, Assert.Single(context.Kernel.ResourceBudgetRecovery!
                .ConservativeRecoveryCharge).Amount);
            File.WriteAllBytes(path, []);

            Assert.Throws<InvalidDataException>(() =>
                _ = context.Kernel.ResourceBudgetRecovery);
            Assert.Equal(10UL, context.Kernel.Budgets.Query(context.ProcessBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
            Assert.Equal(BudgetReservationState.Quarantined,
                context.Kernel.Budgets.Query(commit.Lease).Value!.State);
        });
    }

    private static void WithJournal(Action<string, Context> action)
    {
        string root = Path.Combine(Path.GetTempPath(), $"singnext-p16-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "budget.recovery");
            action(path, Create(Kernel(path)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeKernel Kernel(string path) =>
        new(null, null, new RuntimeKernelRecoveryOptions(path, Key));

    private static KernelResult<ResourceAdmissionCommit> Prepare(Context context) =>
        context.Kernel.PrepareResourceExternalAdmission(
            context.Process, context.EffectCapability, ResourceKind.Compute, "compute:effect", 1,
            context.ResourceGrant, 1, Envelope(10), context.Operation, context.Dependencies);

    private static Context Create(RuntimeKernel kernel)
    {
        ProcessHandle admin = TestFixtures.Create(kernel, 2900, 3900).Handle;
        ProcessHandle target = TestFixtures.Create(kernel, 2901, 3901).Handle;
        CapabilityId administration = kernel.MintCapability(new(3900), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        ProcessBudgetAdmission budget = kernel.AdmitProcessBudget(admin, administration, target, "p16-durable",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100),
             new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!;
        CapabilityId effect = kernel.MintCapability(new(3901), target, ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        CapabilityId grant = kernel.CapabilityAuthority.Mint(new(3901), new(3901), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, target.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(target, 16).Value!;
        ExternalOperationHandle operation = kernel.PrepareExternalOperation(target,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        return new(kernel, target, budget.ProcessBudget, effect, grant, operation, new(1, 1));
    }

    private static ResourceEnvelopeV1 Envelope(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");

    private static ExternalResourceUsageEvidence Evidence(OperationBinding binding, ulong consumed) =>
        new(1, binding, "host:compute-v1", 1,
            new(1, "host:compute-v1:measurement", "1", ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds),
            Guid.Parse("3927020a-e7e2-4b46-a044-0a93209ed7b5"),
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
            new(1, 0, 0, consumed, 0, 0, 0, 0, 0, 0), consumed, 1);

    private sealed record Context(
        RuntimeKernel Kernel,
        ProcessHandle Process,
        BudgetAccountHandle ProcessBudget,
        CapabilityId EffectCapability,
        CapabilityId ResourceGrant,
        ExternalOperationHandle Operation,
        OperationDependencySnapshot Dependencies);

    private sealed class CompleteFrameThenThrowStore(IResourceBudgetJournalStore inner)
        : IResourceBudgetJournalStore
    {
        internal bool ThrowAfterNextWrite { get; set; }
        internal bool ThrowBeforeNextWrite { get; set; }
        internal bool ThrowUnexpectedException { get; set; }
        internal Action? BeforeAppendFailure { get; set; }
        internal Action? AfterFrameWrite { get; set; }
        public IReadOnlyList<byte[]> ReadFrames() => inner.ReadFrames();
        public void AppendFrame(ReadOnlySpan<byte> frame)
        {
            if (ThrowBeforeNextWrite)
            {
                ThrowBeforeNextWrite = false;
                BeforeAppendFailure?.Invoke(); BeforeAppendFailure = null;
                if (ThrowUnexpectedException) throw new InvalidOperationException("Unexpected append rejection before file frame.");
                throw new IOException("Simulated append rejection before any file frame.");
            }
            inner.AppendFrame(frame);
            AfterFrameWrite?.Invoke();
            if (!ThrowAfterNextWrite) return;
            ThrowAfterNextWrite = false;
            BeforeAppendFailure?.Invoke(); BeforeAppendFailure = null;
            if (ThrowUnexpectedException) throw new InvalidOperationException("Unexpected lost append acknowledgement after full file frame.");
            throw new IOException("Simulated lost append acknowledgement after full file frame.");
        }
    }
}
