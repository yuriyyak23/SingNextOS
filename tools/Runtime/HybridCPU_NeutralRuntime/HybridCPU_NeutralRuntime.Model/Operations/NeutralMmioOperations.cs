namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade
{
    public int ActiveMmioLeaseCount => _dependencies.ActiveMmioCount;

    public NeutralMmioMapResult MapMmio(NeutralDeviceLease device, NeutralMmioRegionIdentity region, NeutralMmioRange range, NeutralMmioAccess access)
    {
        var parent = _leases.Validate(device);
        if (!parent.IsValid) return new() { Decision = ToMmioMap(parent.Status) };
        if (string.IsNullOrWhiteSpace(region.ResourceId) || region.ByteLength <= 0) return new() { Decision = NeutralMmioMapDecision.InvalidRegion };
        if (!NeutralRuntimeValidation.IsValid(range, region)) return new() { Decision = NeutralMmioMapDecision.InvalidRange };
        if (access == NeutralMmioAccess.None || !NeutralRuntimeValidation.IsDefinedFlags(access, NeutralMmioAccess.Read | NeutralMmioAccess.Write)) return new() { Decision = NeutralMmioMapDecision.InvalidAccess };
        var required = NeutralDeviceRights.None;
        if ((access & NeutralMmioAccess.Read) != 0) required |= NeutralDeviceRights.Read;
        if ((access & NeutralMmioAccess.Write) != 0) required |= NeutralDeviceRights.Write;
        if (!NeutralRuntimeValidation.Has(parent.State!.Rights, required)) return new() { Decision = NeutralMmioMapDecision.InsufficientDeviceRights };
        if (_dependencies.HasActiveMmioMapping(device, region, range, access)) return new() { Decision = NeutralMmioMapDecision.AlreadyMapped };
        if (!TryAllocate(ref _nextResource, out var handle)) return new() { Decision = NeutralMmioMapDecision.Faulted, Reason = "Neutral resource handle space is exhausted." };
        var lease = new NeutralMmioLease(device, region, range, access, new(handle), new(1));
        _dependencies.RegisterMmio(lease);
        return new() { IsMapped = true, Lease = lease, Decision = NeutralMmioMapDecision.Mapped };
    }

    public NeutralMmioCloseResult CloseMmio(NeutralMmioLease lease)
    {
        var validation = _leases.Validate(lease);
        if (!validation.IsValid) return new() { Decision = ToMmioClose(validation.Status) };
        validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
        return new() { Decision = NeutralMmioCloseDecision.Closed };
    }

    private static NeutralMmioMapDecision ToMmioMap(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralMmioMapDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralMmioMapDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralMmioMapDecision.Revoked, _ => NeutralMmioMapDecision.Faulted };
    private static NeutralMmioCloseDecision ToMmioClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralMmioCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralMmioCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralMmioCloseDecision.Revoked, _ => NeutralMmioCloseDecision.Faulted };
}
