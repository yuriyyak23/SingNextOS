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
    public void RegisterDevice(NeutralDeviceLease lease) => Devices.Add(lease.Handle, new() { Lease = lease, OriginalParent = Domains.GetValueOrDefault(lease.DomainLease.Handle) });
    public void RegisterMmio(NeutralMmioLease lease) => Mmio.Add(lease.Handle, new() { Lease = lease });
    public void RegisterInterrupt(NeutralInterruptLease lease) => Interrupts.Add(lease.Handle, new() { Lease = lease });
    public void RegisterDma(NeutralDmaGrant lease) => Dma.Add(lease.Handle, new() { Lease = lease });

    public int ActiveDomainCount => Domains.Values.Count(x => x.IsActive);
    public int ActiveMappingCount => Mappings.Values.Count(x => x.IsActive);
    public int ActiveDeviceCount => Devices.Values.Count(x => x.IsActive);
    public int ActiveMmioCount => Mmio.Values.Count(x => x.IsActive);
    public int ActiveInterruptCount => Interrupts.Values.Count(x => x.IsActive);
    public int ActiveDmaCount => Dma.Values.Count(x => x.IsActive);

    public bool HasActiveDependents(NeutralDomainBindingLease domain) => Mappings.Values.Any(x => x.IsActive && x.Lease.DomainLease == domain) || Devices.Values.Any(x => x.IsActive && x.Lease.DomainLease == domain) ||
        Domains.TryGetValue(domain.Handle,out NeutralDomainState? root) && (!root.HasIntactOriginalDeviceHistory || root.OriginalDeviceAdmissions.Any(fact => !ConsumedOriginalDeviceAdmission(fact)));
    public bool HasActiveDependents(NeutralDeviceLease device) => Mmio.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device) || Interrupts.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device) || Dma.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device) || Devices.TryGetValue(device.Handle,out NeutralDeviceState? owner) && (!owner.HasIntactOriginalLoanHistory || owner.OriginalDeviceLoans.Any(fact => !fact.IsConsumed) || (ulong)owner.OriginalIrqAdmissions.Count != owner.OriginalIrqIssued || owner.OriginalIrqAdmissions.Any(fact => !ConsumedOriginalIrqAdmission(fact)) || !owner.HasIntactOriginalDmaHistory || owner.OriginalDmaAdmissions.Any(fact => !ConsumedOriginalDmaAdmission(fact)) || (ulong)owner.OriginalMmioAdmissions.Count != owner.OriginalMmioIssued || owner.OriginalMmioAdmissions.Any(fact => !ConsumedOriginalMmioAdmission(fact)));
    public bool HasActiveDependents(NeutralOwnedRegionMappingLease mapping) => Dma.Values.Any(x => x.IsActive && x.Lease.MappingLease == mapping);
    private static bool ConsumedOriginalIrqAdmission(NeutralOriginalIrqAdmissionFact fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.Source is null && fact.Observation is { NoEffectGuaranteed: true } raw && raw.Request == fact.Request && !raw.Observation.IsBound && raw.Observation.Lease == default ||
         fact.Source is { } source && ReferenceEquals(source.OriginalAdmission,fact) && source.HasExactOriginalClosure);
    private static bool ConsumedOriginalMmioAdmission(NeutralOriginalMmioAdmissionFact fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.NoEffect && fact.Source is null && fact.Observation is { SourceFenceMatched: true, NoEffectGuaranteed: true } raw && raw.Request == fact.Request &&
         raw.Observation is { IsMapped: false } result && result.Lease == default && Enum.IsDefined(result.Decision) && result.Decision != NeutralMmioMapDecision.Mapped ||
         fact.Source is { } source && ReferenceEquals(source.OriginalAdmission,fact) && source.HasExactOriginalClosure);
    private static bool ConsumedOriginalDmaAdmission(NeutralOriginalDmaAdmissionFact fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.Source is null && fact.Observation is { NoEffectGuaranteed: true } noEffect && noEffect.Request == fact.Request && !noEffect.Observation.IsGranted && noEffect.Observation.Grant == default ||
         fact.Source is { } source && ReferenceEquals(source.OwnerSelf,source) && ReferenceEquals(source.OriginalAdmission,fact) && source.OriginalClose is { } close &&
         ReferenceEquals(close.OwnerSelf,close) && ReferenceEquals(close.Source,source) && ReferenceEquals(close.Admission,fact) && close.Request.Grant == source.Lease &&
         close.Request.DmaBindInvocationId == fact.Request.InvocationId && close.Observation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw && raw.Request == close.Request && raw.Observation.Decision == NeutralDmaGrantCloseDecision.Closed);
    private static bool ConsumedOriginalDeviceAdmission(NeutralOriginalDeviceAdmissionFact fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.Source is null && fact.Observation is { NoEffectGuaranteed: true } raw && raw.Request == fact.Request && raw.Observation is { IsBound: false } && raw.Observation.Lease == default ||
         fact.Source is { } source && ReferenceEquals(source.OriginalAdmission,fact) && source.HasExactOriginalClosure);
    public bool HasActiveDeviceBinding(NeutralDomainBindingLease domain, NeutralDeviceIdentity identity, NeutralOriginalDeviceAdmissionFact? intent = null) => Devices.Values.Any(x => x.IsActive && x.Lease.DomainLease == domain && x.Identity == identity) ||
        Domains.TryGetValue(domain.Handle,out NeutralDomainState? root) && root.OriginalDeviceAdmissions.Any(fact => !ReferenceEquals(fact,intent) && fact.Request.Device == identity && !ConsumedOriginalDeviceAdmission(fact));
    public bool HasActiveMmioMapping(NeutralDeviceLease device, NeutralMmioRegionIdentity region, NeutralMmioRange range, NeutralMmioAccess access, NeutralOriginalMmioAdmissionFact? intent = null) => Mmio.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device && x.Lease.Region == region && x.Lease.Range == range && x.Lease.Access == access) || Devices.TryGetValue(device.Handle,out NeutralDeviceState? owner) && owner.OriginalMmioAdmissions.Any(fact => !ReferenceEquals(fact,intent) && fact.Request.Region == region && fact.Request.Range == range && fact.Request.Access == access && !ConsumedOriginalMmioAdmission(fact));
    public bool HasActiveInterruptBinding(NeutralDeviceLease device, NeutralInterruptSourceIdentity source,NeutralOriginalIrqAdmissionFact? intent = null) => Interrupts.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device && x.Lease.Source == source) || Devices.TryGetValue(device.Handle,out NeutralDeviceState? owner) && owner.OriginalIrqAdmissions.Any(fact => !ReferenceEquals(fact,intent) && fact.Request.Source == source && !ConsumedOriginalIrqAdmission(fact));
    public bool HasActiveDmaGrant(NeutralDeviceLease device, NeutralOwnedRegionMappingLease mapping, NeutralDmaRange range, NeutralDmaDirection direction) => Dma.Values.Any(x => x.IsActive && x.Lease.DeviceLease == device && x.Lease.MappingLease == mapping && x.Lease.Range == range && x.Lease.Direction == direction);
}

