namespace SingPlus.Contracts;

[Flags]
public enum RemoteLeaseRightsV1 : byte
{
    None = 0,
    Read = 1 << 0,
    Execute = 1 << 1,
    StagedWrite = 1 << 2,
}

public readonly record struct RemoteAuthorityLeaseV1(
    ushort Version,
    Guid LeaseId,
    string OwnerHostIdentity,
    ulong OwnerIncarnation,
    string RemoteHostIdentity,
    ulong RemoteIncarnation,
    string ResourceCorrelationDigest,
    RemoteLeaseRightsV1 Rights,
    ulong OwnerEpoch,
    ulong LeaseGeneration,
    ulong IssuedAtOwnerSequence,
    ulong NotAfterOwnerSequence)
{
    public const ushort CurrentVersion = 1;
    public bool IsParentAuthority => false;
    public bool TransfersOwnership => false;
    public bool UsesWallClockExpiry => false;

    public RemoteAuthorityLeaseV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Remote authority lease version is unsupported.");
        if (LeaseId == Guid.Empty)
            throw new ArgumentException("Remote authority lease identity must be materialized.");
        ValidateHost(OwnerHostIdentity);
        ValidateHost(RemoteHostIdentity);
        if (OwnerHostIdentity == RemoteHostIdentity)
            throw new ArgumentException("Remote lease owner and holder must be distinct hosts.");
        if (ResourceCorrelationDigest is null || ResourceCorrelationDigest.Length != 64 ||
            ResourceCorrelationDigest.Any(character => !Uri.IsHexDigit(character) || character is >= 'A' and <= 'F'))
            throw new ArgumentException("Remote lease resource correlation must be lowercase SHA-256 hex.");
        const RemoteLeaseRightsV1 all = RemoteLeaseRightsV1.Read |
            RemoteLeaseRightsV1.Execute | RemoteLeaseRightsV1.StagedWrite;
        if (Rights == RemoteLeaseRightsV1.None || (Rights & ~all) != 0)
            throw new ArgumentException("Remote lease rights must be a non-empty known subset.");
        if (OwnerIncarnation == 0 || RemoteIncarnation == 0 || OwnerEpoch == 0 ||
            LeaseGeneration == 0 || IssuedAtOwnerSequence == 0 ||
            NotAfterOwnerSequence < IssuedAtOwnerSequence)
            throw new ArgumentException("Remote lease generations and monotonic validity interval are invalid.");
        return this;
    }

    private static void ValidateHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Remote lease host identity must be canonical and bounded.");
    }
}

public readonly record struct RemoteLeaseCurrentStateV1(
    ulong OwnerIncarnation,
    ulong RemoteIncarnation,
    ulong OwnerEpoch,
    ulong LeaseGeneration,
    ulong OwnerSequence,
    bool Partitioned,
    bool Revoked);

public enum RemoteLeaseAdmissionCodeV1 : byte
{
    Eligible = 1,
    StaleOwnerIncarnation = 2,
    StaleRemoteIncarnation = 3,
    StaleOwnerEpoch = 4,
    StaleLeaseGeneration = 5,
    ExpiredOwnerSequence = 6,
    Partitioned = 7,
    Revoked = 8,
}

public static class RemoteAuthorityLeaseEvaluatorV1
{
    public static RemoteLeaseAdmissionCodeV1 Evaluate(
        RemoteAuthorityLeaseV1 lease,
        RemoteLeaseCurrentStateV1 current)
    {
        lease.Validate();
        if (current.OwnerIncarnation != lease.OwnerIncarnation) return RemoteLeaseAdmissionCodeV1.StaleOwnerIncarnation;
        if (current.RemoteIncarnation != lease.RemoteIncarnation) return RemoteLeaseAdmissionCodeV1.StaleRemoteIncarnation;
        if (current.OwnerEpoch != lease.OwnerEpoch) return RemoteLeaseAdmissionCodeV1.StaleOwnerEpoch;
        if (current.LeaseGeneration != lease.LeaseGeneration) return RemoteLeaseAdmissionCodeV1.StaleLeaseGeneration;
        if (current.Revoked) return RemoteLeaseAdmissionCodeV1.Revoked;
        if (current.Partitioned) return RemoteLeaseAdmissionCodeV1.Partitioned;
        return current.OwnerSequence > lease.NotAfterOwnerSequence
            ? RemoteLeaseAdmissionCodeV1.ExpiredOwnerSequence
            : RemoteLeaseAdmissionCodeV1.Eligible;
    }
}

/// <summary>Closure evidence only. It cannot reclaim or transfer the parent resource.</summary>
public readonly record struct RemoteEffectClosureV1(
    ushort Version,
    Guid LeaseId,
    ulong OwnerEpoch,
    ulong LeaseGeneration,
    ulong ProviderGeneration,
    ulong ClosureSequence,
    bool Fenced,
    bool EffectClosed)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesReclaim => false;
}

public static class RemoteReclaimPredicateV1
{
    public static bool IsSatisfied(RemoteAuthorityLeaseV1 lease, RemoteEffectClosureV1 closure)
    {
        lease.Validate();
        return closure.Version == RemoteEffectClosureV1.CurrentVersion &&
               closure.LeaseId == lease.LeaseId && closure.OwnerEpoch == lease.OwnerEpoch &&
               closure.LeaseGeneration == lease.LeaseGeneration && closure.ProviderGeneration != 0 &&
               closure.ClosureSequence != 0 && closure.Fenced && closure.EffectClosed;
    }
}

public readonly record struct RemoteResourceEscrowV1(
    ushort Version,
    ulong ParentAllocation,
    ulong DelegatedAllocation,
    ulong ConsumedAllocation,
    ulong ReturnedAllocation)
{
    public const ushort CurrentVersion = 1;

    public RemoteResourceEscrowV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Remote resource escrow version is unsupported.");
        if (DelegatedAllocation > ParentAllocation ||
            ConsumedAllocation > DelegatedAllocation ||
            ReturnedAllocation > DelegatedAllocation - ConsumedAllocation)
            throw new ArgumentException("Remote resource escrow violates parent/child conservation.");
        return this;
    }
}
