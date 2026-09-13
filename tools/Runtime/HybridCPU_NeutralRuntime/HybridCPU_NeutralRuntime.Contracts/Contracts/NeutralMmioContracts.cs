namespace YAKSys_Hybrid_CPU.Core;

[Flags]
public enum NeutralMmioAccess { None = 0, Read = 1, Write = 2 }
public enum NeutralMmioMapDecision { Mapped, InvalidRegion, InvalidRange, InvalidAccess, InsufficientDeviceRights, AlreadyMapped, NotFound, Stale, Revoked, Faulted }
public enum NeutralMmioCloseDecision { Closed, Revoked, Stale, NotFound, Faulted }
public readonly record struct NeutralMmioRegionIdentity(string ResourceId, long ByteLength);
public readonly record struct NeutralMmioRange(long Offset, long Length);
public readonly record struct NeutralMmioLeaseHandle(ulong Value);
public readonly record struct NeutralMmioLeaseEpoch(ulong Value);
public readonly record struct NeutralMmioLease(NeutralDeviceLease DeviceLease, NeutralMmioRegionIdentity Region, NeutralMmioRange Range, NeutralMmioAccess Access, NeutralMmioLeaseHandle Handle, NeutralMmioLeaseEpoch Epoch);
public sealed class NeutralMmioMapResult { public bool IsMapped { get; init; } public NeutralMmioLease Lease { get; init; } public NeutralMmioMapDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralMmioCloseResult { public NeutralMmioCloseDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
