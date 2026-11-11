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
    private sealed class Record
    {
        public Record(CxlMemoryPlacementSnapshot snapshot, RegionOwner owner, RegionBackingLeaseHandle backingLease,
            CxlEndpointSnapshot endpoint, CxlFabricBinding fabric, CxlMemoryBinding memory, CxlAuthorityBridge.BackingCleanupSources cleanup)
        { OwnerSelf = this; OriginalId = snapshot.PlacementId; OriginalRegion = snapshot.Region; Snapshot = snapshot;
          Owner = owner; BackingLease = backingLease; Endpoint = endpoint; Fabric = fabric; Memory = memory; CleanupSources = cleanup; }
        public Record OwnerSelf { get; }
        public CxlMemoryPlacementId OriginalId { get; }
        public RegionHandle OriginalRegion { get; }
        private CxlMemoryPlacementSnapshot _snapshot = null!;
        public CxlMemoryPlacementSnapshot Snapshot
        {
            get => _snapshot;
            set { if (CloseInFlight) ClosureSourceChanged = true;
                if (SourceRevision == ulong.MaxValue) RevisionExhausted = true; else SourceRevision++;
                _snapshot = value; }
        }
        public ulong SourceRevision { get; private set; }
        public bool RevisionExhausted { get; private set; }
        public bool ClosureSourceChanged { get; set; }
        public bool ClosureConsumed { get; set; }
        public RegionOwner Owner { get; }
        public RegionBackingLeaseHandle BackingLease { get; }
        public CxlEndpointSnapshot Endpoint { get; }
        public CxlFabricBinding Fabric { get; }
        public CxlMemoryBinding Memory { get; }
        public CxlAuthorityBridge.BackingCleanupSources CleanupSources { get; }
        public bool CreationInFlight { get; set; } = true;
        public bool CloseInFlight { get; set; }
        public CxlMemoryBinding? ClosedMemory { get; set; }
        public CxlFabricBinding? ClosedFabric { get; set; }
    }
    private readonly List<Record> _placementOwners = new();
    private bool IsOriginalPlacement(Record record) => ReferenceEquals(record.OwnerSelf, record) &&
        _placementOwners.Any(owner => ReferenceEquals(owner, record)) &&
        _placements.TryGetValue(record.OriginalId, out Record? routed) && ReferenceEquals(routed, record) &&
        record.Snapshot.PlacementId == record.OriginalId && record.Snapshot.Region == record.OriginalRegion;
    private static bool HasCompletedPlacement(Record record) => ReferenceEquals(record.OwnerSelf, record) && record.ClosureConsumed;
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
        KernelResult<RegionDescriptor> descriptor = _regions.Validate(region, owner);
        if (!descriptor.IsSuccess) return KernelResult<CxlMemoryPlacementSnapshot>.Fail(descriptor.Error, descriptor.Message!);
        KernelResult<RegionBackingLeaseDescriptor> backing = _regions.ReserveBacking(region, owner);
        if (!backing.IsSuccess) return KernelResult<CxlMemoryPlacementSnapshot>.Fail(backing.Error, backing.Message!);
        KernelResult<RegionBackingLeaseDescriptor> admission = default;
        KernelResult committed = _kernel.CommitCxlBackingPublication(subject.Process, owner, () =>
        {
            admission = _regions.BeginBackingCreation(backing.Value!.Handle, owner);
            return admission.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(admission.Error, admission.Message!);
        });
        if (!committed.IsSuccess)
        {
            _ = _regions.ReleaseBacking(backing.Value!.Handle, owner);
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(committed.Error, committed.Message!);
        }
        bool releaseUnused = false;
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
        CxlFabricBindingRequest request = new CxlFabricBindingRequest(endpoint.EndpointId, endpoint.DeviceGeneration,
            regionDescriptor.ByteLength, persistence, CxlMemorySharing.Exclusive);
        KernelResult<CxlFabricBinding> fabric = _bridge.BindFabricForBacking(owner, backing.Handle, subject, deviceLease, request);
        if (!fabric.IsSuccess)
        {
            releaseUnused = fabric.Error != KernelError.ExternalEffectUncontained;
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(fabric.Error, fabric.Message!);
        }
        KernelResult device = _kernel.PlatformAuthority.ValidateDeviceLease(deviceLease, subject);
        if (!device.IsSuccess)
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                "Device authorization changed after fabric creation; tracked fabric remains pinned for closure.");
        KernelResult<CxlMemoryBinding> memory = _bridge.BindMemory(owner, backing.Handle, fabric.Value!);
        if (!memory.IsSuccess)
        {
            if (memory.Error != KernelError.ExternalEffectUncontained)
            {
                KernelResult compensated = _bridge.ReleaseFabric(fabric.Value!);
                if (!compensated.IsSuccess)
                    return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                        "Rejected memory creation left a tracked fabric without exact closure.");
                releaseUnused = true;
            }
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(memory.Error, memory.Message!);
        }
        CxlMemoryPlacementSnapshot snapshot = new CxlMemoryPlacementSnapshot(id, region, endpoint.EndpointId, endpoint.DeviceGeneration,
            fabric.Value!.Generation, memory.Value!.BindingId, memory.Value.Generation, persistence, CxlMemoryPlacementState.Active);
        KernelResult<CxlAuthorityBridge.BackingCleanupSources> cleanup = _bridge.CaptureBackingCleanupSources(memory.Value!, fabric.Value!);
        if (!cleanup.IsSuccess) return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.ExternalEffectUncontained,
            "Known backing remains pinned without original cleanup sources.");
        Record record = new Record(snapshot with { State = CxlMemoryPlacementState.Quarantined }, owner,
            backing.Handle, endpoint, fabric.Value!, memory.Value!, cleanup.Value);
        CxlMemoryPlacementSnapshot staged;
        ulong stagedRevision;
        lock (_gate)
        {
            _placementOwners.EnsureCapacity(_placementOwners.Count + 1);
            _placementOwners.Add(record);
            _placements.Add(id, record);
            staged = record.Snapshot; stagedRevision = record.SourceRevision;
        }
        KernelResult published;
        try
        {
            AfterPlacementReceipt?.Invoke(memory.Value!);
            // Query provider generations outside owner locks, then commit only
            // through the existing capability/device/tracked backing sources.
            KernelResult finalBacking = _bridge.RevalidateBacking(owner, backing.Handle, endpoint, fabric.Value!, memory.Value!);
            KernelResult<Func<Func<KernelResult>, KernelResult>> source = finalBacking.IsSuccess
                ? _bridge.PrepareBackingConsequenceSource(owner, backing.Handle, fabric.Value!, memory.Value!)
                : KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(finalBacking.Error, finalBacking.Message!);
            BeforePlacementPublication?.Invoke();
            published = !source.IsSuccess ? KernelResult.Fail(source.Error, source.Message!) : source.Value!(() =>
            {
                lock (_gate)
                {
                    if (!IsOriginalPlacement(record) || record.RevisionExhausted || record.SourceRevision != stagedRevision ||
                        !ReferenceEquals(record.Snapshot, staged) || record.CloseInFlight || record.ClosureConsumed)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Original placement changed before publication.");
                    record.Snapshot = snapshot;
                    return KernelResult.Ok();
                }
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            published = KernelResult.Fail(KernelError.ExternalEffectUncontained,
                $"Placement source observation failed after a known memory receipt: {exception.Message}");
        }
        finally { lock (_gate) record.CreationInFlight = false; }
        if (!published.IsSuccess)
        {
            lock (_gate) if (!record.ClosureConsumed) record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Quarantined };
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.ExternalEffectUncontained,
                "Late placement receipt is retained for closure; changed source cannot publish Active placement.");
        }
        return KernelResult<CxlMemoryPlacementSnapshot>.Ok(snapshot);
    }

    internal Action<CxlMemoryBinding>? AfterPlacementReceipt { get; set; }
    internal Action? BeforePlacementPublication { get; set; }
    internal Action? BeforePlacementClosureLocalCommit { get; set; }

    public KernelResult<CxlMemoryPlacementSnapshot> Refresh(CxlMemoryPlacementId placementId)
    {
        Record record;
        CxlMemoryPlacementSnapshot admitted;
        ulong admittedRevision;
        lock (_gate)
        {
            if (!_placements.TryGetValue(placementId, out record!) || !IsOriginalPlacement(record))
                return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.PlatformBindingNotFound, "Type-3 placement does not exist.");
            admitted = record.Snapshot; admittedRevision = record.SourceRevision;
            if (admitted.State != CxlMemoryPlacementState.Active)
                return KernelResult<CxlMemoryPlacementSnapshot>.Ok(admitted);
        }
        KernelResult validation = _bridge.RevalidateBacking(record.Owner, record.BackingLease, record.Endpoint, record.Fabric, record.Memory);
        lock (_gate)
        {
            if (!IsOriginalPlacement(record) || record.RevisionExhausted || record.SourceRevision != admittedRevision || !ReferenceEquals(record.Snapshot, admitted))
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
            return _placements.TryGetValue(placementId, out Record? record) && IsOriginalPlacement(record)
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
        ulong admittedRevision;
        lock (_gate)
        {
            if (!_placements.TryGetValue(placementId, out record!) || !IsOriginalPlacement(record))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.PlatformBindingNotFound,
                    "Type-3 placement does not exist.");
            if (record.Owner != owner)
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.WrongRegionOwner,
                    "Type-3 health consequence owner does not match the placement owner.");
            if (record.Snapshot.State != CxlMemoryPlacementState.Active)
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidTransition,
                    "Only an active Type-3 placement can accept new health evidence.");
            admittedPlacement = record.Snapshot; admittedRevision = record.SourceRevision;
        }
        KernelResult live = _bridge.RevalidateBacking(record.Owner, record.BackingLease,
            record.Endpoint, record.Fabric, record.Memory);
        lock (_gate)
        {
            if (!IsOriginalPlacement(record) || record.RevisionExhausted || record.SourceRevision != admittedRevision || !ReferenceEquals(record.Snapshot, admittedPlacement))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Type-3 placement changed during initial backing revalidation.");
            if (!live.IsSuccess)
            {
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.MigrationRequired };
                return KernelResult<RegionDamageDescriptorV1>.Fail(live.Error, live.Message!);
            }
        }
        KernelResult<Func<Func<KernelResult>, KernelResult>> source = _bridge.PrepareBackingConsequenceSource(record.Owner, record.BackingLease, record.Fabric, record.Memory);
        if (!source.IsSuccess)
            return KernelResult<RegionDamageDescriptorV1>.Fail(source.Error, source.Message!);
        CxlType3HealthObservationRequest request = new CxlType3HealthObservationRequest(record.Endpoint.EndpointId,
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
            if (!IsOriginalPlacement(record) || record.RevisionExhausted || record.SourceRevision != admittedRevision || !ReferenceEquals(record.Snapshot, admittedPlacement))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Type-3 placement changed during health observation; fresh admission is required.");
        CxlType3HealthObservation observation = observed.Value;
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
        KernelResult finalBacking = _bridge.RevalidateBacking(record.Owner, record.BackingLease,
            record.Endpoint, record.Fabric, record.Memory);
        BeforeHealthConsequenceCommit?.Invoke();
        KernelResult<RegionDamageDescriptorV1> consequence = default;
        KernelResult committed = source.Value!(() =>
        {
          lock (_gate)
          {
            if (!IsOriginalPlacement(record) || record.RevisionExhausted || record.SourceRevision != admittedRevision || !ReferenceEquals(record.Snapshot, admittedPlacement))
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Type-3 placement changed during final backing revalidation.");
            if (!finalBacking.IsSuccess)
            {
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.MigrationRequired };
                return KernelResult.Fail(finalBacking.Error, finalBacking.Message!);
            }
            consequence = _regions.QuarantineSubrange(record.Snapshot.Region, record.Owner, evidence, record.BackingLease);
            if (consequence.IsSuccess)
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Quarantined };
            return consequence.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(consequence.Error, consequence.Message!);
          }
        });
        return committed.IsSuccess ? consequence : KernelResult<RegionDamageDescriptorV1>.Fail(committed.Error, committed.Message!);
    }

    internal Action? BeforeHealthConsequenceCommit { get; set; }

    public KernelResult Close(CxlMemoryPlacementId placementId)
    {
        Record record;
        ulong closingRevision;
        lock (_gate)
        {
            if (!_placements.TryGetValue(placementId, out record!) || !IsOriginalPlacement(record))
            {
                Record? completed = _placementOwners.SingleOrDefault(owner => owner.OriginalId == placementId && HasCompletedPlacement(owner));
                return completed is not null ? KernelResult.Ok() : KernelResult.Fail(KernelError.StaleGeneration, "Original placement route is unavailable.");
            }
            if (HasCompletedPlacement(record)) return KernelResult.Ok();
            if (record.Snapshot.State == CxlMemoryPlacementState.Released)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Released placement projection lacks original consumption.");
            if (record.CreationInFlight || record.CloseInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Type-3 closure is already in flight.");
            // A Type-3 lease backs the existing guest mapping. Do not drop it while
            // that mapping (and therefore any secure overlay) can still reference it.
            KernelResult<RegionDescriptor> region = _regions.Validate(record.Snapshot.Region, record.Owner);
            if (!region.IsSuccess) return KernelResult.Fail(region.Error, region.Message!);
            KernelResult admission = _regions.BeginBackingClosure(record.BackingLease, record.Owner);
            if (!admission.IsSuccess) return admission;
            record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Draining };
            closingRevision = record.SourceRevision;
            record.ClosureSourceChanged = false;
            record.CloseInFlight = true;
        }
        bool closed = false;
        try
        {
            CxlMemoryBinding? closedMemory;
            CxlFabricBinding? closedFabric;
            closedMemory = _bridge.ConsumedOriginalMemoryClosure(record.CleanupSources);
            closedFabric = _bridge.ConsumedOriginalFabricClosure(record.CleanupSources);
            lock (_gate)
            {
                if (!IsOriginalPlacement(record)) return KernelResult.Fail(KernelError.StaleGeneration, "Placement changed before closure.");
                if (record.ClosedMemory is not null && record.ClosedMemory != closedMemory ||
                    record.ClosedFabric is not null && record.ClosedFabric != closedFabric)
                    return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Local closure projection lacks original backing consumption.");
                if (closedMemory is not null) record.ClosedMemory = closedMemory;
                if (closedFabric is not null) record.ClosedFabric = closedFabric;
            }
            if (closedMemory is null)
            {
                KernelResult<CxlMemoryBinding> currentMemory = _bridge.QueryMemoryForCleanup(record.Memory);
                CxlMemoryBinding exactMemory = currentMemory.IsSuccess && currentMemory.Value!.BackingLease == record.BackingLease
                    ? currentMemory.Value : record.Memory;
                KernelResult memory = _bridge.ReleaseMemory(exactMemory);
                if (!memory.IsSuccess) return memory;
                CxlMemoryBinding? consumedMemory = _bridge.ConsumedOriginalMemoryClosure(record.CleanupSources);
                if (consumedMemory != exactMemory) return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original memory closure was not consumed.");
                lock (_gate) record.ClosedMemory = exactMemory;
            }
            if (closedFabric is null)
            {
                KernelResult<CxlFabricBinding> currentFabric = _bridge.QueryFabricForCleanup(record.Fabric);
                CxlFabricBinding exactFabric = currentFabric.IsSuccess && currentFabric.Value!.EndpointId == record.Fabric.EndpointId
                    ? currentFabric.Value : record.Fabric;
                KernelResult fabric = _bridge.ReleaseFabric(exactFabric);
                if (!fabric.IsSuccess) return fabric;
                CxlFabricBinding? consumedFabric = _bridge.ConsumedOriginalFabricClosure(record.CleanupSources);
                if (consumedFabric != exactFabric) return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original fabric closure was not consumed.");
                lock (_gate) record.ClosedFabric = exactFabric;
            }
            BeforePlacementClosureLocalCommit?.Invoke();
            lock (_gate)
            {
                if (!IsOriginalPlacement(record) || record.SourceRevision != closingRevision || record.ClosureSourceChanged)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original placement changed before local backing consumption.");
                KernelResult backing = _regions.ReleaseBacking(record.BackingLease, record.Owner);
                if (!backing.IsSuccess) return backing;
                record.ClosureConsumed = true;
                record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Released };
            }
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
                if (!closed && !record.ClosureConsumed)
                    record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Quarantined };
                record.CloseInFlight = false;
            }
        }
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        CxlMemoryPlacementId[] placements;
        lock (_gate)
            placements = _placementOwners.Where(item => item.Owner == owner && !HasCompletedPlacement(item))
                .Select(item => item.OriginalId).ToArray();
        foreach (CxlMemoryPlacementId placement in placements)
        {
            KernelResult closed = Close(placement);
            if (!closed.IsSuccess) return closed;
        }
        return KernelResult.Ok();
    }

    KernelResult ISecureGuestBackingAuthority.RevalidateForSecureGuest(RegionOwner owner, RegionHandle region)
    {
        CxlMemoryPlacementId[] placements;
        lock (_gate)
            placements = _placementOwners.Where(x => x.Owner == owner && x.OriginalRegion == region && !HasCompletedPlacement(x))
                .Select(item => item.OriginalId).ToArray();
        foreach (CxlMemoryPlacementId placement in placements)
        {
            KernelResult<CxlMemoryPlacementSnapshot> validation = Refresh(placement);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
            if (validation.Value!.State != CxlMemoryPlacementState.Active)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Type-3 backing is not active for a new secure guest effect.");
        }
        return KernelResult.Ok();
    }

    KernelResult<Func<Func<KernelResult>, KernelResult>> ISecureGuestBackingAuthority.PrepareForSecureGuest(
        RegionOwner owner, RegionHandle region, out RegionBackingLeaseHandle? backing)
    {
        backing = null;
        Record[] records;
        CxlMemoryPlacementSnapshot[] snapshots;
        ulong[] revisions;
        lock (_gate)
        {
            records = _placementOwners.Where(x => x.Owner == owner && x.OriginalRegion == region && !HasCompletedPlacement(x)).ToArray();
            snapshots = records.Select(x => x.Snapshot).ToArray();
            revisions = records.Select(x => x.SourceRevision).ToArray();
        }
        foreach (Record record in records)
        {
            KernelResult<CxlMemoryPlacementSnapshot> refreshed = Refresh(record.Snapshot.PlacementId); // Provider queries outside commit locks.
            if (!refreshed.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(refreshed.Error, refreshed.Message!);
        }
        if (records.Length > 1)
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformDenied, "Secure guest backing owner is ambiguous.");
        Func<Func<KernelResult>, KernelResult>? source = null;
        if (records.Length == 1)
        {
            KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = _bridge.PrepareSecureGuestBackingSource(owner, records[0].BackingLease, records[0].Fabric, records[0].Memory);
            if (!prepared.IsSuccess) return prepared;
            source = prepared.Value!;
        }
        KernelResult Local(Func<KernelResult> commit)
        {
            lock (_gate)
            {
                Record[] current = _placementOwners.Where(x => x.Owner == owner && x.OriginalRegion == region && !HasCompletedPlacement(x)).ToArray();
                if (!current.SequenceEqual(records) || records.Where((record, index) =>
                    !IsOriginalPlacement(record) || record.RevisionExhausted || record.SourceRevision != revisions[index] || !ReferenceEquals(record.Snapshot, snapshots[index]) || record.CreationInFlight || record.CloseInFlight ||
                    record.Snapshot.State != CxlMemoryPlacementState.Active).Any())
                    return KernelResult.Fail(KernelError.StaleGeneration, "Exact Type-3 placement stopped or changed before secure guest commit.");
                return commit(); // Trusted pure owner transition only; no provider callbacks.
            }
        }
        KernelResult Commit(Func<KernelResult> commit) => source is null ? Local(commit) : source(() => Local(commit));
        KernelResult initial = Commit(KernelResult.Ok);
        if (!initial.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(initial.Error, initial.Message!);
        backing = records.SingleOrDefault()?.BackingLease;
        return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit);
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
