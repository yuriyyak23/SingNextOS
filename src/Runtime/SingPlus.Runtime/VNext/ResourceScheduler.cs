using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct ProviderSchedulingObservation(
    ComputeProviderId ProviderId,
    ulong ProviderGeneration,
    ulong ObservationGeneration,
    ResourceClassV1 ResourceClass,
    int QueueDepth,
    int LoadBasisPoints);

internal readonly record struct ResourcePlacementHint(
    Guid RequestId,
    ComputeProviderId ProviderId,
    ulong ProviderGeneration,
    ulong SchedulerGeneration,
    ulong ObservationGeneration);

/// <summary>
/// Replicated policy/cache only. A hint is never admission and must be checked
/// against live provider state before the authoritative P08/P04 path is entered.
/// </summary>
internal sealed class ResourceScheduler
{
    private readonly object gate = new();
    private readonly Dictionary<ComputeProviderId, ProviderSchedulingObservation> observations = [];
    private ulong schedulerGeneration = 1;

    internal KernelResult Observe(ProviderSchedulingObservation observation)
    {
        if (string.IsNullOrWhiteSpace(observation.ProviderId.Value) ||
            observation.ProviderGeneration == 0 || observation.ObservationGeneration == 0 ||
            !Enum.IsDefined(observation.ResourceClass) || observation.QueueDepth < 0 ||
            observation.LoadBasisPoints is < 0 or > 10_000)
            return KernelResult.Fail(KernelError.InvalidMessage, "Provider scheduling evidence is malformed.");

        lock (gate)
        {
            if (observations.TryGetValue(observation.ProviderId, out var current) &&
                observation.ObservationGeneration <= current.ObservationGeneration)
                return KernelResult.Fail(KernelError.StaleGeneration, "Provider scheduling evidence is stale or replayed.");
            observations[observation.ProviderId] = observation;
        }
        return KernelResult.Ok();
    }

    internal KernelResult<ResourcePlacementHint> Select(
        Guid requestId,
        ResourceClassV1 resourceClass,
        int requestedPriority,
        int priorityCeiling)
    {
        if (requestId == Guid.Empty || !Enum.IsDefined(resourceClass) ||
            priorityCeiling < 0 || requestedPriority < 0 || requestedPriority > priorityCeiling)
            return KernelResult<ResourcePlacementHint>.Fail(
                KernelError.DelegationDenied, "Scheduling priority is invalid or exceeds its authoritative ceiling.");

        lock (gate)
        {
            var selected = observations.Values
                .Where(item => item.ResourceClass == resourceClass)
                .OrderBy(item => item.QueueDepth)
                .ThenBy(item => item.LoadBasisPoints)
                .ThenBy(item => item.ProviderId.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected.ProviderGeneration == 0)
                return KernelResult<ResourcePlacementHint>.Fail(
                    KernelError.PlatformUnavailable, "No provider observation matches the semantic resource class.");
            return KernelResult<ResourcePlacementHint>.Ok(new(
                requestId, selected.ProviderId, selected.ProviderGeneration,
                schedulerGeneration, selected.ObservationGeneration));
        }
    }

    internal KernelResult Revalidate(
        ResourcePlacementHint hint,
        Guid requestId,
        IReadOnlyList<ComputeProviderCandidate> liveCandidates)
    {
        ArgumentNullException.ThrowIfNull(liveCandidates);
        lock (gate)
        {
            if (hint.RequestId != requestId || hint.SchedulerGeneration != schedulerGeneration ||
                !observations.TryGetValue(hint.ProviderId, out var observation) ||
                observation.ObservationGeneration != hint.ObservationGeneration ||
                observation.ProviderGeneration != hint.ProviderGeneration)
                return KernelResult.Fail(KernelError.StaleGeneration, "Scheduling hint or observation generation is stale.");
        }

        var matches = liveCandidates.Where(candidate => candidate.ProviderId == hint.ProviderId).Take(2).ToArray();
        if (matches.Length > 1)
            return KernelResult.Fail(KernelError.InvalidMessage,
                "Live provider candidates contain a duplicate provider identity.");
        var live = matches.SingleOrDefault();
        if (live is null || !live.Available || live.Faulted)
            return KernelResult.Fail(KernelError.PlatformUnavailable, "Scheduled provider is not live.");
        return live.Generation == hint.ProviderGeneration
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.StaleGeneration, "Scheduled provider generation changed.");
    }

    internal void Restart()
    {
        lock (gate)
        {
            observations.Clear();
            schedulerGeneration = checked(schedulerGeneration + 1);
        }
    }
}
