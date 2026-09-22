using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class HybridCpuExternalOperationProviderTests
{
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
        var replacement = Generations(Guid.Parse("fa6c6d88-d2ee-4dd6-8d09-cbbf0735aac8"));
        var published = scenario.Provider.Publish(request, () =>
        {
            scenario.Provider.Reconfigure(replacement);
            nested = scenario.Provider.Poll(request);
            scenario.Input.Span.CopyTo(scenario.Output.Span);
        });

        Assert.True(published.IsSuccess, published.Message);
        Assert.NotNull(nested);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, nested.Status);
        Assert.Equal(request.Generations, nested.CurrentGenerations);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(scenario.Input.Span.ToArray(), scenario.Output.Span.ToArray());
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
}
