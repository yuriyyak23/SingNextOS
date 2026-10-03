using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal sealed record VirtualDomainInspectionRecord(
    VirtualDomainHandle Handle,
    ProcessHandle Owner,
    VirtualDomainHandle? Parent,
    VirtualDomainState State,
    int GuestMappingCount);

internal sealed class VirtualDomainAuthority
{
    internal sealed class Record(object stateGate)
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
        public bool AuthorityPublished { get; set; } = true;
        private VirtualDomainState _state;
        public VirtualDomainState State
        {
            get { lock (stateGate) return _state; }
            set { lock (stateGate) _state = value; }
        }
        public Dictionary<GuestRegionMappingId, GuestRegionMapping> Mappings { get; } = [];
        public Dictionary<GuestRegionMappingId, (PlatformGuestMapping Guest, PlatformRegionMapping Parent)> PlatformMappings { get; } = [];
        public PlatformExecutableArtifactBinding? ExecutableArtifact { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<VirtualDomainId, Record> _records = [];
    private ulong _nextDomainId = 1;
    private ulong _nextAddressSpaceId = 1;
    private ulong _nextMappingId = 1;

    internal Record AddModel(ProcessHandle owner, PlatformAuthorityBridge.VirtualDomainBinding binding,
        VirtualDomainProfile profile, CapabilityId[] capabilities, bool authorityPublished = true) =>
        Add(owner, profile, capabilities, binding, null, null, null, PlatformChildAuthorityClass.None, authorityPublished);

    internal Record AddChild(ProcessHandle owner, PlatformDomainBinding parent, PlatformChildBinding child,
        VirtualDomainProfile profile, CapabilityId[] capabilities, PlatformChildAuthorityClass authority,
        bool authorityPublished = true) =>
        Add(owner, profile, capabilities, null, parent, child, null, authority, authorityPublished);

    internal Record AddNested(ProcessHandle owner, Record parentRecord, PlatformChildBinding child,
        VirtualDomainProfile profile, CapabilityId[] capabilities, PlatformChildAuthorityClass authority, bool authorityPublished = true) =>
        Add(owner, profile, capabilities, null, parentRecord.ParentBinding, child, parentRecord.Handle, authority, authorityPublished);

    private Record Add(ProcessHandle owner, VirtualDomainProfile profile, CapabilityId[] capabilities,
        PlatformAuthorityBridge.VirtualDomainBinding? model, PlatformDomainBinding? parent, PlatformChildBinding? child,
        VirtualDomainHandle? parentDomain, PlatformChildAuthorityClass authority, bool authorityPublished = true)
    {
        lock (_gate)
        {
            var domain = new VirtualDomainHandle(new VirtualDomainId(_nextDomainId++), new VirtualDomainGeneration(1));
            var addressSpace = new VirtualAddressSpaceHandle(new VirtualAddressSpaceId(_nextAddressSpaceId++), new VirtualAddressSpaceGeneration(1));
            var record = new Record(_gate) { Handle = domain, AddressSpace = addressSpace, Owner = owner,
                ModelBinding = model, ParentBinding = parent, ChildBinding = child,
                ParentDomain = parentDomain, Authority = authority,
                Profile = profile, Capabilities = capabilities, State = VirtualDomainState.Created, AuthorityPublished = authorityPublished };
            _records.Add(domain.DomainId, record);
            return record;
        }
    }

    internal KernelResult RetainExecutableArtifact(Record record, PlatformExecutableArtifactBinding binding)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(record.Handle.DomainId, out var current) || !ReferenceEquals(current, record) ||
                record.ChildBinding != binding.Child || record.ExecutableArtifact is not null)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual artifact cleanup owner changed.");
            record.ExecutableArtifact = binding; // known receipt retention only; fresh source admission still required
            return KernelResult.Ok();
        }
    }

    internal KernelResult CommitStateTransition(Record record, VirtualDomainState from, VirtualDomainState to)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(record.Handle.DomainId, out var current) || !ReferenceEquals(current, record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Virtual state owner record changed.");
            if (!record.AuthorityPublished || record.State != from)
                return KernelResult.Fail(KernelError.InvalidTransition, "Virtual state changed before publication.");
            record.State = to;
            return KernelResult.Ok();
        }
    }

    internal KernelResult CommitAuthorityPublication(Record record)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(record.Handle.DomainId, out var current) || !ReferenceEquals(current, record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Virtual publication record is no longer current.");
            record.AuthorityPublished = true;
            return KernelResult.Ok();
        }
    }

    internal KernelResult CommitEffectAdmission(Record record, VirtualDomainState expected, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            var lineage = ValidateActiveAncestors(record);
            if (!lineage.IsSuccess) return lineage;
            if (!record.AuthorityPublished || record.State != expected)
                return KernelResult.Fail(KernelError.InvalidTransition, "Virtual effect source state/publication changed.");
            return commit(); // Trusted pure admission/state mutation; no caller/provider callback.
        }
    }

    internal KernelResult ValidateActiveAncestors(Record record)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(record.Handle.DomainId, out var current) || !ReferenceEquals(current, record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual lineage owner changed.");
            var ancestor = record;
            var visited = new HashSet<VirtualDomainId> { record.Handle.DomainId };
            while (ancestor.ParentDomain is { } parent)
            {
                if (!visited.Add(parent.DomainId))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual lineage is cyclic.");
                var exact = Resolve(record.Owner, parent);
                if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                if (!exact.Value!.AuthorityPublished || exact.Value.State is not
                    (VirtualDomainState.Configured or VirtualDomainState.Running or VirtualDomainState.Parked))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual ancestor cannot admit a new effect/publication.");
                ancestor = exact.Value;
            }
            return KernelResult.Ok();
        }
    }

    internal KernelResult<Record> ResolveAdmission(ProcessHandle owner, VirtualDomainHandle handle)
    {
        lock (_gate)
        {
            var current = Resolve(owner, handle);
            if (!current.IsSuccess) return current;
            return current.Value!.AuthorityPublished ? current :
                KernelResult<Record>.Fail(KernelError.PlatformBindingActive, "Virtual authority publication has not committed.");
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

    internal VirtualDomainInspectionRecord[] InspectionSnapshot()
    {
        lock (_gate)
            return _records.Values
                .Select(static record => new VirtualDomainInspectionRecord(
                    record.Handle,
                    record.Owner,
                    record.ParentDomain,
                    record.State,
                    record.Mappings.Count))
                .OrderBy(static record => record.Handle.DomainId.Value)
                .ToArray();
    }

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
