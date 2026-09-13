namespace YAKSys_Hybrid_CPU.Core;

internal enum NeutralDeliveryState { Idle, Pending }

internal sealed class NeutralInterruptState
{
    public required NeutralInterruptLease Lease { get; init; }
    public NeutralInterruptLeaseHandle Handle => Lease.Handle;
    public NeutralInterruptLeaseEpoch Epoch => Lease.Epoch;
    public NeutralDeviceLeaseHandle ParentDevice => Lease.DeviceLease.Handle;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
    public NeutralDeliveryState Delivery { get; set; } = NeutralDeliveryState.Idle;
    public NeutralInterruptDeliverySequence Sequence { get; set; }
}
