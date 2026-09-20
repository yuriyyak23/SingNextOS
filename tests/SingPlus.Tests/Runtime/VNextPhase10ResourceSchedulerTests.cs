using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase10ResourceSchedulerTests
{
    [Fact]
    public void PoisonedOrStaleCacheCannotPassLiveProviderRevalidation()
    {
        var scheduler = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        var id = Guid.NewGuid();
        var hint = scheduler.Select(id, ResourceClassV1.ComputeTime, 0, 0).Value!;

        Assert.Equal(KernelError.StaleGeneration,
            scheduler.Revalidate(hint, id, [Candidate("p", 8)]).Error);
        Assert.Equal(KernelError.PlatformUnavailable,
            scheduler.Revalidate(hint, id, [Candidate("p", 7) with { Available = false }]).Error);
    }

    [Fact]
    public void RestartInvalidatesHintsAndLosesAllAgentEvidence()
    {
        var scheduler = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        var id = Guid.NewGuid();
        var hint = scheduler.Select(id, ResourceClassV1.ComputeTime, 0, 0).Value!;
        scheduler.Restart();

        Assert.Equal(KernelError.StaleGeneration, scheduler.Revalidate(hint, id, [Candidate("p", 7)]).Error);
        Assert.Equal(KernelError.PlatformUnavailable,
            scheduler.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 0, 0).Error);
    }

    [Fact]
    public void PriorityCannotExceedCallerCeilingAndDoesNotAppearInHint()
    {
        var scheduler = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        Assert.Equal(KernelError.DelegationDenied,
            scheduler.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 5, 4).Error);
        Assert.DoesNotContain(typeof(ResourcePlacementHint).GetProperties(), property =>
            property.Name.Contains("Priority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdversarialAndReorderedLoadEvidenceFailsClosed()
    {
        var scheduler = new ResourceScheduler();
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
        var surface = typeof(ResourceScheduler).GetMethods(BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(method => method.Name)
            .Concat(typeof(ResourcePlacementHint).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain(surface, name => forbidden.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));

        var first = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        var second = SchedulerWith(new(new("p"), 7, 1, ResourceClassV1.ComputeTime, 0, 0));
        Assert.Equal(first.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 0, 0).Value!.ProviderId,
            second.Select(Guid.NewGuid(), ResourceClassV1.ComputeTime, 0, 0).Value!.ProviderId);
    }

    private static ResourceScheduler SchedulerWith(ProviderSchedulingObservation observation)
    {
        var scheduler = new ResourceScheduler();
        Assert.True(scheduler.Observe(observation).IsSuccess);
        return scheduler;
    }

    private static ComputeProviderCandidate Candidate(string id, ulong generation) => new(
        new(id), generation,
        ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
        4096, 1, 1, true, false);
}
