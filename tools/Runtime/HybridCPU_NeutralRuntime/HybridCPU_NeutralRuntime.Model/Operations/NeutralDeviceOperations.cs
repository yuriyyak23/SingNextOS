namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade
{
    public int ActiveDeviceLeaseCount => _dependencies.ActiveDeviceCount;

    public NeutralDeviceBindResult BindDevice(NeutralDomainBindingLease domain, NeutralDeviceIdentity identity, NeutralDeviceRights rights)
    {
        var parent = _leases.Validate(domain);
        if (!parent.IsValid) return new() { Decision = ToDeviceBind(parent.Status) };
        if (string.IsNullOrWhiteSpace(identity.ResourceId)) return new() { Decision = NeutralDeviceBindDecision.InvalidDevice };
        if (rights == NeutralDeviceRights.None || !NeutralRuntimeValidation.IsDefinedFlags(rights, NeutralDeviceRights.Read | NeutralDeviceRights.Write | NeutralDeviceRights.Configure)) return new() { Decision = NeutralDeviceBindDecision.InvalidRights };
        if (_dependencies.HasActiveDeviceBinding(domain, identity)) return new() { Decision = NeutralDeviceBindDecision.AlreadyBound };
        if (!TryAllocate(ref _nextResource, out var handle)) return new() { Decision = NeutralDeviceBindDecision.Faulted, Reason = "Neutral resource handle space is exhausted." };
        var lease = new NeutralDeviceLease(domain, identity, rights, new(handle), new(1));
        _dependencies.RegisterDevice(lease);
        return new() { IsBound = true, Lease = lease, Decision = NeutralDeviceBindDecision.Bound };
    }

    public NeutralDeviceCloseResult CloseDevice(NeutralDeviceLease lease)
    {
        var validation = _leases.Validate(lease);
        if (!validation.IsValid) return new() { Decision = ToDeviceClose(validation.Status) };
        if (_dependencies.HasActiveDependents(lease)) return new() { Decision = NeutralDeviceCloseDecision.ActiveDependents };
        validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
        return new() { Decision = NeutralDeviceCloseDecision.Closed };
    }

    private static NeutralDeviceBindDecision ToDeviceBind(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDeviceBindDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDeviceBindDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDeviceBindDecision.Revoked, _ => NeutralDeviceBindDecision.Faulted };
    private static NeutralDeviceCloseDecision ToDeviceClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDeviceCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDeviceCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDeviceCloseDecision.Revoked, _ => NeutralDeviceCloseDecision.Faulted };
}
