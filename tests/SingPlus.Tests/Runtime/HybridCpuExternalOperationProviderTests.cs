using Hc = HybridCPU.ExternalRuntime.Contracts;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class HybridCpuExternalOperationProviderTests
{
    [Fact]
    public void PackagedAdapterSessionDrivesExactSingNextProviderStages()
    {
        var scenario = CreateScenario();
        var capturingProvider = new CapturingProvider(scenario.Provider);
        var session = new ExternalOperationAdapterSession(capturingProvider);
        var semantic = Semantic();

        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Admit(semantic));
        Assert.Equal(1, capturingProvider.AdmitCount);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Submit(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));

        var request = Assert.IsType<Hc.ExternalOperationRequest>(capturingProvider.AdmittedRequest);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.Publish(request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(scenario.Input.Span.ToArray(), scenario.Output.Span.ToArray());

        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Poll(semantic.Correlation));
    }

    [Fact]
    public void PackagedAdapterSessionDoesNotTreatAmbiguousCancelAsClosure()
    {
        var scenario = CreateScenario();
        var session = new ExternalOperationAdapterSession(scenario.Provider);
        var semantic = Semantic();
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Submit(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Cancel(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);
    }

    [Fact]
    public void PackagedAdapterSessionKeepsPossibleEffectStaleAfterProviderReconfiguration()
    {
        var scenario = CreateScenario();
        var session = new ExternalOperationAdapterSession(scenario.Provider);
        var semantic = Semantic();

        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Submit(semantic.Correlation));
        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        Assert.Equal(ExternalOperationAdapterStatus.Stale, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
    }

    [Fact]
    public void CancellationReceiptDistinguishesPreSubmitClosureFromPendingPostSubmitEffect()
    {
        var before = CreateScenario();
        var beforeRequest = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            before.Provider.Admit(Semantic()).Receipt).Request;
        var preSubmit = before.Provider.RequestCancellation(beforeRequest);
        Assert.Equal(Hc.ExternalOperationCancellationOutcome.ConfirmedBeforeSubmit,
            preSubmit.Outcome);
        Assert.True(preSubmit.IsConfirmed);
        var closedAdmission = Assert.Single(before.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationState.Released, closedAdmission.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled, closedAdmission.Disposition);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            before.Provider.Submit(beforeRequest).Status);

        var after = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            after.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            after.Provider.Submit(request).Receipt!.Stage);
        var postSubmit = after.Provider.RequestCancellation(request);
        Assert.Equal(Hc.ExternalOperationCancellationOutcome.Ambiguous,
            postSubmit.Outcome);
        Assert.False(postSubmit.IsConfirmed);
        Assert.False(after.Provider.Release(request, providerResourcesClosed: false).IsSuccess);
        Assert.True(after.Kernel.Regions.Validate(after.Output.Handle, after.Owner).IsSuccess);

        Assert.True(after.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(after.Provider.RecordVisibility(request).IsSuccess);
        Assert.True(after.Provider.Publish(request,
            () => after.Input.Span.CopyTo(after.Output.Span)).IsSuccess);
        Assert.Equal(after.Input.Span.ToArray(), after.Output.Span.ToArray());
    }

    [Fact]
    public void VersionedAdapterDrivesExactSingNextOsLifecycleWithoutConflatingStages()
    {
        var scenario = CreateScenario();
        var semantic = Semantic();

        var admitted = scenario.Provider.Admit(semantic);
        var admission = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(admitted.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.Admitted, admission.Stage);

        var submitted = scenario.Provider.Submit(admission.Request);
        Assert.Equal(Hc.ExternalOperationStage.Submitted, submitted.Receipt!.Stage);

        Assert.True(scenario.Provider.RecordDeviceCompletion(admission.Request).IsSuccess);
        var completed = scenario.Provider.Poll(admission.Request);
        Assert.IsType<Hc.ExternalOperationCompletionReceipt>(completed.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.DeviceComplete, completed.Receipt!.Stage);
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.RecordVisibility(admission.Request).IsSuccess);
        var visible = scenario.Provider.Poll(admission.Request);
        Assert.Equal(Hc.ExternalOperationStage.Visible, visible.Receipt!.Stage);
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.Publish(admission.Request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        var published = scenario.Provider.Poll(admission.Request);
        Assert.IsType<Hc.ExternalOperationPublicationReceipt>(published.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.Published, published.Receipt!.Stage);
        Assert.Equal(scenario.Input.Span.ToArray(), scenario.Output.Span.ToArray());

        Assert.True(scenario.Provider.Release(admission.Request, providerResourcesClosed: true).IsSuccess);
        var released = scenario.Provider.Poll(admission.Request);
        Assert.IsType<Hc.ExternalOperationReleaseReceipt>(released.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.Released, released.Receipt!.Stage);
    }

    [Fact]
    public void AdapterRejectsDuplicateCrossOperationAndReconfiguredGenerationFailClosed()
    {
        var scenario = CreateScenario();
        var semantic = Semantic();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Admit(semantic).Status);
        var wrong = new Hc.ExternalOperationRequest(
            new(new(Guid.NewGuid()), request.Operation.Generation), request.Scope, request.ContractVersion,
            request.Generations, request.Correlation, request.EffectClass,
            request.VisibilityRequirement, request.CancellationMode);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Submit(wrong).Status);
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.Equal(KernelError.InvalidTransition, scenario.Provider.RecordDeviceCompletion(wrong).Error);
        Assert.Equal(KernelError.InvalidTransition, scenario.Provider.RecordVisibility(wrong).Error);
        var publicationRan = false;
        Assert.Equal(KernelError.ExternalOperationNotFound,
            scenario.Provider.Publish(wrong, () => publicationRan = true).Error);
        Assert.False(publicationRan);
        Assert.Equal(KernelError.ExternalOperationNotFound,
            scenario.Provider.Release(wrong, providerResourcesClosed: true).Error);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Submit(request).Status);
        Assert.Equal(Hc.ExternalOperationCancellationOutcome.Stale,
            scenario.Provider.RequestCancellation(request).Outcome);
        Assert.Equal(0, scenario.Output.Span[0]);
    }

    [Fact]
    public void ReconfigureAfterSubmitRejectsStaleClosureAndKeepsRegionPinned()
    {
        var scenario = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true,
                providerUnavailable: true, providerEffectContained: true).Error);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
        var owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        Assert.Equal(ExternalOperationState.Submitted, owner.State);
    }

    [Fact]
    public void ReconfigureBeforeSubmitClosesLocalAdmissionWithoutInventingEffect()
    {
        var scenario = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Submit(request).Status);
        var owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationState.Released, owner.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconfigureAfterCompletionOrVisibilityProjectsProviderLossWithoutPublication(bool visible)
    {
        var scenario = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        if (visible) Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        var owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(visible ? ExternalOperationState.Visible : ExternalOperationState.DeviceComplete,
            owner.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Publish(request, () => throw new Exception("stale publication")).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReconfigureAfterReleasePreservesPublishedTerminalDecision()
    {
        var scenario = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        Assert.True(scenario.Provider.Publish(request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        var owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationState.Released, owner.State);
        Assert.Equal(ExternalOperationDisposition.Published, owner.Disposition);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
    }

    [Fact]
    public void ReconfiguredOperationRequiresCurrentGenerationAndExplicitClosureForReconciliation()
    {
        var scenario = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        var replacement = Generations(Guid.NewGuid());
        scenario.Provider.Reconfigure(replacement);

        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.ReconcileReconfiguredOperation(request, request.Generations,
                providerResourcesClosed: true).Error);
        Assert.True(scenario.Provider.ReconcileReconfiguredOperation(request, replacement,
            providerResourcesClosed: false).IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.True(scenario.Provider.ReconcileReconfiguredOperation(request, replacement,
            providerResourcesClosed: true).IsSuccess);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReconfigureGenerationAbaNeverReauthorizesOldRequest()
    {
        var scenario = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));
        scenario.Provider.Reconfigure(request.Generations);

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.RecordDeviceCompletion(request).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Publish(request, () => throw new Exception("stale publication")).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.True(scenario.Provider.ReconcileReconfiguredOperation(request, request.Generations,
            providerResourcesClosed: false).IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
    }

    [Fact]
    public void ReconfigureGenerationAbaInsideAmbiguousPublicationCannotReauthorizeOldRequest()
    {
        var scenario = CreateScenario();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        var changed = Generations(Guid.NewGuid());

        Assert.Equal(KernelError.ExternalEffectUncontained,
            scenario.Provider.Publish(request, () =>
        {
            scenario.Provider.Reconfigure(changed);
            scenario.Provider.Reconfigure(request.Generations);
            throw new InvalidOperationException("publication outcome unknown");
        }).Error);

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Disposition);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void CompletionWithoutVisibilityAndUnavailableReleaseNeverPublishOrReclaim()
    {
        var scenario = CreateScenario();
        var semantic = Semantic();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);

        var publicationRan = false;
        var publish = scenario.Provider.Publish(request, () => publicationRan = true);
        var release = scenario.Provider.Release(request, providerResourcesClosed: false);

        Assert.False(publish.IsSuccess);
        Assert.False(release.IsSuccess);
        Assert.False(publicationRan);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
    }

    [Fact]
    public void FaultedCompletionAndProviderLossNeverBecomePendingOrSuccessfulReceipts()
    {
        var faulted = CreateScenario();
        var semantic = Semantic();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(faulted.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, faulted.Provider.Submit(request).Receipt!.Stage);
        Assert.True(faulted.Provider.RecordDeviceCompletion(
            request, ExternalOperationCompletionDisposition.Faulted).IsSuccess);
        var fault = faulted.Provider.Poll(request);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Faulted, fault.Status);
        Assert.Equal(Hc.ExternalRuntimeOutcome.Faulted, fault.Receipt!.Outcome);

        var lost = CreateScenario();
        request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(lost.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, lost.Provider.Submit(request).Receipt!.Stage);
        Assert.False(lost.Provider.Release(request, providerResourcesClosed: false,
            providerUnavailable: true).IsSuccess);
        var unavailable = lost.Provider.Poll(request);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Unavailable, unavailable.Status);
        Assert.Equal(Hc.ExternalRuntimeOutcome.Unknown, unavailable.Receipt!.Outcome);
        Assert.All(lost.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void PollDeliversEveryReachedStageInOrderAndTerminalReplayIsStale()
    {
        var scenario = CreateScenario();
        var semantic = Semantic();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        Assert.True(scenario.Provider.Publish(request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);

        Assert.Equal(Hc.ExternalOperationStage.DeviceComplete, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationStage.Visible, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationStage.Published, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationStage.Released, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Poll(request).Status);
    }

    [Fact]
    public void ProviderUnavailableAndContainmentClaimAreNotClosureButResourceCloseCanRelease()
    {
        var scenario = CreateScenario();
        var semantic = Semantic();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);

        var unavailable = scenario.Provider.Release(request,
            providerResourcesClosed: false, providerUnavailable: true);
        Assert.False(unavailable.IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);

        var contained = scenario.Provider.Release(request, providerResourcesClosed: false,
            providerUnavailable: true, providerEffectContained: true);
        Assert.False(contained.IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);

        var closed = scenario.Provider.Release(request, providerResourcesClosed: true,
            providerUnavailable: true);
        Assert.True(closed.IsSuccess, closed.Message);
    }

    [Fact]
    public void PublicationCallbackReentryIsFailClosedAndGenerationChangeWaitsForBoundaryExit()
    {
        var scenario = CreateScenario();
        var semantic = Semantic();
        var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);

        Hc.ExternalOperationProviderPollResult? nested = null;
        Hc.ExternalOperationProviderPollResult? admissionDuringReset = null;
        KernelResult? prematureReconciliation = null;
        var replacement = Generations(Guid.Parse("fa6c6d88-d2ee-4dd6-8d09-cbbf0735aac8"));
        var published = scenario.Provider.Publish(request, () =>
        {
            scenario.Provider.Reconfigure(replacement);
            nested = scenario.Provider.Poll(request);
            admissionDuringReset = scenario.Provider.Admit(new(
                Hc.ExternalOperationContract.Version,
                new(Guid.NewGuid()),
                Hc.ExternalEffectClass.NonIdempotent,
                Hc.ExternalVisibilityRequirement.StagedOutput,
                Hc.ExternalCancellationMode.ExactAcknowledgement,
                Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish));
            prematureReconciliation = scenario.Provider.ReconcileReconfiguredOperation(request,
                replacement, providerResourcesClosed: true);
            scenario.Input.Span.CopyTo(scenario.Output.Span);
        });

        Assert.True(published.IsSuccess, published.Message);
        Assert.NotNull(nested);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, nested.Status);
        Assert.Equal(request.Generations, nested.CurrentGenerations);
        Assert.NotNull(admissionDuringReset);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, admissionDuringReset.Status);
        Assert.Null(admissionDuringReset.Receipt);
        Assert.Equal(KernelError.StaleGeneration, prematureReconciliation!.Value.Error);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(scenario.Input.Span.ToArray(), scenario.Output.Span.ToArray());
        Assert.Equal(ExternalOperationDisposition.Published,
            Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Disposition);
    }

    private static Scenario CreateScenario()
    {
        var kernel = new RuntimeKernel();
        var (_, process) = TestFixtures.Create(kernel, 1701, 1702);
        var resolved = kernel.Processes.Resolve(process).Value!;
        var owner = new RegionOwner(resolved.DomainId, process.Generation);
        var input = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(process, 8).Value!;
        for (var index = 0; index < input.Length; index++) input.Span[index] = (byte)(index + 1);
        var generations = Generations(Guid.Parse("5aa6b3e1-1865-4f5a-a498-e401616d8f20"));
        var provider = new HybridCpuExternalOperationProvider(kernel, process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, input.Length)),
            new(output.Handle, RegionUseMode.StagedOutput, new(0, output.Length))
        ], new(7, 11, 13, 17), new("singnextos-runtime"),
            new(new(Guid.Parse("229b207b-feef-450d-b2fc-e498bfdb76df")), new(1)), generations);
        return new(kernel, owner, input, output, provider);
    }

    private static Hc.ExternalOperationSemanticRequest Semantic() => new(
        Hc.ExternalOperationContract.Version,
        new(Guid.Parse("4bcf3f23-e366-4849-a5d6-48dd062ad932")),
        Hc.ExternalEffectClass.NonIdempotent,
        Hc.ExternalVisibilityRequirement.StagedOutput,
        Hc.ExternalCancellationMode.ExactAcknowledgement,
        Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);

    private static Hc.ExternalGenerationSet Generations(Guid value) =>
        new(Hc.ExternalOperationContract.Version, [value]);

    private sealed record Scenario(RuntimeKernel Kernel, RegionOwner Owner,
        OwnedBuffer<byte> Input, OwnedBuffer<byte> Output,
        HybridCpuExternalOperationProvider Provider);

    private sealed class CapturingProvider(HybridCpuExternalOperationProvider inner) :
        Hc.IExternalOperationProvider, Hc.IExternalOperationCancellationProvider
    {
        public Hc.ExternalOperationRequest? AdmittedRequest { get; private set; }
        public int AdmitCount { get; private set; }

        public Hc.ExternalOperationProviderPollResult Admit(Hc.ExternalOperationSemanticRequest semantic)
        {
            AdmitCount++;
            var result = inner.Admit(semantic);
            if (result.Receipt is Hc.ExternalOperationAdmissionReceipt receipt)
                AdmittedRequest = receipt.Request;
            return result;
        }

        public Hc.ExternalOperationProviderPollResult Submit(Hc.ExternalOperationRequest request) => inner.Submit(request);
        public Hc.ExternalOperationProviderPollResult Poll(Hc.ExternalOperationRequest request) => inner.Poll(request);
        public Hc.ExternalOperationProviderPollResult Cancel(Hc.ExternalOperationRequest request) => inner.Cancel(request);
        public Hc.ExternalOperationCancellationReceipt RequestCancellation(Hc.ExternalOperationRequest request) =>
            inner.RequestCancellation(request);
    }
}
