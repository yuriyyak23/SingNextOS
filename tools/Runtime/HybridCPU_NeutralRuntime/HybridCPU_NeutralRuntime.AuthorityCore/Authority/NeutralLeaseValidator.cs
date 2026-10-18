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
    public NeutralLeaseValidation<NeutralDomainState> Validate(NeutralDomainBindingLease lease)
    {
        var result = Validate(registry.Domains, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
        return (result.State?.OriginalAdmission is { } fact && !ReferenceEquals(fact.Source, result.State)) ||
            (result.State?.OriginalClose is { } close && !ReferenceEquals(close.Source, result.State))
            ? new(NeutralLeaseValidationStatus.Faulted, null) : result;
    }
    public NeutralLeaseValidation<NeutralMappingState> Validate(NeutralOwnedRegionMappingLease lease)
    {
        var result = Validate(registry.Mappings, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
        if (result.State?.OriginalAdmission is { } fact &&
            (!ReferenceEquals(fact.Source, result.State) || !registry.Domains.TryGetValue(lease.DomainLease.Handle, out var parent) ||
             !ReferenceEquals(fact.Parent, parent) || parent.Lease != lease.DomainLease ||
             parent.OriginalAdmission is not { } original || !ReferenceEquals(original.Source, parent) ||
             original.Request.InvocationId != fact.Request.DomainBindInvocationId))
            return new(NeutralLeaseValidationStatus.Faulted, null);
        return result;
    }
    public NeutralLeaseValidation<NeutralDeviceState> Validate(NeutralDeviceLease lease)
    {
        var result = Validate(registry.Devices, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
        return result.State is { } state && (!ReferenceEquals(state.OwnerSelf,state) ||
            state.OriginalAdmission is { } admission && (!ReferenceEquals(admission.OwnerSelf,admission) || !ReferenceEquals(admission.Source,state) ||
                !registry.Domains.TryGetValue(state.ParentDomain,out var parent) || !ReferenceEquals(parent,admission.Parent) || !ReferenceEquals(parent.OriginalAdmission,admission.RootAdmission) ||
                !parent.OriginalDeviceAdmissions.Any(old => ReferenceEquals(old,admission))) ||
            state.OriginalClose is { } close && (!ReferenceEquals(close.OwnerSelf,close) || !ReferenceEquals(close.Source,state))) ? new(NeutralLeaseValidationStatus.Faulted,null) : result;
    }
    public NeutralLeaseValidation<NeutralMmioState> Validate(NeutralMmioLease lease)
    {
        var result = Validate(registry.Mmio,lease.Handle,lease.Epoch,lease,x => x.Epoch,x => x.Lease,x => x.IsActive);
        return result.State is { } state && (!ReferenceEquals(state.OwnerSelf,state) || state.OriginalAdmission is { } admission &&
            (!ReferenceEquals(admission.OwnerSelf,admission) || !ReferenceEquals(admission.Source,state) ||
             !registry.Devices.TryGetValue(state.ParentDevice,out var parent) || !ReferenceEquals(parent,admission.Parent) || !ReferenceEquals(parent.OriginalAdmission,admission.DeviceAdmission) ||
             !parent.OriginalMmioAdmissions.Any(old => ReferenceEquals(old,admission))) ||
             state.OriginalClose is { } close && (!ReferenceEquals(close.OwnerSelf,close) || !ReferenceEquals(close.Source,state)))
            ? new(NeutralLeaseValidationStatus.Faulted,null) : result;
    }
    public NeutralLeaseValidation<NeutralInterruptState> Validate(NeutralInterruptLease lease)
    {
        var result = Validate(registry.Interrupts,lease.Handle,lease.Epoch,lease,x => x.Epoch,x => x.Lease,x => x.IsActive);
        return result.State is { } state && (!ReferenceEquals(state.OwnerSelf,state) || state.OriginalAdmission is { } admission &&
            (!ReferenceEquals(admission.OwnerSelf,admission) || !ReferenceEquals(admission.Source,state) ||
             !registry.Devices.TryGetValue(state.ParentDevice,out var parent) || !ReferenceEquals(parent,admission.Parent) || !ReferenceEquals(parent.OriginalAdmission,admission.DeviceAdmission) ||
             !parent.OriginalIrqAdmissions.Any(old => ReferenceEquals(old,admission))) ||
             state.OriginalClose is { } close && (!ReferenceEquals(close.OwnerSelf,close) || !ReferenceEquals(close.Source,state)))
            ? new(NeutralLeaseValidationStatus.Faulted,null) : result;
    }
    public NeutralLeaseValidation<NeutralDmaGrantState> Validate(NeutralDmaGrant lease)
    {
        var result = Validate(registry.Dma, lease.Handle, lease.Epoch, lease, x => x.Epoch, x => x.Lease, x => x.IsActive);
        return result.State is { } state && (!ReferenceEquals(state.OwnerSelf, state) ||
            state.OriginalAdmission is { } admission && (!ReferenceEquals(admission.OwnerSelf, admission) || !ReferenceEquals(admission.Source, state)) ||
            state.OriginalClose is { } close && (!ReferenceEquals(close.OwnerSelf, close) || !ReferenceEquals(close.Source, state)))
            ? new(NeutralLeaseValidationStatus.Faulted, null) : result;
    }

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
