using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ComputePlannerTests
{
    private static readonly ComputeSelectionPolicy DefaultPolicy = new(true, true, true);

    [Fact]
    public void SecondMockProviderIsSelectedWithoutChangingSemanticIntent()
    {
        var scenario = CreateScenario();
        var intent = Intent(scenario, ComputePublicationPreference.StagedRequired);
        var slow = Provider("mock-a", 1, StagedCapabilities, latency: 20, bandwidth: 3);
        var fast = Provider("mock-b", 1, StagedCapabilities, latency: 4, bandwidth: 7);

        var plan = scenario.Kernel.PlanCompute(scenario.Owner, intent, DefaultPolicy, new[] { slow, fast });

        Assert.True(plan.IsSuccess, plan.Message);
        Assert.Same(intent, plan.Value!.Intent);
        Assert.Equal(fast.ProviderId, plan.Value.ProviderId);
        var prepared = scenario.Kernel.PrepareExternalOperation(
            scenario.Owner,
            plan.Value.RequiredRegionUses,
            ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged);
        Assert.True(prepared.IsSuccess, prepared.Message);
        Assert.Equal(ExternalOperationState.Prepared, scenario.Kernel.QueryExternalOperation(scenario.Owner, prepared.Value!.Operation).Value!.State);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, prepared.Value.Operation, new(0, plan.Value.ProviderGeneration)).IsSuccess);
    }

    [Fact]
    public void DirectPreferenceFallsBackOnlyToSemanticallyEquivalentStaging()
    {
        var scenario = CreateScenario();
        var intent = Intent(scenario, ComputePublicationPreference.DirectPreferredWithStagedFallback);
        var staged = Provider("staged", 1, StagedCapabilities, 1, 1);

        var fallback = scenario.Kernel.PlanCompute(scenario.Owner, intent, DefaultPolicy, new[] { staged });
        var forbiddenFallback = scenario.Kernel.PlanCompute(scenario.Owner, intent, DefaultPolicy with { AllowStagedFallback = false }, new[] { staged });

        Assert.True(fallback.IsSuccess, fallback.Message);
        Assert.Equal(ComputePublicationPath.Staged, fallback.Value!.PublicationPath);
        Assert.Contains(fallback.Value.Dependencies.Nodes, node => node.Kind == ComputeDependencyKind.StagedOutputReady);
        Assert.DoesNotContain(fallback.Value.Dependencies.Nodes, node => node.Kind == ComputeDependencyKind.DirectOutputBinding);
        Assert.False(forbiddenFallback.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, forbiddenFallback.Error);
    }

    [Fact]
    public void StagedRequirementNeverSilentlySelectsDirectCoherentOutput()
    {
        var scenario = CreateScenario();
        var directOnly = Provider("direct", 1, DirectCapabilities, 1, 1);

        var plan = scenario.Kernel.PlanCompute(
            scenario.Owner,
            Intent(scenario, ComputePublicationPreference.StagedRequired),
            DefaultPolicy,
            new[] { directOnly });

        Assert.False(plan.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, plan.Error);
    }

    [Fact]
    public void CoherenceCapabilityCannotOverrideIncompatibleRegionUse()
    {
        var scenario = CreateScenario();
        var held = scenario.Kernel.AcquireRegionUse(scenario.Owner, scenario.Output.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(held.IsSuccess, held.Message);
        var coherent = Provider("coherent", 1, DirectCapabilities, 1, 1);

        var plan = scenario.Kernel.PlanCompute(
            scenario.Owner,
            Intent(scenario, ComputePublicationPreference.DirectRequired),
            DefaultPolicy,
            new[] { coherent });

        Assert.False(plan.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, plan.Error);
        Assert.True(scenario.Kernel.ReleaseRegionUse(scenario.Owner, held.Value!.Handle).IsSuccess);
    }

    [Fact]
    public void SecureEvidenceFiltersProvidersButNeverCreatesRegionAuthority()
    {
        var scenario = CreateScenario();
        var secureIntent = Intent(scenario, ComputePublicationPreference.StagedRequired) with { RequiresSecureEvidence = true };
        var ordinary = Provider("ordinary", 1, StagedCapabilities, 1, 9);
        var secure = Provider("secure", 1, StagedCapabilities | ComputeProviderCapabilities.SecureComputeEvidence, 20, 1);

        var selected = scenario.Kernel.PlanCompute(scenario.Owner, secureIntent, DefaultPolicy, new[] { ordinary, secure });
        Assert.True(selected.IsSuccess, selected.Message);
        Assert.Equal(secure.ProviderId, selected.Value!.ProviderId);

        var (_, wrongOwner) = TestFixtures.Create(scenario.Kernel, 2, 20);
        var noAuthority = scenario.Kernel.PlanCompute(wrongOwner, secureIntent, DefaultPolicy, new[] { secure });
        Assert.False(noAuthority.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, noAuthority.Error);
    }

    [Fact]
    public void ProviderDisappearanceOrGenerationChangeRequiresReplanBeforeSubmit()
    {
        var scenario = CreateScenario();
        var provider = Provider("replaceable", 5, StagedCapabilities, 1, 1);
        var plan = scenario.Kernel.PlanCompute(scenario.Owner, Intent(scenario, ComputePublicationPreference.StagedRequired), DefaultPolicy, new[] { provider }).Value!;

        var disappeared = scenario.Kernel.ValidateComputePlanBeforeSubmit(scenario.Owner, plan, Array.Empty<ComputeProviderCandidate>());
        var replaced = scenario.Kernel.ValidateComputePlanBeforeSubmit(scenario.Owner, plan, new[] { provider with { Generation = 6 } });
        var current = scenario.Kernel.ValidateComputePlanBeforeSubmit(scenario.Owner, plan, new[] { provider });

        Assert.False(disappeared.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, disappeared.Error);
        Assert.False(replaced.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, replaced.Error);
        Assert.True(current.IsSuccess, current.Message);
    }

    [Fact]
    public void DagRejectsDeviceCompleteConsumptionBeforeVisibilityAndPublication()
    {
        var kinds = new[]
        {
            ComputeDependencyKind.InputPreparation,
            ComputeDependencyKind.MemoryPlacement,
            ComputeDependencyKind.StagedOutputReady,
            ComputeDependencyKind.DeviceSubmission,
            ComputeDependencyKind.DeviceCompletion,
            ComputeDependencyKind.VisibilityAcquire,
            ComputeDependencyKind.Publication,
            ComputeDependencyKind.DownstreamConsumerReady,
            ComputeDependencyKind.ReleaseReclaim
        };
        var nodes = kinds.Select((kind, index) => new ComputeDependencyNode((ulong)index + 1, kind)).ToArray();
        var unsafeEdges = new[]
        {
            new ComputeDependencyEdge(1, 2),
            new ComputeDependencyEdge(2, 3),
            new ComputeDependencyEdge(3, 4),
            new ComputeDependencyEdge(4, 5),
            new ComputeDependencyEdge(5, 8),
            new ComputeDependencyEdge(8, 9)
        };

        var validation = ComputePlanner.ValidateGraph(new(nodes, unsafeEdges));

        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, validation.Error);
    }

    [Fact]
    public void DagRejectsAnIsolatedOutputPathNode()
    {
        var nodes = Enum.GetValues<ComputeDependencyKind>()
            .Where(kind => kind != ComputeDependencyKind.DirectOutputBinding)
            .Select((kind, index) => new ComputeDependencyNode((ulong)index + 1, kind)).ToArray();
        ulong Id(ComputeDependencyKind kind) => nodes.Single(node => node.Kind == kind).NodeId;
        var orderedWithoutPath = new[]
        {
            ComputeDependencyKind.InputPreparation, ComputeDependencyKind.MemoryPlacement,
            ComputeDependencyKind.DeviceSubmission, ComputeDependencyKind.DeviceCompletion,
            ComputeDependencyKind.VisibilityAcquire, ComputeDependencyKind.Publication,
            ComputeDependencyKind.DownstreamConsumerReady, ComputeDependencyKind.ReleaseReclaim
        };
        var edges = orderedWithoutPath.Zip(orderedWithoutPath.Skip(1),
            (left, right) => new ComputeDependencyEdge(Id(left), Id(right))).ToArray();

        var validation = ComputePlanner.ValidateGraph(new(nodes, edges));

        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, validation.Error);
    }

    [Fact]
    public void DirectPlanIsFutureGatedWhileStagedPlanRemainsAvailable()
    {
        var stagedScenario = CreateScenario();
        var staged = stagedScenario.Kernel.PlanCompute(
            stagedScenario.Owner,
            Intent(stagedScenario, ComputePublicationPreference.StagedRequired),
            DefaultPolicy,
            new[] { Provider("both", 1, StagedCapabilities | DirectCapabilities, 1, 1) }).Value!;
        var directScenario = CreateScenario();
        var direct = directScenario.Kernel.PlanCompute(
            directScenario.Owner,
            Intent(directScenario, ComputePublicationPreference.DirectRequired),
            DefaultPolicy,
            new[] { Provider("both", 1, StagedCapabilities | DirectCapabilities, 1, 1) });

        Assert.Equal(RegionUseMode.StagedOutput, staged.RequiredRegionUses[1].Mode);
        Assert.Contains(staged.Dependencies.Nodes, node => node.Kind == ComputeDependencyKind.StagedOutputReady);
        Assert.False(direct.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, direct.Error);
    }

    [Fact]
    public void MemoryOnlyProviderIsNotAnAcceleratorProvider()
    {
        var scenario = CreateScenario();
        var memoryOnly = Provider(
            "memory-only",
            1,
            ComputeProviderCapabilities.MemoryPlacement | ComputeProviderCapabilities.CoherentHostAccess | ComputeProviderCapabilities.StagedPublication,
            1,
            1);

        var plan = scenario.Kernel.PlanCompute(scenario.Owner, Intent(scenario, ComputePublicationPreference.StagedRequired), DefaultPolicy, new[] { memoryOnly });

        Assert.False(plan.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, plan.Error);
    }

    [Fact]
    public void PublicPlanningSurfaceHasNoCxlShortcutOrPhysicalIdentity()
    {
        var forbidden = new[] { "IsCxl", "Hdm", "Dpa", "Hpa", "Pasid", "Requester", "PhysicalAddress", "FabricRoute", "ProviderToken" };
        var names = typeof(ComputeIntent).Assembly.GetExportedTypes()
            .Where(type => type.Name.StartsWith("Compute", StringComparison.Ordinal))
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(property => property.Name))
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(field => field.Name)))
            .ToArray();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CapabilityReceiptAnswersSemanticQuestionsAndIsIndependentlyVersioned()
    {
        var scenario = CreateScenario();
        var provider = Provider("semantic-provider", 7,
            DirectCapabilities | ComputeProviderCapabilities.SecureComputeEvidence, 1, 1);
        var query = new ComputeRegionCapabilityQuery(provider.ProviderId, provider.Generation,
            new(scenario.Output.Handle, new(0, 8)), RequestDeviceRead: true,
            RequestDeviceWrite: true, RequiresSecureEvidence: true);

        var result = scenario.Kernel.QueryComputeRegionCapabilities(scenario.Owner, query, provider);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ComputePlanningContract.Version, result.Value!.ContractVersion);
        Assert.True(result.Value.DeviceReadable);
        Assert.False(result.Value.DeviceWritable);
        Assert.True(result.Value.CoherentAccessAvailable);
        Assert.True(result.Value.StagingRequired);
        Assert.Equal(ExternalVisibilityRequirement.PublicationFence, result.Value.VisibilityRequirement);
        Assert.Equal(SecureComputeAdmissionReadiness.Ready, result.Value.SecureComputeReadiness);
    }

    [Fact]
    public void CapabilityReceiptFailsClosedForStaleProviderAndMissingRegionAuthority()
    {
        var scenario = CreateScenario();
        var provider = Provider("semantic-provider", 7, StagedCapabilities, 1, 1);
        var stale = new ComputeRegionCapabilityQuery(provider.ProviderId, 6,
            new(scenario.Output.Handle, new(0, 8)), true, true, true);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.QueryComputeRegionCapabilities(scenario.Owner, stale, provider).Error);

        var (_, otherOwner) = TestFixtures.Create(scenario.Kernel, 2, 20);
        var current = stale with { ProviderGeneration = 7 };
        var denied = scenario.Kernel.QueryComputeRegionCapabilities(otherOwner, current, provider);
        Assert.True(denied.IsSuccess, denied.Message);
        Assert.False(denied.Value!.DeviceReadable);
        Assert.False(denied.Value.DeviceWritable);
        Assert.True(denied.Value.StagingRequired);
        Assert.Equal(SecureComputeAdmissionReadiness.EvidenceUnavailable, denied.Value.SecureComputeReadiness);
    }

    private static Scenario CreateScenario()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        return new(kernel, owner, kernel.AllocateBuffer<byte>(owner, 8).Value!, kernel.AllocateBuffer<byte>(owner, 8).Value!);
    }

    private static ComputeIntent Intent(Scenario scenario, ComputePublicationPreference preference) => new(
        ComputeOperationKind.Copy,
        new(scenario.Input.Handle, new(0, 8)),
        new(scenario.Output.Handle, new(0, 8)),
        preference,
        RequiresSecureEvidence: false,
        RequiresVirtualizedDomain: false);

    private static ComputeProviderCandidate Provider(
        string id,
        ulong generation,
        ComputeProviderCapabilities capabilities,
        int latency,
        int bandwidth) => new(
            new ComputeProviderId(id),
            generation,
            capabilities,
            MaximumOperationBytes: 1024,
            latency,
            bandwidth,
            Available: true,
            Faulted: false);

    private const ComputeProviderCapabilities StagedCapabilities =
        ComputeProviderCapabilities.AcceleratorExecution |
        ComputeProviderCapabilities.StagedPublication;

    private const ComputeProviderCapabilities DirectCapabilities =
        ComputeProviderCapabilities.AcceleratorExecution |
        ComputeProviderCapabilities.CoherentHostAccess |
        ComputeProviderCapabilities.DirectCoherentOutput;

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output);
}
