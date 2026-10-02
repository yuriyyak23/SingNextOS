using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public enum CxlMemoryPlacementState { Active = 0, MigrationRequired, Draining, Released, Quarantined }
public readonly record struct CxlMemoryPlacementId(ulong Value);
public sealed record CxlMemoryPlacementSnapshot(
    CxlMemoryPlacementId PlacementId,
    RegionHandle Region,
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    CxlFabricBindingGeneration FabricGeneration,
    CxlMemoryBindingId MemoryBindingId,
    CxlMemoryBindingGeneration BackingGeneration,
    CxlMemoryPersistence Persistence,
    CxlMemoryPlacementState State);

/// <summary>Tracks Type-3 backing while the application retains ordinary region ownership.</summary>
public sealed class CxlType3MemoryAuthority : ICxlTeardownParticipant, ISecureGuestBackingAuthority
{
    private sealed class Record(CxlMemoryPlacementSnapshot snapshot, RegionOwner owner, RegionBackingLeaseHandle backingLease,
        CxlEndpointSnapshot endpoint, CxlFabricBinding fabric, CxlMemoryBinding memory)
    {
        public CxlMemoryPlacementSnapshot Snapshot { get; set; } = snapshot;
        public RegionOwner Owner { get; } = owner;
        public RegionBackingLeaseHandle BackingLease { get; } = backingLease;
        public CxlEndpointSnapshot Endpoint { get; } = endpoint;
        public CxlFabricBinding Fabric { get; } = fabric;
        public CxlMemoryBinding Memory { get; } = memory;
        public bool CloseInFlight { get; set; }
        // Exact successful step results consumed by this existing lifecycle owner.
        // Absence, failure and exception never populate these closure facts.
        public CxlMemoryBinding? ClosedMemory { get; set; }
        public CxlFabricBinding? ClosedFabric { get; set; }
    }

    private readonly RegionAuthority _regions;
    private readonly RuntimeKernel _kernel;
    private readonly CxlAuthorityBridge _bridge;
    private readonly Dictionary<CxlMemoryPlacementId, Record> _placements = [];
    private readonly object _gate = new();
    private ulong _nextId = 1;

    public CxlType3MemoryAuthority(RuntimeKernel kernel, CxlAuthorityBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _kernel = kernel;
        _regions = kernel.Regions;
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        kernel.RegisterCxlTeardownParticipant(this);
        kernel.RegisterSecureGuestBackingAuthority(this);
    }

    public KernelResult<CxlMemoryPlacementSnapshot> Place(
        RegionOwner owner, RegionHandle region, PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint, CxlMemoryPersistence persistence)
    {
        var descriptor = _regions.Validate(region, owner);
        if (!descriptor.IsSuccess) return KernelResult<CxlMemoryPlacementSnapshot>.Fail(descriptor.Error, descriptor.Message!);
        var backing = _regions.ReserveBacking(region, owner);
        if (!backing.IsSuccess) return KernelResult<CxlMemoryPlacementSnapshot>.Fail(backing.Error, backing.Message!);
        KernelResult<RegionBackingLeaseDescriptor> admission = default;
        var committed = _kernel.CommitCxlBackingPublication(subject.Process, owner, () =>
        {
            admission = _regions.BeginBackingCreation(backing.Value!.Handle, owner);
            return admission.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(admission.Error, admission.Message!);
        });
        if (!committed.IsSuccess)
        {
            _ = _regions.ReleaseBacking(backing.Value!.Handle, owner);
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(committed.Error, committed.Message!);
        }
        var releaseUnused = false;
        KernelResult<CxlMemoryPlacementSnapshot> result;
        try { result = PlaceCore(owner, region, subject, deviceLease, endpoint, persistence,
            descriptor.Value!, admission.Value!, out releaseUnused); }
        finally { _regions.EndBackingCreation(admission.Value!); }
        if (releaseUnused) _ = _regions.ReleaseBacking(admission.Value!.Handle, owner);
        return result;
    }

