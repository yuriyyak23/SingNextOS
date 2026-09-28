namespace SingPlus.Contracts;

public static class OrdinaryCheckpointContract
{
    public const uint Version = 1;
    public const int MaximumLogicalStateBytes = 16 * 1024 * 1024;
}

public readonly record struct CheckpointId(ulong Value);
public readonly record struct CheckpointGeneration(ulong Value);
public readonly record struct CheckpointHandle(CheckpointId CheckpointId, CheckpointGeneration Generation);

public enum CheckpointLifecycleState
{
    Requested = 0,
    Quiescing,
    Snapshotting,
    Validating,
    Committed,
    Failed,
    Aborted,
    Deleted,
}

public enum CheckpointResourceClassification
{
    Checkpointable = 0,
    RecreateOnRestore,
    RequiresDrain,
    NonCheckpointable,
}

public enum CheckpointResourceKind
{
    Manifest = 0,
    LogicalState,
    OwnedMemory,
    Capability,
    Budget,
    DependencyBinding,
    IpcSession,
    ExternalOperation,
    PlatformResource,
    VirtualDomain,
    SecureDomain,
    TraceTelemetry,
    CancellationScope,
    Unknown,
}

public sealed record CheckpointResourceDisposition(
    CheckpointResourceKind Kind,
    string Correlation,
    CheckpointResourceClassification Classification,
    string? Reason = null);

public sealed record CheckpointRegionImage(
    int Ordinal,
    string ElementType,
    byte[] Content,
    string ContentDigest);

public sealed record OrdinaryCheckpointImage(
    CheckpointHandle Handle,
    CheckpointLifecycleState State,
    ComponentIdentity Component,
    ComponentVersion Version,
    string ComponentImageDigest,
    string ManifestDigest,
    ProcessHandle SourceProcess,
    byte[] LogicalState,
    IReadOnlyList<CheckpointRegionImage> Regions,
    IReadOnlyList<CheckpointResourceDisposition> Resources,
    string ImageDigest,
    bool Complete,
    uint ContractVersion = OrdinaryCheckpointContract.Version)
{
    public bool ContainsLiveAuthority => false;
    public bool RestoresExternalAuthority => false;
}

public sealed record OrdinaryCheckpointSnapshot(
    CheckpointHandle Handle,
    CheckpointLifecycleState State,
    ComponentIdentity Component,
    ProcessHandle SourceProcess,
    IReadOnlyList<CheckpointResourceDisposition> Resources,
    string? Failure,
    bool Complete,
    uint ContractVersion = OrdinaryCheckpointContract.Version)
{
    public bool AuthorizesEffect => false;
}

public sealed record OrdinaryRestoreReceipt(
    CheckpointHandle Checkpoint,
    ProcessHandle SourceProcess,
    ComponentIdentity Component,
    ProcessHandle RestoredProcess,
    IReadOnlyList<RegionHandle> RestoredRegions,
    byte[] LogicalState,
    uint ContractVersion = OrdinaryCheckpointContract.Version)
{
    public bool ReusedCapability => false;
    public bool ReusedExternalAuthority => false;
}
