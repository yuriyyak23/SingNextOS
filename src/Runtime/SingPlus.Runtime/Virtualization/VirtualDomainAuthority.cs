using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal sealed record VirtualDomainInspectionRecord(
    VirtualDomainHandle Handle,
    ProcessHandle Owner,
    VirtualDomainHandle? Parent,
    VirtualDomainState State,
    int GuestMappingCount);

internal sealed partial class VirtualDomainAuthority
{
    internal sealed class Record
    {
        private readonly object stateGate;
        internal Record(object gate) { stateGate = gate; OriginalOwner = this; }
        internal Record OriginalOwner { get; }
        private long _sourceRevision;
        private bool _sourceRevisionExhausted;
        internal long SourceRevision { get { lock (stateGate) return _sourceRevision; } }
        internal bool SourceRevisionExhausted { get { lock (stateGate) return _sourceRevisionExhausted; } }
        private void AdvanceSourceRevision()
        {
            if (_sourceRevision == long.MaxValue) _sourceRevisionExhausted = true;
            else _sourceRevision++;
        }
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
        private bool _authorityPublished = true;
        public bool AuthorityPublished
        {
            get { lock (stateGate) return _authorityPublished; }
            set { lock (stateGate) { AdvanceSourceRevision(); _authorityPublished = value; } }
        }
        private VirtualDomainState _state;
        public VirtualDomainState State
        {
            get { lock (stateGate) return _state; }
            set { lock (stateGate) { AdvanceSourceRevision(); _state = value; } }
        }
        public Dictionary<GuestRegionMappingId, GuestRegionMapping> Mappings { get; } = [];
        // Original immutable correlation within the same VDA owner; routes cannot
        // replace the tuple that created a Region/provider cleanup obligation.
        internal Dictionary<GuestRegionMappingId, GuestRegionMapping> OriginalMappings { get; } = [];
        internal Dictionary<GuestRegionMappingId, LocalGuestLifetime> LocalGuestLifetimes { get; } = [];
        public HashSet<GuestRegionMappingId> StagedMappings { get; } = [];
        // Correlation only; CapabilityAuthority remains the source permission owner.
        public Dictionary<GuestRegionMappingId, (CapabilityId Memory, CapabilityId Region)> MappingSources { get; } = [];
        public Dictionary<GuestRegionMappingId, (PlatformGuestMapping Guest, PlatformRegionMapping Parent)> PlatformMappings { get; } = [];
        public PlatformExecutableArtifactBinding? ExecutableArtifact { get; set; }
        // Correlation to the existing original mapping, never new permission.
        internal GuestRegionMappingHandle? ExecutableMapping { get; set; }
        internal long? TerminalPublicationRevision { get; set; }
        internal PlatformExecutionTerminalKind? TerminalPublicationKind { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<VirtualDomainId, Record> _records = [];
    // Stable reference to the same authoritative owner; routes are not cleanup permission.
    private readonly Dictionary<VirtualDomainId, Record> _originalRecords = [];
    private ulong _nextDomainId = 1;
    private ulong _nextAddressSpaceId = 1;
    private ulong _nextMappingId = 1;
    private int _pendingDomainIdentities;

    internal sealed class IdentityPreparation(VirtualDomainAuthority authority, ProcessHandle owner,
        VirtualDomainHandle domain, VirtualAddressSpaceHandle addressSpace) : IDisposable
    {
        internal VirtualDomainAuthority Authority { get; } = authority;
        internal ProcessHandle Owner { get; } = owner;
        internal VirtualDomainHandle Domain { get; } = domain;
        internal VirtualAddressSpaceHandle AddressSpace { get; } = addressSpace;
        internal bool Consumed { get; set; }
        internal bool Disposed { get; private set; }
        public void Dispose()
        {
            lock (Authority._gate)
            {
                if (Disposed) return;
                Disposed = true;
                if (!Consumed) Authority._pendingDomainIdentities--;
            }
        }
    }

    internal KernelResult<IdentityPreparation> PrepareIdentity(ProcessHandle owner)
    {
        lock (_gate)
        {
            if (_nextDomainId == 0 || _nextAddressSpaceId == 0 || _pendingDomainIdentities == int.MaxValue ||
                _originalRecords.Count > int.MaxValue - _pendingDomainIdentities - 1)
                return KernelResult<IdentityPreparation>.Fail(KernelError.CapacityExhausted, "Local virtual identity publication is exhausted.");
            _records.EnsureCapacity(_records.Count + _pendingDomainIdentities + 1);
            _originalRecords.EnsureCapacity(_originalRecords.Count + _pendingDomainIdentities + 1);
            IdentityPreparation prepared = new IdentityPreparation(this, owner,
                new(new(_nextDomainId), new(1)), new(new(_nextAddressSpaceId), new(1)));
            _nextDomainId = _nextDomainId == ulong.MaxValue ? 0 : _nextDomainId + 1;
            _nextAddressSpaceId = _nextAddressSpaceId == ulong.MaxValue ? 0 : _nextAddressSpaceId + 1;
            _pendingDomainIdentities++;
            return KernelResult<IdentityPreparation>.Ok(prepared);
        }
    }

    internal Record AddModel(IdentityPreparation identity, ProcessHandle owner, PlatformAuthorityBridge.VirtualDomainBinding binding,
        VirtualDomainProfile profile, CapabilityId[] capabilities, bool authorityPublished = true) =>
        Add(identity, owner, profile, capabilities, binding, null, null, null, PlatformChildAuthorityClass.None, authorityPublished);

    internal Record AddChild(IdentityPreparation identity, ProcessHandle owner, PlatformDomainBinding parent, PlatformChildBinding child,
        VirtualDomainProfile profile, CapabilityId[] capabilities, PlatformChildAuthorityClass authority,
        bool authorityPublished = true) =>
        Add(identity, owner, profile, capabilities, null, parent, child, null, authority, authorityPublished);

    internal Record AddNested(IdentityPreparation identity, ProcessHandle owner, Record parentRecord, PlatformChildBinding child,
        VirtualDomainProfile profile, CapabilityId[] capabilities, PlatformChildAuthorityClass authority, bool authorityPublished = true) =>
        Add(identity, owner, profile, capabilities, null, parentRecord.ParentBinding, child, parentRecord.Handle, authority, authorityPublished);

    private Record Add(IdentityPreparation identity, ProcessHandle owner, VirtualDomainProfile profile, CapabilityId[] capabilities,
        PlatformAuthorityBridge.VirtualDomainBinding? model, PlatformDomainBinding? parent, PlatformChildBinding? child,
        VirtualDomainHandle? parentDomain, PlatformChildAuthorityClass authority, bool authorityPublished = true)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(identity.Authority, this) || identity.Owner != owner || identity.Disposed || identity.Consumed)
                throw new InvalidOperationException("Exact local virtual identity preparation was lost.");
            VirtualDomainHandle domain = identity.Domain;
            VirtualAddressSpaceHandle addressSpace = identity.AddressSpace;
            Record record = new Record(_gate) { Handle = domain, AddressSpace = addressSpace, Owner = owner,
                ModelBinding = model, ParentBinding = parent, ChildBinding = child,
                ParentDomain = parentDomain, Authority = authority,
                Profile = profile, Capabilities = capabilities, State = VirtualDomainState.Created, AuthorityPublished = authorityPublished };
            _records.Add(domain.DomainId, record);
            _originalRecords.Add(domain.DomainId, record);
            identity.Consumed = true;
            _pendingDomainIdentities--;
            return record;
        }
    }

    internal KernelResult RetainExecutableArtifact(Record record, GuestRegionMappingHandle mapping, PlatformExecutableArtifactBinding binding)
    {
        lock (_gate)
        {
            if (!_originalRecords.TryGetValue(record.Handle.DomainId, out Record? original) || !ReferenceEquals(original, record) ||
                record.ChildBinding != binding.Child || record.ExecutableArtifact is not null)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual artifact cleanup owner changed.");
            record.ExecutableArtifact = binding; // known receipt retention only; fresh source admission still required
            record.ExecutableMapping = mapping;
            return KernelResult.Ok();
        }
    }

    internal KernelResult CommitStateTransition(Record record, VirtualDomainState from, VirtualDomainState to)
    {
        lock (_gate)
        {
            if (!HasCurrentOriginalRecord(record))
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
            if (!HasCurrentOriginalRecord(record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Virtual publication record is no longer current.");
            record.AuthorityPublished = true;
            return KernelResult.Ok();
        }
    }

    internal bool HasUnconsumedOriginalOwner(ProcessHandle owner)
    {
        lock (_gate) return _originalRecords.Values.Any(record => record.Owner == owner);
    }

    internal KernelResult ValidateCleanupOwner(Record record)
    {
        lock (_gate)
            return HasCurrentOriginalRecord(record) && record.State != VirtualDomainState.Closed
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.StaleGeneration, "Original virtual cleanup routing owner changed.");
    }

    internal KernelResult ValidateMappingCleanupComplete(Record record)
    {
        lock (_gate)
        {
            if (!HasCurrentOriginalRecord(record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original virtual cleanup owner changed.");
            if (record.ModelBinding is not null)
                return record.Mappings.Count == record.OriginalMappings.Count &&
                    record.Mappings.Values.All(mapping => HasOriginalMapping(record, mapping)) &&
                    record.PlatformMappings.Count == 0
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.PlatformBindingDraining, "Original model mapping inventory is incomplete or changed before provider closure.");
            return record.Mappings.Count == 0 && record.OriginalMappings.Count == 0 &&
                record.PlatformMappings.Count == 0 && record.StagedMappings.Count == 0
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformBindingDraining, "Original guest mapping obligations remain before provider closure.");
        }
    }

    internal KernelResult CommitVirtualCleanup(Record record, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(record.Handle.DomainId, out Record? current) || !ReferenceEquals(current, record) ||
                !_originalRecords.TryGetValue(record.Handle.DomainId, out Record? original) || !ReferenceEquals(original, record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact model cleanup owner changed.");
            if (record.State is not (VirtualDomainState.Draining or VirtualDomainState.Quarantined) ||
                record.Mappings.Count != 0 || record.OriginalMappings.Count != 0 || record.StagedMappings.Count != 0 || record.PlatformMappings.Count != 0 ||
                _originalRecords.Values.Any(child => child.ParentDomain == record.Handle))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Model cleanup still has local dependents.");
            KernelResult result = commit(); // Trusted exact bridge cleanup mutation; no provider callback.
            if (result.IsSuccess)
            {
                record.State = VirtualDomainState.Closed;
                _originalRecords.Remove(record.Handle.DomainId);
            }
            return result;
        }
    }

    internal sealed record EffectSource(Record Owner,
        IReadOnlyList<(Record Owner, long Revision, VirtualDomainState State)> Lineage);

    internal KernelResult<EffectSource> CaptureEffectSource(Record record, VirtualDomainState expected)
    {
        lock (_gate)
        {
            KernelResult valid = ValidateActiveAncestors(record);
            if (!valid.IsSuccess) return KernelResult<EffectSource>.Fail(valid.Error, valid.Message!);
            if (!record.AuthorityPublished || record.State != expected)
                return KernelResult<EffectSource>.Fail(KernelError.InvalidTransition, "Virtual effect source changed before capture.");
            List<(Record, long, VirtualDomainState)> lineage = new List<(Record, long, VirtualDomainState)>();
            Record current = record;
            while (true)
            {
                if (current.SourceRevisionExhausted)
                    return KernelResult<EffectSource>.Fail(KernelError.CapacityExhausted, "Virtual source revision exhausted.");
                lineage.Add((current, current.SourceRevision, current.State));
                if (current.ParentDomain is not { } parent) break;
                current = _records[parent.DomainId]; // Validated same-original lineage under this gate.
            }
            return KernelResult<EffectSource>.Ok(new(record, lineage));
        }
    }

    internal KernelResult CommitEffectAdmission(Record record, VirtualDomainState expected, Func<KernelResult> commit,
        EffectSource? source = null)
    {
        lock (_gate)
        {
            KernelResult lineage = ValidateActiveAncestors(record);
            if (!lineage.IsSuccess) return lineage;
            if (!record.AuthorityPublished || record.State != expected)
                return KernelResult.Fail(KernelError.InvalidTransition, "Virtual effect source state/publication changed.");
            if (record.SourceRevisionExhausted)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Virtual source revision exhausted before effect.");
            if (source is not null && (!ReferenceEquals(source.Owner, record) || source.Lineage.Count == 0 ||
                !ReferenceEquals(source.Lineage[0].Owner, record) || source.Lineage.Any(node =>
                    !HasCurrentOriginalRecord(node.Owner) || !node.Owner.AuthorityPublished || node.Owner.SourceRevisionExhausted ||
                    node.Owner.SourceRevision != node.Revision || node.Owner.State != node.State)))
                return KernelResult.Fail(KernelError.InvalidTransition, "Original virtual source revision/lineage changed.");
            return commit(); // Trusted pure admission/state mutation; no caller/provider callback.
        }
    }

    internal KernelResult ValidateActiveAncestors(Record record)
    {
        lock (_gate)
        {
            if (!HasCurrentOriginalRecord(record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual lineage owner changed.");
            Record ancestor = record;
            HashSet<VirtualDomainId> visited = new HashSet<VirtualDomainId> { record.Handle.DomainId };
            while (ancestor.ParentDomain is { } parent)
            {
                if (!visited.Add(parent.DomainId))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual lineage is cyclic.");
                KernelResult<Record> exact = Resolve(record.Owner, parent);
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
            KernelResult<Record> current = Resolve(owner, handle);
            if (!current.IsSuccess) return current;
            return current.Value!.AuthorityPublished ? current :
                KernelResult<Record>.Fail(KernelError.PlatformBindingActive, "Virtual authority publication has not committed.");
        }
    }

    internal KernelResult<Record> Resolve(ProcessHandle owner, VirtualDomainHandle handle)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(handle.DomainId, out Record? record)) return KernelResult<Record>.Fail(KernelError.VirtualDomainNotFound, "Virtual domain was not found.");
            if (record.Handle.Generation != handle.Generation) return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Virtual domain generation is stale.");
            if (record.Owner != owner) return KernelResult<Record>.Fail(KernelError.WrongVirtualDomainOwner, "Virtual domain belongs to another process generation.");
            if (record.State == VirtualDomainState.Closed) return KernelResult<Record>.Fail(KernelError.VirtualDomainNotFound, "Virtual domain is closed.");
            if (!_originalRecords.TryGetValue(handle.DomainId, out Record? original) || !ReferenceEquals(original, record))
                return KernelResult<Record>.Fail(KernelError.PlatformFaulted, "Exact original virtual owner is unavailable.");
            return KernelResult<Record>.Ok(record);
        }
    }

    internal KernelResult<GuestRegionMapping> AddMapping(Record record, GuestAddressRange range, RegionHandle region, GuestMemoryAccess access,
        CapabilityId memorySource, CapabilityId regionSource)
    {
        lock (_gate)
        {
            if (!HasCurrentOriginalRecord(record) || record.State is not (VirtualDomainState.Configured or VirtualDomainState.Parked))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Original guest mapping admission owner changed.");
            if (_nextMappingId == 0 || record.Mappings.Count == int.MaxValue || record.OriginalMappings.Count == int.MaxValue ||
                record.StagedMappings.Count == int.MaxValue || record.MappingSources.Count == int.MaxValue)
                return KernelResult<GuestRegionMapping>.Fail(KernelError.CapacityExhausted, "Local guest mapping publication capacity is exhausted.");
            ulong rangeEnd = range.GuestAddress + (ulong)range.ByteLength;
            if (record.OriginalMappings.Values.Any(existing =>
                range.GuestAddress < existing.GuestRange.GuestAddress + (ulong)existing.GuestRange.ByteLength &&
                existing.GuestRange.GuestAddress < rangeEnd))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformDenied, "Guest range overlaps an existing exact mapping.");
            record.Mappings.EnsureCapacity(record.Mappings.Count + 1);
            record.OriginalMappings.EnsureCapacity(record.OriginalMappings.Count + 1);
            record.StagedMappings.EnsureCapacity(record.StagedMappings.Count + 1);
            record.MappingSources.EnsureCapacity(record.MappingSources.Count + 1);
            record.LocalGuestLifetimes.EnsureCapacity(record.LocalGuestLifetimes.Count + 1);
            GuestRegionMapping mapping = new GuestRegionMapping(new GuestRegionMappingHandle(new GuestRegionMappingId(_nextMappingId++), new GuestRegionMappingGeneration(1)), record.Handle, record.AddressSpace, range, region, access);
            LocalGuestLifetime? localLifetime = new LocalGuestLifetime(record, mapping);
            record.Mappings.Add(mapping.Mapping.MappingId, mapping);
            record.OriginalMappings.Add(mapping.Mapping.MappingId, mapping);
            record.StagedMappings.Add(mapping.Mapping.MappingId);
            record.MappingSources.Add(mapping.Mapping.MappingId, (memorySource, regionSource));
            if (localLifetime is not null) record.LocalGuestLifetimes.Add(mapping.Mapping.MappingId, localLifetime);
            return KernelResult<GuestRegionMapping>.Ok(mapping);
        }
    }

    internal KernelResult<GuestRegionMapping> RemoveMapping(Record record, GuestRegionMappingHandle handle,
        GuestRegionMapping? expected = null, Func<GuestRegionMapping, KernelResult>? cleanupConsumer = null)
    {
        lock (_gate)
        {
            if (!HasCurrentOriginalRecord(record))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Exact guest mapping owner changed.");
            if (!record.Mappings.TryGetValue(handle.MappingId, out GuestRegionMapping mapping)) return KernelResult<GuestRegionMapping>.Fail(KernelError.GuestMappingNotFound, "Guest mapping was not found.");
            if (mapping.Mapping.Generation != handle.Generation) return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Guest mapping generation is stale.");
            if (!HasOriginalMapping(record, mapping))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Original guest mapping tuple changed.");
            if (expected is not null && mapping != expected)
                return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Original guest mapping cleanup tuple changed.");
            if (cleanupConsumer is not null)
            {
                KernelResult cleanup = cleanupConsumer(mapping); // Trusted local Region owner mutation, no external callback.
                if (!cleanup.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(cleanup.Error, cleanup.Message!);
            }
            if (record.ChildBinding is null && (!TryLocalGuestLifetime(record, mapping, out LocalGuestLifetime? local) ||
                local.RegionReserved && !local.RegionReleased || local.Reservation is not null && !local.ChargeConsumed))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformFaulted, "Original local guest Region/charge remains unconsumed.");
            record.Mappings.Remove(handle.MappingId);
            record.OriginalMappings.Remove(handle.MappingId);
            record.StagedMappings.Remove(handle.MappingId);
            record.MappingSources.Remove(handle.MappingId);
            record.LocalGuestLifetimes.Remove(handle.MappingId);
            return KernelResult<GuestRegionMapping>.Ok(mapping);
        }
    }

    internal KernelResult RemovePlatformMapping(Record record, GuestRegionMapping expectedMapping,
        (PlatformGuestMapping Guest, PlatformRegionMapping Parent) expectedPlatform, Func<KernelResult>? closureConsumer=null)
    {
        lock (_gate)
        {
            GuestRegionMappingId id = expectedMapping.Mapping.MappingId;
            if (!HasCurrentOriginalRecord(record) ||
                !record.Mappings.TryGetValue(id, out GuestRegionMapping mapping) || mapping != expectedMapping || !HasOriginalMapping(record, mapping) ||
                !record.PlatformMappings.TryGetValue(id, out (PlatformGuestMapping Guest, PlatformRegionMapping Parent) platform) || platform != expectedPlatform)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original platform mapping cleanup owner or tuple changed.");
            KernelResult consumed =closureConsumer?.Invoke()??KernelResult.Ok(); // Trusted local invocation consumption, no callback/effect.
            if(!consumed.IsSuccess)return consumed;
            record.PlatformMappings.Remove(id);
            record.Mappings.Remove(id);
            record.OriginalMappings.Remove(id);
            record.StagedMappings.Remove(id);
            record.MappingSources.Remove(id);
            record.LocalGuestLifetimes.Remove(id);
            return KernelResult.Ok();
        }
    }

    private static bool HasOriginalMapping(Record record, GuestRegionMapping mapping) =>
        record.OriginalMappings.TryGetValue(mapping.Mapping.MappingId, out GuestRegionMapping original) && original == mapping &&
        record.LocalGuestLifetimes.TryGetValue(mapping.Mapping.MappingId, out LocalGuestLifetime? lifetime) &&
        lifetime.Original && ReferenceEquals(lifetime.Domain, record) && lifetime.Mapping == mapping && lifetime.HasOriginalParent;

    // Caller holds the existing VDA gate. The index references the same logical owner.
    private bool HasCurrentOriginalRecord(Record record) =>
        ReferenceEquals(record.OriginalOwner, record) &&
        _records.TryGetValue(record.Handle.DomainId, out Record? current) && ReferenceEquals(current, record) &&
        _originalRecords.TryGetValue(record.Handle.DomainId, out Record? original) && ReferenceEquals(original, record);

    internal int QuarantineForPlatformBackendReset()
    {
        lock (_gate)
        {
            int count = 0;
            foreach (Record record in _originalRecords.Values)
            {
                if (record.State is VirtualDomainState.Closed or VirtualDomainState.Quarantined)
                    continue;

                record.State = VirtualDomainState.Quarantined;
                count++;
            }

            return count;
        }
    }

    internal Record[] ForOwner(ProcessHandle owner) { lock (_gate) return _originalRecords.Values.Where(x => x.Owner == owner).ToArray(); }

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
            return _originalRecords.Values.Where(x => x.Owner == parent.Owner && x.ParentDomain == parent.Handle).ToArray();
    }

    internal KernelResult<GuestRegionMapping> ResolveMapping(Record record, GuestRegionMappingHandle handle, bool allowLocalCleanup = false)
    {
        lock (_gate)
        {
            if (!record.Mappings.TryGetValue(handle.MappingId, out GuestRegionMapping mapping))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.GuestMappingNotFound, "Guest mapping was not found.");
            if (mapping.Mapping.Generation != handle.Generation)
                return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Guest mapping generation is stale.");
            if (record.StagedMappings.Contains(handle.MappingId) && !(allowLocalCleanup && record.ChildBinding is null))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformBindingActive, "Guest mapping has not been published.");
            if (!HasOriginalMapping(record, mapping))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.StaleGeneration, "Original guest mapping tuple changed.");
            if (record.ChildBinding is null && (!TryLocalGuestLifetime(record, mapping, out LocalGuestLifetime? lifetime) ||
                (!allowLocalCleanup && (!lifetime.RegionReserved || lifetime.RegionReleased || lifetime.ChargeConsumed))))
                return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformBindingDraining, "Original local guest lifetime is unavailable for use.");
            return KernelResult<GuestRegionMapping>.Ok(mapping);
        }
    }

    internal KernelResult PublishMapping(Record record, GuestRegionMapping mapping)
    {
        lock (_gate)
        {
            if (!record.Mappings.TryGetValue(mapping.Mapping.MappingId, out GuestRegionMapping exact) || exact != mapping || !HasOriginalMapping(record, mapping) ||
                !record.StagedMappings.Remove(mapping.Mapping.MappingId))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact staged guest mapping changed.");
            return KernelResult.Ok();
        }
    }

    internal KernelResult RetainGuestCleanupMapping(Record record, GuestRegionMappingHandle local,
        (PlatformGuestMapping Guest, PlatformRegionMapping Parent) platform)
    {
        lock (_gate)
        {
            if (!HasCurrentOriginalRecord(record) || !record.Mappings.TryGetValue(local.MappingId, out GuestRegionMapping mapping) ||
                mapping.Mapping != local || !HasOriginalMapping(record, mapping) || record.ChildBinding != platform.Guest.Child ||
                platform.Guest.ParentMapping != platform.Parent)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original staged guest cleanup owner changed.");
            if (record.PlatformMappings.TryGetValue(local.MappingId, out (PlatformGuestMapping Guest, PlatformRegionMapping Parent) current))
                return current == platform ? KernelResult.Ok() : KernelResult.Fail(KernelError.StaleGeneration, "Foreign guest cleanup correlation retained.");
            record.PlatformMappings.Add(local.MappingId, platform); // Cleanup only; StagedMappings is not published.
            return KernelResult.Ok();
        }
    }

    internal KernelResult CommitExecutableMappingAdmission(Record record, GuestRegionMapping expected,
        (PlatformGuestMapping Guest, PlatformRegionMapping Parent) expectedPlatform, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<GuestRegionMapping> mapping = ResolveMapping(record, expected.Mapping);
            if (!HasCurrentOriginalRecord(record) || !mapping.IsSuccess || mapping.Value != expected ||
                (expected.Access & GuestMemoryAccess.Execute) == 0 ||
                !record.PlatformMappings.TryGetValue(expected.Mapping.MappingId, out (PlatformGuestMapping Guest, PlatformRegionMapping Parent) platform) || platform != expectedPlatform)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original executable local mapping or platform correlation changed.");
            return commit(); // Trusted pure admission/publication mutation; no provider callback.
        }
    }

    internal KernelResult CommitInitializedLoadMapping(Record record, GuestRegionMapping expected,
        PlatformGuestMapping platform, (CapabilityId Memory, CapabilityId Region) capabilities, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            var mapping = ResolveMapping(record, expected.Mapping);
            if (!HasCurrentOriginalRecord(record) || !mapping.IsSuccess || mapping.Value != expected ||
                expected.Access != GuestMemoryAccess.Read ||
                !record.PlatformMappings.TryGetValue(expected.Mapping.MappingId, out var current) || current.Guest != platform ||
                current.Parent != platform.ParentMapping ||
                !record.MappingSources.TryGetValue(expected.Mapping.MappingId, out var source) || source != capabilities)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original read-only initialized-load mapping changed.");
            return commit();
        }
    }

    internal KernelResult CommitStagedGuestInvocation(Record record,GuestRegionMapping expected,Func<KernelResult> commit)
    {
        lock(_gate)
        {
            if(!HasCurrentOriginalRecord(record) || record.ChildBinding is null ||
                record.State is not (VirtualDomainState.Configured or VirtualDomainState.Parked) ||
                !record.Mappings.TryGetValue(expected.Mapping.MappingId,out GuestRegionMapping mapping) || mapping!=expected ||
                !HasOriginalMapping(record,mapping) || !record.StagedMappings.Contains(expected.Mapping.MappingId))
                return KernelResult.Fail(KernelError.StaleGeneration,"Original staged guest mapping invocation source changed.");
            return commit(); // Trusted local invocation correlation; no external callback.
        }
    }

    internal KernelResult CommitExecutableArtifactAdmission(Record record, PlatformExecutableArtifactBinding artifact,
        GuestRegionMapping expected, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            if (record.ExecutableArtifact != artifact || record.ExecutableMapping != expected.Mapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original executable local mapping association changed.");
            return CommitExecutableMappingAdmission(record, expected,
                (artifact.GuestMapping, artifact.GuestMapping.ParentMapping), commit);
        }
    }

    internal KernelResult<(CapabilityId Memory, CapabilityId Region)> ResolveMappingSource(Record record,
        GuestRegionMappingHandle handle)
    {
        lock (_gate)
        {
            KernelResult<GuestRegionMapping> mapping = ResolveMapping(record, handle);
            if (!mapping.IsSuccess) return KernelResult<(CapabilityId, CapabilityId)>.Fail(mapping.Error, mapping.Message!);
            return record.MappingSources.TryGetValue(handle.MappingId, out (CapabilityId Memory, CapabilityId Region) source)
                ? KernelResult<(CapabilityId, CapabilityId)>.Ok(source)
                : KernelResult<(CapabilityId, CapabilityId)>.Fail(KernelError.StaleGeneration, "Exact mapping source correlation is absent.");
        }
    }
}
