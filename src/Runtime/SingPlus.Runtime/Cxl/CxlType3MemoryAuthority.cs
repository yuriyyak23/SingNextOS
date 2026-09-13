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
    CxlMemoryBindingGeneration BackingGeneration,
    CxlMemoryPersistence Persistence,
    CxlMemoryPlacementState State);

/// <summary>Tracks Type-3 backing while the application retains ordinary region ownership.</summary>
public sealed class CxlType3MemoryAuthority : ICxlTeardownParticipant
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
    }

    private readonly RegionAuthority _regions;
    private readonly CxlAuthorityBridge _bridge;
    private readonly Dictionary<CxlMemoryPlacementId, Record> _placements = [];
    private ulong _nextId = 1;

    public CxlType3MemoryAuthority(RuntimeKernel kernel, CxlAuthorityBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _regions = kernel.Regions;
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        kernel.RegisterCxlTeardownParticipant(this);
    }

    public KernelResult<CxlMemoryPlacementSnapshot> Place(
        RegionOwner owner, RegionHandle region, PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint, CxlMemoryPersistence persistence)
    {
        var descriptor = _regions.Validate(region, owner);
        if (!descriptor.IsSuccess) return KernelResult<CxlMemoryPlacementSnapshot>.Fail(descriptor.Error, descriptor.Message!);
        var regionDescriptor = descriptor.Value!;
        var backing = _regions.ReserveBacking(region, owner);
        if (!backing.IsSuccess) return KernelResult<CxlMemoryPlacementSnapshot>.Fail(backing.Error, backing.Message!);
        var request = new CxlFabricBindingRequest(endpoint.EndpointId, endpoint.DeviceGeneration,
            regionDescriptor.ByteLength, persistence, CxlMemorySharing.Exclusive);
        var fabric = _bridge.BindFabricForBacking(owner, backing.Value!.Handle, subject, deviceLease, request);
        if (!fabric.IsSuccess)
        {
            if (fabric.Error != KernelError.ExternalEffectUncontained)
                _ = _regions.ReleaseBacking(backing.Value.Handle, owner);
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(fabric.Error, fabric.Message!);
        }
        var memory = _bridge.BindMemory(owner, backing.Value.Handle, fabric.Value!);
        if (!memory.IsSuccess)
        {
            if (memory.Error != KernelError.ExternalEffectUncontained)
            {
                _ = _bridge.ReleaseFabric(fabric.Value!);
                _ = _regions.ReleaseBacking(backing.Value.Handle, owner);
            }
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(memory.Error, memory.Message!);
        }
        var id = new CxlMemoryPlacementId(_nextId++);
        var snapshot = new CxlMemoryPlacementSnapshot(id, region, endpoint.EndpointId, endpoint.DeviceGeneration,
            fabric.Value!.Generation, memory.Value!.Generation, persistence, CxlMemoryPlacementState.Active);
        _placements.Add(id, new(snapshot, owner, backing.Value.Handle, endpoint, fabric.Value, memory.Value));
        return KernelResult<CxlMemoryPlacementSnapshot>.Ok(snapshot);
    }

    public KernelResult<CxlMemoryPlacementSnapshot> Refresh(CxlMemoryPlacementId placementId)
    {
        if (!_placements.TryGetValue(placementId, out var record))
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.PlatformBindingNotFound, "Type-3 placement does not exist.");
        if (record.Snapshot.State != CxlMemoryPlacementState.Active)
            return KernelResult<CxlMemoryPlacementSnapshot>.Ok(record.Snapshot);
        var validation = _bridge.RevalidateBacking(record.Owner, record.BackingLease, record.Endpoint, record.Fabric, record.Memory);
        if (!validation.IsSuccess)
        {
            record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.MigrationRequired };
            return KernelResult<CxlMemoryPlacementSnapshot>.Fail(validation.Error, validation.Message!);
        }
        return KernelResult<CxlMemoryPlacementSnapshot>.Ok(record.Snapshot);
    }

    public KernelResult<CxlMemoryPlacementSnapshot> Query(CxlMemoryPlacementId placementId) =>
        _placements.TryGetValue(placementId, out var record)
            ? KernelResult<CxlMemoryPlacementSnapshot>.Ok(record.Snapshot)
            : KernelResult<CxlMemoryPlacementSnapshot>.Fail(KernelError.PlatformBindingNotFound, "Type-3 placement does not exist.");

    public KernelResult Close(CxlMemoryPlacementId placementId)
    {
        if (!_placements.TryGetValue(placementId, out var record)) return KernelResult.Ok();
        if (record.Snapshot.State == CxlMemoryPlacementState.Released) return KernelResult.Ok();
        record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Draining };
        var currentMemory = _bridge.QueryMemory(record.Memory.BindingId);
        var memory = currentMemory.IsSuccess && currentMemory.Value!.BackingLease == record.BackingLease
            ? _bridge.ReleaseMemory(currentMemory.Value)
            : _bridge.ReleaseMemory(record.Memory);
        if (!memory.IsSuccess)
        {
            record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Quarantined };
            return memory;
        }
        var currentFabric = _bridge.QueryFabric(record.Fabric.BindingId);
        var fabric = currentFabric.IsSuccess && currentFabric.Value!.EndpointId == record.Fabric.EndpointId
            ? _bridge.ReleaseFabric(currentFabric.Value)
            : _bridge.ReleaseFabric(record.Fabric);
        if (!fabric.IsSuccess)
        {
            record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Quarantined };
            return fabric;
        }
        var backing = _regions.ReleaseBacking(record.BackingLease, record.Owner);
        if (!backing.IsSuccess) return backing;
        record.Snapshot = record.Snapshot with { State = CxlMemoryPlacementState.Released };
        return KernelResult.Ok();
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        foreach (var placement in _placements.Values.Where(item => item.Owner == owner && item.Snapshot.State != CxlMemoryPlacementState.Released).ToArray())
        {
            var closed = Close(placement.Snapshot.PlacementId);
            if (!closed.IsSuccess) return closed;
        }
        return KernelResult.Ok();
    }
}
