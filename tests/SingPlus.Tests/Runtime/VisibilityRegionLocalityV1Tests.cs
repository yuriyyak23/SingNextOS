using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class VisibilityRegionLocalityV1Tests
{
    [Fact]
    public void VisibilityClassesMapExactlyOntoExistingOwnersAndUnknownValuesFailClosed()
    {
        ExternalVisibilitySemanticsV1 staged = ExternalVisibilitySemantics.ForClass(ExternalVisibilityClassV1.StagedCopyBack).Canonicalize();
        Assert.Equal(RegionUseMode.StagedOutput, staged.RegionUseMode);
        Assert.Equal(ExternalVisibilityRequirement.PublicationFence, staged.Requirement);
        Assert.Equal(ExternalPublicationPolicy.Staged, staged.PublicationPolicy);
        Assert.True(staged.RequiresExactMutationEpoch);
        Assert.True(staged.RequiresPublicationDecision);
        Assert.False(staged.AuthorizesVisibility || staged.AuthorizesPublication);

        ExternalVisibilitySemanticsV1 direct = ExternalVisibilitySemantics.ForClass(ExternalVisibilityClassV1.DirectCoherentWrite).Canonicalize();
        Assert.Equal(RegionUseMode.DirectCoherentWrite, direct.RegionUseMode);
        Assert.False(direct.RequiresPublicationDecision);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExternalVisibilitySemantics.ForClass((ExternalVisibilityClassV1)99));
        Assert.Throws<NotSupportedException>(() => (staged with { Version = 2 }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (staged with { RequiresPublicationDecision = false }).Canonicalize());
    }

    [Fact]
    public void CompleteIsNotVisibleAndStagedMutationEpochDriftBlocksPublication()
    {
        Context context = ReadyThroughCompletion();
        KernelResult<ExternalOperationSnapshot> beforeVisibility = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.Staged), () => Assert.Fail("complete-only published"));
        Assert.Equal(KernelError.InvalidTransition, beforeVisibility.Error);
        Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Owner,
            new(context.Binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        ProcessHandle other = TestFixtures.Create(context.Kernel, 1402, 2502).Handle;
        Assert.True(context.Kernel.TransferRegion(context.Owner, other, context.Input).IsSuccess);

        KernelResult<ExternalOperationSnapshot> stale = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.Staged), () => Assert.Fail("mutation-drift published"));

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(ExternalOperationDisposition.Discarded,
            context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!.Disposition);
    }

    [Fact]
    public void DirectAndSharedMutableRegionModesRemainFutureGated()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle owner = TestFixtures.Create(kernel, 1400, 2500).Handle;
        RegionHandle region = kernel.AllocateBuffer<byte>(owner, 16).Value!.Handle;

        Assert.Equal(KernelError.PlatformUnsupported,
            kernel.AcquireRegionUse(owner, region, RegionUseMode.DirectCoherentWrite, new(0, 16)).Error);
        Assert.Equal(KernelError.PlatformUnsupported,
            kernel.AcquireRegionUse(owner, region, RegionUseMode.SharedReadMostly, new(0, 16)).Error);
    }

    [Fact]
    public void MandatoryLocalityFiltersAndIsRevalidatedByExactProviderGeneration()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle owner = TestFixtures.Create(kernel, 1401, 2501).Handle;
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        ComputeIntent intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 8)), new(output.Handle, new(0, 8)),
            ComputePublicationPreference.StagedRequired, false, false, null,
            new(1, SemanticRequirementStrengthV1.Mandatory, LocalityClassV1.ConsumerDomainLocal));
        ComputeProviderCandidate unsupported = Provider("unsupported", 4, null);
        ComputeProviderCandidate wrong = Provider("wrong", 4,
            new(1, SemanticGuaranteeSupportV1.Supported, LocalityClassV1.ProviderLocal, 4));
        ComputeProviderCandidate exact = Provider("exact", 4,
            new(1, SemanticGuaranteeSupportV1.Supported, LocalityClassV1.ConsumerDomainLocal, 4));

        KernelResult<ComputePlan> plan = kernel.PlanCompute(owner, intent, new(true, true, true), [unsupported, wrong, exact]);
        Assert.True(plan.IsSuccess, plan.Message);
        Assert.Equal("exact", plan.Value!.ProviderId.Value);
        Assert.True(kernel.ValidateComputePlanBeforeSubmit(owner, plan.Value, [exact]).IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported,
            kernel.ValidateComputePlanBeforeSubmit(owner, plan.Value,
                [exact with { LocalityGuarantee = exact.LocalityGuarantee!.Value with { ProviderGeneration = 5 } }]).Error);
    }

    [Fact]
    public void PortableLocalityContractsExposeNoHardwareIdentifiersOrAuthorityVerbs()
    {
        string[] names = new[] { typeof(ComputeLocalityRequirementV1), typeof(ComputeLocalityGuaranteeV1) }
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name).Append(type.Name)).ToArray();
        string[] forbidden = ["Core", "Lane", "Numa", "Bdf", "PhysicalAddress", "Pasid", "Mint", "Reserve"];
        Assert.DoesNotContain(names, name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        Assert.False(new ComputeLocalityGuaranteeV1(1, SemanticGuaranteeSupportV1.Supported,
            LocalityClassV1.ProviderLocal, 1).AuthorizesPlacement);
    }

    private static Context ReadyThroughCompletion()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle owner = TestFixtures.Create(kernel, 1410, 2510).Handle;
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        RegionHandle output = kernel.AllocateBuffer<byte>(owner, 8).Value!.Handle;
        ExternalOperationHandle operation = kernel.PrepareExternalOperation(owner,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)), new(output, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        Assert.True(kernel.AdmitExternalOperation(owner, operation, Dependencies).IsSuccess);
        OperationBinding binding = kernel.RecordExternalOperationSubmission(owner, operation, Dependencies).Value!;
        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        return new(kernel, owner, input, operation, binding);
    }

    private static ComputeProviderCandidate Provider(string id, ulong generation,
        ComputeLocalityGuaranteeV1? locality) => new(new(id), generation,
        ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
        1024, 1, 1, true, false, locality);

    private static readonly OperationDependencySnapshot Dependencies = new(3, 5, 7, 11);
    private sealed record Context(RuntimeKernel Kernel, ProcessHandle Owner, OwnedBuffer<byte> Input,
        ExternalOperationHandle Operation, OperationBinding Binding);
}
