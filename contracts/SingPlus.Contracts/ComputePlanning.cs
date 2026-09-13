namespace SingPlus.Contracts;

public static class ComputePlanningContract
{
    public const uint Version = 1;
}

public enum ComputeOperationKind
{
    Copy = 0,
    Transform = 1,
    Reduce = 2
}

public enum ComputePublicationPreference
{
    StagedRequired = 0,
    DirectPreferredWithStagedFallback = 1,
    DirectRequired = 2
}

public enum ComputePublicationPath
{
    Staged = 0,
    DirectCoherent = 1
}

[Flags]
public enum ComputeProviderCapabilities
{
    None = 0,
    MemoryPlacement = 1 << 0,
    CoherentHostAccess = 1 << 1,
    DeviceLocalMemory = 1 << 2,
    Dma = 1 << 3,
    AcceleratorExecution = 1 << 4,
    StagedPublication = 1 << 5,
    DirectCoherentOutput = 1 << 6,
    SecureComputeEvidence = 1 << 7,
    VirtualizedDomain = 1 << 8
}

public readonly record struct ComputeProviderId(string Value);

public enum SecureComputeAdmissionReadiness
{
    NotRequired = 0,
    Ready = 1,
    EvidenceUnavailable = 2
}

public sealed record ComputeRegionCapabilityQuery(
    ComputeProviderId ProviderId,
    ulong ProviderGeneration,
    ComputeRegionOperand Region,
    bool RequestDeviceRead,
    bool RequestDeviceWrite,
    bool RequiresSecureEvidence);

public sealed record ComputeRegionCapabilityReceipt(
    uint ContractVersion,
    ComputeProviderId ProviderId,
    ulong ProviderGeneration,
    bool DeviceReadable,
    bool DeviceWritable,
    bool CoherentAccessAvailable,
    bool StagingRequired,
    ExternalVisibilityRequirement VisibilityRequirement,
    SecureComputeAdmissionReadiness SecureComputeReadiness);

public sealed record ComputeProviderCandidate(
    ComputeProviderId ProviderId,
    ulong Generation,
    ComputeProviderCapabilities Capabilities,
    long MaximumOperationBytes,
    int LatencyClass,
    int BandwidthClass,
    bool Available,
    bool Faulted);

public readonly record struct ComputeRegionOperand(
    RegionHandle Region,
    RegionUseRange Range);

public sealed record ComputeIntent(
    ComputeOperationKind Operation,
    ComputeRegionOperand Input,
    ComputeRegionOperand Output,
    ComputePublicationPreference PublicationPreference,
    bool RequiresSecureEvidence,
    bool RequiresVirtualizedDomain);

public readonly record struct ComputeSelectionPolicy(
    bool AllowStagedFallback,
    bool PreferLowerLatency,
    bool PreferHigherBandwidth);

public enum ComputeDependencyKind
{
    InputPreparation = 0,
    MemoryPlacement = 1,
    StagedOutputReady = 2,
    DirectOutputBinding = 3,
    DeviceSubmission = 4,
    DeviceCompletion = 5,
    VisibilityAcquire = 6,
    Publication = 7,
    DownstreamConsumerReady = 8,
    ReleaseReclaim = 9
}

public readonly record struct ComputeDependencyNode(ulong NodeId, ComputeDependencyKind Kind);
public readonly record struct ComputeDependencyEdge(ulong PredecessorNodeId, ulong SuccessorNodeId);

public sealed record ComputeDependencyGraph(
    IReadOnlyList<ComputeDependencyNode> Nodes,
    IReadOnlyList<ComputeDependencyEdge> Edges);

public sealed record ComputePlan(
    ComputeIntent Intent,
    ComputeProviderId ProviderId,
    ulong ProviderGeneration,
    ComputePublicationPath PublicationPath,
    IReadOnlyList<OperationRegionUseRequest> RequiredRegionUses,
    ComputeDependencyGraph Dependencies);
