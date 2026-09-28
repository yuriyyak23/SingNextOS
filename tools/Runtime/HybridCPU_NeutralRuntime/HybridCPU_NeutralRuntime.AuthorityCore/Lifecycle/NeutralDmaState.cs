namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDmaGrantState
{
    public required NeutralDmaGrant Lease { get; init; }
    public NeutralDmaGrantHandle Handle => Lease.Handle;
    public NeutralDmaGrantEpoch Epoch => Lease.Epoch;
    public NeutralDeviceLeaseHandle ParentDevice => Lease.DeviceLease.Handle;
    public NeutralOwnedRegionMappingHandle ParentMapping => Lease.MappingLease.Handle;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
    public NeutralDmaVisibilityState? Visibility { get; set; }
}

internal sealed class NeutralDmaVisibilityState
{
    public required NeutralDmaVisibilityCycle Cycle { get; init; }
    public bool Acquired { get; set; }
}
