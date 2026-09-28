namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDependencyRegistry
{
    internal readonly Dictionary<NeutralDomainBindingHandle, NeutralDomainState> Domains = [];
    internal readonly Dictionary<NeutralOwnedRegionMappingHandle, NeutralMappingState> Mappings = [];
    internal readonly Dictionary<NeutralDeviceLeaseHandle, NeutralDeviceState> Devices = [];
    internal readonly Dictionary<NeutralMmioLeaseHandle, NeutralMmioState> Mmio = [];
    internal readonly Dictionary<NeutralInterruptLeaseHandle, NeutralInterruptState> Interrupts = [];
    internal readonly Dictionary<NeutralDmaGrantHandle, NeutralDmaGrantState> Dma = [];

    public void RegisterDomain(NeutralDomainBindingLease lease) => Domains.Add(lease.Handle, new() { Lease = lease });
    public void RegisterMapping(NeutralOwnedRegionMappingLease lease) => Mappings.Add(lease.Handle, new() { Lease = lease });
    public void RegisterDevice(NeutralDeviceLease lease) => Devices.Add(lease.Handle, new() { Lease = lease });
    public void RegisterMmio(NeutralMmioLease lease) => Mmio.Add(lease.Handle, new() { Lease = lease });
    public void RegisterInterrupt(NeutralInterruptLease lease) => Interrupts.Add(lease.Handle, new() { Lease = lease });
    public void RegisterDma(NeutralDmaGrant lease) => Dma.Add(lease.Handle, new() { Lease = lease });

    public int ActiveDomainCount => Domains.Values.Count(x => x.IsActive);
    public int ActiveMappingCount => Mappings.Values.Count(x => x.IsActive);
    public int ActiveDeviceCount => Devices.Values.Count(x => x.IsActive);
    public int ActiveMmioCount => Mmio.Values.Count(x => x.IsActive);
    public int ActiveInterruptCount => Interrupts.Values.Count(x => x.IsActive);
    public int ActiveDmaCount => Dma.Values.Count(x => x.IsActive);

    public bool HasActiveDependents(NeutralDomainBindingLease domain) => Mappings.Values.Any(x => x.IsActive && x.Lease.DomainLease == domain) || Devices.Values.Any(x => x.IsActive && x.Lease.DomainLease == domain);
    public bool HasActiveDependents(NeutralDeviceLease device) => Mmio.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device) || Interrupts.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device) || Dma.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device);
    public bool HasActiveDependents(NeutralOwnedRegionMappingLease mapping) => Dma.Values.Any(x => x.IsActive && x.Lease.MappingLease == mapping);
    public bool HasActiveDeviceBinding(NeutralDomainBindingLease domain, NeutralDeviceIdentity identity) => Devices.Values.Any(x => x.IsActive && x.Lease.DomainLease == domain && x.Identity == identity);
    public bool HasActiveMmioMapping(NeutralDeviceLease device, NeutralMmioRegionIdentity region, NeutralMmioRange range, NeutralMmioAccess access) => Mmio.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device && x.Lease.Region == region && x.Lease.Range == range && x.Lease.Access == access);
    public bool HasActiveInterruptBinding(NeutralDeviceLease device, NeutralInterruptSourceIdentity source) => Interrupts.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device && x.Lease.Source == source);
    public bool HasActiveDmaGrant(NeutralDeviceLease device, NeutralOwnedRegionMappingLease mapping, NeutralDmaRange range, NeutralDmaDirection direction) => Dma.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device && x.Lease.MappingLease == mapping && x.Lease.Range == range && x.Lease.Direction == direction);
}
