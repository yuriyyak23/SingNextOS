namespace SingPlus.Contracts;

public static class AuthorityInspectionContract
{
    public const uint Version = 1;
}

public enum AuthorityInspectionConsistency
{
    PointInTimeBestEffort = 0,
    AuthorityLockedSnapshot = 1,
    HistoricalReference = 2
}

public enum AuthorityInspectionScope
{
    Self = 0,
    System = 1
}

public enum AuthorityNodeKind
{
    Process = 0,
    Domain,
    ServiceInstance,
    Capability,
    OwnedRegion,
    BorrowLease,
    RegionUse,
    BackingLease,
    PlatformMapping,
    DeviceLease,
    DmaGrant,
    ExternalOperation,
    VirtualDomain,
    SecureDomain,
    ServiceBinding,
    BudgetReservation,
    CheckpointPin,
    Redacted
}

public enum AuthorityEdgeKind
{
    Owns = 0,
    MintedBy,
    DelegatedTo,
    BorrowedBy,
    MappedInto,
    BackedBy,
    Pins,
    Requires,
    WaitingForClosure,
    AuthorizedBy,
    QuarantinedBy,
    ReplacedBy
}

public enum AuthorityBlockReasonKind
{
    None = 0,
    StaleGeneration,
    WrongOwner,
    BorrowActive,
    RegionUseActive,
    PlatformMappingActive,
    BackingLeaseActive,
    ExternalBorrowActive,
    EndpointSessionActive,
    DeviceOrDmaActive,
    ExternalOperationActive,
    ProviderClosurePending,
    ProviderEffectUncontained,
    PublicationPending,
    ServiceDependencyActive,
    Quarantined,
    UnsupportedAuthoritativeSource,
    Redacted
}

public readonly record struct AuthorityNodeId(string Value);

public sealed record AuthorityInspectionNode(
    AuthorityNodeId Id,
    AuthorityNodeKind Kind,
    string State,
    ulong Generation,
    bool Stale,
    bool Redacted,
    string? TraceCorrelationId = null);

public sealed record AuthorityInspectionEdge(
    AuthorityNodeId Source,
    AuthorityNodeId Target,
    AuthorityEdgeKind Kind,
    bool Redacted);

public sealed record AuthorityInspectionSnapshot(
    uint ContractVersion,
    ulong CaptureSequence,
    long MonotonicTimestamp,
    AuthorityInspectionConsistency Consistency,
    AuthorityInspectionScope Scope,
    IReadOnlyList<AuthorityInspectionNode> Nodes,
    IReadOnlyList<AuthorityInspectionEdge> Edges,
    int RedactedFactCount)
{
    public bool AuthorizesMutation => false;
}

public sealed record AuthorityBlockReason(
    AuthorityBlockReasonKind Kind,
    AuthorityNodeId? BlockingNode,
    string Detail,
    bool Redacted);

public sealed record AuthorityInspectionExplanation(
    AuthorityInspectionSnapshot Snapshot,
    IReadOnlyList<AuthorityBlockReason> Reasons)
{
    public bool AuthorizesMutation => false;
    public bool IsBlocked => Reasons.Count != 0;
}
