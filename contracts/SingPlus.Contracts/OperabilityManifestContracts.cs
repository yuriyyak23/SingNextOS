namespace SingPlus.Contracts;

public enum ManifestRequirementCriticality
{
    Mandatory = 0,
    Optional = 1,
}

public enum ServiceDependencyKind
{
    Hard = 0,
    Optional = 1,
}

public readonly record struct ServiceDependencyRequirementV1(
    ServiceContractIdentity Contract,
    ServiceDependencyKind Kind);

public enum ServiceRestartMode
{
    Never = 0,
    OnFailure = 1,
    Always = 2,
}

public sealed record ServiceRestartPolicyV1(
    ServiceRestartMode Mode,
    uint MaximumAttempts,
    ulong InitialBackoffMilliseconds,
    ulong MaximumBackoffMilliseconds,
    ulong WindowMilliseconds = 60_000)
{
    public static ServiceRestartPolicyV1 Never { get; } = new(ServiceRestartMode.Never, 0, 0, 0, 0);
}

public enum ServiceDrainTimeoutAction
{
    FailReplacement = 0,
    ContinueWaiting = 1,
    Quarantine = 2,
}

public sealed record ServiceDrainPolicyV1(
    ulong TimeoutMilliseconds,
    ServiceDrainTimeoutAction TimeoutAction)
{
    public static ServiceDrainPolicyV1 Default { get; } = new(30_000, ServiceDrainTimeoutAction.FailReplacement);
}

public enum ServiceCheckpointMode
{
    Disabled = 0,
    PlannedReplacementOnly = 1,
    OperatorRequested = 2,
}

public sealed record ServiceCheckpointPolicyV1(ServiceCheckpointMode Mode)
{
    public static ServiceCheckpointPolicyV1 Disabled { get; } = new(ServiceCheckpointMode.Disabled);
}

public enum ServiceTelemetryVisibility
{
    None = 0,
    Self = 1,
    ServiceAggregate = 2,
}

public sealed record ServiceTelemetryPolicyV1(
    ServiceTelemetryVisibility Visibility,
    ulong MaximumBufferedBytes)
{
    public static ServiceTelemetryPolicyV1 None { get; } = new(ServiceTelemetryVisibility.None, 0);
}

public enum ServiceBudgetDimension
{
    CpuAllocation = 0,
    OwnedMemoryBytes,
    PinnedMappedMemoryBytes,
    RegionUses,
    IpcMessages,
    IpcBytes,
    ExternalOperations,
    DeviceDmaOperations,
    GuestMemoryBytes,
    CheckpointStorageBytes,
    TraceTelemetryBufferBytes,
    ComputeTimeNanoseconds,
}

public readonly record struct ServiceBudgetRequestV1(ServiceBudgetDimension Dimension, ulong Limit);

public sealed record ServiceCompatibilityConstraintsV1(
    uint MinimumRuntimeContractVersion,
    uint? MaximumRuntimeContractVersion = null)
{
    public static ServiceCompatibilityConstraintsV1 Current { get; } = new(1, 1);
}

public enum ManifestRequirementKind
{
    Dependency = 0,
    PlatformFeature,
    LocalCapability,
    PlatformAuthorityDomain,
    Budget,
    Compatibility,
    ResourceUse,
}

public enum ManifestRequirementDisposition
{
    Requested = 0,
    Granted,
    Denied,
    Unsupported,
    Degraded,
}

public readonly record struct ManifestRequirementDecisionV1(
    ManifestRequirementKind Kind,
    string Identity,
    ManifestRequirementCriticality Criticality,
    ManifestRequirementDisposition Disposition,
    string Reason);

public enum ManifestAdmissionDisposition
{
    Granted = 0,
    Degraded = 1,
    Denied = 2,
}

public sealed record ManifestAdmissionResultV1(
    string ManifestDigest,
    ManifestAdmissionDisposition Disposition,
    IReadOnlyList<ManifestRequirementDecisionV1> Decisions)
{
    public bool MaterializesAuthority => false;
}
