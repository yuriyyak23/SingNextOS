namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralMappingState
{
    public required NeutralOwnedRegionMappingLease Lease { get; init; }
    public NeutralOwnedRegionMappingHandle Handle => Lease.Handle;
    public NeutralOwnedRegionMappingEpoch Epoch => Lease.Epoch;
    public NeutralDomainBindingHandle ParentDomain => Lease.DomainLease.Handle;
    public NeutralOwnedRegionSlice Slice => Lease.Slice;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
}
