using System.Text;
using System.Security.Cryptography;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class V6MemoryRuntimeEnforcementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedPreSubmitAccountingCompensationRetainsLiveMarkerAndBudgetPin(int phase)
    {
        var c = Create();
        using var commit = c.Commit;
        var sink = new CollectingTraceSink();
        var calls = 0;
        var runtime = new Runtime(c.BaseBinding, () =>
        {
            if (phase == 1) Assert.True(c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess);
            if (phase == 2)
            {
                Assert.True(c.Kernel.CancelExternalOperation(c.Principal, c.Operation, false).IsSuccess);
                Assert.True(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
            }
            Assert.True(c.Kernel.Budgets.QuarantineLease(c.Commit.BudgetOwner, c.Lease).IsSuccess);
        });
        Assert.Equal(KernelError.Quarantined, Submit(c, () => 1, runtime,
            () => { calls++; return KernelResult.Ok(); }, sink).Error);
        Assert.Equal(0, calls);
        Assert.Contains(sink.Events, item => item.Kind == SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit);
        Assert.Contains(sink.Events, item => item.Kind == SemanticTraceEventKindV1.CancelledBeforeSubmit);
        if (phase == 2) Assert.Equal(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit, sink.Events[^1].Kind);
        else Assert.DoesNotContain(sink.Events, item => item.Kind == SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Lease).Value!.State);
        Assert.Equal(ExternalResourceBindingState.Quarantined, c.Kernel.ExternalOperations.QueryResourceBinding(c.Operation).Value!.State);
        var beforeRetry = c.Kernel.ExternalOperations.Query(c.Operation).Value!;
        Assert.Equal(KernelError.Quarantined, c.Kernel.CompensateResourceAdmissionBeforeSubmit(c.Commit).Error);
        Assert.Equal(beforeRetry.Transitions, c.Kernel.ExternalOperations.Query(c.Operation).Value!.Transitions);
        Assert.True(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
        var released = c.Kernel.ExternalOperations.Query(c.Operation).Value!;
        Assert.Equal(ExternalEffectBoundaryState.NotCrossed, released.EffectBoundary);
        Assert.Null(released.Binding);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Lease).Value!.State);
        Assert.Equal(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released,
            c.MemoryBinding.ExtensionBinding.Digest.Value), sink.Events);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.DoesNotContain(sink.Events, item => item.Kind is SemanticTraceEventKindV1.Submit or
            SemanticTraceEventKindV1.EffectPossible or SemanticTraceEventKindV1.Settled or
            SemanticTraceEventKindV1.EffectClosedWithoutPublication or SemanticTraceEventKindV1.Released);
        Assert.True(c.Kernel.TerminateProcess(c.Principal).IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.Budgets.Query(c.Lease).Value!.State);
        Assert.Equal(ExternalResourceBindingState.Quarantined,
            c.Kernel.ExternalOperations.QueryResourceBinding(c.Operation).Value!.State);
    }

    [Fact]
    public void ActualPreSubmitAccountingHistoryBeforeSubmitRetainsPossibleEffectAndRejectsPhaseSubstitution()
    {
        var c = Create();
        using var commit = c.Commit;
        Assert.True(c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess);
        var binding = c.Kernel.RecordExternalOperationSubmission(c.Principal, c.Operation, c.Dependencies);
        Assert.True(binding.IsSuccess);
        var snapshot = c.Kernel.ExternalOperations.Query(c.Operation).Value!;
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, c.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.Equal([SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit,
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible], trace.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        var changed = trace.ToArray();
        changed[0] = changed[0] with { Kind = SemanticTraceEventKindV1.ResourceAccountingQuarantined };
        Assert.False(SemanticTraceValidatorV1.Validate(changed).IsValid);
        Assert.Equal(ExternalResourceBindingState.Quarantined, c.Kernel.ExternalOperations.QueryResourceBinding(c.Operation).Value!.State);
        Assert.False(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(true, false)).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedPreSubmitMemoryTraceRetainsCancellationUntilActualLocalRelease(int fault)
    {
        var c = Create();
        using var commit = c.Commit;
        ulong generation = 1;
        var callbacks = 0;
        var sink = new CollectingTraceSink();
        var runtime = new Runtime(c.BaseBinding, () =>
        {
            if (fault == 0) generation = 2;
            else if (fault == 1) Assert.True(c.Kernel.CancelExternalOperation(c.Principal, c.Operation, false).IsSuccess);
            else throw new IOException("runtime decision lost before submit");
        });
        Assert.False(Submit(c, () => generation, runtime,
            () => { callbacks++; return KernelResult.Ok(); }, sink).IsSuccess);
        Assert.Equal(0, callbacks);
        Assert.Equal(SemanticTraceEventKindV1.CancelledBeforeSubmit, Assert.Single(sink.Events).Kind);
        Assert.Equal(c.MemoryBinding.ExtensionBinding.Digest.Value, sink.Events[0].GenerationVectorDigest);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.Budgets.Query(c.Lease).Value!.State);
        Assert.True(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
        Assert.Equal([SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit], sink.Events.Select(item => item.Kind));
        Assert.True(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!, c.MemoryBinding.ExtensionBinding.Digest.Value), sink.Events);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void PreSubmitTraceReentrantTeardownDrainsCommittedLocalReleaseOutsideOwnerLocks()
    {
        var c = Create();
        using var commit = c.Commit;
        var sink = new PreSubmitTraceSink(() => Assert.True(c.Kernel.TerminateProcess(c.Principal).IsSuccess));
        Assert.False(Submit(c, () => 2, new Runtime(c.BaseBinding), KernelResult.Ok, sink).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, c.Kernel.ExternalOperations.Query(c.Operation).Value!.State);
        Assert.Equal([SemanticTraceEventKindV1.CancelledBeforeSubmit, SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit], sink.Events.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPreSubmitSinkCannotVetoCompensationOrLocalRelease(bool throws)
    {
        var c = Create();
        using var commit = c.Commit;
        var sink = new FailingTraceSink(1, throws);
        Assert.Equal(KernelError.StaleGeneration,
            Submit(c, () => 2, new Runtime(c.BaseBinding), KernelResult.Ok, sink).Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.Budgets.Query(c.Lease).Value!.State);
        Assert.True(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
        Assert.Empty(sink.Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPreSubmitTraceRejectsUnknownOrForeignCapturedSidecar(bool foreign)
    {
        var c = Create();
        using var commit = c.Commit;
        var sink = new CollectingTraceSink();
        var invalid = foreign ? c.MemoryBinding with { BaseBindingDigest = new(new string('f', 64)) }
            : c.MemoryBinding with { Version = 99 };
        Assert.False(c.Kernel.SubmitV6MemorySemanticResourceExternalAdmission(commit, c.Dependencies,
            c.BaseBinding, invalid, c.Obligations, c.Guarantees, c.Refinement, ProviderIdentity, () => 1,
            c.ProviderGenerations, new Provider(c.BaseBinding), new Runtime(c.BaseBinding), KernelResult.Ok, sink).IsSuccess);
        Assert.Empty(sink.Events);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.Budgets.Query(c.Lease).Value!.State);
    }

    [Fact]
    public void LosingPreSubmitAttemptCannotStealSubmitWinnersTraceOrRefundBudget()
    {
        var c = Create();
        using var commit = c.Commit;
        var winner = new CollectingTraceSink();
        var loser = new CollectingTraceSink();
        var calls = 0;
        var runtime = new Runtime(c.BaseBinding, () => Assert.True(Submit(c, () => 1,
            new Runtime(c.BaseBinding), () => { calls++; return KernelResult.Ok(); }, winner).IsSuccess));
        Assert.Equal(KernelError.InvalidTransition, Submit(c, () => 1, runtime, KernelResult.Ok, loser).Error);
        Assert.Equal(1, calls);
        Assert.Empty(loser.Events);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible], winner.Events.Select(item => item.Kind));
        Assert.Equal(BudgetReservationState.Consuming, c.Kernel.Budgets.Query(c.Lease).Value!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AccountingQuarantineReconciliationPreservesLivePublicationAndExactSettlement(int phase)
    {
        var c = Create();
        using var commit = c.Commit;
        var sink = new CollectingTraceSink();
        var binding = Submit(c, () => 1, new Runtime(c.BaseBinding), KernelResult.Ok, sink).Value!;
        if (phase == 2) _ = CompleteVisibleAndPublish(c, binding);
        Assert.True(c.Kernel.Budgets.QuarantineLease(c.Principal, c.Lease).IsSuccess);
        Assert.True(c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess);
        var usage = UsageEvidence(binding, "7179ce5f-b971-4fc2-a82f-a0012cf3bdc1");
        if (phase != 2)
        {
            Assert.True(c.Kernel.RecordExternalOperationCompletion(c.Principal,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            if (phase == 1) Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal, usage).IsSuccess);
            Assert.True(c.Kernel.RecordExternalOperationVisibility(c.Principal,
                new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
            Assert.True(c.Kernel.PublishExternalOperation(c.Principal, c.Operation, c.Dependencies,
                new(ExternalPublicationPolicy.Staged), static () => { }).IsSuccess);
        }
        if (phase != 1) Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal, usage).IsSuccess);
        Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal, usage).IsSuccess);
        var snapshot = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        Assert.Equal(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            c.MemoryBinding.ExtensionBinding.Digest.Value), sink.Events);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Single(sink.Events, e => e.Kind == SemanticTraceEventKindV1.ResourceAccountingQuarantined);
        Assert.Single(sink.Events, e => e.Kind == SemanticTraceEventKindV1.Settled);
        Assert.Contains(sink.Events, e => e.Kind == SemanticTraceEventKindV1.Published);
        Assert.DoesNotContain(sink.Events, e => e.Kind is SemanticTraceEventKindV1.Quarantined or SemanticTraceEventKindV1.Released);
        Assert.True(c.Kernel.Budgets.IsExactSettledExternalLease(c.Principal, c.Lease, usage.ConsumedAmount));
        Assert.Equal(ExternalResourceBindingState.Settled,
            c.Kernel.ExternalOperations.QueryResourceBinding(c.Operation).Value!.State);
        Assert.False(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
    }

    [Fact]
    public void JournalFailureAfterAccountingSettlementRetainsObservationUntilExactKernelRetry()
    {
        using var store = new BlockingSubmitJournalStore();
        var c = Create(journalStore: store);
        using var commit = c.Commit;
        var sink = new CollectingTraceSink();
        var calls = 0;
        var binding = Submit(c, () => 1, new Runtime(c.BaseBinding),
            () => { calls++; return KernelResult.Ok(); }, sink).Value!;
        _ = CompleteVisibleAndPublish(c, binding);
        var usage = UsageEvidence(binding, "090d753e-dffa-4bf8-93a4-9dc692043e5a");
        store.BeforeNextAppend = () => throw new IOException("Managed settlement journal fault injection.");
        Assert.Equal(KernelError.Quarantined, c.Kernel.SettleResourceExternalOperation(c.Principal, usage).Error);
        Assert.True(c.Kernel.Budgets.IsExactSettledExternalLease(c.Principal, c.Lease, usage.ConsumedAmount));
        Assert.Equal(ExternalResourceBindingState.Quarantined,
            c.Kernel.ExternalOperations.QueryResourceBinding(c.Operation).Value!.State);
        Assert.Equal(SemanticTraceEventKindV1.ResourceAccountingQuarantined, sink.Events[^1].Kind);
        Assert.DoesNotContain(sink.Events, e => e.Kind == SemanticTraceEventKindV1.Settled);
        Assert.False(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
        Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal, usage).IsSuccess);
        Assert.Equal(1, calls);
        Assert.Equal(SemanticTraceEventKindV1.Settled, sink.Events[^1].Kind);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!,
            c.MemoryBinding.ExtensionBinding.Digest.Value), sink.Events);
        Assert.False(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void ActualDirectCancellationRetainsIrreversibleFaultAndRejectsImpossibleHistory(bool visible, int mutation)
    {
        var kernel = new RuntimeKernel();
        var principal = TestFixtures.Create(kernel, 7202, 8202).Handle;
        var output = kernel.AllocateBuffer<byte>(principal, 8).Value!;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(output.Handle, RegionUseMode.ExclusiveWrite, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.DirectCoherent,
            new(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true)).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1, 1);
        Assert.True(kernel.AdmitExternalOperation(principal, operation, dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(principal, operation, dependencies).Value!;
        Assert.True(kernel.RecordExternalOperationCompletion(principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        if (visible)
            Assert.True(kernel.RecordExternalOperationVisibility(principal,
                new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var before = kernel.QueryExternalOperation(principal, operation).Value!;
        Assert.False(kernel.CancelExternalOperation(principal,
            operation with { Generation = new(operation.Generation.Value + 1) }, false).IsSuccess);
        Assert.Equal(before.Transitions, kernel.QueryExternalOperation(principal, operation).Value!.Transitions);
        var cancelled = kernel.CancelExternalOperation(principal, operation, false).Value!;
        Assert.Equal(ExternalOperationDisposition.Faulted, cancelled.Disposition);
        Assert.Equal(ExternalEffectBoundaryState.Irreversible, cancelled.EffectBoundary);
        Assert.Contains(cancelled.Transitions, t => t.Event == "DirectWriteCannotBeUndone");
        Assert.True(kernel.CancelExternalOperation(principal, operation, true).IsSuccess);
        Assert.False(kernel.ReleaseExternalOperation(principal, operation, new(true, false)).IsSuccess);
        Assert.Equal(cancelled.Transitions, kernel.QueryExternalOperation(principal, operation).Value!.Transitions);
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(cancelled, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(SemanticTraceEventKindV1.Quarantined, trace[^1].Kind);
        Assert.DoesNotContain(trace, e => e.Kind is SemanticTraceEventKindV1.Published or
            SemanticTraceEventKindV1.EffectClosedWithoutPublication or SemanticTraceEventKindV1.Released);
        if (mutation == 0) return;
        var changed = mutation == 1
            ? cancelled with { PublicationPolicy = ExternalPublicationPolicy.Staged }
            : cancelled with { Transitions = cancelled.Transitions.Append(new ExternalOperationTransition(
                cancelled.Transitions[^1].Sequence + 1, cancelled.State, cancelled.State, "DirectWriteCannotBeUndone")).ToArray() };
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(changed, new string('a', 64)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualDirectPublicationObservationRequiresExactPolicyWriter(int mutation)
    {
        var kernel = new RuntimeKernel();
        var principal = TestFixtures.Create(kernel, 7201, 8201).Handle;
        var output = kernel.AllocateBuffer<byte>(principal, 8).Value!;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(output.Handle, RegionUseMode.ExclusiveWrite, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.DirectCoherent,
            new(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true)).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1, 1);
        Assert.True(kernel.AdmitExternalOperation(principal, operation, dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(principal, operation, dependencies).Value!;
        Assert.Equal(ExternalEffectBoundaryState.Irreversible,
            kernel.QueryExternalOperation(principal, operation).Value!.EffectBoundary);
        Assert.True(kernel.RecordExternalOperationCompletion(principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(principal,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var calls = 0;
        var published = kernel.PublishExternalOperation(principal, operation, dependencies,
            new(ExternalPublicationPolicy.DirectCoherent), () => calls++);
        Assert.True(published.IsSuccess);
        Assert.Equal(0, calls); // The existing direct path observes publication; no withheld callback.
        var snapshot = published.Value!;
        Assert.Contains(snapshot.Transitions, t => t.Event == "DirectPublicationObserved");
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published }, trace.Select(e => e.Kind));
        Assert.False(kernel.ReleaseExternalOperation(principal, operation, new(false, false)).IsSuccess);
        Assert.Equal(snapshot.Transitions, kernel.QueryExternalOperation(principal, operation).Value!.Transitions);
        if (mutation == 0) return;
        var changed = mutation == 1
            ? snapshot with { Transitions = snapshot.Transitions.Select(t =>
                t.Event == "DirectPublicationObserved" ? t with { Event = "Published" } : t).ToArray() }
            : snapshot with { PublicationPolicy = ExternalPublicationPolicy.Staged };
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(changed, new string('a', 64)));
    }

    [Theory]
    [InlineData(ExternalOperationCompletionDisposition.Cancelled, false)]
    [InlineData(ExternalOperationCompletionDisposition.Cancelled, true)]
    [InlineData(ExternalOperationCompletionDisposition.Faulted, false)]
    [InlineData(ExternalOperationCompletionDisposition.Faulted, true)]
    public void TerminalCompletionPreservesQuarantineAndExactSettlementInLiveTrace(
        ExternalOperationCompletionDisposition disposition, bool priorQuarantine)
    {
        var c = Create();
        using var commit = c.Commit;
        var sink = new CollectingTraceSink();
        var binding = Submit(c, () => 1, new Runtime(c.BaseBinding), KernelResult.Ok, sink).Value!;
        if (priorQuarantine)
            Assert.True(c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess);
        var before = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        Assert.False(c.Kernel.RecordExternalOperationCompletion(c.Principal,
            new(binding with { Generation = binding.Generation + 1 }, disposition)).IsSuccess);
        Assert.Equal(before.Transitions, c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!.Transitions);
        Assert.True(c.Kernel.RecordExternalOperationCompletion(c.Principal, new(binding, disposition)).IsSuccess);
        var after = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        Assert.False(c.Kernel.RecordExternalOperationCompletion(c.Principal, new(binding, disposition)).IsSuccess);
        Assert.False(c.Kernel.RecordExternalOperationVisibility(c.Principal,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.Equal(after.Transitions, c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!.Transitions);
        var evidence = UsageEvidence(binding, "17e35c1f-a3ad-4720-9215-d45859abf59f");
        Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal, evidence).IsSuccess);
        Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal, evidence).IsSuccess);
        var snapshot = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        var projected = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            c.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.Equal(projected, sink.Events);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(priorQuarantine
            ? new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.ResourceAccountingQuarantined, SemanticTraceEventKindV1.RetireOrComplete,
                SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Settled }
            : new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Settled },
            sink.Events.Select(e => e.Kind));
        Assert.DoesNotContain(sink.Events, e => e.Kind is SemanticTraceEventKindV1.Published or
            SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceQuarantineRetainsActualLateCompletionAndSettlement(bool settle)
    {
        var c = Create();
        using var commit = c.Commit;
        var binding = Submit(c, () => 1, new Runtime(c.BaseBinding), KernelResult.Ok).Value!;
        Assert.True(c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess);
        Assert.True(c.Kernel.RecordExternalOperationCompletion(c.Principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        if (settle)
            Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal,
                UsageEvidence(binding, "cc7f78ae-05f5-44f2-b4bf-5194a3712501")).IsSuccess);
        var snapshot = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            c.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(settle
            ? new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.ResourceAccountingQuarantined, SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Settled }
            : new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.ResourceAccountingQuarantined, SemanticTraceEventKindV1.RetireOrComplete }, trace.Select(e => e.Kind));
        Assert.DoesNotContain(trace, e => e.Kind is SemanticTraceEventKindV1.Visible or
            SemanticTraceEventKindV1.Published or SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
    }

    [Theory]
    [InlineData("ResourceQuarantined")]
    [InlineData("ResourceSettlementQuarantined")]
    [InlineData("ResourceSettled")]
    public void ResourceProjectionRequiresRealPrecedingAssociation(string ownerEvent)
    {
        var c = Create();
        using var commit = c.Commit;
        var binding = Submit(c, () => 1, new Runtime(c.BaseBinding), KernelResult.Ok).Value!;
        _ = CompleteVisibleAndPublish(c, binding);
        var evidence = UsageEvidence(binding, "a43e78be-ef53-45e3-a462-4c06d9ef7785");
        if (ownerEvent == "ResourceSettled")
            Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal, evidence).IsSuccess);
        else if (ownerEvent == "ResourceQuarantined")
            Assert.True(c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess);
        else
        {
            Assert.True(c.Kernel.ExternalOperations.BeginResourceSettlement(evidence).IsSuccess);
            Assert.True(c.Kernel.ExternalOperations.CompleteResourceSettlement(c.Operation, commit.Lease, false).IsSuccess);
        }
        var original = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        Assert.Contains(original.Transitions, item => item.Event == ownerEvent);
        var valid = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(original, c.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.True(SemanticTraceValidatorV1.Validate(valid).IsValid);
        Assert.DoesNotContain(valid, item => item.Kind == SemanticTraceEventKindV1.Released);
        var changed = original with { Transitions = original.Transitions.Where(item => item.Event != "ResourceLeaseBound")
            .Select((item, index) => item with { Sequence = (ulong)index + 1 }).ToArray() };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, c.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceProjectionRejectsImpossibleSettlementOrTerminalQuarantine(bool afterSettlement)
    {
        var c = Create();
        using var commit = c.Commit;
        var binding = Submit(c, () => 1, new Runtime(c.BaseBinding), KernelResult.Ok).Value!;
        if (afterSettlement)
        {
            _ = CompleteVisibleAndPublish(c, binding);
            Assert.True(c.Kernel.SettleResourceExternalOperation(c.Principal,
                UsageEvidence(binding, "0c9397ba-7d19-41d3-9728-d5d8acac4d85")).IsSuccess);
            Assert.Equal(KernelError.InvalidTransition, c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).Error);
        }
        var original = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        var changed = original with { Transitions = original.Transitions.Append(new ExternalOperationTransition(
            original.Transitions[^1].Sequence + 1, original.State, original.State,
            afterSettlement ? "ResourceQuarantined" : "ResourceSettled")).ToArray() };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, c.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PreSubmitProjectionRejectsImpossibleResourceWriterHistory(int mutation)
    {
        var c = Create();
        using var commit = c.Commit;
        var original = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!;
        Assert.Empty(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(original, c.MemoryBinding.ExtensionBinding.Digest.Value));
        var history = original.Transitions.ToList();
        if (mutation == 1)
            history.Add(new((ulong)history.Count + 1, original.State, original.State, "CancelledBeforeSubmit"));
        history.Add(new((ulong)history.Count + 1, original.State, original.State,
            mutation == 2 ? "ResourceCancelledBeforeSubmit" : "ResourceLeaseBound"));
        var changed = original with { Transitions = history.ToArray(),
            Disposition = mutation == 1 ? ExternalOperationDisposition.Cancelled : original.Disposition };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, c.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ActualPreSubmitTeardownProjectsCancellationAndLocalRelease(bool admitted, bool alreadyCancelled)
    {
        var kernel = new RuntimeKernel();
        var (_, principal) = TestFixtures.Create(kernel, 7101, 8101);
        var input = kernel.AllocateBuffer<byte>(principal, 8).Value!;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        if (admitted) Assert.True(kernel.AdmitExternalOperation(principal, operation, new(1, 1, 1)).IsSuccess);
        if (alreadyCancelled) Assert.True(kernel.CancelExternalOperation(principal, operation, false).IsSuccess);
        Assert.True(kernel.TerminateProcess(principal).IsSuccess);
        var snapshot = kernel.ExternalOperations.Query(operation).Value!;
        Assert.Equal(ExternalOperationState.Released, snapshot.State);
        Assert.Equal(ExternalEffectBoundaryState.NotCrossed, snapshot.EffectBoundary);
        Assert.Contains(snapshot.Transitions, item => item.Event == "TeardownCancelledBeforeSubmit");
        Assert.DoesNotContain(snapshot.Transitions, item => item.Event == "Submitted");
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(alreadyCancelled ? 3 : 2, trace.Count);
        Assert.All(trace.Take(trace.Count - 1), item => Assert.Equal(SemanticTraceEventKindV1.CancelledBeforeSubmit, item.Kind));
        Assert.Equal(SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit, trace[^1].Kind);
        Assert.All(trace, item => Assert.False(item.AuthorizesExecution));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualPreSubmitCancellationReleaseRejectsStaleRetryWithoutInventingClosure(bool admitted)
    {
        var kernel = new RuntimeKernel();
        var (_, principal) = TestFixtures.Create(kernel, 7103, 8103);
        var input = kernel.AllocateBuffer<byte>(principal, 8).Value!;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        if (admitted) Assert.True(kernel.AdmitExternalOperation(principal, operation, new(1, 1, 1)).IsSuccess);
        Assert.True(kernel.CancelExternalOperation(principal, operation, false).IsSuccess);
        var cancelled = kernel.QueryExternalOperation(principal, operation).Value!;
        var prefix = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(cancelled, new string('a', 64));
        Assert.Equal(SemanticTraceEventKindV1.CancelledBeforeSubmit, Assert.Single(prefix).Kind);
        Assert.Equal(KernelError.InvalidTransition, kernel.ExternalOperations.RecordSubmission(operation, new(1, 1, 1)).Error);
        var stale = operation with { Generation = new OperationGeneration(operation.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, kernel.ReleaseExternalOperation(principal, stale, new(false, false)).Error);
        Assert.Equal(cancelled.Transitions, kernel.QueryExternalOperation(principal, operation).Value!.Transitions);
        Assert.True(kernel.ReleaseExternalOperation(principal, operation, new(false, false)).IsSuccess);
        var released = kernel.QueryExternalOperation(principal, operation).Value!;
        Assert.True(kernel.ReleaseExternalOperation(principal, operation, new(false, false)).IsSuccess);
        Assert.Equal(released.Transitions, kernel.QueryExternalOperation(principal, operation).Value!.Transitions);
        Assert.Null(released.Binding);
        Assert.Equal(ExternalEffectBoundaryState.NotCrossed, released.EffectBoundary);
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released, new string('a', 64));
        Assert.Equal([SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit], trace.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Submit or
            SemanticTraceEventKindV1.EffectPossible or SemanticTraceEventKindV1.Settled or
            SemanticTraceEventKindV1.EffectClosedWithoutPublication or SemanticTraceEventKindV1.Released);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EmptyPreSubmitPrefixCannotHideContradictoryOwnerFacts(int mutation)
    {
        var kernel = new RuntimeKernel();
        var (_, principal) = TestFixtures.Create(kernel, 7102, 8102);
        var input = kernel.AllocateBuffer<byte>(principal, 8).Value!;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        Assert.True(kernel.TerminateProcess(principal).IsSuccess);
        var snapshot = kernel.ExternalOperations.Query(operation).Value!;
        snapshot = mutation switch
        {
            0 => snapshot with { Transitions = snapshot.Transitions.Where(item => item.Event != "TeardownCancelledBeforeSubmit")
                .Select((item, index) => item with { Sequence = (ulong)index + 1 }).ToArray() },
            1 => snapshot with { EffectBoundary = ExternalEffectBoundaryState.ExternallyVisible },
            _ => snapshot with { Disposition = ExternalOperationDisposition.Active },
        };
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedTeardownTraceDeliveryCannotChangeDrainOrReclaim(bool throws)
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new FailingTraceSink(3, throws);
        Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok, sink).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, context.Kernel.TerminateProcess(context.Principal).Error);
        Assert.True(context.Kernel.ObserveProcessTeardown(context.Principal).IsSuccess);
        var snapshot = context.Kernel.QueryProcessTeardown(context.Principal).Value;
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, snapshot.Phase);
        Assert.False(snapshot.LocalReclaimCompleted);
        Assert.Equal(ExternalOperationDisposition.CancellationPending,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
        Assert.Equal(3, sink.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessTeardownDeliversCommittedDrainOrDiscardOutsideTeardownLock(bool completed)
    {
        var context = Create();
        using var commit = context.Commit;
        var queries = 0;
        var sink = new TeardownTraceSink(() =>
        {
            var query = Task.Run(() => context.Kernel.QueryProcessTeardown(context.Principal));
            Assert.True(query.Wait(TimeSpan.FromSeconds(5)), "Observation callback retained teardown lock.");
            Assert.True(query.Result.IsSuccess);
            queries++;
            Assert.True(context.Kernel.ObserveProcessTeardown(context.Principal).IsSuccess);
        });
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok, sink).Value!;
        if (completed)
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, context.Kernel.TerminateProcess(context.Principal).Error);
        Assert.Equal(1, queries);
        var snapshot = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var projected = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.Equal(projected, sink.Events);
        Assert.Equal(completed ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.CancellationRequested,
            sink.Events[^1].Kind);
        Assert.False(context.Kernel.QueryProcessTeardown(context.Principal).Value.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TeardownProjectionObservesDrainOrDiscardWithoutClosure(bool completed)
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        if (completed)
            Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        var owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Principal;
        Assert.Equal(KernelError.PlatformBindingDraining, context.Kernel.ExternalOperations.AdvanceForTeardown(owner).Error);
        Assert.Equal(KernelError.PlatformBindingDraining, context.Kernel.ExternalOperations.AdvanceForTeardown(owner).Error);
        var snapshot = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(completed ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.CancellationRequested,
            trace[^1].Kind);
        Assert.Single(trace.Where(item => item.Kind == (completed
            ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.CancellationRequested)));
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.EffectClosedWithoutPublication or
            SemanticTraceEventKindV1.Released);
        Assert.NotEqual(ExternalOperationState.Released, snapshot.State);
    }
    [Theory]
    [InlineData("Admitted")]
    [InlineData("Submitted")]
    public void ProjectionRejectsActiveOwnerCommitAfterRecordedPreSubmitCancellation(string commitEvent)
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        var published = CompleteVisibleAndPublish(context, binding);
        var history = published.Transitions.ToList();
        var index = history.FindIndex(item => item.Event == commitEvent);
        Assert.True(index > 0);
        var state = history[index].From;
        history.Insert(index, new ExternalOperationTransition(0, state, state, "CancelledBeforeSubmit"));
        var changed = published with { Transitions = history.Select((item, offset) =>
            item with { Sequence = (ulong)offset + 1 }).ToArray() };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.True(SemanticTraceValidatorV1.Validate(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            published, context.MemoryBinding.ExtensionBinding.Digest.Value)).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ProjectionRejectsKnownSnapshotFactsContradictingOwnerHistory(int mutation)
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        var submitted = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var published = CompleteVisibleAndPublish(context, binding);
        var changed = mutation switch
        {
            0 => submitted with { Disposition = ExternalOperationDisposition.Published },
            1 => submitted with { EffectBoundary = ExternalEffectBoundaryState.NotCrossed },
            2 => published with { Disposition = ExternalOperationDisposition.Completed },
            3 => published with { EffectBoundary = ExternalEffectBoundaryState.StagedPending },
            4 => published with { Disposition = ExternalOperationDisposition.Discarded },
            _ => published with { EffectBoundary = ExternalEffectBoundaryState.PossiblyExternallyVisible }
        };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.True(SemanticTraceValidatorV1.Validate(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            published, context.MemoryBinding.ExtensionBinding.Digest.Value)).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectionRejectsPreSubmitSnapshotFactsWithoutOwnerEffect(bool boundary)
    {
        var context = Create();
        using var commit = context.Commit;
        var original = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var changed = boundary
            ? original with { EffectBoundary = ExternalEffectBoundaryState.StagedPending }
            : original with { Disposition = ExternalOperationDisposition.Cancelled };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.Empty(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(original,
            context.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Fact]
    public void ProjectionRejectsPublicationFailureEventWithoutExistingOwnerWriter()
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var original = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var changed = original with { Transitions = original.Transitions.Append(
            new ExternalOperationTransition(original.Transitions[^1].Sequence + 1,
                original.State, original.State, "PublicationFailed")).ToArray() };
        Assert.Throws<NotSupportedException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, context.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MemoryGenerationCallbackCannotAuthorizeChangedDispatchOwners(int fault)
    {
        var c = Create();
        using var commit = c.Commit;
        var armed = false;
        c.Kernel.ResourceAdmissionQualificationHook = new MemoryDispatchHook(() => armed = true);
        ulong Generation()
        {
            if (armed)
            {
                armed = false;
                var changed = fault switch
                {
                    0 => c.Kernel.ExternalOperations.RecordProviderLoss(c.Operation).IsSuccess,
                    1 => c.Kernel.Budgets.QuarantineLease(c.Commit.BudgetOwner, c.Lease).IsSuccess,
                    _ => c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess,
                };
                Assert.True(changed);
            }
            return 1;
        }
        var callbacks = 0;
        var result = Submit(c, Generation, new Runtime(c.BaseBinding),
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.QueryBudget(c.Lease).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted, c.Kernel.ExternalOperations.Query(c.Operation).Value!.State);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void LateMemoryProviderEpochDriftCannotBorrowPreviouslyRefinedSidecar(int boundary)
    {
        var c = Create();
        ulong generation = 1;
        var throws = false;
        c.Kernel.ResourceAdmissionQualificationHook = new MemoryDispatchHook(() =>
        {
            throws = boundary == -1;
            generation = boundary < 0 ? 1UL : (ulong)boundary;
        });
        var callbacks = 0;
        var result = Submit(c, () => throws ? throw new InvalidOperationException("generation unavailable") : generation, new Runtime(c.BaseBinding),
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(boundary == 1, result.IsSuccess);
        if (boundary != 1) Assert.Equal(boundary == -1 ? KernelError.PlatformFaulted : KernelError.StaleGeneration, result.Error);
        Assert.Equal(boundary == 1 ? 1 : 0, callbacks);
        Assert.Equal(boundary == 1 ? BudgetReservationState.Consuming : BudgetReservationState.Quarantined,
            c.Kernel.QueryBudget(c.Lease).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted, c.Kernel.ExternalOperations.Query(c.Operation).Value!.State);
    }

    private sealed class MemoryDispatchHook(Action beforeCallback) : IResourceAdmissionQualificationHook
    {
        public void At(ResourceAdmissionQualificationPoint point)
        {
            if (point == ResourceAdmissionQualificationPoint.BeforeProviderCallback) beforeCallback();
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void TemporalCompositionConflictAfterResourceOwnerSubmitRetainsExactPins(bool bound, int consumer)
    {
        var context = Create();
        using var commit = context.Commit;
        var capacity = new V6ManagedTemporalCapacityProvider(100);
        var coordinator = new V6TemporalCapacityCoordinator(context.Kernel, capacity);
        var temporal = new TemporalSemanticsV1(1,
            new(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds, 10, 0, "managed:protected-capacity"),
            ResourceAssuranceV1.GuaranteedReservation, TemporalDeadlineSemanticsV1.None,
            DeadlineClockClass.MonotonicRuntime, 0);
        var admission = bound
            ? coordinator.AdmitBoundToExternalOperation(context.Principal, context.Lease, context.Operation, temporal)
            : coordinator.Admit(context.Principal, context.Lease, "resource-consumer-conflict", temporal);
        Assert.True(admission.IsSuccess, admission.Message);
        var sink = new CollectingTraceSink();
        var calls = 0;
        var result = Submit(context, () => 1, new Runtime(context.BaseBinding),
            () => { calls++; return KernelResult.Ok(); }, sink);
        Assert.Equal(KernelError.InvalidTransition, result.Error);
        Assert.Equal(0, calls);
        var owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Submitted, owner.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        var resource = context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!;
        Assert.Equal(context.Lease, resource.Lease);
        Assert.Equal(ExternalResourceBindingState.Quarantined, resource.State);
        Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.QueryBudget(context.Lease).Value!.State);
        Assert.Equal(10UL, capacity.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Reserved,
            capacity.Query(admission.Value!.ProviderReservation.Handle).Value!.State);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.ResourceAccountingQuarantined], sink.Events.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.False(context.Kernel.ReleaseBudget(context.Principal, context.Lease).IsSuccess);
        Assert.False(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(ProviderResourcesClosed: true, ProviderUnavailable: false)).IsSuccess);
        Assert.Equal(ExternalOperationState.Submitted,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.QueryBudget(context.Lease).Value!.State);
        Assert.Equal(10UL, capacity.ReservedNanoseconds);
        var closureCalls = 0;
        var binding = admission.Value!;
        if (bound && consumer == 2)
            Assert.Equal(KernelError.ExternalEffectUncontained, coordinator.CancelAdmitted(binding).Error);
        var consumerResult = consumer switch
        {
            0 => bound
                ? coordinator.SubmitBoundExternalOperation(binding, context.Dependencies,
                    KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
                    () => { calls++; return KernelResult.Ok(); })
                : coordinator.Submit(binding, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
                    () => { calls++; return KernelResult.Ok(); }),
            1 => coordinator.CancelAdmitted(binding),
            _ => bound
                ? coordinator.ReconcileQuarantinedBoundExternalOperation(binding)
                : coordinator.ReconcileQuarantined(binding,
                    () => { closureCalls++; return KernelResult.Ok(); })
        };
        Assert.False(consumerResult.IsSuccess);
        Assert.Equal(consumer switch
        {
            0 => KernelError.StaleGeneration,
            1 when bound => KernelError.ExternalEffectUncontained,
            2 when bound => KernelError.ExternalEffectUncontained,
            _ => KernelError.InvalidTransition
        }, consumerResult.Error);
        Assert.Equal(0, calls);
        Assert.Equal(0, closureCalls);
        Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.QueryBudget(context.Lease).Value!.State);
        Assert.Equal(ExternalResourceBindingState.Quarantined,
            context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
        // Unbound submit can close its exact unused model capacity, independently
        // of the OS resource effect. It cannot refund that effect's quarantined lease.
        Assert.Equal(!bound && consumer == 0 ? 0UL : 10UL, capacity.ReservedNanoseconds);
        Assert.Equal(!bound && consumer == 0 ? V6TemporalProviderReservationState.Released
                : bound && consumer != 0 ? V6TemporalProviderReservationState.Quarantined
                : V6TemporalProviderReservationState.Reserved,
            capacity.Query(binding.ProviderReservation.Handle).Value!.State);
        foreach (var use in owner.Admission!.RegionUses)
        {
            Assert.NotEqual(RegionUseState.Released,
                context.Kernel.Regions.SnapshotUses().Single(item => item.Handle == use.Handle).State);
            Assert.Equal(KernelError.RegionUseConflict,
                context.Kernel.Regions.ReleaseUse(use.Handle, owner.Principal).Error);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ProjectionRejectsContradictoryOperationAndBindingIdentity(int mutation)
    {
        var context = Create();
        using var commit = context.Commit;
        var admitted = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        var submitted = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var handle = submitted.Operation;
        var changed = mutation switch
        {
            0 => submitted with { Operation = handle with { OperationId = new(0) } },
            1 => submitted with { Operation = handle with { Generation = new(0) } },
            2 => submitted with { Operation = handle with { OperationId = new(handle.OperationId.Value + 1) } },
            3 => submitted with { Operation = handle with { Generation = new(handle.Generation.Value + 1) } },
            4 => submitted with { Binding = null },
            5 => submitted with { Binding = binding with { BindingId = new(0) } },
            6 => submitted with { Binding = binding with { Generation = 0 } },
            7 => submitted with { Binding = binding with { Operation = handle with { Generation = new(handle.Generation.Value + 1) } } },
            _ => admitted with { Binding = binding }
        };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            changed, context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.Empty(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(admitted,
            context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.True(SemanticTraceValidatorV1.Validate(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            submitted, context.MemoryBinding.ExtensionBinding.Digest.Value)).IsValid);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectionRejectsEmptyOwnerHistoryBeforeAndAfterSubmit(bool submitted)
    {
        var context = Create();
        using var commit = context.Commit;
        if (submitted)
            Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).IsSuccess);
        var original = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            original with { Transitions = [] }, context.MemoryBinding.ExtensionBinding.Digest.Value));
        var valid = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(original,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        if (submitted) Assert.True(SemanticTraceValidatorV1.Validate(valid).IsValid);
        else Assert.Empty(valid);
    }

    [Fact]
    public void ProjectionRejectsRepeatedPreparedOriginWithContiguousSequence()
    {
        var context = Create();
        using var commit = context.Commit;
        var original = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var transitions = new[] { original.Transitions[0], original.Transitions[0] with { Sequence = 2 } }
            .Concat(original.Transitions.Skip(1).Select(item => item with { Sequence = item.Sequence + 1 })).ToArray();
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            original with { Transitions = transitions }, context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.Empty(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(original,
            context.MemoryBinding.ExtensionBinding.Digest.Value));
    }
    [Theory]
    [InlineData("Admitted")]
    [InlineData("Submitted")]
    [InlineData("DeviceComplete")]
    [InlineData("Visible")]
    [InlineData("Published")]
    [InlineData("ResourceLeaseBound")]
    public void ProjectionRejectsKnownEventWithContradictoryOwnerStateEdge(string eventName)
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        var published = CompleteVisibleAndPublish(context, binding);
        var index = published.Transitions.ToList().FindIndex(item => item.Event == eventName);
        Assert.True(index >= 0);
        var prefix = published.Transitions.Take(index + 1).ToArray();
        var last = prefix[^1];
        prefix[^1] = last with { To = last.From == last.To ? ExternalOperationState.Released : last.From };
        var contradiction = published with { State = prefix[^1].To, Transitions = prefix };
        Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            contradiction, context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.True(SemanticTraceValidatorV1.Validate(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            published, context.MemoryBinding.ExtensionBinding.Digest.Value)).IsValid);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ProjectionRejectsUnknownMandatoryOwnerSemantics(int field)
    {
        var context = Create();
        using var commit = context.Commit;
        Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).IsSuccess);
        var original = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var transitions = original.Transitions.ToArray();
        transitions[^1] = transitions[^1] with { To = (ExternalOperationState)99 };
        var unknown = field switch
        {
            0 => original with { State = (ExternalOperationState)99, Transitions = transitions },
            1 => original with { Disposition = (ExternalOperationDisposition)99 },
            2 => original with { VisibilityRequirement = (ExternalVisibilityRequirement)99 },
            3 => original with { PublicationPolicy = (ExternalPublicationPolicy)99 },
            4 => original with { EffectBoundary = (ExternalEffectBoundaryState)99 },
            5 => original with { EffectPolicy = original.EffectPolicy with { EffectClass = (ExternalEffectClass)99 } },
            6 => original with { EffectPolicy = original.EffectPolicy with { ReplayProtection = (ExternalReplayProtection)99 } },
            7 => original with { Transitions = original.Transitions.SkipLast(1).Append(
                original.Transitions[^1] with { From = (ExternalOperationState)99 }).ToArray() },
            _ => original with { Transitions = transitions }
        };
        Assert.Throws<NotSupportedException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            unknown, context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.True(SemanticTraceValidatorV1.Validate(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            original, context.MemoryBinding.ExtensionBinding.Digest.Value)).IsValid);
    }
    [Fact]
    public void BudgetLossBeforeConsumptionRetainsCommittedSubmitTraceAndReclaimPin()
    {
        using var store = new BlockingSubmitJournalStore();
        var context = Create(journalStore: store);
        using var commit = context.Commit;
        var quarantined = false;
        store.BeforeNextAppend = () => quarantined =
            context.Kernel.Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease).IsSuccess;
        var sink = new CollectingTraceSink();
        var calls = 0;
        var result = Submit(context, () => 1, new Runtime(context.BaseBinding),
            () => { calls++; return KernelResult.Ok(); }, sink);
        Assert.False(result.IsSuccess);
        Assert.True(quarantined);
        Assert.Equal(0, calls);
        var snapshot = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Submitted, snapshot.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, snapshot.Disposition);
        Assert.Equal(ExternalResourceBindingState.Quarantined,
            context.Kernel.ExternalOperations.QueryResourceBinding(context.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.QueryBudget(context.Lease).Value!.State);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.ResourceAccountingQuarantined], sink.Events.Select(item => item.Kind));
        Assert.Equal(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            context.MemoryBinding.ExtensionBinding.Digest.Value), sink.Events);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.False(context.Kernel.ReleaseExternalOperation(context.Principal,
            context.Operation, new(true, false)).IsSuccess);
        Assert.Equal(ExternalOperationState.Submitted,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
    }
    [Fact]
    public void ThrowingSubmissionObservationCannotVetoOrCompensateOwnerSubmit()
    {
        var context = Create();
        using var commit = context.Commit;
        var calls = 0;
        var result = context.Kernel.SubmitResourceExternalAdmissionWithBinding(commit, context.Dependencies,
            _ => { calls++; return KernelResult.Ok(); }, submissionObservation: () =>
                throw new InvalidOperationException("Observation loss after owner commit."));
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, calls);
        Assert.Equal(ExternalOperationState.Submitted,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.Consuming, context.Kernel.QueryBudget(context.Lease).Value!.State);
    }
    [Fact]
    public void ReentrantSubmitWinnerOwnsTraceRegistrationInsteadOfFinalSentryLoser()
    {
        var context = Create();
        using var commit = context.Commit;
        var loserSink = new CollectingTraceSink();
        var winnerSink = new CollectingTraceSink();
        var calls = 0;
        var provider = new Provider(context.BaseBinding, revalidation =>
        {
            if (revalidation == 2)
                Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding),
                    () => { calls++; return KernelResult.Ok(); }, winnerSink).IsSuccess);
        });
        var loser = context.Kernel.SubmitV6MemorySemanticResourceExternalAdmission(commit, context.Dependencies,
            context.BaseBinding, context.MemoryBinding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, () => 1, context.ProviderGenerations,
            provider, new Runtime(context.BaseBinding),
            () => { calls++; return KernelResult.Ok(); }, loserSink);
        Assert.Equal(KernelError.InvalidTransition, loser.Error);
        Assert.Equal(1, calls);
        Assert.Empty(loserSink.Events);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible],
            winnerSink.Events.Select(item => item.Kind));
        Assert.Equal(BudgetReservationState.Consuming, context.Kernel.QueryBudget(context.Lease).Value!.State);
    }
    [Fact]
    public async Task RejectedDuplicateDuringDurableSubmitCannotDiscardWinnerTrace()
    {
        using var store = new BlockingSubmitJournalStore();
        var context = Create(journalStore: store);
        using var commit = context.Commit;
        var sink = new CollectingTraceSink();
        var calls = 0;
        store.BlockNextAppend = true;
        var winner = Task.Run(() => Submit(context, () => 1, new Runtime(context.BaseBinding),
            () => { calls++; return KernelResult.Ok(); }, sink));
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(commit.SubmitStarted);
            Assert.Equal(ExternalOperationState.Admitted,
                context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
            var loser = Submit(context, () => 1, new Runtime(context.BaseBinding),
                () => { calls++; return KernelResult.Ok(); }, new CollectingTraceSink());
            Assert.Equal(KernelError.InvalidTransition, loser.Error);
        }
        finally
        {
            store.Continue.Set();
            Assert.True((await winner).IsSuccess);
        }
        Assert.Equal(1, calls);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible],
            sink.Events.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(BudgetReservationState.Consuming, context.Kernel.QueryBudget(context.Lease).Value!.State);
    }

    private sealed class BlockingSubmitJournalStore : IResourceBudgetJournalStore, IDisposable
    {
        private readonly List<byte[]> frames = [];
        internal bool BlockNextAppend { get; set; }
        internal Action? BeforeNextAppend { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Continue { get; } = new(false);
        public IReadOnlyList<byte[]> ReadFrames() => frames.Select(frame => frame.ToArray()).ToArray();
        public void AppendFrame(ReadOnlySpan<byte> frame)
        {
            var before = BeforeNextAppend;
            BeforeNextAppend = null;
            before?.Invoke();
            if (BlockNextAppend)
            {
                BlockNextAppend = false;
                Entered.TrySetResult();
                if (!Continue.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Submit journal rendezvous timed out.");
            }
            frames.Add(frame.ToArray());
        }
        public void Dispose() => Continue.Dispose();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingOptionalCompanionRequiresRebuiltExactMemorySidecar(bool guarantee)
    {
        var context = Create();
        using var commit = context.Commit;
        var original = context.MemoryBinding;
        var optional = SemanticExtensionClauseV1.Create(new("zzz.optional-companion"), "companion/1", 1,
            SemanticExtensionRequirement.Optional, [1]);
        var requirements = OperationSemanticExtensionsV1.Create(guarantee ? original.Requirements.Clauses :
            original.Requirements.Clauses.Append(optional));
        var guarantees = ExecutionGuaranteeExtensionsV1.Create(guarantee ? original.Guarantees.Clauses.Append(optional) :
            original.Guarantees.Clauses);
        var extended = context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding, context.Obligations,
            context.Guarantees, requirements, guarantees, 1);
        Assert.True(extended.IsSuccess, extended.Message);
        var changed = extended.Value! with { Requirements = original.Requirements, Guarantees = original.Guarantees };
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.RevalidateV6MemorySemanticBinding(changed,
            context.BaseBinding, context.Obligations, context.Guarantees, 1).Error);
        var fresh = context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding, context.Obligations,
            context.Guarantees, original.Requirements, original.Guarantees, 1);
        Assert.True(fresh.IsSuccess, fresh.Message);
        Assert.NotEqual(extended.Value.ExtensionBinding.Digest, fresh.Value!.ExtensionBinding.Digest);
        Assert.True(context.Kernel.RevalidateV6MemorySemanticBinding(fresh.Value,
            context.BaseBinding, context.Obligations, context.Guarantees, 1).IsSuccess);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    [Fact]
    public void RejectedDuplicateCannotAttachForgedSidecarTraceToSubmittedOperation()
    {
        var context = Create();
        using var commit = context.Commit;
        Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).IsSuccess);
        var original = context.MemoryBinding.ExtensionBinding;
        var changed = (original.ObligationsV1Digest[0] == '0' ? "1" : "0") + original.ObligationsV1Digest[1..];
        var forged = SemanticBindingExtensionSetV1.Create(changed, original.GuaranteesV1Digest,
            context.MemoryBinding.Requirements, context.MemoryBinding.Guarantees, original.Generations);
        var duplicate = context with { MemoryBinding = context.MemoryBinding with { ExtensionBinding = forged } };
        var sink = new CollectingTraceSink();
        var calls = 0;
        var result = Submit(duplicate, () => 1, new Runtime(context.BaseBinding),
            () => { calls++; return KernelResult.Ok(); }, sink);
        Assert.Equal(KernelError.InvalidTransition, result.Error);
        Assert.Equal(0, calls);
        Assert.Empty(sink.Events);
        Assert.Equal(ExternalOperationState.Submitted, context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.Consuming, context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }
    [Fact]
    public void MandatoryMemoryCannotDowngradeToOptionalEvenWithFreshBinding()
    {
        var context = Create();
        using var commit = context.Commit;
        var optional = OperationSemanticExtensionsV1.Create([Memory().ToClause(SemanticExtensionRequirement.Optional)]);
        Assert.Equal(KernelError.PlatformDenied, context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding,
            context.Obligations, context.Guarantees, optional, context.MemoryBinding.Guarantees, 1).Error);
        Assert.Equal(KernelError.PlatformDenied, context.Kernel.RevalidateV6MemorySemanticBinding(
            context.MemoryBinding with { Requirements = optional }, context.BaseBinding, context.Obligations,
            context.Guarantees, 1).Error);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlteredBaseDigestSidecarCannotPassFreshMemoryBinding(bool guarantee)
    {
        var context = Create();
        using var commit = context.Commit;
        var sidecar = context.MemoryBinding;
        var original = sidecar.ExtensionBinding;
        var forged = SemanticBindingExtensionSetV1.Create(
            guarantee ? original.ObligationsV1Digest : Change(original.ObligationsV1Digest),
            guarantee ? Change(original.GuaranteesV1Digest) : original.GuaranteesV1Digest,
            sidecar.Requirements, sidecar.Guarantees, original.Generations);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateV6MemorySemanticBinding(sidecar with { ExtensionBinding = forged },
                context.BaseBinding, context.Obligations, context.Guarantees, 1).Error);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
        static string Change(string digest) => (digest[0] == '0' ? "1" : "0") + digest[1..];
    }

    [Theory]
    [InlineData("future.memory/1", 1)]
    [InlineData("singnext.memory-semantics/1", 2)]
    public void MatchingUnknownMemorySchemasCannotBorrowV1PayloadPermission(string schema, int version)
    {
        var context = Create(createMemoryBinding: false);
        using var commit = context.Commit;
        var clause = SemanticExtensionClauseV1.Create(MemorySemanticsV1.ExtensionClassId,
            schema, (ushort)version, SemanticExtensionRequirement.Mandatory, Memory().SerializeCanonical());
        var result = context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding,
            context.Obligations, context.Guarantees, OperationSemanticExtensionsV1.Create([clause]),
            ExecutionGuaranteeExtensionsV1.Create([clause]), 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void StagedMemoryBindingAwareSubmitUsesOneCommittedOperation()
    {
        var context = Create();
        using var commit = context.Commit;
        OperationBinding? observed = null;

        var submitted = context.Kernel.SubmitV6MemorySemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.BaseBinding, context.MemoryBinding,
            context.Obligations, context.Guarantees, context.Refinement, ProviderIdentity, () => 1,
            context.ProviderGenerations, new Provider(context.BaseBinding),
            new Runtime(context.BaseBinding), binding =>
            {
                observed = binding;
                var owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
                Assert.Equal(context.Operation, owner.Operation);
                Assert.Equal(ExternalOperationState.Submitted, owner.State);
                Assert.Equal(binding, owner.Binding);
                return KernelResult.Ok();
            });

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(submitted.Value, observed);
        Assert.Single(context.Kernel.ExternalOperations.InspectionSnapshot());
    }

    [Fact]
    public void FirstQualificationVerticalRunsPlannerSessionSubmitPublishSettleAndReleaseEndToEnd()
    {
        var context = Create(withQualificationVertical: true);
        using var commit = context.Commit;
        using var invocation = context.Invocation!;
        Assert.NotNull(context.Plan);
        Assert.NotNull(context.Session);
        Assert.Equal(new ComputeProviderId(ProviderIdentity), context.Plan!.ProviderId);
        Assert.Equal(ComputePublicationPath.Staged, context.Plan.PublicationPath);

        var providerCalls = 0;
        var binding = Submit(context, () => context.Provider!.Generation,
            new Runtime(context.BaseBinding), () => { providerCalls++; return KernelResult.Ok(); }).Value!;
        _ = CompleteVisibleAndPublish(context, binding);
        var usage = new ExternalResourceUsageEvidence(1, binding, ProviderIdentity, 1,
            new(1, $"{ProviderIdentity}:measurement", "1", ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds), Guid.Parse("9c453d93-e2e1-48aa-b0af-e252c4d24032"),
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
            new(1, 0, 0, 5, 0, 0, 0, 0, 0, 0), 5, 1);
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal, usage).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(true, false)).IsSuccess);
        Assert.True(context.Kernel.SettleInlineSessionInvocation(context.Service!.Value, invocation, true).IsSuccess);

        var snapshot = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(ExternalOperationState.Released, snapshot.State);
        Assert.Equal(1, providerCalls);

        var vertical = FirstQualificationVerticalEvidenceV1.Create(QualificationEvidence(context));
        Assert.Equal(16, vertical.Transitions.Count);
        Assert.All(vertical.Transitions, transition =>
            Assert.Equal(QualificationFailureStateV1.None, transition.FailureState));
        Assert.False(vertical.AuthorizesExecution);
    }

    [Fact]
    public void SafeContourRevalidatesThenCrossesEffectPossibleBoundaryExactlyOnce()
    {
        var context = Create();
        using var commit = context.Commit;
        var callbacks = 0;

        var result = Submit(context, () => 1, new Runtime(context.BaseBinding), () =>
        {
            callbacks++;
            Assert.Equal(ExternalOperationState.Submitted,
                context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
            Assert.Equal(BudgetReservationState.Consuming,
                context.Kernel.Budgets.Query(context.Lease).Value!.State);
            return KernelResult.Ok();
        });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, callbacks);
        Assert.False(context.MemoryBinding.AuthorizesExecution);
        Assert.False(context.MemoryBinding.AuthorizesEffect);
        Assert.False(context.MemoryBinding.AuthorizesPublication);
    }

    [Fact]
    public void ProviderGenerationDriftDuringRuntimeLegalityFailsBeforeSubmit()
    {
        var context = Create();
        using var commit = context.Commit;
        ulong liveGeneration = 1;
        var callbacks = 0;
        var runtime = new Runtime(context.BaseBinding, beforeDecision: () => liveGeneration = 2);

        var result = Submit(context, () => liveGeneration, runtime, () =>
        {
            callbacks++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, callbacks);
        Assert.NotEqual(ExternalOperationState.Submitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void UnknownMandatoryClauseCannotHideBehindValidMemoryClause()
    {
        var context = Create(createMemoryBinding: false);
        using var commit = context.Commit;
        var unknown = SemanticExtensionClauseV1.Create(new("zzz.unknown"), "unknown/1", 1,
            SemanticExtensionRequirement.Mandatory, Encoding.UTF8.GetBytes("required"));
        var requirements = OperationSemanticExtensionsV1.Create([
            Memory().ToClause(SemanticExtensionRequirement.Mandatory), unknown
        ]);

        var result = context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding, context.Obligations,
            context.Guarantees, requirements, MemoryGuarantees(Memory()), 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
    }

    [Fact]
    public void UnknownMandatoryProviderCompanionCannotHideBehindMemoryGuarantee()
    {
        var context = Create(createMemoryBinding: false);
        using var commit = context.Commit;
        var unknown = SemanticExtensionClauseV1.Create(new("zzz.provider-unknown"), "unknown/1", 1,
            SemanticExtensionRequirement.Mandatory, []);
        var offered = ExecutionGuaranteeExtensionsV1.Create([
            .. MemoryGuarantees(Memory()).Clauses, unknown]);
        var result = context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding, context.Obligations,
            context.Guarantees, OperationSemanticExtensionsV1.Create([
                Memory().ToClause(SemanticExtensionRequirement.Mandatory)]), offered, 1);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    [Fact]
    public void IncomparableDirectCoherentGuaranteeIsRejected()
    {
        var context = Create(createMemoryBinding: false);
        using var commit = context.Commit;
        var direct = new MemorySemanticsV1(1, MemoryOwnershipClassV1.SharedMutable,
            MemoryAccessClassV1.DirectMutableOutput, MemoryOrderClassV1.SequentiallyConsistent,
            MemoryAtomicityClassV1.NaturallyAlignedScalar, MemoryCoherenceAssumptionV1.HardwareCoherent,
            MemoryVisibilityClassV1.ConsumerVisibleAfterFence, MemoryPublicationModeV1.DirectCoherent).Validate();

        var result = context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding, context.Obligations,
            context.Guarantees, MemoryRequirements(), MemoryGuarantees(direct), 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
    }

    [Fact]
    public void MissingStagedOutputIsRejectedEvenWhenSidecarClaimsSafeContour()
    {
        var context = Create(includeStagedOutput: false, createMemoryBinding: false);
        using var commit = context.Commit;

        var result = context.Kernel.CreateV6MemorySemanticBinding(context.BaseBinding, context.Obligations,
            context.Guarantees, MemoryRequirements(), MemoryGuarantees(Memory()), 1);

        Assert.Equal(KernelError.InvalidRegionState, result.Error);
    }

    [Fact]
    public void CompletionCannotPublishBeforeVisibilityAndMutationBeforePublicationFailsClosed()
    {
        var context = Create();
        using var commit = context.Commit;
        var submitted = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok);
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(submitted.Value!, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        var published = false;
        Assert.Equal(KernelError.InvalidTransition, context.Kernel.PublishExternalOperation(context.Principal,
            context.Operation, context.Dependencies, new(ExternalPublicationPolicy.Staged), () => published = true).Error);
        Assert.False(published);
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(submitted.Value!, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var domain = context.Kernel.Processes.Resolve(context.Principal).Value!.DomainId;
        Assert.True(context.Kernel.Regions.Transfer(context.Input,
            new(domain, context.Principal.Generation),
            new(domain, context.Principal.Generation + 1)).IsSuccess);

        var stalePublish = context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
            context.Dependencies, new(ExternalPublicationPolicy.Staged), () => published = true);

        Assert.False(stalePublish.IsSuccess);
        Assert.False(published);
    }

    [Fact]
    public void V1AndV6StagedPathsHaveTheSameAllowedOwnerTraceProjection()
    {
        var reference = Create();
        using var referenceCommit = reference.Commit;
        var referenceBinding = reference.Kernel.SubmitSemanticResourceExternalAdmission(reference.Commit,
            reference.Dependencies, reference.BaseBinding, reference.Obligations, reference.Guarantees,
            reference.Refinement, ProviderIdentity, reference.ProviderGenerations,
            new Provider(reference.BaseBinding), new Runtime(reference.BaseBinding), KernelResult.Ok).Value!;
        var referenceSnapshot = CompleteVisibleAndPublish(reference, referenceBinding);

        var v6 = Create();
        using var v6Commit = v6.Commit;
        var v6Binding = Submit(v6, () => 1, new Runtime(v6.BaseBinding), KernelResult.Ok).Value!;
        var v6Snapshot = CompleteVisibleAndPublish(v6, v6Binding);

        var referenceTrace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(referenceSnapshot,
            reference.MemoryBinding.ExtensionBinding.Digest.Value);
        var v6Trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(v6Snapshot,
            v6.MemoryBinding.ExtensionBinding.Digest.Value);

        Assert.Equal(referenceTrace.Select(item => item.Kind), v6Trace.Select(item => item.Kind));
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published], v6Trace.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(referenceTrace).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(v6Trace).IsValid);
        Assert.True(SemanticTraceDifferentialV1.CompareAllowedProjection(referenceTrace, v6Trace,
            new string('1', 64), new string('2', 64)).IsEquivalent);
    }

    [Fact]
    public void ProviderLossProjectsToQuarantineWithoutInventingRelease()
    {
        var context = Create();
        using var commit = context.Commit;
        Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).IsSuccess);
        Assert.True(context.Kernel.RecordExternalOperationProviderLoss(context.Principal, context.Operation).IsSuccess);
        var snapshot = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;

        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            context.MemoryBinding.ExtensionBinding.Digest.Value);

        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.ResourceAccountingQuarantined], trace.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Fact]
    public void ReleasedSnapshotRequiresSeparateOrderedSettlementEvidence()
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        _ = CompleteVisibleAndPublish(context, binding);
        Assert.Equal(KernelError.InvalidTransition,
            context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
                new(true, false)).Error);
        var published = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var last = published.Transitions[^1];
        var released = published with
        {
            State = ExternalOperationState.Released,
            Transitions = published.Transitions.Concat([
                new ExternalOperationTransition(last.Sequence + 1, published.State,
                    ExternalOperationState.Released, "Released")
            ]).ToArray()
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released,
                context.MemoryBinding.ExtensionBinding.Digest.Value));

        Assert.Contains("budget-settlement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionRejectsNoPublicationClosureAfterPublishedEffect()
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        var published = CompleteVisibleAndPublish(context, binding);
        var last = published.Transitions[^1];
        var contradictory = published with
        {
            Transitions = published.Transitions.Concat([
                new ExternalOperationTransition(last.Sequence + 1, published.State, published.State,
                    "ResourceSettlementQuarantined"),
                new ExternalOperationTransition(last.Sequence + 2, published.State, published.State,
                    "PublicationEffectClosedWithoutPublication")
            ]).ToArray()
        };

        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(contradictory,
                context.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Fact]
    public void ProjectionRejectsVisibilityBeforeOwnerCompletion()
    {
        var context = Create();
        using var commit = context.Commit;
        Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).IsSuccess);
        var submitted = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var last = submitted.Transitions[^1];
        var contradictory = submitted with
        {
            Transitions = submitted.Transitions.Append(
                new ExternalOperationTransition(last.Sequence + 1, submitted.State,
                    ExternalOperationState.Visible, "Visible")).ToArray()
        };

        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(contradictory,
                context.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Fact]
    public void ProjectionRejectsDiscontinuousOwnerStatesAndContradictorySnapshotState()
    {
        var context = Create();
        using var commit = context.Commit;
        Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).IsSuccess);
        var submitted = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var transitions = submitted.Transitions.ToArray();
        var last = transitions[^1];
        transitions[^1] = last with { From = ExternalOperationState.Prepared };

        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
                submitted with { Transitions = transitions },
                context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
                submitted with { State = ExternalOperationState.Prepared },
                context.MemoryBinding.ExtensionBinding.Digest.Value));
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
                submitted with { Transitions = submitted.Transitions.Skip(1).ToArray() },
                context.MemoryBinding.ExtensionBinding.Digest.Value));
        transitions = submitted.Transitions.ToArray();
        transitions[^1] = transitions[^1] with { Sequence = transitions[^1].Sequence + 1 };
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
                submitted with { Transitions = transitions },
                context.MemoryBinding.ExtensionBinding.Digest.Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EarlySettlementPreservesPublicationOrExactClosureRequirement(bool beforeVisibility, bool ambiguous)
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new CollectingTraceSink();
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok, sink).Value!;
        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        if (!beforeVisibility)
            Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
                new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal,
            UsageEvidence(binding, "f03e3993-d97e-4b17-9e53-950551c33661")).IsSuccess);
        var settled = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var prefix = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(settled,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.Equal(SemanticTraceEventKindV1.Settled, prefix[^1].Kind);
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        Assert.Equal(prefix, sink.Events);
        Assert.False(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation, new(true, false)).IsSuccess);
        if (beforeVisibility)
            Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
                new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        if (ambiguous)
        {
            ExternalPublicationDecisionV1 decision = default;
            Assert.Equal(KernelError.ExternalEffectUncontained,
                context.Kernel.PublishExternalOperation(context.Principal, context.Operation, context.Dependencies,
                    new(ExternalPublicationPolicy.Staged), (ExternalPublicationDecisionV1 current) =>
                    {
                        decision = current;
                        throw new InvalidOperationException("Ambiguous publication after early settlement.");
                    }).Error);
            Assert.False(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation, new(true, false)).IsSuccess);
            Assert.True(context.Kernel.ReconcileFailedExternalPublication(context.Principal,
                context.Operation, decision, KernelResult.Ok).IsSuccess);
        }
        else Assert.True(context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
            context.Dependencies, new(ExternalPublicationPolicy.Staged),
            (ExternalPublicationDecisionV1 _) => KernelResult.Ok()).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation, new(true, false)).IsSuccess);
        var final = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.Equal(final, sink.Events);
        Assert.True(SemanticTraceValidatorV1.Validate(final).IsValid);
        Assert.Single(final, item => item.Kind == SemanticTraceEventKindV1.Settled);
    }

    [Fact]
    public void OrderedSettlementAndReleaseProduceACompleteValidTrace()
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok).Value!;
        _ = CompleteVisibleAndPublish(context, binding);
        var evidence = new ExternalResourceUsageEvidence(1, binding, ProviderIdentity, 1,
            new(1, $"{ProviderIdentity}:measurement", "1", ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds), Guid.Parse("0151123b-3134-4f46-a5f6-cb22d537e23e"),
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
            new(1, 0, 0, 5, 0, 0, 0, 0, 0, 0), 5, 1);
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal, evidence).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(true, false)).IsSuccess);
        var released = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;

        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released,
            context.MemoryBinding.ExtensionBinding.Digest.Value);

        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released], trace.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Fact]
    public void DirectSemanticTraceStreamMatchesCommittedOwnerProjection()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new CollectingTraceSink();
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok,
            sink).Value!;
        _ = CompleteVisibleAndPublish(context, binding);
        var evidence = UsageEvidence(binding, "8ea615bc-427e-4b80-a7c9-dd21b02abdd6");
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal, evidence).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(true, false)).IsSuccess);

        var snapshot = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var projected = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot,
            context.MemoryBinding.ExtensionBinding.Digest.Value);

        Assert.Equal(projected, sink.Events);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.All(sink.Events, traceEvent =>
        {
            Assert.False(traceEvent.AuthorizesExecution);
            Assert.False(traceEvent.AuthorizesEffect);
            Assert.False(traceEvent.AuthorizesPublication);
        });
    }

    [Fact]
    public void ThrowingDirectTraceSinkCannotChangeAuthoritativeLifecycle()
    {
        var context = Create();
        using var commit = context.Commit;
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok,
            new ThrowingTraceSink()).Value!;

        _ = CompleteVisibleAndPublish(context, binding);
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal,
            UsageEvidence(binding, "6a585807-b385-49dc-88bf-41a1bce37356")).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(true, false)).IsSuccess);
        Assert.Equal(ExternalOperationState.Released,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void FailedDirectTraceDeliveryNeverEmitsAnUnobservedSuffix(int failureCall, bool throws)
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new FailingTraceSink(failureCall, throws);
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding),
            KernelResult.Ok, sink).Value!;
        _ = CompleteVisibleAndPublish(context, binding);
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal,
            UsageEvidence(binding, "c5e86b3a-c9c4-4bed-9240-41da59442188")).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(true, false)).IsSuccess);

        Assert.Equal(failureCall, sink.Calls);
        Assert.Equal(failureCall - 1, sink.Accepted.Count);
        Assert.Equal(ExternalOperationState.Released,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
        if (sink.Accepted.Count != 0)
            Assert.True(SemanticTraceValidatorV1.Validate(sink.Accepted).IsValid);
    }

    [Fact]
    public void ReentrantDirectTraceSinkDrainsNewOwnerTransitionAfterOriginalPrefix()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new ReentrantTraceSink(() =>
            Assert.True(context.Kernel.RecordExternalOperationProviderLoss(
                context.Principal, context.Operation).IsSuccess));

        _ = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok, sink);

        Assert.Equal([SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.ResourceAccountingQuarantined],
            sink.Events.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
    }

    [Fact]
    public void ReentrantReleaseFromSettlementTraceDeliversFinalOwnerEvent()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new ReentrantReleaseTraceSink(() =>
            Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal,
                context.Operation, new(true, false)).IsSuccess));
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding),
            KernelResult.Ok, sink).Value!;
        _ = CompleteVisibleAndPublish(context, binding);

        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal,
            UsageEvidence(binding, "afc485d5-e881-4b79-ad4a-25b121be009d")).IsSuccess);
        var released = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        var projected = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.Equal(ExternalOperationState.Released, released.State);
        Assert.Equal(projected, sink.Events);
        Assert.Equal(SemanticTraceEventKindV1.Released, sink.Events[^1].Kind);
    }

    [Fact]
    public void ConcurrentReleaseFromSettlementTraceDoesNotBlockOwnerOrLoseFinalEvent()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new ReentrantReleaseTraceSink(() =>
        {
            var release = Task.Run(() => context.Kernel.ReleaseExternalOperation(
                context.Principal, context.Operation, new(true, false)));
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)),
                "Owner release waited on a trace callback holding the delivery lock.");
            Assert.True(release.Result.IsSuccess, release.Result.Message);
        });
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding),
            KernelResult.Ok, sink).Value!;
        _ = CompleteVisibleAndPublish(context, binding);

        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal,
            UsageEvidence(binding, "61b94d76-5f8e-4dad-b0ba-08398820d7d7")).IsSuccess);
        var released = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, released.State);
        Assert.Equal(V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released,
            context.MemoryBinding.ExtensionBinding.Digest.Value), sink.Events);
    }

    [Fact]
    public void FailedOwnerTransitionEmitsNoDirectSemanticEvent()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new CollectingTraceSink();
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok,
            sink).Value!;
        Assert.Equal(2, sink.Events.Count);

        var invalid = context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true));

        Assert.Equal(KernelError.InvalidTransition, invalid.Error);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible],
            sink.Events.Select(traceEvent => traceEvent.Kind));
    }

    [Fact]
    public void ProviderLossEmitsOneQuarantineEventFromCommittedOwnerState()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new CollectingTraceSink();
        Assert.True(Submit(context, () => 1, new Runtime(context.BaseBinding), KernelResult.Ok,
            sink).IsSuccess);

        Assert.True(context.Kernel.RecordExternalOperationProviderLoss(
            context.Principal, context.Operation).IsSuccess);

        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.ResourceAccountingQuarantined], sink.Events.Select(traceEvent => traceEvent.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void PostSubmitCancelRequestRemainsPossibleEffectInDirectTrace()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new CollectingTraceSink();
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding),
            KernelResult.Ok, sink).Value!;

        var cancelled = context.Kernel.CancelExternalOperation(context.Principal,
            context.Operation, providerCancellationSupported: false);
        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(ExternalOperationDisposition.CancellationPending,
            cancelled.Value!.Disposition);
        Assert.Equal(SemanticTraceEventKindV1.CancellationRequested,
            sink.Events[^1].Kind);
        Assert.DoesNotContain(sink.Events, item => item.Kind is
            SemanticTraceEventKindV1.Quarantined or SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);

        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var published = context.Kernel.PublishExternalOperation(context.Principal,
            context.Operation, context.Dependencies,
            new(ExternalPublicationPolicy.Staged), static () => { });
        Assert.True(published.IsSuccess, published.Message);
        Assert.Equal(SemanticTraceEventKindV1.Published, sink.Events[^1].Kind);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);

        var fault = Create();
        using var faultCommit = fault.Commit;
        var faultSink = new CollectingTraceSink();
        var faultBinding = Submit(fault, () => 1, new Runtime(fault.BaseBinding),
            KernelResult.Ok, faultSink).Value!;
        Assert.True(fault.Kernel.CancelExternalOperation(fault.Principal, fault.Operation,
            providerCancellationSupported: false).IsSuccess);
        Assert.True(fault.Kernel.RecordExternalOperationCompletion(fault.Principal,
            new(faultBinding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.CancellationRequested,
            SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Quarantined], faultSink.Events.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(faultSink.Events).IsValid);
    }

    [Fact]
    public void AmbiguousPublicationAndExactReconciliationRemainInDirectSemanticTrace()
    {
        var context = Create();
        using var commit = context.Commit;
        var sink = new CollectingTraceSink();
        var binding = Submit(context, () => 1, new Runtime(context.BaseBinding),
            KernelResult.Ok, sink).Value!;
        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);

        ExternalPublicationDecisionV1 decision = default;
        var failed = context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
            context.Dependencies, new(ExternalPublicationPolicy.Staged),
            (ExternalPublicationDecisionV1 current) =>
            {
                decision = current;
                throw new InvalidOperationException("ambiguous staged publication");
            });
        Assert.Equal(KernelError.ExternalEffectUncontained, failed.Error);
        Assert.Equal(SemanticTraceEventKindV1.Quarantined, sink.Events[^1].Kind);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
                new(true, false)).Error);

        var reconciled = context.Kernel.ReconcileFailedExternalPublication(context.Principal,
            context.Operation, decision, KernelResult.Ok);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            sink.Events[^1].Kind);
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal,
            UsageEvidence(binding, "efdb1b93-7f3d-4f80-84d0-d67b0612b96c")).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(true, false)).IsSuccess);
        var projected = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!,
            context.MemoryBinding.ExtensionBinding.Digest.Value);
        Assert.Equal(projected, sink.Events);
        Assert.Equal(SemanticTraceEventKindV1.Released, sink.Events[^1].Kind);
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    private static KernelResult<OperationBinding> Submit(Context context, Func<ulong> generation,
        IRuntimeLegalityService runtime, Func<KernelResult> callback,
        ISemanticTraceSinkV1? traceSink = null) =>
        context.Kernel.SubmitV6MemorySemanticResourceExternalAdmission(context.Commit, context.Dependencies,
            context.BaseBinding, context.MemoryBinding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, generation, context.ProviderGenerations,
            new Provider(context.BaseBinding), runtime, callback, traceSink);

    private static ExternalResourceUsageEvidence UsageEvidence(OperationBinding binding, string evidenceId) =>
        new(1, binding, ProviderIdentity, 1,
            new(1, $"{ProviderIdentity}:measurement", "1", ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds), Guid.Parse(evidenceId),
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
            new(1, 0, 0, 5, 0, 0, 0, 0, 0, 0), 5, 1);

    private static ExternalOperationSnapshot CompleteVisibleAndPublish(Context context, OperationBinding binding)
    {
        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var publication = context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
            context.Dependencies, new(ExternalPublicationPolicy.Staged), static () => { });
        Assert.True(publication.IsSuccess, publication.Message);
        return publication.Value!;
    }

    private static Context Create(bool includeStagedOutput = true, bool createMemoryBinding = true,
        bool withQualificationVertical = false, IResourceBudgetJournalStore? journalStore = null)
    {
        var time = new TestTimeProvider(new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        var kernel = journalStore is null ? new RuntimeKernel(null, time) :
            new RuntimeKernel(null, time, new("qualification-only", new byte[32]), journalStore);
        var admin = TestFixtures.Create(kernel, 5100, 6100).Handle;
        var principal = TestFixtures.Create(kernel, 5101, 6101).Handle;
        var administration = kernel.MintCapability(new(6100), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, principal, "v6-memory",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var effect = kernel.MintCapability(new(6101), principal, ResourceKind.Compute,
            "compute:v6-memory", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var envelope = OperationObligationsV1Tests.Envelope(10);
        var grant = kernel.CapabilityAuthority.Mint(new(6101), new(6101), ResourceKind.Compute,
            "resource-use:v6-memory", CapabilityRights.Delegate, principal.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 2,
            resourceUse: new(1, OperationObligationsV1Tests.Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 2)).Value!.CapabilityId;
        var lease = kernel.Budgets.Reserve(principal,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        var input = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        var output = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        ProcessHandle? service = null;
        EndpointSessionHandle? session = null;
        InlineSipInvocationLease? invocation = null;
        if (withQualificationVertical)
        {
            service = TestFixtures.Create(kernel, 5102, 6102).Handle;
            var protocol = new ProtocolDefinitionV1("Qv1Managed", "qv1-managed-v1", "Ready", ["Done"],
                [new ProtocolMessageDescriptorV1(1, "Run",
                    requestPayload: new RequestPayloadDescriptorV1(RequestPayloadKind.Primitive,
                        "value", typeof(int).FullName!))],
                [new ProtocolTransitionV1(1, "Ready", "Done")]);
            var descriptor = kernel.RegisterService(service.Value, "qv1-managed",
                new("Qv1Managed", "1", "qv1-managed-v1"), protocol).Value!;
            session = kernel.OpenSession(principal, descriptor).Value;
            var begun = kernel.BeginInlineSessionInvocation(principal, service.Value,
                session.Value, 1, 1);
            Assert.True(begun.IsSuccess, begun.Message);
            invocation = begun.Value!;
        }
        IReadOnlyList<OperationRegionUseRequest> uses = new List<OperationRegionUseRequest>
        {
            new(input, RegionUseMode.ReadOnly, new(0, 16)),
        };
        if (includeStagedOutput)
            uses = uses.Append(new OperationRegionUseRequest(output, RegionUseMode.StagedOutput, new(0, 16))).ToArray();
        ComputeProviderCandidate? provider = null;
        ComputePlan? plan = null;
        if (withQualificationVertical)
        {
            provider = new(new(ProviderIdentity), 1,
                ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
                1024, 1, 1, true, false);
            var intent = new ComputeIntent(ComputeOperationKind.Copy,
                new(input, new(0, 16)), new(output, new(0, 16)),
                ComputePublicationPreference.StagedRequired, false, false, envelope);
            plan = kernel.PlanCompute(principal, intent, new(true, true, true), [provider]).Value!;
            Assert.True(kernel.ValidateComputePlanBeforeSubmit(principal, plan, [provider]).IsSuccess);
            uses = plan.RequiredRegionUses;
        }
        var operation = kernel.PrepareExternalOperation(principal, uses,
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1, 1, 1);
        var commit = kernel.PrepareResourceExternalAdmission(principal, effect, ResourceKind.Compute,
            "compute:v6-memory", 1, grant, 1, envelope, operation, dependencies,
            existingLease: lease, providerIdentity: ProviderIdentity, providerGeneration: 1).Value!;
        var now = time.GetUtcNow();
        var obligations = kernel.ConstructOperationObligationsV1(principal, operation, [envelope],
            RefinableRequirements(), new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks), session).Value!;
        var guarantees = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;
        var providerGenerations = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("765cdd6f-592b-4e0d-9218-ea31e418e069")]);
        var baseBinding = kernel.CreateSemanticExecutionBindingV1(obligations, guarantees, principal, lease,
            envelope, ProviderIdentity, ExecutionGuaranteesV1Tests.Request().Correlation,
            providerGenerations).Value!;
        var refinement = SemanticExecutionRefinementV1.Evaluate(baseBinding, obligations, guarantees).Value!;
        var requirements = MemoryRequirements();
        var extensionGuarantees = MemoryGuarantees(Memory());
        var memoryBinding = createMemoryBinding
            ? kernel.CreateV6MemorySemanticBinding(baseBinding, obligations, guarantees,
                requirements, extensionGuarantees, 1).Value!
            : null!;
        return new(kernel, principal, input, output, operation, lease, obligations, guarantees, baseBinding,
            memoryBinding, refinement, providerGenerations, dependencies, commit,
            plan, provider, service, session, invocation);
    }

    private static QualificationTransitionEvidenceV1[] QualificationEvidence(Context context)
    {
        var input = context.BaseBinding.Digest.Value;
        var generations = context.MemoryBinding.ExtensionBinding.Digest.Value;
        string[] owners =
        ["SipIntent", "CapabilityAuthority+EndpointSessionRegistry+RegionAuthority", "ResourceBudgetAuthority",
         "RuntimeKernel.OperationObligations", "ComputePlanner", "ProviderAdmission",
         "HybridCpuContractAdapter", "SemanticExecutionRefinement", "RuntimeKernel.SemanticBinding",
         "RuntimeLegalityService", "ExternalOperationAuthority", "ExternalOperationAuthority",
         "ExternalOperationAuthority", "ExternalOperationAuthority", "ResourceBudgetAuthority",
         "RegionAuthority+ExternalOperationAuthority"];
        string[] points =
        ["intent-created", "session-invocation-and-region-uses-acquired", "lease-reserved",
         "obligations-canonicalized", "plan-selected", "provider-decision",
         "guarantees-canonicalized", "refinement-digest", "binding-digest",
         "runtime-decision", "submit-winner", "completion-recorded", "visibility-recorded",
         "publication-owner-commit", "settlement-journal", "release-owner-commit"];
        string[] rollback =
        ["reject-before-admission", "close-session-and-release-uses", "cancel-before-submit",
         "reject-malformed", "replan", "cancel-before-submit", "reject-mismatch",
         "deny-before-submit", "rebuild-on-drift", "deny-before-submit", "contain-after-submit",
         "quarantine-on-ambiguous-completion", "withhold-publication", "publication-is-final",
         "quarantine-on-settlement-failure", "idempotent-release"];
        return Enum.GetValues<QualificationVerticalStepV1>().Select((step, index) =>
            new QualificationTransitionEvidenceV1(1, (ulong)index + 1, step, owners[index],
                input, generations, QualificationFailureStateV1.None, points[index], rollback[index])).ToArray();
    }

    private static MemorySemanticsV1 Memory() => MemorySemanticsV1Tests.StagedOperation();
    private static OperationSemanticExtensionsV1 MemoryRequirements() =>
        OperationSemanticExtensionsV1.Create([Memory().ToClause(SemanticExtensionRequirement.Mandatory)]);
    private static ExecutionGuaranteeExtensionsV1 MemoryGuarantees(MemorySemanticsV1 memory) =>
        ExecutionGuaranteeExtensionsV1.Create([memory.ToClause(SemanticExtensionRequirement.Mandatory)]);

    private static OperationSemanticRequirementsV1 RefinableRequirements() => new(
        Advisory(IsolationClassV1.DomainSeparated),
        Required(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
        Advisory(ReplayClassV1.None), Advisory(DeterminismClassV1.StableOrdering),
        Required(CancellationClassV1.ExactAcknowledgement), Advisory(ContainmentClassV1.None),
        Advisory(LocalityClassV1.Any), Advisory(ResourceAssuranceV1.RuntimeEnforced));
    private static SemanticRequirementV1<T> Required<T>(T value) where T : struct, Enum => new(1,
        SemanticRequirementStrengthV1.Mandatory, value);
    private static SemanticRequirementV1<T> Advisory<T>(T value) where T : struct, Enum => new(1,
        SemanticRequirementStrengthV1.Advisory, value);

    private sealed class Provider(SemanticExecutionBindingV1 binding, Action<int>? beforeDecision = null) : ISemanticProviderAdmissionService
    {
        private int revalidations;
        public KernelResult<ProviderAdmissionDecisionV1> Revalidate(SemanticExecutionBindingV1 _)
        {
            beforeDecision?.Invoke(++revalidations);
            return KernelResult<ProviderAdmissionDecisionV1>.Ok(new(1, binding.Digest, binding.ProviderIdentity,
                binding.ProviderGenerationDigest, binding.ProviderRequestCorrelation,
                SemanticGateDecisionStatusV1.Allowed, "v6-test-provider"));
        }
    }

    private sealed class Runtime(SemanticExecutionBindingV1 binding, Action? beforeDecision = null)
        : IRuntimeLegalityService
    {
        public KernelResult<RuntimeLegalityDecisionV1> Evaluate(SemanticExecutionBindingV1 _)
        {
            beforeDecision?.Invoke();
            return KernelResult<RuntimeLegalityDecisionV1>.Ok(new(1, binding.Digest, "v6-test-runtime", 1,
                SemanticGateDecisionStatusV1.Allowed, "v6-test-runtime-evidence"));
        }
    }

    private sealed class PreSubmitTraceSink(Action onCancelled) : ISemanticTraceSinkV1
    {
        private bool invoked;
        internal List<SemanticTraceEventV1> Events { get; } = [];
        public bool TryRecord(SemanticTraceEventV1 item)
        {
            Events.Add(item);
            if (!invoked && item.Kind == SemanticTraceEventKindV1.CancelledBeforeSubmit)
            {
                invoked = true;
                onCancelled();
            }
            return true;
        }
    }

    private sealed class CollectingTraceSink : ISemanticTraceSinkV1
    {
        internal List<SemanticTraceEventV1> Events { get; } = [];

        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            return true;
        }
    }

    private sealed class TeardownTraceSink(Action onTeardown) : ISemanticTraceSinkV1
    {
        internal List<SemanticTraceEventV1> Events { get; } = [];
        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            if (traceEvent.Kind is SemanticTraceEventKindV1.CancellationRequested or SemanticTraceEventKindV1.Quarantined)
                onTeardown();
            return true;
        }
    }

    private sealed class ThrowingTraceSink : ISemanticTraceSinkV1
    {
        public bool TryRecord(SemanticTraceEventV1 traceEvent) =>
            throw new InvalidOperationException("observation failure");
    }

    private sealed class FailingTraceSink(int failureCall, bool throws) : ISemanticTraceSinkV1
    {
        internal int Calls { get; private set; }
        internal List<SemanticTraceEventV1> Accepted { get; } = [];

        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Calls++;
            if (Calls == failureCall)
            {
                if (throws)
                    throw new InvalidOperationException("observation failure");
                return false;
            }
            Accepted.Add(traceEvent);
            return true;
        }
    }

    private sealed class ReentrantTraceSink(Action onFirstSubmit) : ISemanticTraceSinkV1
    {
        private bool _invoked;
        internal List<SemanticTraceEventV1> Events { get; } = [];

        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            if (!_invoked && traceEvent.Kind == SemanticTraceEventKindV1.Submit)
            {
                _invoked = true;
                onFirstSubmit();
            }
            return true;
        }
    }

    private sealed class ReentrantReleaseTraceSink(Action release) : ISemanticTraceSinkV1
    {
        private bool invoked;
        internal List<SemanticTraceEventV1> Events { get; } = [];

        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            if (!invoked && traceEvent.Kind == SemanticTraceEventKindV1.Settled)
            {
                invoked = true;
                release();
            }
            return true;
        }
    }

    private sealed record Context(RuntimeKernel Kernel, ProcessHandle Principal, RegionHandle Input, RegionHandle Output,
        ExternalOperationHandle Operation, BudgetReservationHandle Lease, OperationObligationsV1 Obligations,
        ExecutionGuaranteesV1 Guarantees, SemanticExecutionBindingV1 BaseBinding,
        V6MemorySemanticBindingV1 MemoryBinding, SemanticRefinementProofV1 Refinement,
        Hc.ExternalGenerationSet ProviderGenerations, OperationDependencySnapshot Dependencies,
        ResourceAdmissionCommit Commit, ComputePlan? Plan, ComputeProviderCandidate? Provider,
        ProcessHandle? Service, EndpointSessionHandle? Session, InlineSipInvocationLease? Invocation);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private const string ProviderIdentity = "hybridcpu:external-runtime";
}
