namespace SingPlus.Contracts;

public static class StructuredTelemetryContract
{
    public const uint Version = 1;
    public const int MaximumSubscriptionEntries = 256;
    public const ulong EstimatedSnapshotBytes = 1024;
}

public enum TelemetryProjectionClass
{
    SelfOperational = 0,
    ServiceAggregate,
    PrivilegedSystemDiagnostics,
    DebugTraceMetadata,
}

public enum TelemetrySubscriptionOverflowPolicy
{
    DropOldestWithMarker = 0,
    RejectSample,
    StopSubscription,
}

public enum TelemetrySubscriptionState
{
    Active = 0,
    OverflowStopped,
    Closed,
    Stale,
}

public readonly record struct TelemetrySubscriptionId(ulong Value);
public readonly record struct TelemetrySubscriptionGeneration(ulong Value);
public readonly record struct TelemetrySubscriptionHandle(
    TelemetrySubscriptionId SubscriptionId,
    TelemetrySubscriptionGeneration Generation);

public sealed record TelemetryResourceMetrics(
    ulong OwnedMemoryBytes,
    int OwnedRegionCount,
    int PinnedRegionCount,
    int ReclaimPinCount,
    int IpcChannelCount,
    int IpcSessionCount,
    int IpcQueuedMessages);

public sealed record TelemetryOperationMetrics(
    int Prepared,
    int Admitted,
    int Submitted,
    int DeviceComplete,
    int Visible,
    int Published,
    int Released,
    int FaultedOrUncontained);

public sealed record TelemetryDeadlineMetrics(
    int ActiveScopes,
    int CancellationRequested,
    int Expired,
    int EffectMayExist,
    int EffectContained);

public sealed record TelemetryCheckpointMetrics(
    int Committed,
    int Failed,
    int Deleted,
    ulong StoredBytes);

public sealed record TelemetryTraceMetrics(
    int Sessions,
    int BufferedEvents,
    ulong DroppedEvents);

public sealed record TelemetryServiceMetrics(
    ServiceLifecycleState State,
    ServiceHealthState? Health,
    uint RestartAttemptsInWindow,
    int DegradedDependencyCount,
    bool ReplacementBlocked);

public sealed record StructuredTelemetrySnapshot(
    ProcessHandle Subject,
    TelemetryProjectionClass Projection,
    ulong CaptureSequence,
    long MonotonicTimestamp,
    IReadOnlyList<BudgetUsage> BudgetUsage,
    TelemetryResourceMetrics Resources,
    TelemetryOperationMetrics Operations,
    TelemetryDeadlineMetrics Deadlines,
    TelemetryCheckpointMetrics Checkpoints,
    TelemetryTraceMetrics Trace,
    TelemetryServiceMetrics? Service,
    int RedactedFactCount,
    uint ContractVersion = StructuredTelemetryContract.Version)
{
    public bool AuthorizesEffect => false;
    public bool SatisfiesSecurityEvidence => false;
}

public sealed record TelemetrySubscriptionAdmission(
    TelemetrySubscriptionHandle Subscription,
    ProcessHandle Owner,
    ProcessHandle Subject,
    TelemetryProjectionClass Projection,
    int Capacity,
    ulong ReservedBytes,
    TelemetrySubscriptionOverflowPolicy OverflowPolicy,
    TelemetrySubscriptionState State,
    uint ContractVersion = StructuredTelemetryContract.Version)
{
    public bool AuthorizesEffect => false;
}

public sealed record TelemetrySubscriptionBatch(
    TelemetrySubscriptionAdmission Admission,
    IReadOnlyList<StructuredTelemetrySnapshot> Snapshots,
    ulong DroppedSnapshots,
    bool Complete,
    uint ContractVersion = StructuredTelemetryContract.Version);

// Security evidence intentionally remains EvidenceRecord on its separate capability and freshness path.
// Telemetry DTOs cannot be converted into this typed projection.
public sealed record SecurityEvidenceProjection(
    EvidenceRecord Evidence,
    long ProjectedMonotonicTimestamp,
    uint ContractVersion = StructuredTelemetryContract.Version)
{
    public EvidenceFreshness Freshness => Evidence.Freshness;
    public bool AuthorizesEffect => false;
}
