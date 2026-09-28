namespace SingPlus.Contracts;

public static class DeterministicTraceContract
{
    public const uint Version = 1;
    public const int MaximumProducerCapacity = 16_384;
}

public readonly record struct TraceSessionId(ulong Value);
public readonly record struct TraceGeneration(ulong Value);
public readonly record struct TraceSessionHandle(TraceSessionId SessionId, TraceGeneration Generation);
public readonly record struct TraceProducerId(ulong Value);
public readonly record struct TraceProducerHandle(TraceSessionHandle Session, TraceProducerId ProducerId, TraceGeneration Generation);
public readonly record struct TraceSequence(ulong Value);
public readonly record struct CausalCorrelationId(string Value);
public readonly record struct TraceCausalContext(
    CausalCorrelationId Correlation,
    CausalCorrelationId? ParentCorrelation = null);

public enum TraceVisibilityClass
{
    Self = 0,
    ServiceAggregate,
    PrivilegedSystem,
    SecurityEvidence,
}

public enum TraceOverflowPolicy
{
    DropWithMarker = 0,
    BackpressureTestMode,
    StopSession,
}

public enum TraceSessionState
{
    Active = 0,
    Stopped,
    OverflowStopped,
}

public enum TraceEventKind
{
    ProcessLifecycle = 0,
    ServiceLifecycle,
    ManifestAdmission,
    CapabilityMinted,
    CapabilityDelegated,
    CapabilityRevoked,
    RegionOwnership,
    RegionBorrow,
    RegionUse,
    PlatformResource,
    IpcSent,
    IpcReceived,
    BudgetReserved,
    BudgetReleased,
    DeadlineCancellation,
    ExternalOperationAdmitted,
    ExternalOperationSubmitted,
    ExternalOperationCompleted,
    ExternalOperationVisible,
    ExternalOperationPublished,
    ExternalOperationReleased,
    ExternalOperationFaulted,
    ProviderGenerationChanged,
    VirtualDomainLifecycle,
    SecureDomainLifecycle,
    CheckpointLifecycle,
    SupervisorObservation,
    ExternalRuntimeReplayEvidence,
    BufferOverflow,
}

public sealed record TraceSemanticData(
    string ResourceClass,
    string ResourceCorrelation,
    string State,
    string Outcome,
    string? MetadataDigest = null)
{
    public bool ContainsPayload => false;
    public bool ContainsCapabilityMaterial => false;
    public bool ContainsProviderCredential => false;
}

public sealed record SemanticTraceEvent(
    TraceProducerHandle Producer,
    TraceSequence Sequence,
    long MonotonicTimestamp,
    TraceEventKind Kind,
    CausalCorrelationId Correlation,
    CausalCorrelationId? ParentCorrelation,
    ProcessHandle Subject,
    TraceSemanticData Data,
    bool Incomplete = false,
    uint ContractVersion = DeterministicTraceContract.Version)
{
    public bool AuthorizesEffect => false;
    public bool AuthorizesReplayEffect => false;
}

public sealed record TraceSessionAdmission(
    TraceSessionHandle Session,
    TraceProducerHandle Producer,
    ProcessHandle Owner,
    TraceVisibilityClass Visibility,
    TraceOverflowPolicy OverflowPolicy,
    int ProducerCapacity,
    TraceSessionState State,
    uint ContractVersion = DeterministicTraceContract.Version)
{
    public bool MaterializesAuthority => false;
}

public sealed record TraceSnapshot(
    TraceSessionAdmission Session,
    IReadOnlyList<SemanticTraceEvent> Events,
    ulong DroppedEventCount,
    bool Complete,
    uint ContractVersion = DeterministicTraceContract.Version)
{
    public bool AuthorizesEffect => false;
}

public enum TraceReplayMode
{
    Diagnostic = 0,
    DeterministicModel,
    ExternalRuntimeCorrelated,
}

public enum TraceDivergenceKind
{
    None = 0,
    GenerationMismatch,
    AuthorityDecisionMismatch,
    DependencyOrderMismatch,
    ProviderSemanticMismatch,
    PublicationMismatch,
    NondeterministicInputMissing,
    IncompleteTrace,
}

public sealed record TraceDivergence(
    TraceDivergenceKind Kind,
    TraceProducerHandle? Producer,
    TraceSequence? Sequence,
    string Detail);

public sealed record TraceReplayReport(
    TraceReplayMode Mode,
    bool Deterministic,
    IReadOnlyList<TraceDivergence> Divergences,
    string? EvidenceDigest = null,
    uint ContractVersion = DeterministicTraceContract.Version)
{
    public bool AuthorizesEffect => false;
    public bool AuthorizesProviderSubmission => false;
}
