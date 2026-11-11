using Hc = HybridCPU.ExternalRuntime.Contracts;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class SemanticAdmissionSentryTests
{
    [Fact]
    public async Task SharedSessionInFlightCancellationCannotCloseOrExecuteTwice()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            {
                Interlocked.Increment(ref calls);
                entered.SetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(10)))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Test callback rendezvous expired.");
                return KernelResult.Ok();
            });
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(Hc.ExternalOperationContract.Version,
            new(Guid.NewGuid()), Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput, Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(provider.Admit(semantic).Receipt).Request;
        Hc.ExternalOperationProviderPollResult? late = null;
        Task<KernelResult<OperationBinding>> submitting = Task.Run(() => context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                Assert.True(provider.AttachCommittedBinding(binding).IsSuccess);
                late = provider.Submit(request);
                return KernelResult.Fail(KernelError.PlatformFaulted, "Cancelled callback effect remains ambiguous.");
            }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, provider.Submit(request).Status);
            Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, provider.Poll(request).Status);
            Assert.Equal(Hc.ExternalOperationCancellationOutcome.Ambiguous,
                provider.RequestCancellation(request).Outcome);
            Assert.False(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
                new(false, false)).IsSuccess);
            Assert.NotEqual(ExternalOperationState.Released,
                context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
        }
        finally
        {
            resume.Set();
            await submitting.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False((await submitting).IsSuccess);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Faulted, late!.Status);
        Assert.Equal(1, calls);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
        Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedSessionBudgetLossAfterAttachDeniesBeforeExecutionCallback(bool regionDamage)
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations,
            _ => { calls++; return KernelResult.Ok(); });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(Hc.ExternalOperationContract.Version,
            new(Guid.NewGuid()), Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput, Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                Assert.True(provider.AttachCommittedBinding(binding).IsSuccess);
                if (regionDamage)
                {
                    RegionUseDescriptor use = context.Kernel.QueryExternalOperation(context.Principal, context.Operation)
                        .Value!.Admission!.RegionUses[0];
                    Assert.True(context.Kernel.Regions.QuarantineSubrange(use.Region, use.Principal,
                        new(1, new("test-provider", "bank-0", 1, 1), 1,
                            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
                }
                else
                    Assert.True(context.Kernel.Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease).IsSuccess);
                Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Submit(semantic.Correlation));
                return KernelResult.Fail(KernelError.PlatformFaulted, "Budget changed before callback.");
            });
        Assert.Equal(0, calls);
        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SharedSessionCannotAcceptLateCallbackSuccessAfterOwnerCancellation(int invalidation)
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            {
                if (invalidation == 2)
                {
                    RegionUseDescriptor use = context.Kernel.QueryExternalOperation(context.Principal, context.Operation)
                        .Value!.Admission!.RegionUses[0];
                    Assert.True(context.Kernel.Regions.QuarantineSubrange(use.Region, use.Principal,
                        new(1, new("test-provider", "bank-0", 1, 1), 1,
                            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
                }
                else if (invalidation == 1)
                    Assert.True(context.Kernel.Budgets.QuarantineLease(commit.BudgetOwner, commit.Lease).IsSuccess);
                else
                    Assert.True(context.Kernel.CancelExternalOperation(context.Principal,
                        context.Operation, providerCancellationSupported: false).IsSuccess);
                return KernelResult.Ok();
            });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(Hc.ExternalOperationContract.Version,
            new(Guid.NewGuid()), Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput, Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        ExternalOperationAdapterStatus? observed = null;
        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                Assert.True(provider.AttachCommittedBinding(binding).IsSuccess);
                observed = session.Submit(semantic.Correlation);
                return KernelResult.Fail(KernelError.PlatformFaulted, "Owner changed during callback.");
            });
        Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, observed);
        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, context.Kernel.Budgets.Query(context.Lease).Value!.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Disposition);
    }

    [Fact]
    public void SharedSessionRejectsSemanticsOutsideExactStagedContour()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest unsupported = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.Unsupported,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Admit(unsupported));
        ExternalOperationSnapshot owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Admitted, owner.State);
    }

    [Fact]
    public void SharedSessionCancellationAcknowledgesOnlyBeforeSubmit()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Cancel(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(0, calls);
        ExternalOperationSnapshot owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, owner.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SharedSessionPostSubmitCancelAndProviderLossDoNotProveClosure()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        KernelResult<OperationBinding> submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                KernelResult attached = provider.AttachCommittedBinding(binding);
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));

        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                Assert.True(provider.AttachCommittedBinding(binding).IsSuccess);
                KernelResult<ExternalOperationSnapshot> cancelled = context.Kernel.CancelExternalOperation(context.Principal,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedSessionObservesOnlyCommittedOwnerLifecycleStages(bool regionDamage)
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        KernelResult<OperationBinding> submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                KernelResult attached = provider.AttachCommittedBinding(binding);
                if (!attached.IsSuccess) return attached;
                return session.Submit(semantic.Correlation) == ExternalOperationAdapterStatus.Accepted
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "submit receipt rejected");
            });
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(ExternalOperationAdapterStatus.Pending, session.Poll(semantic.Correlation));
        if (regionDamage)
        {
            RegionUseDescriptor use = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Admission!.RegionUses[0];
            Assert.True(context.Kernel.Regions.QuarantineSubrange(use.Region, use.Principal,
                new(1, new("test-provider", "bank-0", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
        }
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
        if (regionDamage)
        {
            int calls = 0;
            Assert.False(context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
                context.Dependencies, new(ExternalPublicationPolicy.Staged), () => calls++).IsSuccess);
            Assert.Equal(0, calls);
            Assert.NotEqual(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
            Assert.False(context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation, new(false, false)).IsSuccess);
            Assert.NotEqual(ExternalOperationState.Released,
                context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.State);
            return;
        }
        Assert.True(context.Kernel.PublishExternalOperation(context.Principal, context.Operation,
            context.Dependencies, new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Pending, session.Poll(semantic.Correlation));
        Assert.Equal(KernelError.InvalidTransition,
            context.Kernel.ReleaseExternalOperation(context.Principal, context.Operation,
                new(true, false)).Error);
        ExternalResourceUsageEvidence usage = new ExternalResourceUsageEvidence(1, submitted.Value!, ProviderIdentity, 1,
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ => KernelResult.Ok());
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        KernelResult<OperationBinding> submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                KernelResult attached = provider.AttachCommittedBinding(binding);
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        OperationBinding? executed = null;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, binding =>
            {
                calls++;
                executed = binding;
                return KernelResult.Ok();
            });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                KernelResult attached = provider.AttachCommittedBinding(binding);
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Ok(); });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Stale, session.Submit(semantic.Correlation));
        Assert.Equal(0, calls);

        KernelResult<OperationBinding> denied = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        V6SharedOperationSessionProvider provider = new V6SharedOperationSessionProvider(context.Kernel, commit,
            new(new(Guid.NewGuid()), new(1)), context.ProviderGenerations, _ =>
            { calls++; return KernelResult.Fail(KernelError.PlatformFaulted, "unknown effect"); });
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(provider);
        Hc.ExternalOperationSemanticRequest semantic = new Hc.ExternalOperationSemanticRequest(
            Hc.ExternalOperationContract.Version, new(Guid.NewGuid()),
            Hc.ExternalEffectClass.NonIdempotent,
            Hc.ExternalVisibilityRequirement.StagedOutput,
            Hc.ExternalCancellationMode.ExactAcknowledgement,
            Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));

        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                KernelResult attached = provider.AttachCommittedBinding(binding);
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        OperationBinding? observed = null;

        KernelResult<OperationBinding> submitted = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
            commit, context.Dependencies, context.Binding, context.Obligations, context.Guarantees,
            context.Refinement, ProviderIdentity, context.ProviderGenerations,
            new Provider(context.Binding), new Runtime(context.Binding), binding =>
            {
                observed = binding;
                ExternalOperationSnapshot owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int callbacks = 0;

        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        OperationBinding? observed = null;

        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmissionWithBinding(
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
        ExternalOperationSnapshot owner = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!;
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        Assert.Equal(observed, owner.Binding);
    }


    [Fact]
    public void ExactFourGateConjunctionSubmitsOnceAndSequentialReplayCannotCompensate()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int callbacks = 0;

        KernelResult<OperationBinding> first = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });
        KernelResult<OperationBinding> duplicate = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int callbacks = 0;
        Provider provider = new Provider(context.Binding, failingGate == 0);
        Runtime runtime = new Runtime(context.Binding, failingGate == 2);
        SemanticRefinementProofV1 proof = failingGate == 1
            ? context.Refinement with { Digest = new("00") }
            : context.Refinement;

        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmission(commit, context.Dependencies,
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        Runtime runtime = new Runtime(context.Binding, beforeDecision: () =>
            Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.EffectCapability).IsSuccess));

        KernelResult<OperationBinding> result = Submit(context, new Provider(context.Binding), runtime, KernelResult.Ok);

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void CachedRefinementAndGateEvidenceCannotSubmitAfterRevocation()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.EffectCapability).IsSuccess);
        int callbacks = 0;

        KernelResult<OperationBinding> result = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SessionCloseDuringRuntimeLegalityIsCaughtBeforeSubmit()
    {
        Context context = Create(withSession: true);
        using ResourceAdmissionCommit commit = context.Commit;
        Runtime runtime = new Runtime(context.Binding, beforeDecision: () =>
            Assert.True(context.Kernel.CloseSession(context.Principal, context.Session!.Value).IsSuccess));

        KernelResult<OperationBinding> result = Submit(context, new Provider(context.Binding), runtime, KernelResult.Ok);

        Assert.Equal(KernelError.SessionClosed, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void ProviderGenerationDriftFailsBeforeRuntimeLegalityAndSubmit()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        Hc.ExternalGenerationSet stale = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("70db585c-12e8-4c7b-a8dc-876f69c899c4")]);
        int runtimeCalls = 0;

        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmission(commit, context.Dependencies,
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int liveGeneration = 1;
        int providerReads = 0;
        int callbacks = 0;
        Provider provider = new Provider(context.Binding, denyWhen: () =>
        {
            providerReads++;
            return liveGeneration != 1;
        });
        Runtime runtime = new Runtime(context.Binding, beforeDecision: () => liveGeneration = 2);

        KernelResult<OperationBinding> result = Submit(context, provider, runtime, () =>
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
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        using Barrier barrier = new Barrier(2);
        int callbacks = 0;
        KernelResult<OperationBinding> Invoke() => Submit(context,
            new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => barrier.SignalAndWait(TimeSpan.FromSeconds(5))),
            () => { Interlocked.Increment(ref callbacks); return KernelResult.Ok(); });

        KernelResult<OperationBinding>[] results = await Task.WhenAll(Task.Run(Invoke), Task.Run(Invoke)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error == KernelError.InvalidTransition);
        Assert.Equal(1, callbacks);
        Assert.Equal(BudgetReservationState.Consuming,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void FinalSentryExceptionFailsClosedBeforeSubmit()
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;

        KernelResult<OperationBinding> result = Submit(context, new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => throw new InvalidOperationException("fault")),
            KernelResult.Ok);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AdditionalSentryCannotHideCapabilityRevocationBeforeSubmit(int mutation)
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        bool damageRecorded = false;
        bool providerRevoked = false;
        int providerReads = 0;
        Provider provider = new Provider(context.Binding, denyWhen: () =>
        {
            providerReads++;
            return providerRevoked;
        });
        KernelResult<OperationBinding> result = context.Kernel.SubmitSemanticResourceExternalAdmission(commit, context.Dependencies,
            context.Binding, context.Obligations, context.Guarantees, context.Refinement,
            ProviderIdentity, context.ProviderGenerations, provider, new Runtime(context.Binding),
            () => { calls++; return KernelResult.Ok(); }, () =>
            {
                if (mutation == 1) _ = context.Kernel.RevokeCapability(commit.EffectCapability);
                if (mutation == 2) providerRevoked = true;
                if (mutation == 3)
                {
                    RegionUseDescriptor use = context.Kernel.QueryExternalOperation(context.Principal, context.Operation).Value!.Admission!.RegionUses[0];
                    damageRecorded = context.Kernel.Regions.QuarantineSubrange(use.Region, use.Principal,
                        new(1, new("test-provider", "bank-0", 1, 1), 1,
                            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess;
                }
                return KernelResult.Ok();
            });
        if (mutation == 3) Assert.True(damageRecorded);
        Assert.Equal(mutation == 0, result.IsSuccess);
        Assert.Equal(mutation == 0 ? 1 : 0, calls);
        Assert.Equal(2, providerReads);
        Assert.Equal(mutation == 0 ? BudgetReservationState.Consuming : BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AdmissionDecisionRejectsNoncanonicalSourceLabels(int malformed)
    {
        Context context = Create();
        using ResourceAdmissionCommit commit = context.Commit;
        int calls = 0;
        string invalid = "source-" + (char)0xD800;
        KernelResult<OperationBinding> result = Submit(context,
            new Provider(context.Binding, evidence: malformed == 1 ? invalid : "provider-evidence"),
            new Runtime(context.Binding, identity: malformed == 2 ? "runtime-" + (char)1 : "runtime",
                evidence: malformed == 3 ? invalid : "runtime-evidence"),
            () => { calls++; return KernelResult.Ok(); });
        Assert.Equal(malformed == 0, result.IsSuccess);
        Assert.Equal(malformed == 0 ? 1 : 0, calls);
        Assert.Equal(malformed == 0 ? BudgetReservationState.Consuming : BudgetReservationState.CancelledPreSubmit,
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
        TestTimeProvider time = new TestTimeProvider(new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        RuntimeKernel kernel = new RuntimeKernel(null, time);
        ProcessHandle admin = TestFixtures.Create(kernel, 1200, 2300).Handle;
        ProcessHandle principal = TestFixtures.Create(kernel, 1201, 2301).Handle;
        CapabilityId administration = kernel.MintCapability(new(2300), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, principal, "semantic-sentry",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        CapabilityId effect = kernel.MintCapability(new(2301), principal, ResourceKind.Compute,
            "compute:semantic-sentry", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        CapabilityId grant = kernel.CapabilityAuthority.Mint(new(2301), new(2301), ResourceKind.Compute,
            "resource-use:semantic-sentry", CapabilityRights.Delegate, principal.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 2,
            resourceUse: new(1, OperationObligationsV1Tests.Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 2)).Value!.CapabilityId;
        BudgetReservationHandle lease = kernel.Budgets.Reserve(principal,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        RegionHandle region = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        ExternalOperationHandle operation = kernel.PrepareExternalOperation(principal,
            [new(region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        EndpointSessionHandle? session = null;
        if (withSession)
        {
            ProcessHandle provider = TestFixtures.Create(kernel, 1202, 2302).Handle;
            ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("SentrySession", "semantic-sentry-v1", "Ready", ["Done"],
                [new ProtocolMessageDescriptorV1(1, "Run")],
                [new ProtocolTransitionV1(1, "Ready", "Done")]);
            ServiceEndpointDescriptor service = kernel.RegisterService(provider, "semantic-sentry",
                new("SentrySession", "1", "semantic-sentry-v1"), protocol).Value!;
            session = kernel.OpenSession(principal, service).Value;
        }
        DateTimeOffset now = time.GetUtcNow();
        OperationObligationsV1 obligations = kernel.ConstructOperationObligationsV1(principal, operation,
            [OperationObligationsV1Tests.Envelope(10)], RefinableRequirements(),
            new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks), session).Value!;
        ExecutionGuaranteesV1 guarantees = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;
        Hc.ExternalGenerationSet generations = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("7ba47ce0-cb38-4a60-b542-2013e4576af0")]);
        SemanticExecutionBindingV1 binding = kernel.CreateSemanticExecutionBindingV1(obligations, guarantees, principal, lease,
            OperationObligationsV1Tests.Envelope(10), ProviderIdentity,
            ExecutionGuaranteesV1Tests.Request().Correlation, generations).Value!;
        SemanticRefinementProofV1 refinement = SemanticExecutionRefinementV1.Evaluate(binding, obligations, guarantees).Value!;
        OperationDependencySnapshot dependencies = new OperationDependencySnapshot(1, 1);
        ResourceAdmissionCommit commit = kernel.PrepareResourceExternalAdmission(principal, effect, ResourceKind.Compute,
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
        Func<bool>? denyWhen = null, string evidence = "test-provider")
        : ISemanticProviderAdmissionService
    {
        public KernelResult<ProviderAdmissionDecisionV1> Revalidate(SemanticExecutionBindingV1 _) =>
            KernelResult<ProviderAdmissionDecisionV1>.Ok(new(1, binding.Digest, binding.ProviderIdentity,
                binding.ProviderGenerationDigest, binding.ProviderRequestCorrelation,
                deny || denyWhen?.Invoke() == true
                    ? SemanticGateDecisionStatusV1.Denied : SemanticGateDecisionStatusV1.Allowed, evidence));
    }

    private sealed class Runtime(SemanticExecutionBindingV1 binding, bool deny = false,
        Action? beforeDecision = null, string identity = "test-runtime", string evidence = "test-runtime-evidence") : IRuntimeLegalityService
    {
        public KernelResult<RuntimeLegalityDecisionV1> Evaluate(SemanticExecutionBindingV1 _)
        {
            beforeDecision?.Invoke();
            return KernelResult<RuntimeLegalityDecisionV1>.Ok(new(1, binding.Digest, identity, 1,
                deny ? SemanticGateDecisionStatusV1.Denied : SemanticGateDecisionStatusV1.Allowed,
                evidence));
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
