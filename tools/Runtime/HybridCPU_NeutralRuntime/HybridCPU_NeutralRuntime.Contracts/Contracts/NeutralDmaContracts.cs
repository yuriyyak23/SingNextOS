namespace YAKSys_Hybrid_CPU.Core;

public enum NeutralDmaDirection { DeviceReadsMemory, DeviceWritesMemory, Bidirectional }
public enum NeutralDmaGrantDecision { Granted, InvalidRange, InvalidDirection, InsufficientDeviceRights, InsufficientMappingAccess, WrongDomain, AlreadyGranted, Revoked, Stale, NotFound, Faulted }
public enum NeutralDmaGrantCloseDecision { Closed, Revoked, Stale, NotFound, Faulted }
public enum NeutralDmaPrepareDecision { Prepared, NotFound, Stale, Revoked, VisibilityUnsupported, Faulted }
public enum NeutralDmaAcquireDecision { Acquired, NotFound, Stale, Revoked, VisibilityUnsupported, NotRequired, NotPrepared, AlreadyAcquired, Faulted }
public readonly record struct NeutralDmaRange(long Offset, long Length);
public readonly record struct NeutralDmaVisibilityCycle(ulong Value);
public readonly record struct NeutralDmaGrantHandle(ulong Value);
public readonly record struct NeutralDmaGrantEpoch(ulong Value);
public readonly record struct NeutralDmaGrant(NeutralDeviceLease DeviceLease, NeutralOwnedRegionMappingLease MappingLease, NeutralDmaRange Range, NeutralDmaDirection Direction, NeutralDmaGrantHandle Handle, NeutralDmaGrantEpoch Epoch);
public sealed class NeutralDmaGrantResult { public bool IsGranted { get; init; } public NeutralDmaGrant Grant { get; init; } public NeutralDmaGrantDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralDmaGrantCloseResult { public NeutralDmaGrantCloseDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralDmaVisibilityEvidence { public NeutralDmaGrantHandle GrantHandle { get; init; } public NeutralDmaGrantEpoch GrantEpoch { get; init; } public NeutralDmaDirection Direction { get; init; } public NeutralDmaVisibilityCycle Cycle { get; init; } }
public sealed class NeutralDmaPrepareResult { public bool IsPrepared { get; init; } public NeutralDmaPrepareDecision Decision { get; init; } public NeutralDmaVisibilityEvidence Evidence { get; init; } = new(); public string Reason { get; init; } = string.Empty; }
public sealed class NeutralDmaAcquireResult { public bool IsAcquired { get; init; } public NeutralDmaAcquireDecision Decision { get; init; } public NeutralDmaVisibilityEvidence Evidence { get; init; } = new(); public string Reason { get; init; } = string.Empty; }
