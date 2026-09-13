using SingPlus.Contracts;

namespace SingPlus.Platform.Host;

/// <summary>Deterministic single-host Type-3 model. Hardware routing facts are intentionally private.</summary>
public sealed class CxlType3ModelProvider : ICxlDiscoveryProvider, ICxlIoProvider, ICxlFabricProvider, ICxlMemoryProvider, ICxlFabricManagementProvider
{
    private sealed class EndpointRecord(
        CxlEndpointId id, PlatformDeviceIdentity device, long capacity, CxlMemoryPersistence persistence,
        int proximity, int latency, int bandwidth)
    {
        public CxlEndpointId Id { get; } = id;
        public PlatformDeviceIdentity Device { get; } = device;
        public long Capacity { get; } = capacity;
        public long Reserved { get; set; }
        public CxlMemoryPersistence Persistence { get; } = persistence;
        public int Proximity { get; } = proximity;
        public int Latency { get; } = latency;
        public int Bandwidth { get; } = bandwidth;
        public ulong DeviceGeneration { get; set; } = 1;
        public bool Available { get; set; } = true;
        // A real backend may keep decoder/route/address materialization here, never in public records.
        private readonly Dictionary<ulong, object> _providerPrivateMaterialization = [];
    }

    private sealed record FabricRecord(CxlFabricBinding Binding, long ReservedBytes);
    private sealed record MemoryRecord(CxlMemoryBinding Binding);
    private readonly object _gate = new();
    private readonly Dictionary<CxlEndpointId, EndpointRecord> _endpoints = [];
    private readonly Dictionary<CxlFabricBindingId, FabricRecord> _fabric = [];
    private readonly Dictionary<CxlMemoryBindingId, MemoryRecord> _memory = [];
    private readonly Dictionary<CxlFabricPoolId, CxlFabricPoolSnapshot> _pools = [];
    private readonly Dictionary<CxlPoolAssignmentId, CxlPoolAssignment> _poolAssignments = [];
    private readonly Dictionary<CxlFabricReconfigurationId, CxlFabricReconfigurationTicket> _reconfigurations = [];
    private readonly HashSet<CxlFabricBindingId> _draining = [];
    private ulong _nextFabric = 1;
    private ulong _nextMemory = 1;
    private ulong _nextReconfiguration = 1;
    private ulong _nextPoolAssignment = 1;
    public bool PeerAccessSupported { get; set; }
    public bool ReturnMalformedFabricBinding { get; set; }
    public bool FabricUnbindFails { get; set; }
    public bool ReturnMalformedMemoryBinding { get; set; }
    public bool MemoryReleaseFails { get; set; }
    public bool FabricAcceptanceAmbiguous { get; set; }
    public bool MemoryAcceptanceAmbiguous { get; set; }
    public bool PoolAcceptanceAmbiguous { get; set; }
    public bool FabricThrowsAfterAcceptance { get; set; }
    public bool MemoryThrowsAfterAcceptance { get; set; }

