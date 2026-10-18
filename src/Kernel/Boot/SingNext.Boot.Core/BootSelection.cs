namespace SingNext.Boot.Core;

public readonly record struct BootPhysicalEvidence(
    string ObservationId, ushort Segment, byte Bus, byte Device, byte Function, ulong? Dsn, string RouteDigest);

public sealed record BootCandidate(
    Guid BootVolumeId,
    Guid ReplicaId,
    Guid ImageId,
    Guid RollbackDomainId,
    ulong ImageGeneration,
    ReadOnlyMemory<byte> SignedDigest,
    ulong Properties,
    bool SignatureValid,
    bool MediaValid,
    BootPhysicalEvidence Physical);

public readonly record struct BootSelectionPolicy(
    Guid BootVolumeId,
    Guid RollbackDomainId,
    ulong RequiredProperties,
    Guid? SelectedImageId,
    ulong ImageRollbackFloor,
    ulong? PreferredDsn,
    bool RequirePhysicalMatch,
    bool AllowReplicaFailover);

public static class BootCandidateSelector
{
    public static BootResult<BootCandidate> Select(BootSelectionPolicy policy, IReadOnlyList<BootCandidate> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (policy.BootVolumeId == Guid.Empty || policy.RollbackDomainId == Guid.Empty)
            return BootResult<BootCandidate>.Fail(BootFailure.Malformed, "Selection policy identifiers are required.");
        if (discovered.Count > BootLimits.MaxDiscoveryCandidates)
            return BootResult<BootCandidate>.Fail(BootFailure.LimitExceeded, "Discovery candidate limit exceeded.");

        BootCandidate? selected = null;
        var logicalCount = 0;
        var eligibleCount = 0;
        var distinctReplicaCount = 0;
        var selectedGeneration = 0UL;

        for (var i = 0; i < discovered.Count; i++)
        {
            var item = discovered[i];
            if (item.BootVolumeId != policy.BootVolumeId || item.RollbackDomainId != policy.RollbackDomainId ||
                !item.MediaValid || !item.SignatureValid ||
                (item.Properties & policy.RequiredProperties) != policy.RequiredProperties ||
                (policy.SelectedImageId is { } selectedImage && item.ImageId != selectedImage))
                continue;
            if (policy.RequirePhysicalMatch && (policy.PreferredDsn is null || item.Physical.Dsn != policy.PreferredDsn))
                continue;
            logicalCount++;
            if (item.ImageGeneration < policy.ImageRollbackFloor) continue;
            eligibleCount++;
            if (selected is null || item.ImageGeneration > selectedGeneration)
            {
                selected = item;
                selectedGeneration = item.ImageGeneration;
            }
        }

        if (eligibleCount == 0)
        {
            if (policy.RequirePhysicalMatch && logicalCount == 0)
                return BootResult<BootCandidate>.Fail(BootFailure.NotFound, "Required physical selector has no match.");
            return BootResult<BootCandidate>.Fail(logicalCount == 0 ? BootFailure.NotFound : BootFailure.RollbackRejected,
                logicalCount == 0 ? "No valid logical candidate." : "All logical candidates are below the image rollback floor.");
        }

        for (var i = 0; i < discovered.Count; i++)
        {
            var item = discovered[i];
            if (!IsEligibleAtGeneration(item, policy, selectedGeneration)) continue;
            if (selected!.ImageId != item.ImageId || !selected.SignedDigest.Span.SequenceEqual(item.SignedDigest.Span))
                return BootResult<BootCandidate>.Fail(BootFailure.AmbiguousState, "Same-generation candidates disagree on image identity or signed digest.");
            if (item.ReplicaId != selected.ReplicaId) distinctReplicaCount++;
            if (IsPreferred(item, selected, policy)) selected = item;
        }

        if (policy.SelectedImageId is not null)
        {
            for (var i = 0; i < discovered.Count; i++)
            {
                var item = discovered[i];
                if (IsLogicallyEligible(item, policy) && item.ImageGeneration >= policy.ImageRollbackFloor && item.ImageGeneration != selectedGeneration)
                    return BootResult<BootCandidate>.Fail(BootFailure.AmbiguousState, "Selected image is advertised at conflicting generations.");
            }
        }

        if (!policy.AllowReplicaFailover && distinctReplicaCount != 0)
            return BootResult<BootCandidate>.Fail(BootFailure.AmbiguousState, "Replica failover is disabled.");
        return BootResult<BootCandidate>.Success(selected!);
    }

    private static bool IsLogicallyEligible(BootCandidate item, BootSelectionPolicy policy) =>
        item.BootVolumeId == policy.BootVolumeId && item.RollbackDomainId == policy.RollbackDomainId &&
        item.MediaValid && item.SignatureValid && (item.Properties & policy.RequiredProperties) == policy.RequiredProperties &&
        (policy.SelectedImageId is null || item.ImageId == policy.SelectedImageId) &&
        (!policy.RequirePhysicalMatch || (policy.PreferredDsn is not null && item.Physical.Dsn == policy.PreferredDsn));

    private static bool IsEligibleAtGeneration(BootCandidate item, BootSelectionPolicy policy, ulong generation) =>
        IsLogicallyEligible(item, policy) && item.ImageGeneration == generation && item.ImageGeneration >= policy.ImageRollbackFloor;

    private static bool IsPreferred(BootCandidate candidate, BootCandidate selected, BootSelectionPolicy policy)
    {
        var candidatePhysical = policy.PreferredDsn is not null && candidate.Physical.Dsn == policy.PreferredDsn;
        var selectedPhysical = policy.PreferredDsn is not null && selected.Physical.Dsn == policy.PreferredDsn;
        if (candidatePhysical != selectedPhysical) return candidatePhysical;
        var replicaOrder = candidate.ReplicaId.CompareTo(selected.ReplicaId);
        if (replicaOrder != 0) return replicaOrder < 0;
        return string.CompareOrdinal(candidate.Physical.ObservationId, selected.Physical.ObservationId) < 0;
    }
}
