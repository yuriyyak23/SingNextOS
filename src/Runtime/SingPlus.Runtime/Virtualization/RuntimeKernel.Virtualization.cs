using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly VirtualDomainAuthority _virtualDomains = new();

    public KernelResult<VirtualDomainAuthoritySet> CreateVirtualDomain(ProcessHandle owner, CapabilityId createCapability, VirtualDomainProfile profile)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(process.Error, process.Message!);
        var capability = ValidateCapability(owner, createCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Virtualization || capability.Value.ResourceId != VirtualizationResourceIds.Create)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize virtual-domain creation.");
        if (profile.VirtualProcessorCount <= 0 || profile.VirtualProcessorCount > 256 || profile.MaximumGuestMemoryBytes <= 0)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformDenied, "Virtual-domain profile is outside local bounds.");

        var ownerProcess = process.Value!;
        PlatformDomainIdentity identity = PlatformIdentity(ownerProcess);
        VirtualDomainAuthority.Record record;
        PlatformFeatureDescriptor childFeature = PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.ChildDomainLifecycle);
        // Phase5-03 is a lifecycle RuntimeAdmission contour only. An Executable claim must not
        // reuse the entryless child path: it requires the separate Phase5-04 artifact-admission
        // contract before Start can have executable semantics.
        if (childFeature.ContractVersion == PlatformChildDomainContract.ContractVersion &&
            childFeature.Availability is PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable)
        {
            var parent = PlatformAuthority.BindDomain(identity);
            if (!parent.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(parent.Error, parent.Message!);
            PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle |
                PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory |
                PlatformChildAuthorityClass.Events | PlatformChildAuthorityClass.Traps;
            if (PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.BoundedVirtualIo).Availability ==
                PlatformFeatureAvailability.Executable) authority |= PlatformChildAuthorityClass.Io;
            var intent = new PlatformChildDomainIntent(new(profile.VirtualProcessorCount, profile.MaximumGuestMemoryBytes),
                new(authority, authority));
            var child = PlatformAuthority.CreateChildDomain(parent.Value!, identity, intent);
            if (!child.IsSuccess)
            {
                _ = PlatformAuthority.RevokeDomain(parent.Value!, identity);
                return KernelResult<VirtualDomainAuthoritySet>.Fail(child.Error, child.Message!);
            }
            record = _virtualDomains.AddChild(owner, parent.Value!, child.Value!, profile, [], authority);
        }
        else
        {
            var platform = PlatformAuthority.CreateVirtualDomain(identity,
                new PlatformVirtualDomainProfile(profile.VirtualProcessorCount, profile.MaximumGuestMemoryBytes));
            if (!platform.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(platform.Error, platform.Message!);
            record = _virtualDomains.AddModel(owner, platform.Value!, profile, []);
        }
        var configure = MintCapability(ownerProcess.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Configure).Value!.CapabilityId;
        var memory = MintCapability(ownerProcess.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Memory(record.Handle.DomainId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var execute = MintCapability(ownerProcess.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Execute).Value!.CapabilityId;
        var events = MintCapability(ownerProcess.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Events(record.Handle.DomainId), CapabilityRights.Signal).Value!.CapabilityId;
        var traps = MintCapability(ownerProcess.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Traps(record.Handle.DomainId), CapabilityRights.Read).Value!.CapabilityId;
        record.Capabilities = [configure, memory, execute, events, traps];
        return KernelResult<VirtualDomainAuthoritySet>.Ok(new VirtualDomainAuthoritySet(record.Handle, record.AddressSpace, configure, memory, execute, events, traps));
    }

    public KernelResult<VirtualDomainAuthoritySet> CreateNestedVirtualDomain(
        ProcessHandle owner, VirtualDomainHandle parentDomain, CapabilityId parentCapability,
        NestedVirtualDomainRequestProfile requested)
    {
        var parent = ResolveVirtualCapability(owner, parentDomain, parentCapability,
            VirtualizationResourceIds.Domain(parentDomain.DomainId), CapabilityRights.Configure);
        if (!parent.IsSuccess)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(parent.Error, parent.Message!);
        if (parent.Value!.State is not VirtualDomainState.Configured and not VirtualDomainState.Parked)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.InvalidTransition,
                "Nested domains may be admitted only while the exact parent is configured or parked.");
        if (parent.Value.ChildBinding is not { } parentChild || parent.Value.ParentBinding is null)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformUnsupported,
                "The exact parent has no provider child authority for nesting.");

        var nestedFeature = PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.NestedDomains);
        if (nestedFeature.ContractVersion < PlatformNestedDomainContract.ContractVersion ||
            nestedFeature.Availability is not (PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable))
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformUnsupported,
                "Nested-domain provider admission is unavailable.");
        if (requested.Profile.VirtualProcessorCount <= 0 || requested.Profile.VirtualProcessorCount > 256 ||
            requested.Profile.MaximumGuestMemoryBytes <= 0 ||
            requested.Profile.MaximumGuestMemoryBytes > parent.Value.Profile.MaximumGuestMemoryBytes)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformDenied,
                "Nested-domain profile exceeds the exact parent bounds.");

        PlatformChildAuthorityClass authority = ToPlatformAuthority(requested.Authority);
        if ((authority & PlatformChildAuthorityClass.Lifecycle) == 0 ||
            (authority & parent.Value.Authority) != authority)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformDenied,
                "Nested-domain authority must include lifecycle and remain an exact parent subset.");

        var intent = new PlatformChildDomainIntent(
            new(requested.Profile.VirtualProcessorCount, requested.Profile.MaximumGuestMemoryBytes),
            new(authority, authority));
        var nested = PlatformAuthority.CreateNestedChildDomain(parentChild, owner, intent);
        if (!nested.IsSuccess)
        {
            if (RequiresVirtualDomainQuarantine(nested.Error)) parent.Value.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(nested.Error, nested.Message!);
        }

        var process = Processes.Resolve(owner);
        if (!process.IsSuccess)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(process.Error, process.Message!);
        var record = _virtualDomains.AddNested(owner, parent.Value, nested.Value!, requested.Profile, [], authority);
        var configure = MintCapability(process.Value!.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Configure).Value!.CapabilityId;
        var memory = authority.HasFlag(PlatformChildAuthorityClass.GuestMemory)
            ? MintCapability(process.Value.DomainId, owner, ResourceKind.Virtualization,
                VirtualizationResourceIds.Memory(record.Handle.DomainId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId
            : default;
        var execute = authority.HasFlag(PlatformChildAuthorityClass.Execution)
            ? MintCapability(process.Value.DomainId, owner, ResourceKind.Virtualization,
                VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Execute).Value!.CapabilityId
            : default;
        var events = authority.HasFlag(PlatformChildAuthorityClass.Events)
            ? MintCapability(process.Value.DomainId, owner, ResourceKind.Virtualization,
                VirtualizationResourceIds.Events(record.Handle.DomainId), CapabilityRights.Signal).Value!.CapabilityId
            : default;
        var traps = authority.HasFlag(PlatformChildAuthorityClass.Traps)
            ? MintCapability(process.Value.DomainId, owner, ResourceKind.Virtualization,
                VirtualizationResourceIds.Traps(record.Handle.DomainId), CapabilityRights.Read).Value!.CapabilityId
            : default;
        record.Capabilities = new[] { configure, memory, execute, events, traps }
            .Where(static capability => capability.Value != 0).ToArray();
        return KernelResult<VirtualDomainAuthoritySet>.Ok(new(record.Handle, record.AddressSpace,
            configure, memory, execute, events, traps));
    }

    public KernelResult ConfigureVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId configureCapability)
    {
        var record = ResolveVirtualCapability(owner, domain, configureCapability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Created) return KernelResult.Fail(KernelError.InvalidTransition, "Virtual domain can only be configured from Created.");
        if (record.Value.ModelBinding is { } model)
        {
            var platform = PlatformAuthority.TransitionVirtualDomain(model, PlatformVirtualDomainTransition.Configure);
            if (!platform.IsSuccess) return platform;
        }
        record.Value.State = VirtualDomainState.Configured;
        return KernelResult.Ok();
    }

    public KernelResult<GuestRegionMapping> MapGuestRegion(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, CapabilityId regionCapability, RegionHandle region, GuestAddressRange range, GuestMemoryAccess access)
    {
        var record = ResolveVirtualCapability(owner, domain, memoryCapability, VirtualizationResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(record.Error, record.Message!);
        if (record.Value!.State is not VirtualDomainState.Configured and not VirtualDomainState.Parked)
            return KernelResult<GuestRegionMapping>.Fail(KernelError.InvalidTransition, "Guest memory can only be mapped while configured or parked.");
        const GuestMemoryAccess supportedAccess = GuestMemoryAccess.Read | GuestMemoryAccess.Write | GuestMemoryAccess.Execute;
        if (range.ByteLength <= 0 || range.GuestAddress > ulong.MaxValue - (ulong)range.ByteLength || range.GuestAddress + (ulong)range.ByteLength > (ulong)record.Value.Profile.MaximumGuestMemoryBytes || access == GuestMemoryAccess.None || (access & ~supportedAccess) != 0)
            return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformDenied, "Guest range or access is invalid.");
        var rangeEnd = range.GuestAddress + (ulong)range.ByteLength;
        if (record.Value.Mappings.Values.Any(existing =>
                range.GuestAddress < existing.GuestRange.GuestAddress + (ulong)existing.GuestRange.ByteLength &&
                existing.GuestRange.GuestAddress < rangeEnd))
            return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformDenied, "Guest range overlaps an existing exact mapping.");

        CapabilityRights requiredRegionRights = CapabilityRights.Map;
        if ((access & GuestMemoryAccess.Read) != 0) requiredRegionRights |= CapabilityRights.Read;
        if ((access & GuestMemoryAccess.Write) != 0) requiredRegionRights |= CapabilityRights.Write;
        if ((access & GuestMemoryAccess.Execute) != 0) requiredRegionRights |= CapabilityRights.Execute;
        var regionAuthority = ValidateCapability(owner, regionCapability, requiredRegionRights);
        if (!regionAuthority.IsSuccess || regionAuthority.Value!.ResourceKind != ResourceKind.MemoryRegion || regionAuthority.Value.ResourceId != CapabilityResourceIds.MemoryRegion(region.RegionId))
            return KernelResult<GuestRegionMapping>.Fail(regionAuthority.IsSuccess ? KernelError.WrongCapabilityResource : regionAuthority.Error, regionAuthority.Message ?? "Region mapping capability does not match the exact region and requested access.");
        var process = Processes.Resolve(owner).Value!;
        var reservation = Regions.ReservePlatformMapping(region, new RegionOwner(process.DomainId, owner.Generation));
        if (!reservation.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(reservation.Error, reservation.Message!);
        var local = _virtualDomains.AddMapping(record.Value, range, region, access);
        if (record.Value.ChildBinding is { } child && record.Value.ParentBinding is { } parent)
        {
            PlatformMemoryAccess parentAccess = PlatformMemoryAccess.None;
            if ((access & (GuestMemoryAccess.Read | GuestMemoryAccess.Execute)) != 0)
                parentAccess |= PlatformMemoryAccess.Read;
            if ((access & GuestMemoryAccess.Write) != 0)
                parentAccess |= PlatformMemoryAccess.Write;
            var parentMap = PlatformAuthority.MapOwnedRegion(parent, PlatformIdentity(process), regionCapability,
                new PlatformRegionIdentity(region, new(process.DomainId, owner.Generation), range.ByteLength), parentAccess);
            if (!parentMap.IsSuccess)
            {
                _ = _virtualDomains.RemoveMapping(record.Value, local.Mapping);
                _ = Regions.ReleasePlatformMappingReservation(region, new(process.DomainId, owner.Generation));
                return KernelResult<GuestRegionMapping>.Fail(parentMap.Error, parentMap.Message!);
            }
            var guestMap = PlatformAuthority.MapChildGuestRegion(child, parentMap.Value!,
                new(range.GuestAddress, range.ByteLength), ToPlatformAccess(access));
            if (!guestMap.IsSuccess)
            {
                var cleanup = CloseParentPlatformMapping(parentMap.Value!, PlatformIdentity(process));
                if (!cleanup.IsSuccess)
                {
                    record.Value.State = VirtualDomainState.Quarantined;
                    return KernelResult<GuestRegionMapping>.Fail(
                        KernelError.PlatformFaulted,
                        $"Child guest mapping was rejected and exact parent-mapping cleanup was not proven: {cleanup.Message}");
                }
                _ = _virtualDomains.RemoveMapping(record.Value, local.Mapping);
                return KernelResult<GuestRegionMapping>.Fail(guestMap.Error, guestMap.Message!);
            }
            record.Value.PlatformMappings.Add(local.Mapping.MappingId, (guestMap.Value!, parentMap.Value!));
        }
        return KernelResult<GuestRegionMapping>.Ok(local);
    }

    public KernelResult CloseGuestRegionMapping(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, GuestRegionMappingHandle mapping)
    {
        var record = ResolveVirtualCapability(owner, domain, memoryCapability, VirtualizationResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State is VirtualDomainState.Quarantined or VirtualDomainState.Faulted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "A quarantined virtual-domain mapping remains pinned and cannot be locally reclaimed.");

        var exactMapping = _virtualDomains.ResolveMapping(record.Value, mapping);
        if (!exactMapping.IsSuccess)
            return KernelResult.Fail(exactMapping.Error, exactMapping.Message!);

        bool childPlatformMapping = record.Value.PlatformMappings.ContainsKey(mapping.MappingId);
        if (childPlatformMapping)
        {
            var platformClose = CloseChildPlatformMapping(record.Value, owner, mapping.MappingId);
            if (!platformClose.IsSuccess)
            {
                record.Value.State = VirtualDomainState.Quarantined;
                return platformClose;
            }
        }

        var removed = _virtualDomains.RemoveMapping(record.Value, mapping);
        if (!removed.IsSuccess) return KernelResult.Fail(removed.Error, removed.Message!);
        if (childPlatformMapping)
            return KernelResult.Ok();
        return Regions.ReleasePlatformMappingReservation(removed.Value!.Region, new RegionOwner(Processes.Resolve(owner).Value!.DomainId, owner.Generation));
    }

    public KernelResult BindVirtualDomainExecutable(
        ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability,
        GuestRegionMappingHandle mapping, ReadOnlyMemory<byte> immutablePackage, int maximumExecutionSteps)
    {
        var record = ResolveVirtualCapability(owner, domain, executeCapability,
            VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Configured || record.Value.ChildBinding is not { } child)
            return KernelResult.Fail(KernelError.InvalidTransition, "Executable artifacts require a configured provider child domain.");
        if (record.Value.ExecutableArtifact is not null)
            return KernelResult.Fail(KernelError.PlatformDenied, "An executable artifact is already bound.");
        if (!record.Value.Mappings.TryGetValue(mapping.MappingId, out var local) || local.Mapping != mapping ||
            (local.Access & GuestMemoryAccess.Execute) == 0 ||
            !record.Value.PlatformMappings.TryGetValue(mapping.MappingId, out var platform))
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact executable guest mapping is absent, stale, or lacks execute access.");
        var bound = PlatformAuthority.BindChildExecutableArtifact(child, platform.Guest,
            immutablePackage, maximumExecutionSteps);
        if (!bound.IsSuccess) return KernelResult.Fail(bound.Error, bound.Message!);
        record.Value.ExecutableArtifact = bound.Value;
        return KernelResult.Ok();
    }

    public KernelResult StartVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability)
    {
        var record = ResolveVirtualCapability(owner, domain, executeCapability,
            VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Configured)
            return KernelResult.Fail(KernelError.InvalidTransition, $"Virtual domain cannot start from {record.Value.State}.");
        if (record.Value.ChildBinding is not null &&
            PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.ChildExecutableArtifact).Availability ==
                PlatformFeatureAvailability.Executable)
        {
            if (record.Value.ExecutableArtifact is not { } artifact)
                return KernelResult.Fail(KernelError.PlatformDenied, "Executable child Start requires exact artifact admission.");
            var started = PlatformAuthority.StartChildExecutableArtifact(artifact);
            if (!started.IsSuccess) { record.Value.State = VirtualDomainState.Quarantined; return started; }
            record.Value.State = VirtualDomainState.Running;
            return KernelResult.Ok();
        }
        return TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Configured,
            VirtualDomainState.Running, PlatformVirtualDomainTransition.Start);
    }
    public KernelResult ParkVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability) => TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Running, VirtualDomainState.Parked, PlatformVirtualDomainTransition.Park);
    public KernelResult ResumeVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability) => TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Parked, VirtualDomainState.Running, PlatformVirtualDomainTransition.Resume);

    public KernelResult InjectVirtualEvent(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId eventCapability, KernelEventEndpoint endpoint)
    {
        var record = ResolveVirtualCapability(owner, domain, eventCapability, VirtualizationResourceIds.Events(domain.DomainId), CapabilityRights.Signal);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State is VirtualDomainState.Draining or VirtualDomainState.Closed or VirtualDomainState.Faulted or VirtualDomainState.Quarantined)
            return KernelResult.Fail(KernelError.InvalidTransition, "Virtual domain does not accept event injection while terminal or draining.");
        if (record.Value.ChildBinding is { } child)
        {
            var delivered = PlatformAuthority.InjectChildEvent(child, PlatformVirtualEventClass.ExternalSignal,
                VirtualizationResourceIds.Events(domain.DomainId));
            if (!delivered.IsSuccess) { record.Value.State = VirtualDomainState.Quarantined; return KernelResult.Fail(delivered.Error, delivered.Message!); }
        }
        var staged = _kernelEvents.Stage(owner, endpoint, KernelEventClass.ExternalSignal, VirtualizationResourceIds.Events(domain.DomainId));
        if (!staged.IsSuccess) return KernelResult.Fail(staged.Error, staged.Message!);
        var committed = _kernelEvents.CommitExact(owner, staged.Value!);
        return committed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(committed.Error, committed.Message!);
    }

    public KernelResult<VirtualTrapObservation> ObserveVirtualTrap(
        ProcessHandle owner, VirtualDomainHandle domain, CapabilityId trapCapability)
    {
        var record = ResolveVirtualCapability(owner, domain, trapCapability,
            VirtualizationResourceIds.Traps(domain.DomainId), CapabilityRights.Read);
        if (!record.IsSuccess)
            return KernelResult<VirtualTrapObservation>.Fail(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Running)
            return KernelResult<VirtualTrapObservation>.Fail(KernelError.InvalidTransition,
                "Virtual traps may be observed only for the exact running domain.");
        if (record.Value.ChildBinding is not { } child)
            return KernelResult<VirtualTrapObservation>.Fail(KernelError.PlatformUnsupported,
                "The virtual domain has no neutral trap-delivery provider.");
        var observed = PlatformAuthority.ObserveChildTrap(child);
        if (!observed.IsSuccess)
        {
            if (RequiresVirtualDomainQuarantine(observed.Error))
                record.Value.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualTrapObservation>.Fail(observed.Error, observed.Message!);
        }
        return KernelResult<VirtualTrapObservation>.Ok(new(domain, observed.Value!.Sequence,
            (VirtualTrapKind)(int)observed.Value.Kind));
    }

    public KernelResult DestroyVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId configureCapability)
    {
        var record = ResolveVirtualCapability(owner, domain, configureCapability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State is VirtualDomainState.Quarantined or VirtualDomainState.Faulted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "A faulted or quarantined virtual domain remains pinned until externally proven closure exists.");
        if (record.Value.State != VirtualDomainState.Draining)
        {
            if (record.Value.State is not VirtualDomainState.Created and not VirtualDomainState.Configured and
                not VirtualDomainState.Running and not VirtualDomainState.Parked)
                return KernelResult.Fail(KernelError.InvalidTransition, $"Virtual domain cannot begin destroy from {record.Value.State}.");
            record.Value.State = VirtualDomainState.Draining;
        }
        var nestedClose = CloseNestedDomainsForParent(record.Value, owner);
        if (!nestedClose.IsSuccess) return nestedClose;
        if (record.Value.Mappings.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingDraining, "Guest mappings must close before virtual-domain authority.");
        KernelResult closed = ClosePlatformVirtualDomain(record.Value, owner);
        if (!closed.IsSuccess) { record.Value.State = VirtualDomainState.Quarantined; return closed; }
        record.Value.State = VirtualDomainState.Closed;
        foreach (var capability in record.Value.Capabilities) _ = RevokeCapability(capability);
        return KernelResult.Ok();
    }

    public KernelResult<VirtualDomainState> QueryVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain)
    {
        var record = _virtualDomains.Resolve(owner, domain);
        return record.IsSuccess ? KernelResult<VirtualDomainState>.Ok(record.Value!.State) : KernelResult<VirtualDomainState>.Fail(record.Error, record.Message!);
    }

    private KernelResult TransitionVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId capability, VirtualDomainState from, VirtualDomainState to, PlatformVirtualDomainTransition transition)
    {
        var record = ResolveVirtualCapability(owner, domain, capability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != from) return KernelResult.Fail(KernelError.InvalidTransition, $"Virtual domain cannot transition from {record.Value.State} to {to}.");
        KernelResult platform = record.Value.ChildBinding is { } child
            ? PlatformAuthority.TransitionChildDomain(child, transition switch
            {
                PlatformVirtualDomainTransition.Start => PlatformChildDomainTransition.Start,
                PlatformVirtualDomainTransition.Park => PlatformChildDomainTransition.Park,
                PlatformVirtualDomainTransition.Resume => PlatformChildDomainTransition.Resume,
                _ => PlatformChildDomainTransition.BeginDrain,
            })
            : PlatformAuthority.TransitionVirtualDomain(record.Value.ModelBinding!.Value, transition);
        if (!platform.IsSuccess) return platform;
        record.Value.State = to;
        return KernelResult.Ok();
    }

    private KernelResult<VirtualDomainAuthority.Record> ResolveVirtualCapability(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId capabilityId, string resourceId, CapabilityRights rights)
    {
        var record = _virtualDomains.Resolve(owner, domain);
        if (!record.IsSuccess) return record;
        var capability = ValidateCapability(owner, capabilityId, rights);
        if (!capability.IsSuccess) return KernelResult<VirtualDomainAuthority.Record>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Virtualization || capability.Value.ResourceId != resourceId)
            return KernelResult<VirtualDomainAuthority.Record>.Fail(KernelError.WrongCapabilityResource, "Capability does not match the exact virtual-domain resource.");
        return record;
    }

    private KernelResult CloseVirtualDomainsForProcess(ProcessHandle owner)
    {
        foreach (var record in _virtualDomains.ForOwner(owner)
                     .Where(static domain => domain.ParentDomain is null)
                     .OrderBy(static domain => domain.Handle.DomainId.Value))
        {
            if (record.State == VirtualDomainState.Quarantined)
                return KernelResult.Fail(KernelError.PlatformFaulted, "A virtual domain is quarantined; process authority remains pinned.");

            record.State = VirtualDomainState.Draining;
            var nestedClose = CloseNestedDomainsForParent(record, owner);
            if (!nestedClose.IsSuccess) { record.State = VirtualDomainState.Quarantined; return nestedClose; }
            if (record.ModelBinding is not null)
            {
                var modelClosed = ClosePlatformVirtualDomain(record, owner);
                if (!modelClosed.IsSuccess) { record.State = VirtualDomainState.Quarantined; return modelClosed; }
            }
            foreach (var mapping in record.Mappings.Values.OrderBy(static mapping => mapping.Mapping.MappingId.Value).ToArray())
            {
                bool childPlatformMapping = record.PlatformMappings.ContainsKey(mapping.Mapping.MappingId);
                if (childPlatformMapping)
                {
                    var platformClose = CloseChildPlatformMapping(record, owner, mapping.Mapping.MappingId);
                    if (!platformClose.IsSuccess) { record.State = VirtualDomainState.Quarantined; return platformClose; }
                }
                var removed = _virtualDomains.RemoveMapping(record, mapping.Mapping);
                if (!removed.IsSuccess) { record.State = VirtualDomainState.Quarantined; return KernelResult.Fail(removed.Error, removed.Message!); }
                if (!childPlatformMapping)
                {
                    var released = Regions.ReleasePlatformMappingReservation(mapping.Region, new RegionOwner(Processes.Resolve(owner).Value!.DomainId, owner.Generation));
                    if (!released.IsSuccess) { record.State = VirtualDomainState.Quarantined; return released; }
                }
            }
            if (record.ChildBinding is not null)
            {
                var childClosed = ClosePlatformVirtualDomain(record, owner);
                if (!childClosed.IsSuccess) { record.State = VirtualDomainState.Quarantined; return childClosed; }
            }
            record.State = VirtualDomainState.Closed;
        }

        return KernelResult.Ok();
    }

    private KernelResult CloseChildPlatformMapping(VirtualDomainAuthority.Record record, ProcessHandle owner, GuestRegionMappingId mappingId)
    {
        if (!record.PlatformMappings.TryGetValue(mappingId, out var platformMapping))
            return KernelResult.Ok();
        var unmap = PlatformAuthority.UnmapChildGuestRegion(platformMapping.Guest);
        if (!unmap.IsSuccess) return unmap;
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var parentClose = CloseParentPlatformMapping(platformMapping.Parent, PlatformIdentity(process.Value!));
        if (!parentClose.IsSuccess) return parentClose;
        record.PlatformMappings.Remove(mappingId);
        return KernelResult.Ok();
    }

    private KernelResult CloseParentPlatformMapping(PlatformRegionMapping mapping, PlatformDomainIdentity identity)
    {
        var revoke = PlatformAuthority.BeginRegionMappingRevocation(
            mapping,
            identity,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        if (!revoke.IsSuccess)
            return KernelResult.Fail(revoke.Error, revoke.Message!);
        return FinalizePlatformRegionMappingClosure(mapping, identity, revoke.Value!);
    }

    private KernelResult ClosePlatformVirtualDomain(VirtualDomainAuthority.Record record, ProcessHandle owner)
    {
        if (record.ChildBinding is { } child && record.ParentBinding is { } parent)
        {
            var draining = PlatformAuthority.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain);
            if (!draining.IsSuccess) return draining;
            var closed = PlatformAuthority.CloseChildDomain(child);
            if (!closed.IsSuccess) return closed;
            if (record.ParentDomain is not null)
                return KernelResult.Ok();
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            return PlatformAuthority.RevokeDomain(parent, PlatformIdentity(process.Value!));
        }
        var modelDrain = PlatformAuthority.TransitionVirtualDomain(record.ModelBinding!.Value, PlatformVirtualDomainTransition.BeginDrain);
        if (!modelDrain.IsSuccess) return modelDrain;
        return PlatformAuthority.RevokeVirtualDomain(record.ModelBinding.Value);
    }

    private static PlatformGuestMemoryAccess ToPlatformAccess(GuestMemoryAccess access) =>
        ((access & GuestMemoryAccess.Read) != 0 ? PlatformGuestMemoryAccess.Read : PlatformGuestMemoryAccess.None) |
        ((access & GuestMemoryAccess.Write) != 0 ? PlatformGuestMemoryAccess.Write : PlatformGuestMemoryAccess.None) |
        ((access & GuestMemoryAccess.Execute) != 0 ? PlatformGuestMemoryAccess.Execute : PlatformGuestMemoryAccess.None);

    private KernelResult CloseNestedDomainsForParent(VirtualDomainAuthority.Record parent, ProcessHandle owner)
    {
        foreach (var child in _virtualDomains.ChildrenOf(parent).OrderBy(static x => x.Handle.DomainId.Value))
        {
            if (child.State is VirtualDomainState.Quarantined or VirtualDomainState.Faulted)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "A nested domain is quarantined; parent authority remains pinned.");
            child.State = VirtualDomainState.Draining;
            var descendants = CloseNestedDomainsForParent(child, owner);
            if (!descendants.IsSuccess) return descendants;
            foreach (var mapping in child.Mappings.Values.OrderBy(static x => x.Mapping.MappingId.Value).ToArray())
            {
                bool platformMapping = child.PlatformMappings.ContainsKey(mapping.Mapping.MappingId);
                if (platformMapping)
                {
                    var close = CloseChildPlatformMapping(child, owner, mapping.Mapping.MappingId);
                    if (!close.IsSuccess) { child.State = VirtualDomainState.Quarantined; return close; }
                }
                var removed = _virtualDomains.RemoveMapping(child, mapping.Mapping);
                if (!removed.IsSuccess) return KernelResult.Fail(removed.Error, removed.Message!);
                if (!platformMapping)
                {
                    var process = Processes.Resolve(owner);
                    if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                    var release = Regions.ReleasePlatformMappingReservation(mapping.Region,
                        new RegionOwner(process.Value!.DomainId, owner.Generation));
                    if (!release.IsSuccess) return release;
                }
            }
            var closed = ClosePlatformVirtualDomain(child, owner);
            if (!closed.IsSuccess) { child.State = VirtualDomainState.Quarantined; return closed; }
            child.State = VirtualDomainState.Closed;
            foreach (var capability in child.Capabilities) _ = RevokeCapability(capability);
        }
        return KernelResult.Ok();
    }

    private static PlatformChildAuthorityClass ToPlatformAuthority(VirtualDomainAuthorityClass authority) =>
        (PlatformChildAuthorityClass)(int)authority;

    private static bool RequiresVirtualDomainQuarantine(KernelError error) => error is
        KernelError.PlatformFaulted or KernelError.PlatformBindingRevoked or KernelError.StaleGeneration or
        KernelError.WrongPlatformDomain;
}
