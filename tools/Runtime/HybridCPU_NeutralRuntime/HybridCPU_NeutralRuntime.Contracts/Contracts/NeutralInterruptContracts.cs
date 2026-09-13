namespace YAKSys_Hybrid_CPU.Core;

public enum NeutralInterruptTrigger { Edge, Level }
public enum NeutralInterruptBindDecision { Bound, InvalidSource, InsufficientDeviceRights, AlreadyBound, Revoked, Stale, NotFound, Faulted }
public enum NeutralInterruptSignalDecision { Signaled, AlreadyPending, Revoked, Stale, NotFound, Faulted }
public enum NeutralInterruptPollDecision { Observed, NoDelivery, Revoked, Stale, NotFound, Faulted }
public enum NeutralInterruptCompleteDecision { Completed, NoPendingDelivery, WrongSequence, Revoked, Stale, NotFound, Faulted }
public enum NeutralInterruptCloseDecision { Closed, Revoked, PendingDelivery, Stale, NotFound, Faulted }
public readonly record struct NeutralInterruptSourceIdentity(string ResourceId, NeutralInterruptTrigger Trigger);
public readonly record struct NeutralInterruptDeliverySequence(ulong Value);
public readonly record struct NeutralInterruptLeaseHandle(ulong Value);
public readonly record struct NeutralInterruptLeaseEpoch(ulong Value);
public readonly record struct NeutralInterruptLease(NeutralDeviceLease DeviceLease, NeutralInterruptSourceIdentity Source, NeutralInterruptLeaseHandle Handle, NeutralInterruptLeaseEpoch Epoch);
public sealed class NeutralInterruptBindResult { public bool IsBound { get; init; } public NeutralInterruptLease Lease { get; init; } public NeutralInterruptBindDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralInterruptSignalResult { public bool IsSignaled { get; init; } public NeutralInterruptSignalDecision Decision { get; init; } public NeutralInterruptDeliverySequence Sequence { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralInterruptPollResult { public bool IsObserved { get; init; } public NeutralInterruptPollDecision Decision { get; init; } public NeutralInterruptLease Lease { get; init; } public bool DeliveryAvailable { get; init; } public NeutralInterruptDeliverySequence Sequence { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralInterruptCompleteResult { public NeutralInterruptCompleteDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
public sealed class NeutralInterruptCloseResult { public NeutralInterruptCloseDecision Decision { get; init; } public string Reason { get; init; } = string.Empty; }
