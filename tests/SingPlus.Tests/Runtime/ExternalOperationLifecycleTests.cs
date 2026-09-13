using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ExternalOperationLifecycleTests
{
    private static readonly OperationDependencySnapshot Dependencies = new(7, 11);

    [Fact]
    public void MockNonCxlOperationTraversesAllSevenStatesWithoutConflation()
    {
        var scenario = CreateScenario();
        var provider = new MockExternalProvider(scenario.Kernel, scenario.Owner);
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.Equal(ExternalOperationState.Prepared, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);

        var admission = scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies);
        Assert.True(admission.IsSuccess, admission.Message);
        Assert.Equal(ExternalOperationState.Admitted, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);

        var binding = provider.Submit(preparation.Operation, Dependencies);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.Equal(ExternalOperationState.Submitted, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);

        var completion = provider.Complete(binding.Value!, ExternalOperationCompletionDisposition.Completed);
        Assert.True(completion.IsSuccess, completion.Message);
        Assert.Equal(ExternalOperationState.DeviceComplete, completion.Value!.State);
        Assert.Equal(0, scenario.Output.Span[0]);

        var visible = provider.MakeVisible(binding.Value!, ExternalVisibilityRequirement.PublicationFence, satisfied: true);
        Assert.True(visible.IsSuccess, visible.Message);
        Assert.Equal(ExternalOperationState.Visible, visible.Value!.State);
        Assert.Equal(0, scenario.Output.Span[0]);

        var published = scenario.Kernel.PublishExternalOperation(
            scenario.Owner,
            preparation.Operation,
            Dependencies,
            new PublicationPlan(ExternalPublicationPolicy.Staged),
            () => scenario.Output.Span[0] = 42);
        Assert.True(published.IsSuccess, published.Message);
        Assert.Equal(ExternalOperationState.Published, published.Value!.State);
        Assert.Equal(42, scenario.Output.Span[0]);

        var released = scenario.Kernel.ReleaseExternalOperation(
            scenario.Owner,
            preparation.Operation,
            new ReleasePlan(ProviderResourcesClosed: true, ProviderUnavailable: false));
        Assert.True(released.IsSuccess, released.Message);
        Assert.Equal(ExternalOperationState.Released, released.Value!.State);
        Assert.Equal(ExternalOperationDisposition.Published, released.Value.Disposition);
        Assert.Equal(
            new[] { "Prepared", "Admitted", "Submitted", "DeviceComplete", "Visible", "Published", "Released" },
            released.Value.Transitions.Select(transition => transition.Event));
        Assert.True(scenario.Output.IsValid);
    }

    [Fact]
    public void SubmitWithoutAdmissionAndAllSuccessfulStateSkipsFailClosed()
    {
        var scenario = CreateScenario();
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);

        AssertInvalid(scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies));
        var unsubmittedBinding = new OperationBinding(preparation.Operation, new OperationBindingId(1), 1);
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(unsubmittedBinding, ExternalOperationCompletionDisposition.Completed)));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(unsubmittedBinding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)));

        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        AssertInvalid(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(unsubmittedBinding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)));

        var binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        AssertInvalid(scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)));

        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));

        Assert.True(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)));

        Assert.True(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.CancelExternalOperation(scenario.Owner, preparation.Operation, true));

        Assert.True(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)).IsSuccess);
        AssertInvalid(scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies));
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        Assert.True(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)).IsSuccess);
    }

    [Fact]
    public void WrongAndStaleCompletionNeverAdvanceTheExactOperation()
    {
        var scenario = CreateScenario();
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        var binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;

        var staleOperation = preparation.Operation with { Generation = new OperationGeneration(99) };
        var stale = scenario.Kernel.ExternalOperations.RecordCompletion(new(new OperationBinding(staleOperation, binding.BindingId, binding.Generation), ExternalOperationCompletionDisposition.Completed));
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);

        var wrongBinding = binding with { BindingId = new OperationBindingId(binding.BindingId.Value + 1) };
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(wrongBinding, ExternalOperationCompletionDisposition.Completed)));
        Assert.Equal(ExternalOperationState.Submitted, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);
    }

    [Fact]
    public void MutationBetweenVisibilityAndPublishDiscardsStagedResult()
    {
        var scenario = CreateScenario();
        var (_, target) = TestFixtures.Create(scenario.Kernel, 2, 20);
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        var binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(scenario.Kernel.TransferRegion(scenario.Owner, target, scenario.Input).IsSuccess);
        var publicationRan = false;

        var publish = scenario.Kernel.PublishExternalOperation(
            scenario.Owner,
            preparation.Operation,
            Dependencies,
            new(ExternalPublicationPolicy.Staged),
            () => publicationRan = true);

        Assert.False(publish.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, publish.Error);
        Assert.False(publicationRan);
        Assert.Equal(ExternalOperationDisposition.Discarded, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.Disposition);
    }

    [Fact]
    public void VisibilityFailureCanNeverPublish()
    {
        var scenario = CreateScenario();
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        var binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);

        var visibility = scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, false));
        var publicationRan = false;
        var publish = scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => publicationRan = true);

        Assert.False(visibility.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, visibility.Error);
        Assert.False(publish.IsSuccess);
        Assert.False(publicationRan);
    }

    [Fact]
    public void DependencyGenerationIsCheckedBeforeSubmitAndPublish()
    {
        var scenario = CreateScenario();
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        var staleDependencies = Dependencies with { DeviceGeneration = 12 };
        var staleSubmit = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, staleDependencies);
        Assert.False(staleSubmit.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleSubmit.Error);

        var binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var stalePublish = scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, staleDependencies, new(ExternalPublicationPolicy.Staged), () => Assert.Fail("stale publication ran"));
        Assert.False(stalePublish.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stalePublish.Error);
    }

    [Fact]
    public void CancellationRulesAreExplicitBeforeAndAfterSubmission()
    {
        var beforeSubmit = CreateScenario();
        var prepared = Prepare(beforeSubmit, ExternalPublicationPolicy.Staged);
        Assert.True(beforeSubmit.Kernel.CancelExternalOperation(beforeSubmit.Owner, prepared.Operation, false).IsSuccess);
        Assert.True(beforeSubmit.Kernel.ReleaseExternalOperation(beforeSubmit.Owner, prepared.Operation, new(false, false)).IsSuccess);

        var afterSubmit = CreateScenario();
        var submitted = Prepare(afterSubmit, ExternalPublicationPolicy.Staged);
        Assert.True(afterSubmit.Kernel.AdmitExternalOperation(afterSubmit.Owner, submitted.Operation, Dependencies).IsSuccess);
        var binding = afterSubmit.Kernel.RecordExternalOperationSubmission(afterSubmit.Owner, submitted.Operation, Dependencies).Value!;
        var cancellation = afterSubmit.Kernel.CancelExternalOperation(afterSubmit.Owner, submitted.Operation, providerCancellationSupported: false);
        Assert.True(cancellation.IsSuccess);
        Assert.Equal(ExternalOperationDisposition.CancellationPending, cancellation.Value!.Disposition);
        AssertInvalid(afterSubmit.Kernel.ReleaseExternalOperation(afterSubmit.Owner, submitted.Operation, new(false, false)));
        Assert.True(afterSubmit.Kernel.RecordExternalOperationCompletion(afterSubmit.Owner, new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(afterSubmit.Kernel.ReleaseExternalOperation(afterSubmit.Owner, submitted.Operation, new(true, false)).IsSuccess);

        var direct = CreateScenario();
        var directPrepared = Prepare(direct, ExternalPublicationPolicy.DirectCoherent);
        Assert.True(direct.Kernel.AdmitExternalOperation(direct.Owner, directPrepared.Operation, Dependencies).IsSuccess);
        var directBinding = direct.Kernel.RecordExternalOperationSubmission(direct.Owner, directPrepared.Operation, Dependencies).Value!;
        Assert.True(direct.Kernel.RecordExternalOperationCompletion(direct.Owner, new(directBinding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        var directCancel = direct.Kernel.CancelExternalOperation(direct.Owner, directPrepared.Operation, true);
        Assert.True(directCancel.IsSuccess);
        Assert.Equal(ExternalOperationDisposition.Faulted, directCancel.Value!.Disposition);
        AssertInvalid(direct.Kernel.ReleaseExternalOperation(direct.Owner, directPrepared.Operation, new(true, false)));
        var (_, target) = TestFixtures.Create(direct.Kernel, 2, 20);
        var transfer = direct.Kernel.TransferRegion(direct.Owner, target, direct.Output);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, transfer.Error);
    }

    [Fact]
    public void ProviderLossRequiresExplicitClosureOrContainmentBeforeLocalRelease()
    {
        var scenario = CreateScenario();
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        _ = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;

        var lost = scenario.Kernel.RecordExternalOperationProviderLoss(scenario.Owner, preparation.Operation);
        Assert.True(lost.IsSuccess, lost.Message);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, lost.Value!.Disposition);
        var unavailableOnly = scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(false, true));
        Assert.False(unavailableOnly.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, unavailableOnly.Error);
        var released = scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(false, true, ProviderEffectContained: true));
        Assert.True(released.IsSuccess, released.Message);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(false, true, ProviderEffectContained: true)).IsSuccess);
        Assert.DoesNotContain(released.Value!.Transitions, transition => transition.Event == "Published");
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, new(new DomainId(10), scenario.Owner.Generation)).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownCancelsPreSubmitWorkAndDrainsAcceptedWork()
    {
        var preSubmit = CreateScenario();
        var admitted = Prepare(preSubmit, ExternalPublicationPolicy.Staged);
        Assert.True(preSubmit.Kernel.AdmitExternalOperation(preSubmit.Owner, admitted.Operation, Dependencies).IsSuccess);
        Assert.True(preSubmit.Kernel.TerminateProcess(preSubmit.Owner).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, preSubmit.Kernel.ExternalOperations.Query(admitted.Operation).Value!.State);

        var accepted = CreateScenario();
        var submitted = Prepare(accepted, ExternalPublicationPolicy.Staged);
        Assert.True(accepted.Kernel.AdmitExternalOperation(accepted.Owner, submitted.Operation, Dependencies).IsSuccess);
        var binding = accepted.Kernel.RecordExternalOperationSubmission(accepted.Owner, submitted.Operation, Dependencies).Value!;
        var firstTeardown = accepted.Kernel.TerminateProcess(accepted.Owner);
        Assert.False(firstTeardown.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, firstTeardown.Error);
        Assert.True(accepted.Kernel.RecordExternalOperationCompletion(accepted.Owner, new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(accepted.Kernel.TerminateProcess(accepted.Owner).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, accepted.Kernel.ExternalOperations.Query(submitted.Operation).Value!.State);
    }

    [Fact]
    public void PublicLifecycleSurfaceContainsNoCxlOrRawProviderIdentity()
    {
        var forbidden = new[] { "Cxl", "Hdm", "Dpa", "Hpa", "Pasid", "Requester", "PhysicalAddress", "FabricRoute", "FabricPort", "ProviderToken" };
        var names = typeof(ExternalOperationHandle).Assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("ExternalOperation", StringComparison.Ordinal) || type.Name.StartsWith("Operation", StringComparison.Ordinal) || type == typeof(PublicationPlan) || type == typeof(ReleasePlan))
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(property => property.Name)))
            .ToArray();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AdmissionReceiptCarriesOpaqueServicePolicyAndIndependentVersion()
    {
        var scenario = CreateScenario();
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        var admission = scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies,
            new ExternalServiceIdentity("accelerator-class-a"), ExternalCancellationSupport.ProviderCooperative);

        Assert.True(admission.IsSuccess, admission.Message);
        Assert.Equal(ExternalOperationContract.Version, admission.Value!.ContractVersion);
        Assert.Equal("accelerator-class-a", admission.Value.ServiceIdentity.Value);
        Assert.Equal(ExternalCancellationSupport.ProviderCooperative, admission.Value.CancellationSupport);
        Assert.Equal(preparation.EffectPolicy.EffectClass, admission.Value.EffectClass);
        Assert.Equal(preparation.PublicationPolicy, admission.Value.PublicationPolicy);
    }

    [Fact]
    public void ReceiptVocabularyDistinguishesEveryCrossProjectTerminalOutcome()
    {
        var statuses = Enum.GetValues<ExternalOperationReceiptStatus>();
        Assert.Contains(ExternalOperationReceiptStatus.Submitted, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.DeviceComplete, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Visible, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Published, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Released, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Failed, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Stale, statuses);

        var scenario = CreateScenario();
        var preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        var binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        var submitted = ExternalOperationReceipts.FromSnapshot(
            scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!);
        Assert.Equal(ExternalOperationReceiptStatus.Submitted, submitted.Status);
        Assert.Equal(ExternalOperationContract.Version, submitted.ContractVersion);

        Assert.True(scenario.Kernel.RecordExternalOperationProviderLoss(scenario.Owner, preparation.Operation).IsSuccess);
        var failed = ExternalOperationReceipts.FromSnapshot(
            scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!);
        Assert.Equal(ExternalOperationReceiptStatus.Failed, failed.Status);
        var stale = ExternalOperationReceipts.Stale(preparation.Operation with { Generation = new(99) });
        Assert.Equal(ExternalOperationReceiptStatus.Stale, stale.Status);
    }

    private static Scenario CreateScenario()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        return new Scenario(
            kernel,
            owner,
            kernel.AllocateBuffer<byte>(owner, 8).Value!,
            kernel.AllocateBuffer<byte>(owner, 8).Value!);
    }

    private static OperationPreparation Prepare(Scenario scenario, ExternalPublicationPolicy policy)
    {
        var prepared = scenario.Kernel.PrepareExternalOperation(
            scenario.Owner,
            new[]
            {
                new OperationRegionUseRequest(scenario.Input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
                new OperationRegionUseRequest(scenario.Output.Handle, policy == ExternalPublicationPolicy.Staged ? RegionUseMode.StagedOutput : RegionUseMode.ExclusiveWrite, new(0, 8))
            },
            ExternalVisibilityRequirement.PublicationFence,
            policy,
            policy == ExternalPublicationPolicy.DirectCoherent
                ? new ExternalEffectPolicy(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true)
                : null);
        Assert.True(prepared.IsSuccess, prepared.Message);
        return prepared.Value!;
    }

    private static void AssertInvalid<T>(KernelResult<T> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, result.Error);
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Owner,
        OwnedBuffer<byte> Input,
        OwnedBuffer<byte> Output);

    private sealed class MockExternalProvider(RuntimeKernel kernel, ProcessHandle principal)
    {
        public KernelResult<OperationBinding> Submit(ExternalOperationHandle operation, OperationDependencySnapshot dependencies) =>
            kernel.RecordExternalOperationSubmission(principal, operation, dependencies);

        public KernelResult<ExternalOperationSnapshot> Complete(OperationBinding binding, ExternalOperationCompletionDisposition disposition) =>
            kernel.RecordExternalOperationCompletion(principal, new OperationCompletion(binding, disposition));

        public KernelResult<ExternalOperationSnapshot> MakeVisible(OperationBinding binding, ExternalVisibilityRequirement requirement, bool satisfied) =>
            kernel.RecordExternalOperationVisibility(principal, new OperationVisibilityEvidence(binding, requirement, satisfied));
    }
}
