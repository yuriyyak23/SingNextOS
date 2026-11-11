using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase10ResourceSchedulerTests
{
    [Fact]
    public void PoisonedOrStaleCacheCannotPassLiveProviderRevalidation()
    {
        ResourceScheduler scheduler = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        Guid id = Guid.NewGuid();
        ResourcePlacementHint hint = scheduler.Select(id, ResourceClassV1.ComputeTime, 0, 0).Value!;

        Assert.Equal(KernelError.StaleGeneration,
            scheduler.Revalidate(hint, id, [Candidate("p", 8)]).Error);
        Assert.Equal(KernelError.PlatformUnavailable,
            scheduler.Revalidate(hint, id, [Candidate("p", 7) with { Available = false }]).Error);
        Assert.Equal(KernelError.InvalidMessage,
            scheduler.Revalidate(hint, id, [Candidate("p", 7), Candidate("p", 7)]).Error);
    }

    [Fact]
    public void RestartInvalidatesHintsAndLosesAllAgentEvidence()
    {
        ResourceScheduler scheduler = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        Guid id = Guid.NewGuid();
        ResourcePlacementHint hint = scheduler.Select(id, ResourceClassV1.ComputeTime, 0, 0).Value!;
        scheduler.Restart();

        Assert.Equal(KernelError.StaleGeneration, scheduler.Revalidate(hint, id, [Candidate("p", 7)]).Error);
        Assert.Equal(KernelError.PlatformUnavailable,
            scheduler.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 0, 0).Error);
    }

    [Fact]
    public void PriorityCannotExceedCallerCeilingAndDoesNotAppearInHint()
    {
        ResourceScheduler scheduler = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        Assert.Equal(KernelError.DelegationDenied,
            scheduler.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 5, 4).Error);
        Assert.DoesNotContain(typeof(ResourcePlacementHint).GetProperties(), property =>
            property.Name.Contains("Priority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdversarialAndReorderedLoadEvidenceFailsClosed()
    {
        ResourceScheduler scheduler = new ResourceScheduler();
        Assert.False(scheduler.Observe(new(new("p"), 7, 1,
            ResourceClassV1.ComputeTime, -1, 0)).IsSuccess);
        Assert.False(scheduler.Observe(new(new("p"), 7, 1,
            ResourceClassV1.ComputeTime, 0, 10_001)).IsSuccess);
        Assert.True(scheduler.Observe(new(new("p"), 7, 2,
            ResourceClassV1.ComputeTime, 3, 500)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, scheduler.Observe(new(new("p"), 7, 1,
            ResourceClassV1.ComputeTime, 0, 0)).Error);
    }

    [Fact]
    public void SchedulerSurfaceContainsNoAuthorityOrTerminalMutationApi()
    {
        string[] forbidden = ["Authorize", "Authorized", "Mint", "Reserve", "Lease", "Settle",
            "Release", "Publish", "Ownership", "Capability", "Budget"];
        IEnumerable<string> surface = typeof(ResourceScheduler).GetMethods(BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(method => method.Name)
            .Concat(typeof(ResourcePlacementHint).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain(surface, name => forbidden.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));

        ResourceScheduler first = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        ResourceScheduler second = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        Assert.Equal(first.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 0, 0).Value!.ProviderId,
            second.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 0, 0).Value!.ProviderId);
    }

    [Fact]
    public void FavorableSchedulingEvidenceCannotLegalizeMandatoryGuaranteeMismatch()
    {
        ResourceScheduler scheduler = SchedulerWith(new(new("fast"), 9, 1,
            ResourceClassV1.ComputeTime, 0, 0));
        Guid request = Guid.NewGuid();
        ResourcePlacementHint hint = scheduler.Select(request, ResourceClassV1.ComputeTime, 0, 0).Value!;
        Assert.True(scheduler.Revalidate(hint, request, [Candidate("fast", 9)]).IsSuccess);

        SemanticRefinementDecisionV1 refinement = SemanticRefinementEvaluatorV1.Evaluate(
            new SemanticRequirementV1<IsolationClassV1>(1,
                SemanticRequirementStrengthV1.Mandatory, IsolationClassV1.DomainSeparated),
            new SemanticGuaranteeV1<IsolationClassV1>(1,
                SemanticGuaranteeSupportV1.Supported, IsolationClassV1.None),
            SemanticPartialOrdersV1.IsolationRefines);

        Assert.False(refinement.IsAccepted);
        Assert.Equal(SemanticRefinementStatusV1.ClassMismatch, refinement.Status);
        Assert.DoesNotContain(typeof(ResourceScheduler).GetMethods(), method =>
            method.Name.Contains("Refine", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Guarantee", StringComparison.OrdinalIgnoreCase));
    }

    private static ResourceScheduler SchedulerWith(ProviderSchedulingObservation observation)
    {
        ResourceScheduler scheduler = new ResourceScheduler();
        Assert.True(scheduler.Observe(observation).IsSuccess);
        return scheduler;
    }

    private static ComputeProviderCandidate Candidate(string id, ulong generation) => new(
        new(id), generation,
        ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
        4096, 1, 1, true, false);
}
