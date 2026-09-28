namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade
{
    public int ActiveDmaGrantCount => _dependencies.ActiveDmaCount;

    public NeutralDmaGrantResult BindDmaGrant(NeutralDeviceLease device, NeutralOwnedRegionMappingLease mapping, NeutralDmaRange range, NeutralDmaDirection direction)
    {
        var deviceValidation = _leases.Validate(device);
        if (!deviceValidation.IsValid) return new() { Decision = ToDmaGrant(deviceValidation.Status) };
        var mappingValidation = _leases.Validate(mapping);
        if (!mappingValidation.IsValid) return new() { Decision = ToDmaGrant(mappingValidation.Status) };
        if (device.DomainLease != mapping.DomainLease) return new() { Decision = NeutralDmaGrantDecision.WrongDomain };
        if (!NeutralRuntimeValidation.IsValid(range) || range.Offset >= mapping.Slice.Length || range.Length > mapping.Slice.Length - range.Offset) return new() { Decision = NeutralDmaGrantDecision.InvalidRange };
        if (!Enum.IsDefined(direction)) return new() { Decision = NeutralDmaGrantDecision.InvalidDirection };

        var requiredDevice = direction switch
        {
            NeutralDmaDirection.DeviceReadsMemory => NeutralDeviceRights.Configure | NeutralDeviceRights.Read,
            NeutralDmaDirection.DeviceWritesMemory => NeutralDeviceRights.Configure | NeutralDeviceRights.Write,
            NeutralDmaDirection.Bidirectional => NeutralDeviceRights.Configure | NeutralDeviceRights.Read | NeutralDeviceRights.Write,
            _ => NeutralDeviceRights.None,
        };
        var requiredMapping = direction switch
        {
            NeutralDmaDirection.DeviceReadsMemory => NeutralMemoryAccess.Read,
            NeutralDmaDirection.DeviceWritesMemory => NeutralMemoryAccess.Write,
            NeutralDmaDirection.Bidirectional => NeutralMemoryAccess.Read | NeutralMemoryAccess.Write,
            _ => NeutralMemoryAccess.None,
        };
        if (!NeutralRuntimeValidation.Has(deviceValidation.State!.Rights, requiredDevice)) return new() { Decision = NeutralDmaGrantDecision.InsufficientDeviceRights };
        if (!NeutralRuntimeValidation.Has(mappingValidation.State!.Slice.Access, requiredMapping)) return new() { Decision = NeutralDmaGrantDecision.InsufficientMappingAccess };
        if (_dependencies.HasActiveDmaGrant(device, mapping, range, direction)) return new() { Decision = NeutralDmaGrantDecision.AlreadyGranted };

        if (!TryAllocate(ref _nextResource, out var handle)) return new() { Decision = NeutralDmaGrantDecision.Faulted, Reason = "Neutral resource handle space is exhausted." };
        var grant = new NeutralDmaGrant(device, mapping, range, direction, new(handle), new(1));
        _dependencies.RegisterDma(grant);
        return new() { IsGranted = true, Grant = grant, Decision = NeutralDmaGrantDecision.Granted };
    }

    public NeutralDmaGrantCloseResult CloseDmaGrant(NeutralDmaGrant grant)
    {
        var validation = _leases.Validate(grant);
        if (!validation.IsValid) return new() { Decision = ToDmaClose(validation.Status) };
        validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
        validation.State.Visibility = null;
        return new() { Decision = NeutralDmaGrantCloseDecision.Closed };
    }

    public NeutralDmaPrepareResult PrepareDmaVisibility(NeutralDmaGrant grant)
    {
        var validation = ValidateDmaLifetime(grant);
        if (!validation.IsValid) return new() { Decision = ToDmaPrepare(validation.Status) };
        if (!TryAllocate(ref _nextCycle, out var nextCycle)) return new() { Decision = NeutralDmaPrepareDecision.Faulted, Reason = "DMA visibility cycle space is exhausted." };
        var cycle = new NeutralDmaVisibilityCycle(nextCycle);
        validation.State!.Visibility = new() { Cycle = cycle };
        return new() { IsPrepared = true, Decision = NeutralDmaPrepareDecision.Prepared, Evidence = Evidence(validation.State.Lease, cycle) };
    }

    public NeutralDmaAcquireResult AcquireDmaVisibility(NeutralDmaGrant grant)
    {
        var validation = ValidateDmaLifetime(grant);
        if (!validation.IsValid) return new() { Decision = ToDmaAcquire(validation.Status) };
        var canonical = validation.State!.Lease;
        if (canonical.Direction == NeutralDmaDirection.DeviceReadsMemory) return new() { Decision = NeutralDmaAcquireDecision.NotRequired };
        var visibility = validation.State.Visibility;
        if (visibility is null) return new() { Decision = NeutralDmaAcquireDecision.NotPrepared };
        if (visibility.Acquired) return new() { Decision = NeutralDmaAcquireDecision.AlreadyAcquired, Evidence = Evidence(canonical, visibility.Cycle) };
        visibility.Acquired = true;
        return new() { IsAcquired = true, Decision = NeutralDmaAcquireDecision.Acquired, Evidence = Evidence(canonical, visibility.Cycle) };
    }

    private NeutralLeaseValidation<NeutralDmaGrantState> ValidateDmaLifetime(NeutralDmaGrant grant)
    {
        var validation = _leases.Validate(grant);
        if (!validation.IsValid) return validation;
        var canonical = validation.State!.Lease;
        if (!_leases.Validate(canonical.DeviceLease).IsValid || !_leases.Validate(canonical.MappingLease).IsValid) return new(NeutralLeaseValidationStatus.Faulted, null);
        return validation;
    }

    private static NeutralDmaVisibilityEvidence Evidence(NeutralDmaGrant grant, NeutralDmaVisibilityCycle cycle) => new() { GrantHandle = grant.Handle, GrantEpoch = grant.Epoch, Direction = grant.Direction, Cycle = cycle };
    private static NeutralDmaGrantDecision ToDmaGrant(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDmaGrantDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDmaGrantDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDmaGrantDecision.Revoked, _ => NeutralDmaGrantDecision.Faulted };
    private static NeutralDmaGrantCloseDecision ToDmaClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDmaGrantCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDmaGrantCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDmaGrantCloseDecision.Revoked, _ => NeutralDmaGrantCloseDecision.Faulted };
    private static NeutralDmaPrepareDecision ToDmaPrepare(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDmaPrepareDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDmaPrepareDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDmaPrepareDecision.Revoked, _ => NeutralDmaPrepareDecision.Faulted };
    private static NeutralDmaAcquireDecision ToDmaAcquire(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDmaAcquireDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDmaAcquireDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDmaAcquireDecision.Revoked, _ => NeutralDmaAcquireDecision.Faulted };
}
