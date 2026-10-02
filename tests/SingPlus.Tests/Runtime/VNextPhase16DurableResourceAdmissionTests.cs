using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase16DurableResourceAdmissionTests
{
    private static readonly byte[] Key = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CancellationTerminalReplayRejectsConflictingExactTuple(int conflict)
    {
        WithJournal((path, context) =>
        {
            using var commit = Prepare(context).Value!;
            Assert.True(context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).IsSuccess);
            var method = typeof(RuntimeKernel).GetMethod("AppendResourceRecovery",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
            var owner = conflict == 0 ? commit.BudgetOwner with { Generation = commit.BudgetOwner.Generation + 1 } : commit.BudgetOwner;
            var correlation = conflict == 1 ? commit.ProviderCorrelation with
            {
                Generation = new(commit.ProviderCorrelation.Generation.Value + 1)
            } : commit.ProviderCorrelation;
            var envelope = conflict == 2 ? commit.Envelope with { Amount = commit.Envelope.Amount + 1 } : commit.Envelope;
            IReadOnlyList<BudgetAmount> charged = conflict == 3 ? [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 1)] : [];
            var result = (KernelResult)method.Invoke(context.Kernel,
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
            using var commit = Prepare(context).Value!;
            store.ThrowBeforeNextWrite = !frameWritten;
            store.ThrowAfterNextWrite = frameWritten;
            Assert.Equal(KernelError.Quarantined,
                context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).Error);
            Assert.Equal(BudgetReservationState.CancelledPreSubmit,
                context.Kernel.Budgets.Query(commit.Lease).Value!.State);
            var before = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(frameWritten ? ResourceBudgetRecoveryTransition.CancelledPreSubmit : ResourceBudgetRecoveryTransition.Prepared,
                Assert.Single(before.Items).LastPayload.Transition);
            if (frameWritten) Assert.Empty(before.ConservativeRecoveryCharge);
            else Assert.Equal(10UL, Assert.Single(before.ConservativeRecoveryCharge).Amount);
            Assert.True(context.Kernel.CompensateResourceAdmissionBeforeSubmit(commit).IsSuccess);
            var after = Kernel(path).ResourceBudgetRecovery!;
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
            var owner = context.Kernel.QueryExternalOperation(context.Process, context.Operation).Value!;
            Assert.Null(owner.Binding);
            Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition);
            var binding = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
            Assert.Equal(BudgetReservationState.CancelledPreSubmit, context.Kernel.Budgets.Query(binding.Lease).Value!.State);
            var recovered = Kernel(path).ResourceBudgetRecovery!;
            Assert.Empty(recovered.ConservativeRecoveryCharge);
            if (frameWritten)
                Assert.Equal(ResourceBudgetRecoveryTransition.CancelledPreSubmit, Assert.Single(recovered.Items).LastPayload.Transition);
            else Assert.Empty(recovered.Items);
        });
    }

    private static void WithJournalStore(Action<string, Context, CompleteFrameThenThrowStore> action)
        => WithJournal((path, _) =>
        {
            var store = new CompleteFrameThenThrowStore(new FileResourceBudgetJournalStore(path));
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
            var recovery = Kernel(path).ResourceBudgetRecovery!;
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
            using var commit = Prepare(context).Value!;
            Assert.True(context.Kernel.Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease).IsSuccess);
            if (dispose) commit.Dispose();
            else Assert.Equal(KernelError.Quarantined,
                context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
                    KernelResult.Ok, () => KernelResult.Fail(KernelError.PlatformDenied, "sentry denial")).Error);
            Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.Budgets.Query(commit.Lease).Value!.State);
            Assert.Equal(ExternalResourceBindingState.Quarantined,
                context.Kernel.ExternalOperations.QueryResourceBinding(commit.Operation).Value!.State);
            var backlog = Assert.Single(Kernel(path).ResourceBudgetRecovery!.ReconciliationBacklog);
            Assert.Equal(ResourceBudgetRecoveryTransition.Quarantined, backlog.LastPayload.Transition);
            Assert.Equal(10UL, Assert.Single(Kernel(path).ResourceBudgetRecovery!.ConservativeRecoveryCharge).Amount);
        });
    }

    [Fact]
    public void PossibleSubmitIsDurableBeforeCallbackAndColdRestartDoesNotResurrectLease()
    {
        WithJournal((path, context) =>
        {
            using var commit = Prepare(context).Value!;
            var callbackObserved = false;
            var submitted = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () =>
            {
                callbackObserved = true;
                var pending = Assert.Single(context.Kernel.ResourceBudgetRecovery!.ReconciliationBacklog);
                Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit, pending.LastPayload.Transition);
                return KernelResult.Fail(KernelError.PlatformFaulted, "ambiguous provider disconnect");
            });

            Assert.True(callbackObserved);
            Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
            Assert.Equal(BudgetReservationState.Quarantined,
                context.Kernel.Budgets.Query(commit.Lease).Value!.State);

            var restarted = Kernel(path);
            var backlog = Assert.Single(restarted.ResourceBudgetRecovery!.ReconciliationBacklog);
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
            using var commit = Prepare(context).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
                    () => KernelResult.Fail(KernelError.PlatformFaulted, "ambiguous")).Error);

            foreach (var restart in Enumerable.Range(0, 2))
            {
                var kernel = Kernel(path);
                Assert.True(kernel.Budgets.ConfigureSystem([
                    new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
                var root = kernel.Budgets.Query(kernel.Budgets.SystemBudget).Value!;
                Assert.Equal(10UL, root.Usage.Single(usage =>
                    usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
                Assert.Equal(KernelError.BudgetReservationNotFound, kernel.Budgets.Query(commit.Lease).Error);
            }

            var insufficient = Kernel(path);
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
            var commit = Prepare(context).Value!;
            commit.Dispose();

            var restarted = Kernel(path);
            var item = Assert.Single(restarted.ResourceBudgetRecovery!.Items);
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
            var commit = Prepare(context).Value!;
            commit.Dispose();
            var bytes = File.ReadAllBytes(path);
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
            using var commit = Prepare(context).Value!;
            var binding = context.Kernel.SubmitResourceExternalAdmission(commit,
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
            var restarted = Kernel(path);
            var item = Assert.Single(restarted.ResourceBudgetRecovery!.ReconciliationBacklog);
            Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit, item.LastPayload.Transition);
            Assert.Equal(10UL, Assert.Single(restarted.ResourceBudgetRecovery.ConservativeRecoveryCharge).Amount);
            Assert.True(restarted.ResourceBudgetRecovery.BlocksFreshAdmission);

            Assert.False(context.Kernel.ExternalOperations.QueryResourceBinding(
                context.Operation).Value!.SettlementInFlight);
            context.Kernel.ResourceSettlementAfterBudgetQualificationHook = null;
            Assert.Equal(KernelError.InvalidTransition,
                context.Kernel.SettleResourceExternalOperation(context.Process,
                    Evidence(binding, 5)).Error);
            var retried = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));
            Assert.True(retried.IsSuccess, retried.Message);
            Assert.Equal(ExternalResourceBindingState.Settled,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            Assert.Equal(ExternalOperationDisposition.ProviderLost,
                context.Kernel.QueryExternalOperation(context.Process, context.Operation).Value!.Disposition);
            Assert.Equal(4UL, context.Kernel.Budgets.Query(context.ProcessBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
            var afterRetry = Kernel(path);
            var exact = Assert.Single(afterRetry.ResourceBudgetRecovery!.Items);
            Assert.Equal(ResourceBudgetRecoveryTransition.SettledExact, exact.LastPayload.Transition);
            Assert.Equal(4UL, Assert.Single(afterRetry.ResourceBudgetRecovery.ConservativeRecoveryCharge).Amount);
        });
    }

    [Fact]
    public void ExactRecoveryRecordFollowsBudgetOwnerCommit()
    {
        WithJournal((path, context) =>
        {
            using var commit = Prepare(context).Value!;
            var binding = context.Kernel.SubmitResourceExternalAdmission(commit,
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

            var settled = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));

            Assert.True(settled.IsSuccess, settled.Message);
            var restarted = Kernel(path);
            var item = Assert.Single(restarted.ResourceBudgetRecovery!.Items);
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
            using var commit = Prepare(context).Value!;
            var binding = context.Kernel.SubmitResourceExternalAdmission(commit,
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

            var first = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));

            Assert.Equal(KernelError.StaleGeneration, first.Error);
            var before = Kernel(path).ResourceBudgetRecovery!;
            Assert.Equal(ResourceBudgetRecoveryTransition.SettledExact,
                Assert.Single(before.Items).LastPayload.Transition);
            Assert.Equal(ExternalResourceBindingState.Quarantined,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            var second = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));
            Assert.True(second.IsSuccess, second.Message);
            var after = Kernel(path).ResourceBudgetRecovery!;
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
        var root = Path.Combine(Path.GetTempPath(), $"singnext-p16-ack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "budget.recovery");
            var store = new CompleteFrameThenThrowStore(new FileResourceBudgetJournalStore(path));
            var context = Create(new RuntimeKernel(null, null,
                new RuntimeKernelRecoveryOptions(path, Key), store));
            using var commit = Prepare(context).Value!;
            var binding = context.Kernel.SubmitResourceExternalAdmission(commit,
                context.Dependencies, KernelResult.Ok).Value!;
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Process,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            store.ThrowAfterNextWrite = true;

            var first = context.Kernel.SettleResourceExternalOperation(context.Process,
                Evidence(binding, 4));

            Assert.Equal(KernelError.Quarantined, first.Error);
            Assert.Equal(3UL, context.Kernel.ResourceBudgetRecovery!.LastSequence);
            Assert.Equal(ExternalResourceBindingState.Quarantined,
                context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
            Assert.False(context.Kernel.ExternalOperations.QueryResourceBinding(
                context.Operation).Value!.SettlementInFlight);
            var retry = context.Kernel.SettleResourceExternalOperation(context.Process,
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
            using var commit = Prepare(context).Value!;
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
        var root = Path.Combine(Path.GetTempPath(), $"singnext-p16-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "budget.recovery");
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
        var admin = TestFixtures.Create(kernel, 2900, 3900).Handle;
        var target = TestFixtures.Create(kernel, 2901, 3901).Handle;
        var administration = kernel.MintCapability(new(3900), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, target, "p16-durable",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100),
             new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!;
        var effect = kernel.MintCapability(new(3901), target, ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(3901), new(3901), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, target.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var region = kernel.AllocateBuffer<byte>(target, 16).Value!;
        var operation = kernel.PrepareExternalOperation(target,
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
        public IReadOnlyList<byte[]> ReadFrames() => inner.ReadFrames();
        public void AppendFrame(ReadOnlySpan<byte> frame)
        {
            if (ThrowBeforeNextWrite)
            {
                ThrowBeforeNextWrite = false;
                throw new IOException("Simulated append rejection before any file frame.");
            }
            inner.AppendFrame(frame);
            if (!ThrowAfterNextWrite) return;
            ThrowAfterNextWrite = false;
            throw new IOException("Simulated lost append acknowledgement after full file frame.");
        }
    }
}
