namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralMmioState
{
    public required NeutralMmioLease Lease { get; init; }
    public NeutralMmioLeaseHandle Handle => Lease.Handle;
    public NeutralMmioLeaseEpoch Epoch => Lease.Epoch;
    public NeutralDeviceLeaseHandle ParentDevice => Lease.DeviceLease.Handle;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
}
