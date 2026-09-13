using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal sealed class VirtualDomainAuthority
{
    internal sealed class Record
    {
        public required VirtualDomainHandle Handle { get; init; }
        public required VirtualAddressSpaceHandle AddressSpace { get; init; }
        public required ProcessHandle Owner { get; init; }
        public PlatformAuthorityBridge.VirtualDomainBinding? ModelBinding { get; init; }
        public PlatformDomainBinding? ParentBinding { get; init; }
        public PlatformChildBinding? ChildBinding { get; init; }
        public VirtualDomainHandle? ParentDomain { get; init; }
        public required PlatformChildAuthorityClass Authority { get; init; }
        public required VirtualDomainProfile Profile { get; init; }
        public required CapabilityId[] Capabilities { get; set; }
        public VirtualDomainState State { get; set; }
        public Dictionary<GuestRegionMappingId, GuestRegionMapping> Mappings { get; } = [];
        public Dictionary<GuestRegionMappingId, (PlatformGuestMapping Guest, PlatformRegionMapping Parent)> PlatformMappings { get; } = [];
        public PlatformExecutableArtifactBinding? ExecutableArtifact { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<VirtualDomainId, Record> _records = [];
    private ulong _nextDomainId = 1;
    private ulong _nextAddressSpaceId = 1;
    private ulong _nextMappingId = 1;

    internal Record AddModel(ProcessHandle owner, PlatformAuthorityBridge.VirtualDomainBinding binding, VirtualDomainProfile profile, CapabilityId[] capabilities) =>
        Add(owner, profile, capabilities, binding, null, null, null, PlatformChildAuthorityClass.None);

    internal Record AddChild(ProcessHandle owner, PlatformDomainBinding parent, PlatformChildBinding child,
        VirtualDomainProfile profile, CapabilityId[] capabilities, PlatformChildAuthorityClass authority) =>
        Add(owner, profile, capabilities, null, parent, child, null, authority);

    internal Record AddNested(ProcessHandle owner, Record parentRecord, PlatformChildBinding child,
        VirtualDomainProfile profile, CapabilityId[] capabilities, PlatformChildAuthorityClass authority) =>
        Add(owner, profile, capabilities, null, parentRecord.ParentBinding, child, parentRecord.Handle, authority);

    private Record Add(ProcessHandle owner, VirtualDomainProfile profile, CapabilityId[] capabilities,
        PlatformAuthorityBridge.VirtualDomainBinding? model, PlatformDomainBinding? parent, PlatformChildBinding? child,
        VirtualDomainHandle? parentDomain, PlatformChildAuthorityClass authority)
    {
        lock (_gate)
        {
            var domain = new VirtualDomainHandle(new VirtualDomainId(_nextDomainId++), new VirtualDomainGeneration(1));
            var addressSpace = new VirtualAddressSpaceHandle(new VirtualAddressSpaceId(_nextAddressSpaceId++), new VirtualAddressSpaceGeneration(1));
            var record = new Record { Handle = domain, AddressSpace = addressSpace, Owner = owner,
                ModelBinding = model, ParentBinding = parent, ChildBinding = child,
                ParentDomain = parentDomain, Authority = authority,
                Profile = profile, Capabilities = capabilities, State = VirtualDomainState.Created };
            _records.Add(domain.DomainId, record);
            return record;
        }
    }

    internal KernelResult<Record> Resolve(ProcessHandle owner, VirtualDomainHandle handle)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(handle.DomainId, out var record)) return KernelResult<Record>.Fail(KernelError.VirtualDomainNotFound, "Virtual domain was not found.");
            if (record.Handle.Generation != handle.Generation) return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Virtual domain generation is stale.");
            if (record.Owner != owner) return KernelResult<Record>.Fail(KernelError.WrongVirtualDomainOwner, "Virtual domain belongs to another process generation.");
            if (record.State == VirtualDomainState.Closed) return KernelResult<Record>.Fail(KernelError.VirtualDomainNotFound, "Virtual domain is closed.");
            return KernelResult<Record>.Ok(record);
        }
    }

    internal GuestRegionMapping AddMapping(Record record, GuestAddressRange range, RegionHandle region, GuestMemoryAccess access)
    {
        lock (_gate)
        {
            var mapping = new GuestRegionMapping(new GuestRegionMappingHandle(new GuestRegionMappingId(_nextMappingId++), new GuestRegionMappingGeneration(1)), record.Handle, record.AddressSpace, range, region, access);
            record.Mappings.Add(mapping.Mapping.MappingId, mapping);
            return mapping;
        }
    }

    internal KernelResult<GuestRegionMapping> RemoveMapping(Record record, GuestRegionMappingHandle handle)
    {
        lock (_gate)
        {
            if (!record.Mappings.TryGetValue(handle.MappingId, out var mapping)) return KernelResult<GuestRegionMapping>.Fail(KernelError.GuestMappingNotFound, "Guest mapping was not found.");
            if (mapping.Mapping.Generation != handle.Generation) return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Guest mapping generation is stale.");
            record.Mappings.Remove(handle.MappingId);
            return KernelResult<GuestRegionMapping>.Ok(mapping);
        }
    }

    internal int QuarantineForPlatformBackendReset()
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var record in _records.Values)
            {
                if (record.State is VirtualDomainState.Closed or VirtualDomainState.Quarantined)
                    continue;

                record.State = VirtualDomainState.Quarantined;
                count++;
            }

            return count;
        }
    }

    internal Record[] ForOwner(ProcessHandle owner) { lock (_gate) return _records.Values.Where(x => x.Owner == owner && x.State != VirtualDomainState.Closed).ToArray(); }

    internal Record[] ChildrenOf(Record parent)
    {
        lock (_gate)
            return _records.Values.Where(x => x.Owner == parent.Owner && x.ParentDomain == parent.Handle && x.State != VirtualDomainState.Closed).ToArray();
    }

    internal KernelResult<GuestRegionMapping> ResolveMapping(Record record, GuestRegionMappingHandle handle)
    {
        lock (_gate)
        {
            if (!record.Mappings.TryGetValue(handle.MappingId, out var mapping))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.GuestMappingNotFound, "Guest mapping was not found.");
            if (mapping.Mapping.Generation != handle.Generation)
                return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Guest mapping generation is stale.");
            return KernelResult<GuestRegionMapping>.Ok(mapping);
        }
    }
}
