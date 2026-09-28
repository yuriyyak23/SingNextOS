using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class EffectPublicationSemanticsV1Tests
{
    [Fact]
    public void ExistingPoliciesMapOneWayWithoutInventingDurabilityCompensationOrCommutativity()
    {
        var staged = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.StagedReversibleUntilPublish, ExternalReplayProtection.None, false));
        var snapshot = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.SnapshotOrIdempotenceRequired, ExternalReplayProtection.SnapshotRestore, true));
        var idempotent = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.SnapshotOrIdempotenceRequired, ExternalReplayProtection.Idempotent, true));
        var irreversible = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true));

        Assert.True(staged.Staged && staged.Reversible);
        Assert.False(staged.ExternallyObservable || staged.AuthorizesEffect || staged.AuthorizesPublication);
        Assert.True(snapshot.LocallyIrreversible && snapshot.ExternallyObservable);
        Assert.False(snapshot.Idempotent);
        Assert.True(idempotent.Idempotent);
        Assert.True(irreversible.LocallyIrreversible && irreversible.ExternallyObservable);
        Assert.All(new[] { staged, snapshot, idempotent, irreversible }, value =>
        {
            Assert.False(value.Durable);
            Assert.False(value.Compensatable);
            Assert.False(value.Commutative);
        });
    }

    [Fact]
    public void InvalidOrUnknownTraitCombinationsFailClosed()
    {
        Assert.Throws<NotSupportedException>(() => new EffectSemanticsV1(2, false, false,
            false, false, false, false, false, false).Canonicalize());
        Assert.Throws<ArgumentException>(() => new EffectSemanticsV1(1, true, true,
            false, true, false, false, false, false).Canonicalize());
        Assert.Throws<ArgumentException>(() => new EffectSemanticsV1(1, false, true,
            true, false, false, false, false, false).Canonicalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => ExternalEffectSemantics.FromPolicy(
            new((ExternalEffectClass)99, ExternalReplayProtection.None, false)));
    }

    [Fact]
    public async Task StagedDecisionIsExactCallbackRunsOutsideOwnerLockAndRacesCannotCrossIt()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ExternalPublicationDecisionV1 observed = default;
        var publish = Task.Run(() => context.Kernel.PublishExternalOperation(context.Owner,
            context.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), decision =>
            {
                observed = decision;
                entered.Set();
                Assert.True(Task.Run(() => context.Kernel.QueryExternalOperation(context.Owner, context.Operation))
                    .Wait(TimeSpan.FromSeconds(2)));
                release.Wait(TimeSpan.FromSeconds(5));
            }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var cancel = context.Kernel.CancelExternalOperation(context.Owner, context.Operation, true);
        var loss = context.Kernel.RecordExternalOperationProviderLoss(context.Owner, context.Operation);
        Assert.Equal(KernelError.InvalidTransition, cancel.Error);
        Assert.Equal(KernelError.InvalidTransition, loss.Error);
        release.Set();
        var result = await publish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ExternalPublicationDecisionV1.CurrentVersion, observed.Version);
        Assert.Equal(context.Binding, observed.Binding);
        Assert.Equal(Dependencies, observed.Dependencies);
        Assert.False(observed.IsReusablePermit);
        Assert.False(observed.AuthorizesNewExecution);
        Assert.Equal(ExternalOperationState.Published, result.Value!.State);
    }

    [Fact]
    public void DirectCoherentContourNeverExecutesFictitiousWithheldPublicationAction()
    {
        var context = Ready(ExternalPublicationPolicy.DirectCoherent);
        var callback = false;

        var result = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.DirectCoherent), _ => callback = true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(callback);
        Assert.Equal(ExternalEffectBoundaryState.Irreversible, result.Value!.EffectBoundary);
        Assert.Contains(result.Value.Transitions, transition => transition.Event == "DirectPublicationObserved");
        Assert.DoesNotContain(result.Value.Transitions, transition => transition.Event == "PublicationDecided");
    }

    [Fact]
    public void StagedProviderActionFailureRetainsPossibleEffectUntilExactClosure()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        ExternalPublicationDecisionV1 decision = default;

        var failed = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.Staged),
            (ExternalPublicationDecisionV1 current) =>
            {
                decision = current;
                throw new InvalidOperationException("provider fault after possible effect");
            });
        var replay = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.Staged), _ => Assert.Fail("discarded action replayed"));

        Assert.Equal(KernelError.ExternalEffectUncontained, failed.Error);
        Assert.Equal(KernelError.InvalidTransition, replay.Error);
        Assert.Equal(ExternalOperationDisposition.Faulted,
            context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!.Disposition);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible,
            context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!.EffectBoundary);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.CancelExternalOperation(context.Owner, context.Operation, true).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.ReleaseExternalOperation(context.Owner, context.Operation,
                new(ProviderResourcesClosed: true, ProviderUnavailable: false)).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.ExternalOperations.ReconcileFailedPublication(context.Operation, decision,
                () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "effect not closed")).Error);
        var closed = context.Kernel.ExternalOperations.ReconcileFailedPublication(context.Operation, decision,
            KernelResult.Ok);
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(ExternalOperationDisposition.Discarded, closed.Value!.Disposition);
        Assert.Equal(ExternalEffectBoundaryState.StagedPending, closed.Value.EffectBoundary);
    }

    [Fact]
    public void ManagedDurabilityReconciliationUsesExactPublicationOwnerDecision()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        var semantics = new PersistenceSemanticsV1(1,
            V6ManagedDurableOutputModel.QualifiedProviderIdentity, "media:model:owner",
            PersistenceDomainClassV1.NamedManagedModel,
            PersistenceOrderingV1.DataThenMetadataThenCommitRecord, 7, 11, true);
        var durable = new V6ManagedDurableOutputModel(semantics);
        var binding = new DurableOutputBindingV1(1, "operation:owner-publication", 13,
            new string('a', 64), new string('b', 64), 17, semantics);
        ExternalPublicationDecisionV1 observed = default;
        var possibleEffectClosed = false;
        var attempted = durable.PersistAndPublish(binding, 3, 5, 7,
            () => 7, () => 11, () => PersistenceDomainClassV1.NamedManagedModel,
            () =>
            {
                var publication = context.Kernel.PublishExternalOperation(context.Owner,
                    context.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), decision =>
                    {
                        observed = decision;
                        throw new InvalidOperationException("ambiguous managed publication");
                    });
                return KernelResult.Fail(publication.Error, publication.Message!);
            });

        Assert.Equal(KernelError.ExternalEffectUncontained, attempted.Error);
        Assert.True(durable.Recover().HasAmbiguousPublication);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            durable.ReconcileAmbiguousPublication(binding, () =>
            {
                var owner = context.Kernel.ExternalOperations.ReconcileFailedPublication(
                    context.Operation, observed,
                    () => possibleEffectClosed ? KernelResult.Ok() :
                        KernelResult.Fail(KernelError.ExternalEffectUncontained, "effect not closed"));
                return owner.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(owner.Error, owner.Message!);
            }).Error);
        possibleEffectClosed = true;
        Assert.True(durable.ReconcileAmbiguousPublication(binding, () =>
        {
            var owner = context.Kernel.ExternalOperations.ReconcileFailedPublication(
                context.Operation, observed,
                () => possibleEffectClosed ? KernelResult.Ok() :
                    KernelResult.Fail(KernelError.ExternalEffectUncontained, "effect not closed"));
            return owner.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(owner.Error, owner.Message!);
        }).IsSuccess);
        Assert.Equal(ExternalOperationDisposition.Discarded,
            context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!.Disposition);
    }

    [Fact]
    public void AmbiguousPublicationClosureRunsOutsideOwnerLockAndHasOneWinner()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        ExternalPublicationDecisionV1 decision = default;
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
                Dependencies, new(ExternalPublicationPolicy.Staged), current =>
                {
                    decision = current;
                    throw new InvalidOperationException("effect may exist");
                }).Error);
        var wrongClosureCalls = 0;
        Assert.Equal(KernelError.InvalidTransition,
            context.Kernel.ExternalOperations.ReconcileFailedPublication(context.Operation,
                decision with { DecisionGeneration = decision.DecisionGeneration + 1 },
                () => { wrongClosureCalls++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, wrongClosureCalls);

        var outer = context.Kernel.ExternalOperations.ReconcileFailedPublication(
            context.Operation, decision, () =>
            {
                var inner = Task.Run(() => context.Kernel.ExternalOperations.ReconcileFailedPublication(
                    context.Operation, decision, KernelResult.Ok));
                Assert.True(inner.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(inner.Result.IsSuccess, inner.Result.Message);
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.StaleGeneration, outer.Error);
        Assert.Equal(ExternalOperationDisposition.Discarded,
            context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!.Disposition);
    }

    [Fact]
    public void ProviderLossDuringAmbiguousPublicationClosureCannotClearPossibleEffect()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        ExternalPublicationDecisionV1 decision = default;
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
                Dependencies, new(ExternalPublicationPolicy.Staged), current =>
                {
                    decision = current;
                    throw new InvalidOperationException("publication outcome unknown");
                }).Error);

        var reconciled = context.Kernel.ExternalOperations.ReconcileFailedPublication(
            context.Operation, decision, () =>
            {
                var loss = Task.Run(() => context.Kernel.RecordExternalOperationProviderLoss(
                    context.Owner, context.Operation));
                Assert.True(loss.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(loss.Result.IsSuccess, loss.Result.Message);
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.StaleGeneration, reconciled.Error);
        var after = context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!;
        Assert.Equal(ExternalOperationDisposition.ProviderLost, after.Disposition);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, after.EffectBoundary);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.ReleaseExternalOperation(context.Owner, context.Operation,
                new(ProviderResourcesClosed: true, ProviderUnavailable: true)).Error);
    }

    private static ReadyContext Ready(ExternalPublicationPolicy policy)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 1300, 2400).Handle;
        var input = kernel.AllocateBuffer<byte>(owner, 8).Value!.Handle;
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!.Handle;
        var preparation = kernel.PrepareExternalOperation(owner,
            [new(input, RegionUseMode.ReadOnly, new(0, 8)),
             new(output, policy == ExternalPublicationPolicy.Staged ? RegionUseMode.StagedOutput : RegionUseMode.ExclusiveWrite, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, policy,
            policy == ExternalPublicationPolicy.DirectCoherent
                ? new(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true)
                : null).Value!;
        Assert.True(kernel.AdmitExternalOperation(owner, preparation.Operation, Dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(owner, preparation.Operation, Dependencies).Value!;
        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        return new(kernel, owner, preparation.Operation, binding);
    }

    private static readonly OperationDependencySnapshot Dependencies = new(7, 11, 13, 17);
    private sealed record ReadyContext(RuntimeKernel Kernel, ProcessHandle Owner,
        ExternalOperationHandle Operation, OperationBinding Binding);
}
