namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade
{
    public int ActiveDeviceLeaseCount { get { lock (_lifecycleGate) return _dependencies.ActiveDeviceCount; } }

    public NeutralDeviceBindResult BindDevice(NeutralDomainBindingLease domain, NeutralDeviceIdentity identity, NeutralDeviceRights rights)
        => BindDeviceCore(domain, identity, rights, null,out _);

    private NeutralDeviceBindResult BindDeviceCore(NeutralDomainBindingLease domain, NeutralDeviceIdentity identity, NeutralDeviceRights rights, NeutralOriginalDeviceAdmissionFact? intent,out bool noEffect)
    {
        noEffect=false;
        lock (_lifecycleGate)
        {
            NeutralLeaseValidation<NeutralDomainState> parent = _leases.Validate(domain);
            if (!parent.IsValid) {noEffect=true;return new() { Decision = ToDeviceBind(parent.Status) };}
            if (parent.State!.OriginalClose is not null) {noEffect=true;return new() { Decision = NeutralDeviceBindDecision.Faulted, Reason = "Original root Close is pending." };}
            if (string.IsNullOrWhiteSpace(identity.ResourceId)) {noEffect=true;return new() { Decision = NeutralDeviceBindDecision.InvalidDevice };}
            if (rights == NeutralDeviceRights.None || !NeutralRuntimeValidation.IsDefinedFlags(rights, NeutralDeviceRights.Read | NeutralDeviceRights.Write | NeutralDeviceRights.Configure)) {noEffect=true;return new() { Decision = NeutralDeviceBindDecision.InvalidRights };}
            if (_dependencies.HasActiveDeviceBinding(domain, identity, intent)) {noEffect=true;return new() { Decision = NeutralDeviceBindDecision.AlreadyBound };}
            if (!TryAllocate(ref _nextResource, out ulong handle)) {noEffect=true;return new() { Decision = NeutralDeviceBindDecision.Faulted, Reason = "Neutral resource handle space is exhausted." };}
            NeutralDeviceLease lease = new NeutralDeviceLease(domain, identity, rights, new(handle), new(1));
            _dependencies.RegisterDevice(lease);
            return new() { IsBound = true, Lease = lease, Decision = NeutralDeviceBindDecision.Bound };
        }
    }

    public NeutralDeviceCloseResult CloseDevice(NeutralDeviceLease lease)
    {
        lock (_lifecycleGate)
        {
            NeutralLeaseValidation<NeutralDeviceState> validation = _leases.Validate(lease);
            if (!validation.IsValid) return new() { Decision = ToDeviceClose(validation.Status) };
            if (_dependencies.HasActiveDependents(lease)) return new() { Decision = NeutralDeviceCloseDecision.ActiveDependents };
            validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
            return new() { Decision = NeutralDeviceCloseDecision.Closed };
        }
    }

    private static NeutralDeviceBindDecision ToDeviceBind(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDeviceBindDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDeviceBindDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDeviceBindDecision.Revoked, _ => NeutralDeviceBindDecision.Faulted };
    private static NeutralDeviceCloseDecision ToDeviceClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDeviceCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDeviceCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDeviceCloseDecision.Revoked, _ => NeutralDeviceCloseDecision.Faulted };
}