    public PlatformAuthorityResult RegisterEndpoint(CxlEndpointId id, PlatformDeviceIdentity device, long capacityBytes,
        CxlMemoryPersistence persistence = CxlMemoryPersistence.Volatile,
        int proximityClass = 0, int latencyClass = 0, int bandwidthClass = 0)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(id.Value) || string.IsNullOrWhiteSpace(device.ResourceId) || capacityBytes <= 0 || !Enum.IsDefined(persistence) ||
                proximityClass < 0 || latencyClass < 0 || bandwidthClass < 0)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Type-3 endpoint configuration is invalid.");
            if (_endpoints.ContainsKey(id))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Type-3 endpoint already exists.");
            _endpoints.Add(id, new(id, device, capacityBytes, persistence, proximityClass, latencyClass, bandwidthClass));
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult RegisterPool(CxlFabricPoolId id, long capacityBytes)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(id.Value) || capacityBytes <= 0 || _pools.ContainsKey(id))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Fabric pool configuration is invalid or duplicate.");
            _pools.Add(id, new(id, capacityBytes, capacityBytes, 1));
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpointId)
    {
        lock (_gate)
            return _endpoints.TryGetValue(endpointId, out var e)
                ? PlatformAuthorityResult<CxlEndpointSnapshot>.Ok(new(e.Id, new(e.DeviceGeneration), CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, e.Available))
                : Missing<CxlEndpointSnapshot>();
    }

    public PlatformAuthorityResult<PlatformDeviceIdentity> ResolveDevice(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(endpointId, out var e) || !e.Available) return Missing<PlatformDeviceIdentity>();
            return expectedGeneration.Value == e.DeviceGeneration
                ? PlatformAuthorityResult<PlatformDeviceIdentity>.Ok(e.Device)
                : Stale<PlatformDeviceIdentity>("Device generation changed.");
        }
    }

    public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId endpointId)
    {
        lock (_gate)
            return _endpoints.TryGetValue(endpointId, out var e) && e.Available
                ? PlatformAuthorityResult<CxlMemoryCapacitySnapshot>.Ok(new(e.Id, new(e.DeviceGeneration), e.Capacity,
                    e.Capacity - e.Reserved, e.Persistence, e.Proximity, e.Latency, e.Bandwidth))
                : Missing<CxlMemoryCapacitySnapshot>();
    }

    public PlatformAuthorityResult<CxlFabricBinding> Bind(CxlFabricBindingRequest request)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(request.EndpointId, out var e) || !e.Available)
                return NotAccepted<CxlFabricBinding>("Type-3 endpoint is unavailable before binding.");
            if (request.DeviceGeneration.Value != e.DeviceGeneration)
                return NotAccepted<CxlFabricBinding>("Device generation changed before Type-3 binding.");
            if (request.Persistence != e.Persistence || request.CapacityBytes <= 0 || request.CapacityBytes > e.Capacity - e.Reserved)
                return NotAccepted<CxlFabricBinding>("Requested Type-3 capacity or persistence is unavailable.");
            var binding = new CxlFabricBinding(new(_nextFabric++), new(1), e.Id,
                new(e.DeviceGeneration), request.CapacityBytes);
            _fabric.Add(binding.BindingId, new(binding, request.CapacityBytes));
            e.Reserved += request.CapacityBytes;
            if (FabricThrowsAfterAcceptance)
                throw new InvalidOperationException("Injected provider exception after fabric acceptance.");
            if (FabricAcceptanceAmbiguous)
                return PlatformAuthorityResult<CxlFabricBinding>.Fail(PlatformAuthorityStatus.Unavailable,
                    "Fabric binding was accepted but its response was lost.");
            return PlatformAuthorityResult<CxlFabricBinding>.Ok(ReturnMalformedFabricBinding
                ? binding with { Generation = new(binding.Generation.Value + 1) }
                : binding);
        }
    }

    public PlatformAuthorityResult<CxlFabricResourceSnapshot> QueryResource(CxlFabricBindingId bindingId)
    {
        lock (_gate)
        {
            if (!_fabric.TryGetValue(bindingId, out var record)) return Missing<CxlFabricResourceSnapshot>();
            var current = Query(bindingId);
            if (!current.IsSuccess) return Missing<CxlFabricResourceSnapshot>();
            var state = _draining.Contains(bindingId) ? CxlFabricResourceState.Draining : CxlFabricResourceState.Bound;
            return PlatformAuthorityResult<CxlFabricResourceSnapshot>.Ok(new(current.Value!, state, record.ReservedBytes));
        }
    }

    public PlatformAuthorityResult<CxlFabricReconfigurationTicket> BeginReconfiguration(CxlFabricBinding binding)
    {
        lock (_gate)
        {
            if (!_fabric.TryGetValue(binding.BindingId, out var record) || record.Binding != binding || _draining.Contains(binding.BindingId))
                return NotAccepted<CxlFabricReconfigurationTicket>("Fabric binding is stale or already draining before reconfiguration.");
            var endpoint = _endpoints[binding.EndpointId];
            var replacementGeneration = checked(record.Binding.Generation.Value + 1);
            foreach (var memory in _memory.Where(entry => entry.Value.Binding.FabricBinding.BindingId == binding.BindingId).ToArray())
                _memory[memory.Key] = new(memory.Value.Binding with { Generation = new(memory.Value.Binding.Generation.Value + 1) });
            _draining.Add(binding.BindingId);
            var ticket = new CxlFabricReconfigurationTicket(new(_nextReconfiguration++), binding, new(replacementGeneration));
            _reconfigurations.Add(ticket.ReconfigurationId, ticket);
            return PlatformAuthorityResult<CxlFabricReconfigurationTicket>.Ok(ticket);
        }
    }

    public PlatformAuthorityResult<CxlFabricBinding> CompleteReconfiguration(CxlFabricReconfigurationTicket ticket)
    {
        lock (_gate)
        {
            if (!_reconfigurations.TryGetValue(ticket.ReconfigurationId, out var exact) || exact != ticket ||
                !_fabric.TryGetValue(ticket.PreviousBinding.BindingId, out var record) ||
                record.Binding != ticket.PreviousBinding || !_draining.Contains(ticket.PreviousBinding.BindingId) ||
                ticket.ReplacementGeneration.Value != checked(record.Binding.Generation.Value + 1))
                return Stale<CxlFabricBinding>("Fabric reconfiguration ticket is stale.");
            var replacement = record.Binding with { Generation = ticket.ReplacementGeneration };
            _reconfigurations.Remove(ticket.ReconfigurationId);
            _fabric[ticket.PreviousBinding.BindingId] = new(replacement, record.ReservedBytes);
            _draining.Remove(ticket.PreviousBinding.BindingId);
            return PlatformAuthorityResult<CxlFabricBinding>.Ok(replacement);
        }
    }

    public PlatformAuthorityResult<CxlFabricPoolSnapshot> QueryPool(CxlFabricPoolId poolId)
    {
        lock (_gate)
            return _pools.TryGetValue(poolId, out var pool)
                ? PlatformAuthorityResult<CxlFabricPoolSnapshot>.Ok(pool)
                : Missing<CxlFabricPoolSnapshot>();
    }

    public PlatformAuthorityResult<CxlPoolAssignment> AssignPoolCapacity(CxlFabricPoolId poolId, CxlEndpointId endpointId, long capacityBytes)
    {
        lock (_gate)
        {
            if (!_pools.TryGetValue(poolId, out var pool) || !_endpoints.ContainsKey(endpointId) ||
                capacityBytes <= 0 || capacityBytes > pool.AvailableCapacityBytes)
                return NotAccepted<CxlPoolAssignment>("Pool capacity assignment is unavailable before acceptance.");
            var assignment = new CxlPoolAssignment(new(_nextPoolAssignment++), 1, poolId, endpointId, capacityBytes);
            _poolAssignments.Add(assignment.AssignmentId, assignment);
            _pools[poolId] = pool with { AvailableCapacityBytes = pool.AvailableCapacityBytes - capacityBytes, Generation = pool.Generation + 1 };
            if (PoolAcceptanceAmbiguous)
                return PlatformAuthorityResult<CxlPoolAssignment>.Fail(PlatformAuthorityStatus.Unavailable,
                    "Pool assignment was accepted but its response was lost.");
            return PlatformAuthorityResult<CxlPoolAssignment>.Ok(assignment);
        }
    }

    public PlatformAuthorityResult ReleasePoolCapacity(CxlPoolAssignment assignment)
    {
        lock (_gate)
        {
            if (!_poolAssignments.TryGetValue(assignment.AssignmentId, out var exact) || exact != assignment)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Pool assignment is stale.");
            _poolAssignments.Remove(assignment.AssignmentId);
            var pool = _pools[assignment.PoolId];
            _pools[assignment.PoolId] = pool with { AvailableCapacityBytes = pool.AvailableCapacityBytes + assignment.CapacityBytes, Generation = pool.Generation + 1 };
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult<CxlPeerAccessEvidence> QueryPeerAccess(CxlPeerAccessRequest request)
    {
        lock (_gate)
        {
            if (!_endpoints.ContainsKey(request.Initiator) || !_endpoints.ContainsKey(request.Target) || request.Length <= 0)
                return PlatformAuthorityResult<CxlPeerAccessEvidence>.Fail(PlatformAuthorityStatus.Denied, "Peer request is invalid.");
            return PlatformAuthorityResult<CxlPeerAccessEvidence>.Ok(new(request.Initiator, request.Target,
                PeerAccessSupported && request.PlatformIsolationMaterialized, 1));
        }
    }

    public PlatformAuthorityResult<CxlFabricBinding> Query(CxlFabricBindingId bindingId)
    {
        lock (_gate)
        {
            if (!_fabric.TryGetValue(bindingId, out var record)) return Missing<CxlFabricBinding>();
            return PlatformAuthorityResult<CxlFabricBinding>.Ok(record.Binding);
        }
    }

    public PlatformAuthorityResult Unbind(CxlFabricBinding binding)
    {
        lock (_gate)
        {
            if (!_fabric.TryGetValue(binding.BindingId, out var record)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Fabric binding is stale.");
            if (record.Binding != binding) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Fabric binding generation is stale.");
            if (FabricUnbindFails) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Fabric closure is ambiguous.");
            if (_memory.Values.Any(m => m.Binding.FabricBinding.BindingId == binding.BindingId))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Memory binding remains active.");
            _fabric.Remove(binding.BindingId);
            var e = _endpoints[record.Binding.EndpointId];
            e.Reserved -= record.ReservedBytes;
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabricBinding, RegionBackingLeaseDescriptor backingLease)
    {
        lock (_gate)
        {
            if (!_fabric.TryGetValue(fabricBinding.BindingId, out var f) || f.Binding != fabricBinding)
                return NotAccepted<CxlMemoryBinding>("Fabric binding is stale before memory binding.");
            var e = _endpoints[fabricBinding.EndpointId];
            if (!e.Available)
                return NotAccepted<CxlMemoryBinding>("Type-3 backing is unavailable or rebound before binding.");
            if (backingLease.ByteLength <= 0 || backingLease.ByteLength > fabricBinding.CapacityBytes)
                return NotAccepted<CxlMemoryBinding>("Region backing cannot fit the Type-3 binding.");
            var binding = new CxlMemoryBinding(new(_nextMemory++), new(1), fabricBinding, backingLease.Handle);
            _memory.Add(binding.BindingId, new(binding));
            if (MemoryThrowsAfterAcceptance)
                throw new InvalidOperationException("Injected provider exception after memory acceptance.");
            if (MemoryAcceptanceAmbiguous)
                return PlatformAuthorityResult<CxlMemoryBinding>.Fail(PlatformAuthorityStatus.Unavailable,
                    "Memory binding was accepted but its response was lost.");
            return PlatformAuthorityResult<CxlMemoryBinding>.Ok(ReturnMalformedMemoryBinding
                ? binding with { Generation = new(binding.Generation.Value + 1) }
                : binding);
        }
    }

    public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId bindingId)
    {
        lock (_gate)
        {
            if (!_memory.TryGetValue(bindingId, out var record)) return Missing<CxlMemoryBinding>();
            return PlatformAuthorityResult<CxlMemoryBinding>.Ok(record.Binding);
        }
    }

    public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding)
    {
        lock (_gate)
        {
            if (!_memory.TryGetValue(binding.BindingId, out var current) || current.Binding != binding)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Memory binding generation is stale.");
            if (MemoryReleaseFails) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Memory closure is ambiguous.");
            _memory.Remove(binding.BindingId);
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult HotRemove(CxlEndpointId endpointId)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(endpointId, out var e)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unavailable, "Endpoint not found.");
            e.Available = false;
            checked { e.DeviceGeneration++; }
            InvalidateEndpointBindings(endpointId, e.DeviceGeneration);
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult Rebind(CxlEndpointId endpointId)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(endpointId, out var e)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unavailable, "Endpoint not found.");
            checked { e.DeviceGeneration++; }
            InvalidateEndpointBindings(endpointId, e.DeviceGeneration);
            e.Available = true;
            return PlatformAuthorityResult.Ok();
        }
    }

    private static PlatformAuthorityResult<T> Missing<T>() =>
        PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unavailable, "Type-3 endpoint or binding is unavailable.");
    private static PlatformAuthorityResult<T> Stale<T>(string message) =>
        PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Stale, message);
    private static PlatformAuthorityResult<T> NotAccepted<T>(string message) =>
        PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.NotAccepted, message);

    private void InvalidateEndpointBindings(CxlEndpointId endpointId, ulong deviceGeneration)
    {
        foreach (var fabric in _fabric.Where(entry => entry.Value.Binding.EndpointId == endpointId).ToArray())
        {
            foreach (var ticket in _reconfigurations.Where(entry => entry.Value.PreviousBinding.BindingId == fabric.Key).ToArray())
                _reconfigurations.Remove(ticket.Key);
            _draining.Remove(fabric.Key);
            var next = fabric.Value.Binding with
            {
                Generation = new(fabric.Value.Binding.Generation.Value + 1),
                DeviceGeneration = new(deviceGeneration)
            };
            _fabric[fabric.Key] = new(next, fabric.Value.ReservedBytes);
        }
        foreach (var memory in _memory.Where(entry => entry.Value.Binding.FabricBinding.EndpointId == endpointId).ToArray())
            _memory[memory.Key] = new(memory.Value.Binding with { Generation = new(memory.Value.Binding.Generation.Value + 1) });
    }
}
