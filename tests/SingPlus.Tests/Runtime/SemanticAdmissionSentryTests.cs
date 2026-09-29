using Hc = HybridCPU.ExternalRuntime.Contracts;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class SemanticAdmissionSentryTests
{
    [Fact]
    public void SharedSessionRejectsSemanticsOutsideExactStagedContour()
    {
        var context = Create();
        using var commit = context.Commit;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        var session = new ExternalOperationAdapterSession(provider);
        var unsupported = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.Unsupported,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Admit(unsupported));
        var owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Admitted, owner.State);
    }

    [Fact]
    public void SharedSessionCancellationAcknowledgesOnlyBeforeSubmit()
    {
        var context = Create();
        using var commit = context.Commit;
        var calls = 0;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Cancel(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(0, calls);
        var owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, owner.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SharedSessionPostSubmitCancelAndProviderLossDoNotProveClosure()
    {
        var context = Create();
        using var commit = context.Commit;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        var submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                var attached = provider.AttachCommittedBinding(binding);
                if (!attached.IsSuccess) return attached;
                return session.Submit(semantic.Correlation) == ExternalOperationAdapterStatus.Accepted
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "submit receipt rejected");
            });
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Cancel(semantic.Correlation));
        Assert.Equal(ExternalOperationDisposition.CancellationPending,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
        Assert.False(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(ProviderResourcesClosed: false, ProviderUnavailable: false)).IsSuccess);
        Assert.True(context.Kernel.RecordExternalOperationProviderLoss(context.Principal,
            context.Operation).IsSuccess);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
        Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
    }

    [Fact]
    public void SharedSessionCancelAfterOwnerSubmitCannotReauthorizeExecution()
    {
        var context = Create();
        using var commit = context.Commit;
        var calls = 0;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        var result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Cancel(semantic.Correlation));
                return provider.AttachCommittedBinding(binding);
            });
        Assert.False(result.IsSuccess);
        Assert.Equal(0, calls);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SharedSessionRechecksOwnerAfterBindingAttachBeforeExecution()
    {
        var context = Create();
        using var commit = context.Commit;
        var calls = 0;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));

        var result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                Assert.True(provider.AttachCommittedBinding(binding).IsSuccess);
                var cancelled = context.Kernel.CancelExternalOperation(context.Principal,
                    context.Operation, providerCancellationSupported: false);
                Assert.True(cancelled.IsSuccess);
                Assert.Equal(ExternalOperationDisposition.CancellationPending,
                    cancelled.Value!.Disposition);
                Assert.NotEqual(ExternalOperationAdapterStatus.Accepted,
                    session.Submit(semantic.Correlation));
                return KernelResult.Fail(KernelError.PlatformFaulted, "Owner changed after binding attach.");
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, calls);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SharedSessionObservesOnlyCommittedOwnerLifecycleStages()
    {
        var context = Create();
        using var commit = context.Commit;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        var submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                var attached = provider.AttachCommittedBinding(binding);
                if (!attached.IsSuccess) return attached;
                return session.Submit(semantic.Correlation) == ExternalOperationAdapterStatus.Accepted
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "submit receipt rejected");
            });
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(ExternalOperationAdapterStatus.Pending, session.Poll(semantic.Correlation));
        Assert.False(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(submitted.Value!, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(submitted.Value!, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Pending, session.Poll(semantic.Correlation));
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(submitted.Value!, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Pending, session.Poll(semantic.Correlation));
        Assert.True(context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
            context.Dependencies, new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Pending, session.Poll(semantic.Correlation));
        Assert.Equal(KernelError.InvalidTransition,
            context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
                new(true, false)).Error);
        var usage = new ExternalResourceUsageEvidence(1, submitted.Value!, ProviderIdentity, 1,
            new(1, $"{ProviderIdentity}:measurement", "1", ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds), Guid.Parse("5552d25a-c968-4ea9-82cf-f2eb38df910f"),
            ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
            new(1, 0, 0, 5, 0, 0, 0, 0, 0, 0), 5, 1);
        Assert.True(context.Kernel.SettleResourceExternalOperation(context.Principal, usage).IsSuccess);
        Assert.True(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
            new(true, false)).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Poll(semantic.Correlation));
    }

    [Fact]
    public void SharedSessionDoesNotReportAmbiguousPublicationAsPublishedOrReleased()
    {
        var context = Create();
        using var commit = context.Commit;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        var submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                var attached = provider.AttachCommittedBinding(binding);
                if (!attached.IsSuccess) return attached;
                return session.Submit(semantic.Correlation) == ExternalOperationAdapterStatus.Accepted
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "submit receipt rejected");
            });
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Principal,
            new(submitted.Value!, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Principal,
            new(submitted.Value!, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
                context.Dependencies, new(ExternalPublicationPolicy.Staged),
                () => throw new InvalidOperationException("possible publication")).Error);
        Assert.Equal(ExternalOperationDisposition.Faulted,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
        Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
                new(true, false)).Error);
    }

    [Fact]
    public void PackagedSessionExecutesTheSingleCommittedSingNextBinding()
    {
        var context = Create();
        using var commit = context.Commit;
        var calls = 0;
        OperationBinding? executed = null;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, binding =>
            {
                calls++;
                executed = binding;
                return KernelResult.Ok();
            });
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        var result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                var attached = provider.AttachCommittedBinding(binding);
                if (!attached.IsSuccess) return attached;
                return session.Submit(semantic.Correlation) == ExternalOperationAdapterStatus.Accepted
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.PlatformFaulted, "Packaged session did not accept the exact execution receipt.");
            });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(result.Value, executed);
        Assert.Equal(context.Operation, result.Value!.Operation);
        Assert.Equal(1, calls);
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(BudgetReservationState.Consuming,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void PackagedSessionCannotExecuteBeforeOwnerCommitOrAfterLegalityDenial()
    {
        var context = Create();
        using var commit = context.Commit;
        var calls = 0;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Stale, session.Submit(semantic.Correlation));
        Assert.Equal(0, calls);

        var denied = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding, deny: true), _ =>
            { calls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(0, calls);
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SharedSessionExecutionFailureQuarantinesTheSingleOwner()
    {
        var context = Create();
        using var commit = context.Commit;
        var calls = 0;
        var provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Fail(KernelError.PlatformFaulted, "unknown effect"); });
        var session = new ExternalOperationAdapterSession(provider);
        var semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));

        var result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                var attached = provider.AttachCommittedBinding(binding);
                if (!attached.IsSuccess) return attached;
                return session.Submit(semantic.Correlation) == ExternalOperationAdapterStatus.Accepted
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "execution outcome is uncertain");
            });

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, calls);
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
    }

    [Fact]
    public void BindingAwareSubmitPassesOnlyCommittedSingNextOperationToProviderCallback()
    {
        var context = Create();
        using var commit = context.Commit;
        OperationBinding? observed = null;

        var submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                observed = binding;
                var owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
                Assert.Equal(ExternalOperationState.Submitted, owner.State);
                Assert.Equal(context.Operation, owner.Operation);
                Assert.Equal(binding, owner.Binding);
                Assert.Equal(BudgetReservationState.Consuming,
                    context.Kernel.Budgets.Query(context.Lease).Value!.State);
                return KernelResult.Ok();
            });

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(submitted.Value, observed);
        Assert.Equal(KernelError.InvalidTransition,
            context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
                commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
                context.Refinement, ProviderIdentity, context.ProviderGenerations,
                new Provider(context.Binding), new Runtime(context.Binding), _ => KernelResult.Ok()).Error);
    }

    [Fact]
    public void BindingAwareCallbackIsNotInvokedWhenIndependentLegalityDenies()
    {
        var context = Create();
        using var commit = context.Commit;
        var callbacks = 0;

        var result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding, deny: true), _ =>
            {
                callbacks++;
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void BindingAwareProviderFailureRetainsPossibleEffectAndBudgetQuarantine()
    {
        var context = Create();
        using var commit = context.Commit;
        OperationBinding? observed = null;

        var result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                observed = binding;
                return KernelResult.Fail(KernelError.PlatformFaulted, "provider outcome unknown");
            });

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.NotNull(observed);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
        var owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        Assert.Equal(observed, owner.Binding);
    }


    [Fact]
    public void ExactFourGateConjunctionSubmitsOnceAndSequentialReplayCannotCompensate()
    {
        var context = Create();
        using var commit = context.Commit;
        var callbacks = 0;

        var first = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });
        var duplicate = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(KernelError.InvalidTransition, duplicate.Error);
        Assert.Equal(1, callbacks);
        Assert.Equal(BudgetReservationState.Consuming,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProviderRefinementOrRuntimeGateFailureCompensatesBeforeSubmit(int failingGate)
    {
        var context = Create();
        using var commit = context.Commit;
        var callbacks = 0;
        var provider = new Provider(context.Binding, failingGate == 0);
        var runtime = new Runtime(context.Binding, failingGate == 2);
        var proof = failingGate == 1
            ? context.Refinement with { Digest = new("00") }
            : context.Refinement;

        var result = context.Kernel.SubmitSemanticResourceExternalAdmission(commit, context.Dependencies,
            context.Binding, context.Obligations, context.Guarantees, proof,
            ProviderIdentity, context.ProviderGenerations, provider, runtime,
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
        Assert.NotEqual(ExternalOperationState.Submitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Fact]
    public void RevocationDuringIndependentRuntimeLegalityIsCaughtByFinalOwnerRead()
    {
        var context = Create();
        using var commit = context.Commit;
        var runtime = new Runtime(context.Binding, beforeDecision: () =>
            Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.EffectCapability).IsSuccess));

        var result = Submit(context, new Provider(context.Binding), runtime, KernelResult.Ok);

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void CachedRefinementAndGateEvidenceCannotSubmitAfterRevocation()
    {
        var context = Create();
        using var commit = context.Commit;
        Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.EffectCapability).IsSuccess);
        var callbacks = 0;

        var result = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SessionCloseDuringRuntimeLegalityIsCaughtBeforeSubmit()
    {
        var context = Create(withSession: true);
        using var commit = context.Commit;
        var runtime = new Runtime(context.Binding, beforeDecision: () =>
            Assert.True(context.Kernel.CloseSession(context.Principal, context.Session!.Value).IsSuccess));

        var result = Submit(context, new Provider(context.Binding), runtime, KernelResult.Ok);

        Assert.Equal(KernelError.SessionClosed, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void ProviderGenerationDriftFailsBeforeRuntimeLegalityAndSubmit()
    {
        var context = Create();
        using var commit = context.Commit;
        var stale = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("70db585c-12e8-4c7b-a8dc-876f69c899c4")]);
        var runtimeCalls = 0;

        var result = context.Kernel.SubmitSemanticResourceExternalAdmission(commit, context.Dependencies,
            context.Binding, context.Obligations, context.Guarantees, context.Refinement,
            ProviderIdentity, stale, new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => runtimeCalls++), KernelResult.Ok);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, runtimeCalls);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void ProviderAdmissionRevokedDuringRuntimeLegalityFailsBeforeSubmit()
    {
        var context = Create();
        using var commit = context.Commit;
        var liveGeneration = 1;
        var providerReads = 0;
        var callbacks = 0;
        var provider = new Provider(context.Binding, denyWhen: () =>
        {
            providerReads++;
            return liveGeneration != 1;
        });
        var runtime = new Runtime(context.Binding, beforeDecision: () => liveGeneration = 2);

        var result = Submit(context, provider, runtime, () =>
        {
            callbacks++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(2, providerReads);
        Assert.Equal(0, callbacks);
        Assert.NotEqual(ExternalOperationState.Submitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public async Task ConcurrentCallersHaveOneIrreversibleSubmitWinnerAndNoRefund()
    {
        var context = Create();
        using var commit = context.Commit;
        using var barrier = new Barrier(2);
        var callbacks = 0;
        KernelResult<OperationBinding> Invoke() => Submit(context,
            new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => barrier.SignalAndWait(TimeSpan.FromSeconds(5))),
            () => { Interlocked.Increment(ref callbacks); return KernelResult.Ok(); });

        var results = await Task.WhenAll(Task.Run(Invoke), Task.Run(Invoke)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error == KernelError.InvalidTransition);
        Assert.Equal(1, callbacks);
        Assert.Equal(BudgetReservationState.Consuming,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void FinalSentryExceptionFailsClosedBeforeSubmit()
    {
        var context = Create();
        using var commit = context.Commit;

        var result = Submit(context, new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => throw new InvalidOperationException("fault")),
            KernelResult.Ok);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    private static KernelResult<OperationBinding> Submit(Context context,
        ISemanticProviderAdmissionService provider, IRuntimeLegalityService runtime,
        Func<KernelResult> callback) =>
        context.Kernel.SubmitSemanticResourceExternalAdmission(context.Commit, context.Dependencies,
            context.Binding, context.Obligations, context.Guarantees, context.Refinement,
            ProviderIdentity, context.ProviderGenerations, provider, runtime, callback);

    private static Context Create(bool withSession = false)
    {
        var time = new TestTimeProvider(new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var kernel = new RuntimeKernel(null, time);
        var admin = TestFixtures.Create(kernel, 1200, 2300).Handle;
        var principal = TestFixtures.Create(kernel, 1201, 2301).Handle;
        var administration = kernel.MintCapability(new(2300), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, principal, "semantic-sentry",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var effect = kernel.MintCapability(new(2301), principal, ResourceKind.Compute,
            "compute:semantic-sentry", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(2301), new(2301), ResourceKind.Compute,
            "resource-use:semantic-sentry", CapabilityRights.Delegate, principal.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 2,
            resourceUse: new(1, OperationObligationsV1Tests.Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 2)).Value!.CapabilityId;
        var lease = kernel.Budgets.Reserve(principal,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        var region = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        EndpointSessionHandle? session = null;
        if (withSession)
        {
            var provider = TestFixtures.Create(kernel, 1202, 2302).Handle;
            var protocol = new ProtocolDefinitionV1("SentrySession", "semantic-sentry-v1", "Ready", ["Done"],
                [new ProtocolMessageDescriptorV1(1, "Run")],
                [new ProtocolTransitionV1(1, "Ready", "Done")]);
            var service = kernel.RegisterService(provider, "semantic-sentry",
                new("SentrySession", "1", "semantic-sentry-v1"), protocol).Value!;
            session = kernel.OpenSession(principal, service).Value;
        }
        var now = time.GetUtcNow();
        var obligations = kernel.ConstructOperationObligationsV1(principal, operation,
            [OperationObligationsV1Tests.Envelope(10)], RefinableRequirements(),
            new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks), session).Value!;
        var guarantees = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;
        var generations = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("7ba47ce0-cb38-4a60-b542-2013e4576af0")]);
        var binding = kernel.CreateSemanticExecutionBindingV1(obligations, guarantees, principal, lease,
            OperationObligationsV1Tests.Envelope(10), ProviderIdentity,
            ExecutionGuaranteesV1Tests.Request().Correlation, generations).Value!;
        var refinement = SemanticExecutionRefinementV1.Evaluate(binding, obligations, guarantees).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1);
        var commit = kernel.PrepareResourceExternalAdmission(principal, effect, ResourceKind.Compute,
            "compute:semantic-sentry", 1, grant, 1, OperationObligationsV1Tests.Envelope(10),
            operation, dependencies, existingLease: lease, providerIdentity: ProviderIdentity,
            providerGeneration: 1).Value!;
        return new(kernel, principal, effect, operation, lease, obligations, guarantees, binding,
            refinement, generations, dependencies, commit, session);
    }

    private static OperationSemanticRequirementsV1 RefinableRequirements() => new(
        Advisory(IsolationClassV1.DomainSeparated),
        Required(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
        Advisory(ReplayClassV1.None),
        Advisory(DeterminismClassV1.StableOrdering),
        Required(CancellationClassV1.ExactAcknowledgement),
        Advisory(ContainmentClassV1.None),
        Advisory(LocalityClassV1.Any),
        Advisory(ResourceAssuranceV1.RuntimeEnforced));

    private static SemanticRequirementV1<T> Required<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Mandatory, value);
    private static SemanticRequirementV1<T> Advisory<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Advisory, value);

    private const string ProviderIdentity = "hybridcpu:external-runtime";

    private sealed class Provider(SemanticExecutionBindingV1 binding, bool deny = false,
        Func<bool>? denyWhen = null)
        : ISemanticProviderAdmissionService
    {
        public KernelResult<ProviderAdmissionDecisionV1> Revalidate(SemanticExecutionBindingV1 _) =>
            KernelResult<ProviderAdmissionDecisionV1>.Ok(new(1, binding.Digest, binding.ProviderIdentity,
                binding.ProviderGenerationDigest, binding.ProviderRequestCorrelation,
                deny || denyWhen?.Invoke() == true
                    ? SemanticGateDecisionStatusV1.Denied : SemanticGateDecisionStatusV1.Allowed, "test-provider"));
    }

    private sealed class Runtime(SemanticExecutionBindingV1 binding, bool deny = false,
        Action? beforeDecision = null) : IRuntimeLegalityService
    {
        public KernelResult<RuntimeLegalityDecisionV1> Evaluate(SemanticExecutionBindingV1 _)
        {
            beforeDecision?.Invoke();
            return KernelResult<RuntimeLegalityDecisionV1>.Ok(new(1, binding.Digest, "test-runtime", 1,
                deny ? SemanticGateDecisionStatusV1.Denied : SemanticGateDecisionStatusV1.Allowed,
                "test-runtime-evidence"));
        }
    }

    private sealed record Context(RuntimeKernel Kernel, ProcessHandle Principal, CapabilityId EffectCapability,
        ExternalOperationHandle Operation, BudgetReservationHandle Lease, OperationObligationsV1 Obligations,
        ExecutionGuaranteesV1 Guarantees, SemanticExecutionBindingV1 Binding,
        SemanticRefinementProofV1 Refinement, Hc.ExternalGenerationSet ProviderGenerations,
        OperationDependencySnapshot Dependencies, ResourceAdmissionCommit Commit, EndpointSessionHandle? Session);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
