namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal readonly record struct BootPhysicalEvidence(string EndpointObservation, ushort Segment, byte Bus, byte Device, byte Function, ulong? Dsn, string RouteDigest);
internal sealed record CxlBootCandidate(Guid BootVolumeId, Guid ReplicaId, Guid ImageId, ulong ImageGeneration, byte[] SignedDigest,
    ulong Properties, bool SignatureValid, bool MediaValid, BootPhysicalEvidence Physical);
internal sealed record BootSelectionPolicy(Guid BootVolumeId, ulong RequiredProperties, Guid? SelectedImageId, ulong RollbackFloor,
    ulong? PreferredDsn, bool RequirePhysicalMatch, bool AllowReplicaFailover);
internal enum BootSelectionFailure { None, NoCandidate, RollbackRejected, SplitBrain, RequiredPhysicalSelectorMissing }
internal sealed record BootSelectionResult(CxlBootCandidate? Candidate, BootSelectionFailure Failure);

internal interface ICxlBootDiscoveryModel { IReadOnlyList<CxlBootCandidate> Discover(int maximumCandidates, int operationBudget); }

internal sealed class SemanticCxlBootSelector
{
    public BootSelectionResult Select(BootSelectionPolicy policy, IReadOnlyList<CxlBootCandidate> discovered)
    {
        var logical = discovered.Where(x => x.BootVolumeId == policy.BootVolumeId && x.MediaValid && x.SignatureValid &&
            (x.Properties & policy.RequiredProperties) == policy.RequiredProperties).ToArray();
        if (policy.RequirePhysicalMatch)
        {
            if (policy.PreferredDsn is null) return new(null, BootSelectionFailure.RequiredPhysicalSelectorMissing);
            logical = logical.Where(x => x.Physical.Dsn == policy.PreferredDsn).ToArray();
            if (logical.Length == 0) return new(null, BootSelectionFailure.RequiredPhysicalSelectorMissing);
        }
        if (policy.SelectedImageId is { } selected) logical = logical.Where(x => x.ImageId == selected).ToArray();
        var eligible = logical.Where(x => x.ImageGeneration >= policy.RollbackFloor).ToArray();
        if (eligible.Length == 0) return new(null, logical.Length == 0 ? BootSelectionFailure.NoCandidate : BootSelectionFailure.RollbackRejected);
        if (policy.SelectedImageId is not null && eligible.Select(x => x.ImageGeneration).Distinct().Take(2).Count() != 1)
            return new(null, BootSelectionFailure.SplitBrain);
        var generation = policy.SelectedImageId is null ? eligible.Max(x => x.ImageGeneration) : eligible[0].ImageGeneration;
        var atGeneration = eligible.Where(x => x.ImageGeneration == generation).ToArray();
        var imageIds = atGeneration.Select(x => x.ImageId).Distinct().ToArray();
        if (imageIds.Length != 1) return new(null, BootSelectionFailure.SplitBrain);
        var digests = atGeneration.Select(x => Convert.ToHexString(x.SignedDigest)).Distinct(StringComparer.Ordinal).ToArray();
        if (digests.Length != 1) return new(null, BootSelectionFailure.SplitBrain);
        var ordered = atGeneration.GroupBy(x => (x.ReplicaId, x.ImageId, x.ImageGeneration, Digest: Convert.ToHexString(x.SignedDigest)))
            .Select(group => group.OrderByDescending(x => policy.PreferredDsn is not null && x.Physical.Dsn == policy.PreferredDsn)
                .ThenBy(x => x.Physical.EndpointObservation, StringComparer.Ordinal).First())
            .OrderByDescending(x => policy.PreferredDsn is not null && x.Physical.Dsn == policy.PreferredDsn)
            .ThenBy(x => x.ReplicaId.ToString("N"), StringComparer.Ordinal).ToArray();
        if (ordered.Length > 1 && !policy.AllowReplicaFailover) return new(null, BootSelectionFailure.SplitBrain);
        return new(ordered[0], BootSelectionFailure.None);
    }
}
