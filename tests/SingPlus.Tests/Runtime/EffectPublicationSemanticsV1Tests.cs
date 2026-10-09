using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class EffectPublicationSemanticsV1Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessStopAfterInitialSnapshotRefusesActionBeforeOwnerAdmission(bool sourceHook)
    {
        var c = Ready(ExternalPublicationPolicy.Staged);
        var before = c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!;
        var blocker = new BlockingComposedWork();
        c.Kernel.RegisterComposedWorkTeardownParticipant(blocker);
        c.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () =>
        {
            Assert.False(c.Kernel.TerminateProcess(c.Owner).IsSuccess);
            Assert.Equal(before.Transitions, c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!.Transitions);
        };
        var actionCalls = 0;
        var sourceCalls = 0;
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = sourceHook
                ? c.Kernel.PublishExternalOperation(c.Owner, c.Operation, Dependencies,
                    new(ExternalPublicationPolicy.Staged), _ => actionCalls++, commit => { sourceCalls++; return commit(); })
                : c.Kernel.PublishExternalOperation(c.Owner, c.Operation, Dependencies,
                    new(ExternalPublicationPolicy.Staged), () => actionCalls++);
        }
        finally { c.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.Equal(0, actionCalls);
        Assert.Equal(0, sourceCalls);
        Assert.Equal(before.Transitions, c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!.Transitions);
        blocker.Blocked = false;
        Assert.False(c.Kernel.TerminateProcess(c.Owner).IsSuccess);
        Assert.True(c.Kernel.ReleaseExternalOperation(c.Owner, c.Operation, new(true, false)).IsSuccess);
        Assert.True(c.Kernel.TerminateProcess(c.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void SourceAdmissionReplyCannotCreateDecisionOrLeaveInterlock(int fault)
    {
        var c = Ready(ExternalPublicationPolicy.Staged);
        var before = c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!;
        Func<KernelResult<ExternalOperationSnapshot>>? delayed = null;
        var calls = 0;
        var result = c.Kernel.ExternalOperations.Publish(c.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), _ => calls++, null, admit =>
            {
                delayed = admit;
                if (fault == 0) return KernelResult<ExternalOperationSnapshot>.Ok(before);
                if (fault == 3) return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformDenied, "Controlled refusal.");
                if (fault == 4)
                {
                    var foreign = Task.Run(admit);
                    Assert.True(foreign.Wait(TimeSpan.FromSeconds(5)));
                    Assert.False(foreign.Result.IsSuccess);
                    return KernelResult<ExternalOperationSnapshot>.Ok(before);
                }
                Assert.True(admit().IsSuccess);
                if (fault == 2) throw new InvalidOperationException("Controlled admission reply loss.");
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformDenied, "Controlled late refusal.");
            });
        Assert.False(result.IsSuccess);
        Assert.Equal(0, calls);
        Assert.Equal(before.Transitions, c.Kernel.QueryExternalOperation(c.Owner, c.Operation).Value!.Transitions);
        Assert.False(delayed!().IsSuccess);
        Assert.True(c.Kernel.PublishExternalOperation(c.Owner, c.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), () => calls++).IsSuccess);
        Assert.Equal(1, calls);
    }

    private sealed class BlockingComposedWork : IComposedWorkTeardownParticipant
    {
        internal bool Blocked { get; set; } = true;
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) =>
            Blocked ? KernelResult.Fail(KernelError.PlatformBindingDraining, "Controlled pending composition.") : KernelResult.Ok();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessStopDuringPublicationRetainsLifetimeUntilActionReturnsAndRefusesCommit(bool sourceHook)
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        var before = context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        ExternalPublicationDecisionV1 decision = default;
        Action<ExternalPublicationDecisionV1> action = current =>
        {
            decision = current;
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        };
        var publication = Task.Run(() => sourceHook
            ? context.Kernel.PublishExternalOperation(context.Owner, context.Operation, Dependencies,
                new(ExternalPublicationPolicy.Staged), action, commit => commit())
            : context.Kernel.PublishExternalOperation(context.Owner, context.Operation, Dependencies,
                new(ExternalPublicationPolicy.Staged), action));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(context.Kernel.TerminateProcess(context.Owner).IsSuccess);
            var pending = context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!;
            Assert.Equal(before.Disposition, pending.Disposition);
            Assert.Equal(before.Transitions, pending.Transitions);
            var called = false;
            Assert.False(context.Kernel.PublishExternalOperation(context.Owner, context.Operation, Dependencies,
                new(ExternalPublicationPolicy.Staged), () => called = true).IsSuccess);
            Assert.False(called);
            Assert.Equal(pending.Transitions, context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!.Transitions);
            Assert.False(context.Kernel.ReleaseExternalOperation(context.Owner, context.Operation, new(true, false)).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.ExternalEffectUncontained, (await publication).Error);
        var snapshot = context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Visible, snapshot.State);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, snapshot.EffectBoundary);
        Assert.Single(snapshot.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
        Assert.DoesNotContain(snapshot.Transitions, transition => transition.Event is "Published" or "TeardownResultDiscarded");
        Assert.False(context.Kernel.TerminateProcess(context.Owner).IsSuccess);
        var trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        // Controlled closure evidence tests the existing retry path, not physical closure.
        Assert.True(context.Kernel.ReconcileFailedExternalPublication(context.Owner, context.Operation,
            decision, KernelResult.Ok).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Owner, context.Operation, new(true, false)).IsSuccess);
        Assert.True(context.Kernel.TerminateProcess(context.Owner).IsSuccess);
    }

    [Fact]
    public void SourceHookRunsOutsideProcessStopLockAndCannotPublishAfterStop()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        var actionCalls = 0;
        var result = context.Kernel.PublishExternalOperation(context.Owner, context.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), _ => actionCalls++, commit =>
            {
                var stopping = Task.Run(() => context.Kernel.TerminateProcess(context.Owner));
                Assert.True(stopping.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(stopping.Result.IsSuccess);
                return commit();
            });
        Assert.Equal(1, actionCalls);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        var snapshot = context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!;
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, snapshot.EffectBoundary);
        Assert.DoesNotContain(snapshot.Transitions, transition => transition.Event == "Published");
    }

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
    public async Task StagedDecisionIsExactCallbackRunsOutsideOwnerLockAndLossPreventsCommit()
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
        try
        {
            var cancel = context.Kernel.CancelExternalOperation(context.Owner, context.Operation, true);
            var loss = context.Kernel.RecordExternalOperationProviderLoss(context.Owner, context.Operation);
            Assert.Equal(KernelError.InvalidTransition, cancel.Error);
            Assert.True(loss.IsSuccess, loss.Message);
        }
        finally { release.Set(); }
        var result = await publish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(ExternalPublicationDecisionV1.CurrentVersion, observed.Version);
        Assert.Equal(context.Binding, observed.Binding);
        Assert.Equal(Dependencies, observed.Dependencies);
        Assert.False(observed.IsReusablePermit);
        Assert.False(observed.AuthorizesNewExecution);
        var snapshot = context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Visible, snapshot.State);
        Assert.Single(snapshot.Transitions, transition => transition.Event == "ProviderLost");
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, snapshot.EffectBoundary);
        Assert.DoesNotContain(snapshot.Transitions, transition => transition.Event == "Published");
    }

    [Fact]
    public void RegionDamageDuringPublicationCannotBecomePublishedEvidence()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        var admission = context.Kernel.QueryExternalOperation(context.Owner,
            context.Operation).Value!.Admission!;
        var output = admission.RegionUses.Last();
        var calls = 0;

        var published = context.Kernel.PublishExternalOperation(context.Owner,
            context.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), _ =>
            {
                calls++;
                var damage = context.Kernel.Regions.QuarantineSubrange(output.Region,
                    admission.Principal, new ProviderHealthEvidenceV1(1,
                        new("test-provider", "bank-0", 1, 1), 1,
                        ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission,
                        output.Range));
                Assert.True(damage.IsSuccess, damage.Message);
            });

        Assert.Equal(1, calls);
        Assert.Equal(KernelError.ExternalEffectUncontained, published.Error);
        var owner = context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Visible, owner.State);
        Assert.Equal(ExternalOperationDisposition.Faulted, owner.Disposition);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, owner.EffectBoundary);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.ReleaseExternalOperation(context.Owner, context.Operation,
                new(ProviderResourcesClosed: true, ProviderUnavailable: false)).Error);
        Assert.Equal(KernelError.InvalidTransition,
            context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
                Dependencies, new(ExternalPublicationPolicy.Staged), _ => Assert.Fail()).Error);
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
