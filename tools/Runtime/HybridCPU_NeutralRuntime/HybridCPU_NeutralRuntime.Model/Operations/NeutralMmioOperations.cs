namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade
{
    public int ActiveMmioLeaseCount { get { lock (_lifecycleGate) return _dependencies.ActiveMmioCount; } }

    public NeutralMmioMapResult MapMmio(NeutralDeviceLease device, NeutralMmioRegionIdentity region, NeutralMmioRange range, NeutralMmioAccess access)
        => MapMmioCore(device,region,range,access,null,out _);

    private NeutralMmioMapResult MapMmioCore(NeutralDeviceLease device,NeutralMmioRegionIdentity region,NeutralMmioRange range,NeutralMmioAccess access,NeutralOriginalMmioAdmissionFact? intent,out bool noEffect)
    {
        lock (_lifecycleGate)
        {
            noEffect=false;
            NeutralLeaseValidation<NeutralDeviceState> parent = _leases.Validate(device);
            if (!parent.IsValid) { noEffect=true; return new() { Decision = ToMmioMap(parent.Status) }; }
            if (string.IsNullOrWhiteSpace(region.ResourceId) || region.ByteLength <= 0) { noEffect=true; return new() { Decision = NeutralMmioMapDecision.InvalidRegion }; }
            if (!NeutralRuntimeValidation.IsValid(range, region)) { noEffect=true; return new() { Decision = NeutralMmioMapDecision.InvalidRange }; }
            if (access == NeutralMmioAccess.None || !NeutralRuntimeValidation.IsDefinedFlags(access, NeutralMmioAccess.Read | NeutralMmioAccess.Write)) { noEffect=true; return new() { Decision = NeutralMmioMapDecision.InvalidAccess }; }
            NeutralDeviceRights required = NeutralDeviceRights.None;
            if ((access & NeutralMmioAccess.Read) != 0) required |= NeutralDeviceRights.Read;
            if ((access & NeutralMmioAccess.Write) != 0) required |= NeutralDeviceRights.Write;
            if (!NeutralRuntimeValidation.Has(parent.State!.Rights, required)) { noEffect=true; return new() { Decision = NeutralMmioMapDecision.InsufficientDeviceRights }; }
            if (_dependencies.HasActiveMmioMapping(device, region, range, access, intent)) { noEffect=true; return new() { Decision = NeutralMmioMapDecision.AlreadyMapped }; }
            if(intent?.Request.Backing is { } backing)
            {
                if(_dependencies.Mmio.Values.Any(existing=>existing.IsActive && existing.Lease.DeviceLease==device && existing.Lease.Region.ResourceId==region.ResourceId))
                {noEffect=true;return new(){Decision=NeutralMmioMapDecision.AlreadyMapped};}
                if(!ReferenceEquals(backing.Original,backing) || !backing.IsActive || backing.Owner is not null || backing.ByteLength!=region.ByteLength)
                { noEffect=true;return new(){Decision=NeutralMmioMapDecision.InvalidRegion}; }
                backing.Owner=intent; // Exclusive original backing factory before native effect.
            }
            if (!TryAllocate(ref _nextResource, out ulong handle)) { noEffect=true; return new() { Decision = NeutralMmioMapDecision.Faulted, Reason = "Neutral resource handle space is exhausted." }; }
            NeutralMmioLease lease = new NeutralMmioLease(device, region, range, access, new(handle), new(1));
            _dependencies.RegisterMmio(lease);
            return new() { IsMapped = true, Lease = lease, Decision = NeutralMmioMapDecision.Mapped };
        }
    }

    public NeutralMmioCloseResult CloseMmio(NeutralMmioLease lease)
    {
        lock (_lifecycleGate)
        {
            NeutralLeaseValidation<NeutralMmioState> validation = _leases.Validate(lease);
            if (!validation.IsValid) return new() { Decision = ToMmioClose(validation.Status) };
            validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
            return new() { Decision = NeutralMmioCloseDecision.Closed };
        }
    }

    private static NeutralMmioMapDecision ToMmioMap(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralMmioMapDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralMmioMapDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralMmioMapDecision.Revoked, _ => NeutralMmioMapDecision.Faulted };
    private static NeutralMmioCloseDecision ToMmioClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralMmioCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralMmioCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralMmioCloseDecision.Revoked, _ => NeutralMmioCloseDecision.Faulted };
}
