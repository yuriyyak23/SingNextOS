using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase08ComputePlanIndependentGatesTests
{
    [Fact]
    public void ResourceAwarePlanIsPolicyOnlyAndLiveOwnersCreateAdmission()
    {
        var setup = Create(100);

        var prepared = setup.Kernel.PrepareResourceAwareComputeSubmission(setup.Process, setup.Plan,
            [setup.Provider], setup.Effect, 1, setup.Grant, 1, setup.Operation,
            setup.Dependencies, new Legality(true));

        Assert.True(prepared.IsSuccess, prepared.Message);
        using var commit = prepared.Value!;
        Assert.Equal(setup.Provider.ProviderId.Value,
            setup.Kernel.ExternalOperations.QueryResourceBinding(setup.Operation).Value!.ProviderIdentity);
        Assert.Equal(10UL, Used(setup));
        Assert.Equal(ExternalOperationState.Admitted,
            setup.Kernel.QueryExternalOperation(setup.Process, setup.Operation).Value!.State);
    }

    [Fact]
    public void EffectResourceBudgetOwnershipProviderAndCpuGatesDenyIndependently()
    {
        var effect = Create(100);
        Assert.Equal(KernelError.CapabilityNotFound,
            Prepare(effect, effectCapability: new(ulong.MaxValue)).Error);

        var resource = Create(100);
        Assert.Equal(KernelError.CapabilityNotFound,
            Prepare(resource, resourceGrant: new(ulong.MaxValue)).Error);

        var budget = Create(5);
        Assert.Equal(KernelError.BudgetExceeded, Prepare(budget).Error);

        var ownership = Create(100, mismatchedOperationUses: true);
        Assert.Equal(KernelError.InvalidRegionState, Prepare(ownership).Error);

        var provider = Create(100);
        Assert.Equal(KernelError.StaleGeneration,
            provider.Kernel.PrepareResourceAwareComputeSubmission(provider.Process, provider.Plan,
                [provider.Provider with { Generation = 2 }], provider.Effect, 1, provider.Grant, 1,
                provider.Operation, provider.Dependencies, new Legality(true)).Error);

        var cpu = Create(100);
        Assert.Equal(KernelError.PlatformDenied, Prepare(cpu, legal: false).Error);

        Assert.Equal(0UL, Used(effect));
        Assert.Equal(0UL, Used(resource));
        Assert.Equal(0UL, Used(budget));
        Assert.Equal(0UL, Used(ownership));
        Assert.Equal(0UL, Used(provider));
        Assert.Equal(0UL, Used(cpu));
    }

    [Fact]
    public void RevokedGrantAndStaleCachedPlanFailLiveRevalidation()
    {
        var revoked = Create(100);
        Assert.True(revoked.Kernel.CapabilityAuthority.Revoke(revoked.Grant).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, Prepare(revoked).Error);

        var stale = Create(100);
        Assert.True(stale.Kernel.Regions.Release(stale.Output,
            new(stale.Domain, stale.Process.Generation)).IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, Prepare(stale).Error);
        Assert.Equal(0UL, Used(stale));
    }

    [Fact]
    public void DuplicateLiveProviderIdentityFailsClosedBeforeAdmission()
    {
        var setup = Create(100);

        var result = setup.Kernel.PrepareResourceAwareComputeSubmission(setup.Process, setup.Plan,
            [setup.Provider, setup.Provider], setup.Effect, 1, setup.Grant, 1,
            setup.Operation, setup.Dependencies, new Legality(true));

        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Equal(0UL, Used(setup));
        Assert.Equal(ExternalOperationState.Prepared,
            setup.Kernel.QueryExternalOperation(setup.Process, setup.Operation).Value!.State);
    }

    [Fact]
    public void ProviderChoiceChangesWithoutChangingSemanticRequirementAndIncompatibleFallbackFails()
    {
        var setup = Create(100);
        var slow = setup.Provider with { ProviderId = new("slow"), LatencyClass = 9 };
        var fast = setup.Provider with { ProviderId = new("fast"), LatencyClass = 1 };
        var selected = setup.Kernel.PlanCompute(setup.Process, setup.Plan.Intent,
            new(true, true, false), [slow, fast]).Value!;

        Assert.Same(setup.Plan.Intent, selected.Intent);
        Assert.Equal(fast.ProviderId, selected.ProviderId);
        Assert.Equal(setup.Plan.Intent.ResourceRequirement, selected.Intent.ResourceRequirement);

        var incompatible = fast with
        {
            Capabilities = ComputeProviderCapabilities.AcceleratorExecution |
                ComputeProviderCapabilities.DirectCoherentOutput
        };
        Assert.Equal(KernelError.PlatformUnavailable,
            setup.Kernel.PlanCompute(setup.Process, setup.Plan.Intent,
                new(true, true, false), [incompatible]).Error);
    }

    [Fact]
    public void PublicComputeContractsContainNoProviderPrivatePlacementAuthority()
    {
        var forbidden = new[] { "Lane", "Opcode", "Slot", "Queue", "Dsc", "L7", "Vmcs",
            "Iommu", "Cxl", "Topology", "PhysicalAddress", "ProviderHandle", "Token" };
        var names = typeof(ComputePlan).Assembly.GetExportedTypes()
            .Where(type => type.Name.StartsWith("Compute", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name).Prepend(type.Name));

        Assert.DoesNotContain(names,
            name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static KernelResult<ResourceAdmissionCommit> Prepare(Setup setup,
        CapabilityId? effectCapability = null, CapabilityId? resourceGrant = null, bool legal = true) =>
        setup.Kernel.PrepareResourceAwareComputeSubmission(setup.Process, setup.Plan, [setup.Provider],
            effectCapability ?? setup.Effect, 1, resourceGrant ?? setup.Grant, 1,
            setup.Operation, setup.Dependencies, new Legality(legal));

    private static Setup Create(ulong computeLimit, bool mismatchedOperationUses = false)
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 980, 1980).Handle;
        var process = TestFixtures.Create(kernel, 981, 1981).Handle;
        var administration = kernel.MintCapability(new(1980), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, process, "p08",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, computeLimit),
             new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        var effect = kernel.MintCapability(new(1981), process, ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var envelope = Envelope(10);
        var grant = kernel.CapabilityAuthority.Mint(new(1981), new(1981), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, process.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var input = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 8)), new(output.Handle, new(0, 8)),
            ComputePublicationPreference.StagedRequired, false, false, envelope);
        var provider = new ComputeProviderCandidate(new("host-model"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
            1024, 1, 1, true, false);
        var plan = kernel.PlanCompute(process, intent, new(true, true, true), [provider]).Value!;
        IReadOnlyList<OperationRegionUseRequest> uses = mismatchedOperationUses
            ? [plan.RequiredRegionUses[0]]
            : plan.RequiredRegionUses;
        var operation = kernel.PrepareExternalOperation(process, uses,
            ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged).Value!.Operation;
        return new(kernel, process, new(1981), budget, effect, grant, input.Handle,
            output.Handle, provider, plan, operation, new(1, 1));
    }

    private static ResourceEnvelopeV1 Envelope(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");

    private static ulong Used(Setup setup) => Assert.Single(
        setup.Kernel.QueryBudget(setup.Budget).Value!.Usage,
        usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed class Legality(bool allowed) : IComputeSubmissionLegalityGate
    {
        public KernelResult Validate(ComputePlan plan, ComputeProviderCandidate provider) => allowed
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.PlatformDenied, "CPU/runtime legality denied the plan.");
    }

    private sealed record Setup(RuntimeKernel Kernel, ProcessHandle Process, DomainId Domain,
        BudgetAccountHandle Budget, CapabilityId Effect, CapabilityId Grant,
        RegionHandle Input, RegionHandle Output, ComputeProviderCandidate Provider,
        ComputePlan Plan, ExternalOperationHandle Operation, OperationDependencySnapshot Dependencies);
}
