namespace SingPlus.Contracts;

public enum ServiceLifecycleState
{
    Declared = 0,
    Admitting,
    Starting,
    Ready,
    Degraded,
    Draining,
    Stopped,
    Failed,
    RestartBackoff,
    CrashLoop,
    Quarantined,
}

public enum ServiceHealthState
{
    Starting = 0,
    Ready,
    Degraded,
    Draining,
    Unhealthy,
    Failed,
    Quarantined,
    Stopped,
}

public enum ServiceReadinessPolicy
{
    ProcessRunning = 0,
    HardDependenciesBound = 1,
}

public enum ServiceFailurePropagationPolicy
{
    DegradeDependent = 0,
    DrainDependent = 1,
    StopDependent = 2,
}

public enum ServiceDrainTimeoutPolicy
{
    ContinueDraining = 0,
    Quarantine,
    FailReplacement,
    RequireProvenContainment,
}

public readonly record struct ServiceDependencyBinding(
    ServiceInstanceHandle Consumer,
    ServiceInstanceHandle Provider,
    ServiceContractIdentity Contract,
    ServiceDependencyKind Kind);

public sealed record ServiceHealthSnapshot(
    ServiceInstanceHandle Instance,
    ServiceHealthState State,
    long MonotonicTimestamp,
    string Reason)
{
    public bool AuthorizesMutation => false;
}

public sealed record ServiceSupervisorSnapshot(
    ComponentIdentity Identity,
    ServiceInstanceHandle? Instance,
    ServiceLifecycleState State,
    ServiceHealthSnapshot? Health,
    IReadOnlyList<ServiceDependencyBinding> Dependencies,
    uint RestartAttemptsInWindow,
    long? RestartEligibleTimestamp,
    string ManifestDigest,
    string? BlockingReason);

public readonly record struct ServiceStartRequest(ComponentIdentity Identity, string ManifestDigest);
public sealed record ServiceStartReceipt(ServiceStartRequest Request, ServiceSupervisorSnapshot Snapshot);
public readonly record struct ServiceDrainRequest(ServiceInstanceHandle Instance, bool Planned);
public sealed record ServiceDrainReceipt(ServiceDrainRequest Request, ServiceSupervisorSnapshot Snapshot, bool ClosureProven);
public readonly record struct ServiceReplacementRequest(ServiceInstanceHandle Previous, string NextManifestDigest);
public sealed record ServiceReplacementReceipt(ServiceReplacementRequest Request, ServiceSupervisorSnapshot Snapshot, bool FreshAdmission);
