using System.Text;
using System.Security.Cryptography;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class V6MemoryRuntimeEnforcementTests
{
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
            SemanticTraceEventKindV1.Quarantined], trace.Select(item => item.Kind));
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

        Assert.Throws<NotSupportedException>(() =>
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
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined],
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
            SemanticTraceEventKindV1.Quarantined], sink.Events.Select(traceEvent => traceEvent.Kind));
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
        bool withQualificationVertical = false)
    {
        var time = new TestTimeProvider(new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
        var kernel = new RuntimeKernel(null, time);
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

    private sealed class Provider(SemanticExecutionBindingV1 binding) : ISemanticProviderAdmissionService
    {
        public KernelResult<ProviderAdmissionDecisionV1> Revalidate(SemanticExecutionBindingV1 _) =>
            KernelResult<ProviderAdmissionDecisionV1>.Ok(new(1, binding.Digest, binding.ProviderIdentity,
                binding.ProviderGenerationDigest, binding.ProviderRequestCorrelation,
                SemanticGateDecisionStatusV1.Allowed, "v6-test-provider"));
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

    private sealed class CollectingTraceSink : ISemanticTraceSinkV1
    {
        internal List<SemanticTraceEventV1> Events { get; } = [];

        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
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