    private KernelResult<CxlMemoryPlacementSnapshot> PlaceCore(
        RegionOwner owner, RegionHandle region, PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint, CxlMemoryPersistence persistence,
        RegionDescriptor regionDescriptor, RegionBackingLeaseDescriptor backing, out bool releaseUnused)
    {
        releaseUnused = false;
        CxlMemoryPlacementId id;
        lock (_gate)
        {
            if (_nextId == 0)
            {
                releaseUnused = true;
                return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.CapacityExhausted,
                    "Type-3 placement identity space is exhausted.");
            }
            id = new(_nextId++);
        }
        var request = new CxlFabricBindingRequest(endpoint.EndpointId, endpoint.DeviceGeneration,
            regionDescriptor.ByteLength, persistence, CxlMemorySharing.Exclusive);
        var fabric = _bridge.BindFabricForBacking(owner, backing.Handle, subject, deviceLease, request);
        if (!fabric.IsSuccess)
        {
            releaseUnused = fabric.Error != KernelError.ExternalEffectUncontained;
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(fabric.Error, fabric.Message!);
        }
        var device = _kernel.PlatformAuthority.ValidateDeviceLease(deviceLease, subject);
        if (!device.IsSuccess)
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                "Device authorization changed after fabric creation; tracked fabric remains pinned for closure.");
        var memory = _bridge.BindMemory(owner, backing.Handle, fabric.Value!);
        if (!memory.IsSuccess)
        {
            if (memory.Error != KernelError.ExternalEffectUncontained)
            {
                var compensated = _bridge.ReleaseFabric(fabric.Value!);
                if (!compensated.IsSuccess)
                    return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                        "Rejected memory creation left a tracked fabric without exact closure.");
                releaseUnused = true;
            }
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(memory.Error, memory.Message!);
        }
        var snapshot = new CxlMemoryPlacementSnapshot(id, region, endpoint.EndpointId, endpoint.DeviceGeneration,
            fabric.Value!.Generation, memory.Value!.BindingId, memory.Value.Generation, persistence, CxlMemoryPlacementState.Active);
        var finalDevice = _kernel.PlatformAuthority.ValidateDeviceLease(deviceLease, subject);
        var published = !finalDevice.IsSuccess ? finalDevice : _kernel.CommitCxlBackingPublication(subject.Process, owner, () =>
        {
            lock (_gate)
                _placements.Add(id, new(snapshot, owner, backing.Handle, endpoint, fabric.Value, memory.Value));
            return KernelResult.Ok();
        });
        if (!published.IsSuccess)
        {
            lock (_gate)
                _placements.Add(id, new(snapshot with { State = CxlMemoryPlacementState.Quarantined },
                    owner, backing.Handle, endpoint, fabric.Value, memory.Value));
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                "Late placement receipt is retained for closure; exiting process cannot publish Active placement.");
        }
        return KernelResult<CxlMemoryPlacementSnapshot>.Ok(snapshot);
    }

    public KernelResult<CxlMemoryPlacementSnapshot> Refresh(CxlMemoryPlacementId placementId)
    {
        Record record;
        CxlMemoryPlacementSnapshot admitted;
        lock (_gate)
        {
            if (!_placements.TryGetValue(placementId, out record!))
                return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.PlatformBindingNotFound, "Type-3 placement does not exist.");
            admitted = record.Snapshot;
            if (admitted.State != CxlMemoryPlacementState.Active)
                return KernelResult<CxlMemoryPlacementSnapshot>.Ok(admitted);
        }
        var validation = _bridge.RevalidateBacking(record.Owner, record.BackingLease, record.Endpoint, record.Fabric, record.Memory);
        lock (_gate)
        {
            if (!ReferenceEquals(record.Snapshot, admitted))
                return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.StaleGeneration,
                    "Type-3 placement changed during refresh.");
            if (!validation.IsSuccess)
            {
                record.Snapshot = admitted with { State = CxlMemoryPlacementState.MigrationRequired };
                return KernelResult<CxlMemoryPlacementSnapshot>.Fail(validation.Error, validation.Message!);
            }
            return KernelResult<CxlMemoryPlacementSnapshot>.Ok(record.Snapshot);
        }
    }

    public KernelResult<CxlMemoryPlacementSnapshot> Query(CxlMemoryPlacementId placementId)
    {
        lock (_gate)
            return _placements.TryGetValue(placementId, out var record)
                ? KernelResult<CxlMemoryPlacementSnapshot>.Ok(record.Snapshot)
                : KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.PlatformBindingNotFound, "Type-3 placement does not exist.");
    }

    /// <summary>
    /// Observes an exact live Type-3 binding and asks RegionAuthority to apply the consequence.
    /// Provider evidence is revalidated here and never mutates Region state by itself.
    /// </summary>
    public KernelResult<RegionDamageDescriptorV1> ObserveAndQuarantine(
        CxlMemoryPlacementId placementId, RegionOwner owner, ICxlType3HealthEvidenceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Record record;
        CxlMemoryPlacementSnapshot admittedPlacement;
        lock (_gate)
        {
            if (!_placements.TryGetValue(placementId, out record!))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.PlatformBindingNotFound,
                    "Type-3 placement does not exist.");
            if (record.Owner != owner)
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.WrongRegionOwner,
                    "Type-3 health consequence owner does not match the placement owner.");
            if (record.Snapshot.State != CxlMemoryPlacementState.Active)
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidTransition,
                    "Only an active Type-3 placement can accept new health evidence.");
            admittedPlacement = record.Snapshot;
        }
        var live = _bridge.RevalidateBacking(record.Owner, record.BackingLease,
            record.Endpoint, record.Fabric, record.Memory);
        lock (_gate)
        {
            if (!ReferenceEquals(record.Snapshot, admittedPlacement))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Type-3 placement changed during initial backing revalidation.");
            if (!live.IsSuccess)
            {
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.MigrationRequired };
                return KernelResult<RegionDamageDescriptorV1>.Fail(live.Error, live.Message!);
            }
        }
        var request = new CxlType3HealthObservationRequest(record.Endpoint.EndpointId,
            record.Endpoint.DeviceGeneration, record.Fabric.BindingId, record.Fabric.Generation,
            record.Memory.BindingId, record.Memory.Generation);
        PlatformAuthorityResult<CxlType3HealthObservation> observed;
        try { observed = provider.QueryHealth(request); }
        catch (Exception exception)
        {
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.PlatformFaulted,
                $"Type-3 health provider threw before a consequence was applied: {exception.Message}");
        }
        if (!observed.IsSuccess)
            return KernelResult<RegionDamageDescriptorV1>.Fail(MapHealthStatus(observed.Status),
                observed.Message ?? "Type-3 health observation failed.");
        lock (_gate)
            if (!ReferenceEquals(record.Snapshot, admittedPlacement))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Type-3 placement changed during health observation; fresh admission is required.");
        var observation = observed.Value;
        ProviderHealthEvidenceV1 evidence;
        try { evidence = observation.Evidence.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        if (observation.Request != request ||
            evidence.Scope.ProviderGeneration != request.DeviceGeneration.Value ||
            evidence.Scope.FailureDomainGeneration != request.MemoryGeneration.Value)
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                "Type-3 health evidence does not bind the exact live endpoint and memory generations.");
        var finalBacking = _bridge.RevalidateBacking(record.Owner, record.BackingLease,
            record.Endpoint, record.Fabric, record.Memory);
        lock (_gate)
        {
            if (!ReferenceEquals(record.Snapshot, admittedPlacement))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Type-3 placement changed during final backing revalidation.");
            if (!finalBacking.IsSuccess)
            {
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.MigrationRequired };
                return KernelResult<RegionDamageDescriptorV1>.Fail(finalBacking.Error, finalBacking.Message!);
            }
            var consequence = _regions.QuarantineSubrange(record.Snapshot.Region, record.Owner, evidence);
            if (consequence.IsSuccess)
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Quarantined };
            return consequence;
        }
    }

    public KernelResult Close(CxlMemoryPlacementId placementId)
    {
        Record record;
        lock (_gate)
        {
            if (!_placements.TryGetValue(placementId, out record!)) return KernelResult.Ok();
            if (record.Snapshot.State == CxlMemoryPlacementState.Released) return KernelResult.Ok();
            if (record.CloseInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Type-3 closure is already in flight.");
            // A Type-3 lease backs the existing guest mapping. Do not drop it while
            // that mapping (and therefore any secure overlay) can still reference it.
            var region = _regions.Validate(record.Snapshot.Region, record.Owner);
            if (!region.IsSuccess) return KernelResult.Fail(region.Error, region.Message!);
            var admission = _regions.BeginBackingClosure(record.BackingLease, record.Owner);
            if (!admission.IsSuccess) return admission;
            record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Draining };
            record.CloseInFlight = true;
        }
        var closed = false;
        try
        {
            CxlMemoryBinding? closedMemory;
            CxlFabricBinding? closedFabric;
            lock (_gate)
            {
                closedMemory = record.ClosedMemory;
                closedFabric = record.ClosedFabric;
            }
            if (closedMemory is null)
            {
                var currentMemory = _bridge.QueryMemory(record.Memory.BindingId);
                var exactMemory = currentMemory.IsSuccess && currentMemory.Value!.BackingLease == record.BackingLease
                    ? currentMemory.Value : record.Memory;
                var memory = _bridge.ReleaseMemory(exactMemory);
                if (!memory.IsSuccess) return memory;
                lock (_gate) record.ClosedMemory = exactMemory;
            }
            if (closedFabric is null)
            {
                var currentFabric = _bridge.QueryFabric(record.Fabric.BindingId);
                var exactFabric = currentFabric.IsSuccess && currentFabric.Value!.EndpointId == record.Fabric.EndpointId
                    ? currentFabric.Value : record.Fabric;
                var fabric = _bridge.ReleaseFabric(exactFabric);
                if (!fabric.IsSuccess) return fabric;
                lock (_gate) record.ClosedFabric = exactFabric;
            }
            var backing = _regions.ReleaseBacking(record.BackingLease, record.Owner);
            if (!backing.IsSuccess) return backing;
            lock (_gate)
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Released };
            closed = true;
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Type-3 closure did not establish exact completion: {exception.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (!closed)
                    record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Quarantined };
                record.CloseInFlight = false;
            }
        }
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        CxlMemoryPlacementId[] placements;
        lock (_gate)
            placements = _placements.Values.Where(item => item.Owner == owner && item.Snapshot.State != CxlMemoryPlacementState.Released)
                .Select(item => item.Snapshot.PlacementId).ToArray();
        foreach (var placement in placements)
        {
            var closed = Close(placement);
            if (!closed.IsSuccess) return closed;
        }
        return KernelResult.Ok();
    }

    KernelResult ISecureGuestBackingAuthority.RevalidateForSecureGuest(RegionOwner owner, RegionHandle region)
    {
        CxlMemoryPlacementId[] placements;
        lock (_gate)
            placements = _placements.Values.Where(x => x.Owner == owner && x.Snapshot.Region == region)
                .Select(item => item.Snapshot.PlacementId).ToArray();
        foreach (var placement in placements)
        {
            var validation = Refresh(placement);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
            if (validation.Value!.State != CxlMemoryPlacementState.Active)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Type-3 backing is not active for a new secure guest effect.");
        }
        return KernelResult.Ok();
    }

    private static KernelError MapHealthStatus(PlatformAuthorityStatus status) => status switch
    {
        PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
        PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
        PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.NotAccepted => KernelError.PlatformDenied,
        PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
        _ => KernelError.PlatformFaulted,
    };
}
