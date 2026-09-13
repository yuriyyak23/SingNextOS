namespace YAKSys_Hybrid_CPU.Core;

internal enum NeutralLeaseValidationStatus { Valid, NotFound, Stale, Revoked, Faulted }

internal readonly record struct NeutralLeaseValidation<T>(NeutralLeaseValidationStatus Status, T? State) where T : class
{
    public bool IsValid => Status == NeutralLeaseValidationStatus.Valid;
}

internal interface INeutralLeaseValidator
{
    NeutralLeaseValidation<NeutralDomainState> Validate(NeutralDomainBindingLease lease);
    NeutralLeaseValidation<NeutralMappingState> Validate(NeutralOwnedRegionMappingLease lease);
    NeutralLeaseValidation<NeutralDeviceState> Validate(NeutralDeviceLease lease);
    NeutralLeaseValidation<NeutralMmioState> Validate(NeutralMmioLease lease);
    NeutralLeaseValidation<NeutralInterruptState> Validate(NeutralInterruptLease lease);
    NeutralLeaseValidation<NeutralDmaGrantState> Validate(NeutralDmaGrant lease);
}

internal sealed class NeutralLeaseValidator(NeutralDependencyRegistry registry) : INeutralLeaseValidator
{
    public NeutralLeaseValidation<NeutralDomainState> Validate(NeutralDomainBindingLease lease) =>
        Validate(registry.Domains, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
    public NeutralLeaseValidation<NeutralMappingState> Validate(NeutralOwnedRegionMappingLease lease) =>
        Validate(registry.Mappings, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
    public NeutralLeaseValidation<NeutralDeviceState> Validate(NeutralDeviceLease lease) =>
        Validate(registry.Devices, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
    public NeutralLeaseValidation<NeutralMmioState> Validate(NeutralMmioLease lease) =>
        Validate(registry.Mmio, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
    public NeutralLeaseValidation<NeutralInterruptState> Validate(NeutralInterruptLease lease) =>
        Validate(registry.Interrupts, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
    public NeutralLeaseValidation<NeutralDmaGrantState> Validate(NeutralDmaGrant lease) =>
        Validate(registry.Dma, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);

    private static NeutralLeaseValidation<TState> Validate<THandle, TEpoch, TLease, TState>(
        Dictionary<THandle, TState> records, THandle handle, TEpoch epoch, TLease lease,
        Func<TState, TEpoch> getEpoch, Func<TState, TLease> getLease, Func<TState, bool> isActive)
        where THandle : notnull where TState : class
    {
        if (!records.TryGetValue(handle, out var state)) return new(NeutralLeaseValidationStatus.NotFound, null);
        if (!EqualityComparer<TEpoch>.Default.Equals(getEpoch(state), epoch)) return new(NeutralLeaseValidationStatus.Stale, null);
        if (!EqualityComparer<TLease>.Default.Equals(getLease(state), lease)) return new(NeutralLeaseValidationStatus.Faulted, null);
        if (!isActive(state)) return new(NeutralLeaseValidationStatus.Revoked, state);
        return new(NeutralLeaseValidationStatus.Valid, state);
    }
}
