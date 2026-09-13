namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDeviceState
{
    public required NeutralDeviceLease Lease { get; init; }
    public NeutralDeviceLeaseHandle Handle => Lease.Handle;
    public NeutralDeviceLeaseEpoch Epoch => Lease.Epoch;
    public NeutralDomainBindingHandle ParentDomain => Lease.DomainLease.Handle;
    public NeutralDeviceIdentity Identity => Lease.Device;
    public NeutralDeviceRights Rights => Lease.Rights;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
}
