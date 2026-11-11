namespace YAKSys_Hybrid_CPU.Core;

[Flags]
public enum NeutralDeviceRights { None = 0, Read = 1, Write = 2, Configure = 4 }
public enum NeutralDeviceBindDecision { Bound, InvalidDevice, InvalidRights, AlreadyBound, NotFound, Stale, Revoked, Faulted }
public enum NeutralDeviceCloseDecision { Closed, Revoked, ActiveDependents, Stale, NotFound, Faulted }
public readonly record struct NeutralDeviceIdentity(string ResourceId);
public readonly record struct NeutralDeviceLeaseHandle(ulong Value);
public readonly record struct NeutralDeviceLeaseEpoch(ulong Value);
public readonly record struct NeutralDeviceLease(NeutralDomainBindingLease DomainLease, NeutralDeviceIdentity Device, NeutralDeviceRights Rights, NeutralDeviceLeaseHandle Handle, NeutralDeviceLeaseEpoch Epoch);
public sealed class NeutralDeviceBindResult { public bool IsBound { get; init; } public NeutralDeviceLease Lease { get; init; } public NeutralDeviceBindDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralDeviceCloseResult { public NeutralDeviceCloseDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
