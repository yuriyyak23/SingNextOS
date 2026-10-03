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

        PlatformFeatureDescriptor childFeature = PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.ChildDomainLifecycle);
        // Phase5-03 is a lifecycle RuntimeAdmission contour only. An Executable claim must not
        // reuse the entryless child path: it requires the separate Phase5-04 artifact-admission
        // contract before Start can have executable semantics.
        if (childFeature.ContractVersion == PlatformChildDomainContract.ContractVersion &&
            childFeature.Availability is PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable)
        {
            return CreateChildVirtualDomain(owner, ownerProcess, createCapability, profile);
        }
        return CreateModelVirtualDomain(owner, ownerProcess, createCapability, profile);
    }

    private KernelResult<VirtualDomainAuthoritySet> CreateChildVirtualDomain(
        ProcessHandle owner, SingPlus.Sip.SingProcess process, CapabilityId source, VirtualDomainProfile profile)
    {
        var identity = PlatformIdentity(process);
        VirtualDomainAuthority.Record? record = null;
        KernelResult FreshSubject()
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleHandle, "Virtual creation subject changed.");
            return EnsureProcessAcceptsNewEffects(process);
        }
        KernelResult Admit(Func<KernelResult> begin)
        {
            lock (_platformMemoryUseGate)
            {
                var fresh = FreshSubject();
                return fresh.IsSuccess ? begin() : fresh;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, commit);

        lock (_platformMemoryUseGate)
        {
            var fresh = FreshSubject();
            if (!fresh.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(fresh.Error, fresh.Message!);
            if (_executionTransactions.Contains(owner) || !_pendingPlatformDomainBinds.Add(owner))
                return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformBindingActive, "Root/virtual creation is already pending.");
        }
        try
        {
            var parent = PlatformAuthority.BindDomain(identity, admissionConsumer: Admit, sourceCommitConsumer: SourceCommit,
                completionConsumer: (binding, commit) =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        // Receipt is retained for actual teardown even if local admission stopped.
                        TrackPlatformBinding(owner, binding, executionAttached: false);
                        var fresh = FreshSubject();
                        return fresh.IsSuccess ? commit(() => SourceCommit(KernelResult.Ok)) : fresh;
                    }
                });
            if (!parent.IsSuccess)
            {
                if (PlatformAuthority.TryGetQuarantinedDomainBinding(identity, out var cleanup))
                    TrackPlatformBinding(owner, cleanup, executionAttached: false);
                return KernelResult<VirtualDomainAuthoritySet>.Fail(parent.Error, parent.Message!);
            }
            var authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events | PlatformChildAuthorityClass.Traps;
            if (PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.BoundedVirtualIo).Availability == PlatformFeatureAvailability.Executable)
                authority |= PlatformChildAuthorityClass.Io;
            var intent = new PlatformChildDomainIntent(new(profile.VirtualProcessorCount, profile.MaximumGuestMemoryBytes), new(authority, authority));
            var child = PlatformAuthority.CreateChildDomain(parent.Value!, identity, intent, Admit, SourceCommit);
            if (!child.IsSuccess)
            {
                var closed = PlatformAuthority.RevokeDomain(parent.Value!, identity);
                if (closed.IsSuccess) ConsumeTrackedVirtualRootClosure(owner, parent.Value!);
                // Failed compensation keeps the exact root in the actual process consumer.
                return KernelResult<VirtualDomainAuthoritySet>.Fail(child.Error, child.Message!);
            }
            lock (_platformMemoryUseGate)
            {
                record = _virtualDomains.AddChild(owner, parent.Value!, child.Value!, profile, [], authority, authorityPublished: false);
                var fresh = FreshSubject();
                var permitted = fresh.IsSuccess ? SourceCommit(KernelResult.Ok) : fresh;
                if (!permitted.IsSuccess)
                {
                    record.State = VirtualDomainState.Quarantined;
                    return KernelResult<VirtualDomainAuthoritySet>.Fail(permitted.Error, permitted.Message!);
                }
            }

            return IssueVirtualDomainAuthority(owner, process, source, record,
                commit => PlatformAuthority.CommitActiveDomainConsumer(parent.Value!, identity, commit));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (record is not null) record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
                $"Virtual creation did not publish a complete result; known cleanup remains tracked: {exception.Message}");
        }
        finally { lock (_platformMemoryUseGate) _pendingPlatformDomainBinds.Remove(owner); }
    }

    private KernelResult<VirtualDomainAuthoritySet> CreateModelVirtualDomain(
        ProcessHandle owner, SingPlus.Sip.SingProcess process, CapabilityId source, VirtualDomainProfile profile)
    {
        VirtualDomainAuthority.Record? record = null;
        KernelResult Fresh()
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            return ReferenceEquals(current.Value, process) ? EnsureProcessAcceptsNewEffects(process) :
                KernelResult.Fail(KernelError.StaleHandle, "Model creation subject changed.");
        }
        KernelResult Admit(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var fresh = Fresh();
                return fresh.IsSuccess ? commit() : fresh;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, commit);
        lock (_platformMemoryUseGate)
        {
            var fresh = Fresh();
            if (!fresh.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(fresh.Error, fresh.Message!);
            if (_executionTransactions.Contains(owner) || !_pendingPlatformDomainBinds.Add(owner))
                return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformBindingActive, "Model/root creation is pending.");
        }
        try
        {
            var model = PlatformAuthority.CreateVirtualDomain(PlatformIdentity(process),
                new(profile.VirtualProcessorCount, profile.MaximumGuestMemoryBytes), Admit, SourceCommit,
                completionConsumer: (binding, commit) =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        record = _virtualDomains.AddModel(owner, binding, profile, [], authorityPublished: false);
                        var fresh = Fresh();
                        return fresh.IsSuccess ? commit(() => SourceCommit(KernelResult.Ok)) : fresh;
                    }
                });
            if (!model.IsSuccess)
            {
                if (record is not null) record.State = VirtualDomainState.Quarantined;
                return KernelResult<VirtualDomainAuthoritySet>.Fail(model.Error, model.Message!);
            }
            return IssueVirtualDomainAuthority(owner, process, source, record!,
                commit => PlatformAuthority.CommitModelDomainConsumer(model.Value!, commit));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (record is not null) record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
                $"Model creation did not publish authority; cleanup remains owned: {exception.Message}");
        }
        finally { lock (_platformMemoryUseGate) _pendingPlatformDomainBinds.Remove(owner); }
    }

    private KernelResult<VirtualDomainAuthoritySet> IssueVirtualDomainAuthority(
        ProcessHandle owner, SingPlus.Sip.SingProcess process, CapabilityId source,
        VirtualDomainAuthority.Record record, Func<Func<KernelResult>, KernelResult> bindingCommit,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null)
    {
        KernelResult<CapabilityDescriptorV1> CommitDerived(Func<KernelResult<CapabilityDescriptorV1>> commit)
        {
            var issued = KernelResult<CapabilityDescriptorV1>.Fail(KernelError.PlatformFaulted, "Derived source commit did not run.");
            var boundary = bindingCommit(() =>
            {
                if (sourceCommit is null)
                    issued = CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, commit);
                else
                {
                    var accepted = sourceCommit(() =>
                    {
                        issued = commit();
                        return issued.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(issued.Error, issued.Message!);
                    });
                    if (!accepted.IsSuccess) issued = KernelResult<CapabilityDescriptorV1>.Fail(accepted.Error, accepted.Message!);
                }
                return issued.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(issued.Error, issued.Message!);
            });
            return boundary.IsSuccess ? issued : KernelResult<CapabilityDescriptorV1>.Fail(boundary.Error, boundary.Message!);
        }
        var requests = new (string Resource, CapabilityRights Rights)[]
        {
            (VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Configure),
            (VirtualizationResourceIds.Memory(record.Handle.DomainId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write),
            (VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Execute),
            (VirtualizationResourceIds.Events(record.Handle.DomainId), CapabilityRights.Signal),
            (VirtualizationResourceIds.Traps(record.Handle.DomainId), CapabilityRights.Read)
        };
        var capabilities = new List<CapabilityId>();
        var returned = new CapabilityId[requests.Length];
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            var needed = index switch
            {
                1 => PlatformChildAuthorityClass.GuestMemory,
                2 => PlatformChildAuthorityClass.Execution,
                3 => PlatformChildAuthorityClass.Events,
                4 => PlatformChildAuthorityClass.Traps,
                _ => PlatformChildAuthorityClass.Lifecycle,
            };
            if (record.ParentDomain is not null && !record.Authority.HasFlag(needed)) continue;
            var minted = MintCapabilityCore(process.DomainId, owner, ResourceKind.Virtualization,
                request.Resource, request.Rights, sourceCommitConsumer: CommitDerived);
            if (!minted.IsSuccess)
            {
                record.State = VirtualDomainState.Quarantined;
                return KernelResult<VirtualDomainAuthoritySet>.Fail(minted.Error, minted.Message!);
            }
            returned[index] = minted.Value!.CapabilityId;
            capabilities.Add(minted.Value!.CapabilityId);
            record.Capabilities = capabilities.ToArray();
        }
        KernelResult published;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            var fresh = current.IsSuccess && ReferenceEquals(current.Value, process)
                ? EnsureProcessAcceptsNewEffects(process) :
                KernelResult.Fail(current.IsSuccess ? KernelError.StaleHandle : current.Error, "Virtual publication subject changed.");
            KernelResult CommitPublication() => _virtualDomains.CommitAuthorityPublication(record);
            published = fresh.IsSuccess ? bindingCommit(() => sourceCommit is null
                ? CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, CommitPublication)
                : sourceCommit(CommitPublication)) : fresh;
        }
        if (!published.IsSuccess)
        {
            record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(published.Error, published.Message!);
        }
        RecordTrace(owner, TraceEventKind.VirtualDomainLifecycle, null, "virtual-domain",
            record.Handle.DomainId.Value.ToString(), record.State.ToString(), "created");
        return KernelResult<VirtualDomainAuthoritySet>.Ok(new(record.Handle, record.AddressSpace,
            returned[0], returned[1], returned[2], returned[3], returned[4]));
    }

    private void ConsumeTrackedVirtualRootClosure(ProcessHandle owner, PlatformDomainBinding binding)
    {
        lock (_platformMemoryUseGate)
        {
            UntrackPlatformBinding(owner, binding);
            if (_processTeardowns.TryGetValue(owner, out var teardown) && teardown.DomainBinding == binding)
                teardown.PlatformDomainClosed = true;
        }
    }

    public KernelResult<VirtualDomainAuthoritySet> CreateNestedVirtualDomain(
        ProcessHandle owner, VirtualDomainHandle parentDomain, CapabilityId parentCapability,
        NestedVirtualDomainRequestProfile requested)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: parentDomain);
        if (!composedUse.Result.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var parent = ResolveVirtualCapability(owner, parentDomain, parentCapability,
            VirtualizationResourceIds.Domain(parentDomain.DomainId), CapabilityRights.Configure);
        if (!parent.IsSuccess)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(parent.Error, parent.Message!);
        if (composedUse.HasBindings)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformUnsupported, "Nested secure execution is not admitted by Phase 00.");
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

        const VirtualDomainAuthorityClass knownAuthority = VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution |
            VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Events | VirtualDomainAuthorityClass.Traps | VirtualDomainAuthorityClass.Io;
        if ((requested.Authority & ~knownAuthority) != 0)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformDenied, "Unknown mandatory nested authority.");
        PlatformChildAuthorityClass authority = ToPlatformAuthority(requested.Authority);
        if ((authority & PlatformChildAuthorityClass.Lifecycle) == 0 ||
            (authority & parent.Value.Authority) != authority)
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformDenied,
                "Nested-domain authority must include lifecycle and remain an exact parent subset.");

        var intent = new PlatformChildDomainIntent(
            new(requested.Profile.VirtualProcessorCount, requested.Profile.MaximumGuestMemoryBytes),
            new(authority, authority));
        SingPlus.Sip.SingProcess process;
        var capturedParent = parent.Value;
        var parentState = capturedParent.State;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingPlatformDomainBinds.Add(owner))
                return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformBindingActive, "Virtual creation is pending for this owner.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess || !ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Nested process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, parentDomain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, capturedParent) || capturedParent.State != parentState ||
                    capturedParent.ChildBinding != parentChild)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Exact nested local parent changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(capturedParent);
                return lineage.IsSuccess ? commit() : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(parentCapability, process.DomainId, owner.Generation,
                CapabilityRights.Configure, VirtualizationResourceIds.Domain(parentDomain.DomainId), () => _virtualDomains.CommitEffectAdmission(capturedParent, parentState, commit));
        try
        {
            var nested = PlatformAuthority.CreateNestedChildDomain(parentChild, owner, intent, Fresh, SourceCommit);
            if (!nested.IsSuccess)
            {
                if (RequiresVirtualDomainQuarantine(nested.Error)) capturedParent.State = VirtualDomainState.Quarantined;
                return KernelResult<VirtualDomainAuthoritySet>.Fail(nested.Error, nested.Message!);
            }
            VirtualDomainAuthority.Record record;
            lock (_platformMemoryUseGate)
                record = _virtualDomains.AddNested(owner, capturedParent, nested.Value!, requested.Profile, [], authority, authorityPublished: false);
            KernelResult BindingCommit(Func<KernelResult> commit) =>
                Fresh(() => PlatformAuthority.CommitActiveChildDomainConsumer(nested.Value!, commit));
            return IssueVirtualDomainAuthority(owner, process, parentCapability, record, BindingCommit, SourceCommit);
        }
        finally { lock (_platformMemoryUseGate) _pendingPlatformDomainBinds.Remove(owner); }
    }

    public KernelResult ConfigureVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId configureCapability)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, configureCapability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Created) return KernelResult.Fail(KernelError.InvalidTransition, "Virtual domain can only be configured from Created.");
        if (record.Value.ModelBinding is { } model)
        {
            return TransitionModelVirtualDomain(owner, domain, configureCapability, record.Value, model,
                VirtualDomainState.Created, VirtualDomainState.Configured, PlatformVirtualDomainTransition.Configure, CapabilityRights.Configure);
        }
        if (record.Value.ChildBinding is not { } child)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Virtual domain has no child binding.");
        return TransitionChildVirtualDomain(owner, domain, configureCapability, record.Value, child,
            VirtualDomainState.Created, VirtualDomainState.Configured, null);
    }

    public KernelResult<GuestRegionMapping> MapGuestRegion(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, CapabilityId regionCapability, RegionHandle region, GuestAddressRange range, GuestMemoryAccess access)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(composedUse.Result.Error, composedUse.Result.Message!);
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
                new PlatformRegionIdentity(region, new(process.DomainId, owner.Generation), range.ByteLength),
                parentAccess, out var retainParentReservation);
            if (!parentMap.IsSuccess)
            {
                _ = _virtualDomains.RemoveMapping(record.Value, local.Mapping);
                if (!retainParentReservation)
                    _ = Regions.ReleasePlatformMappingReservation(region, new(process.DomainId, owner.Generation));
                else
                    record.Value.State = VirtualDomainState.Quarantined;
                return KernelResult<GuestRegionMapping>.Fail(parentMap.Error, parentMap.Message!);
            }
            var composedMap = RevalidateComposedDomain(owner, virtualDomain: domain);
            if (!composedMap.IsSuccess)
            {
                var cleanup = CloseParentPlatformMapping(parentMap.Value!, PlatformIdentity(process));
                if (!cleanup.IsSuccess) record.Value.State = VirtualDomainState.Quarantined;
                else _ = _virtualDomains.RemoveMapping(record.Value, local.Mapping);
                return KernelResult<GuestRegionMapping>.Fail(composedMap.Error, composedMap.Message!);
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
        var composedPublication = RevalidateComposedDomain(owner, virtualDomain: domain);
        return composedPublication.IsSuccess ? KernelResult<GuestRegionMapping>.Ok(local) :
            KernelResult<GuestRegionMapping>.Fail(composedPublication.Error, composedPublication.Message!);
    }

    public KernelResult CloseGuestRegionMapping(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, GuestRegionMappingHandle mapping)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain, allowDraining: true);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, memoryCapability, VirtualizationResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State is VirtualDomainState.Quarantined or VirtualDomainState.Faulted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "A quarantined virtual-domain mapping remains pinned and cannot be locally reclaimed.");

        var exactMapping = _virtualDomains.ResolveMapping(record.Value, mapping);
        if (!exactMapping.IsSuccess)
            return KernelResult.Fail(exactMapping.Error, exactMapping.Message!);
        if (HasVirtualComputePin(mapping))
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Guest mapping remains pinned by virtualized provider work.");

        var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping);
        if (!secureClose.IsSuccess) return secureClose;

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
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
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
        var captured = record.Value;
        return UseVirtualExecutableSource(owner, domain, executeCapability, captured, child, (fresh, sourceCommit) =>
        {
            KernelResult Admission(Func<KernelResult> commit) => fresh(() =>
                captured.ExecutableArtifact is null &&
                captured.PlatformMappings.TryGetValue(mapping.MappingId, out var current) && current == platform &&
                captured.Mappings.TryGetValue(mapping.MappingId, out var localCurrent) && localCurrent.Mapping == mapping
                    ? commit() : KernelResult.Fail(KernelError.StaleGeneration, "Executable local mapping/artifact changed."));
            var bound = PlatformAuthority.BindChildExecutableArtifact(child, platform.Guest, immutablePackage, maximumExecutionSteps,
                Admission, sourceCommit, (binding, commit) =>
                {
                    // Retain known receipt for actual cleanup even after local stop/revoke.
                    KernelResult retained;
                    lock (_platformMemoryUseGate) retained = _virtualDomains.RetainExecutableArtifact(captured, binding);
                    if (!retained.IsSuccess) return retained;
                    return fresh(() => commit(() => sourceCommit(() => KernelResult.Ok())));
                });
            return bound.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(bound.Error, bound.Message!);
        });
    }

    public KernelResult StartVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
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
            var captured = record.Value;
            return UseVirtualExecutableSource(owner, domain, executeCapability, captured, captured.ChildBinding.Value,
                (fresh, sourceCommit) => PlatformAuthority.StartChildExecutableArtifact(artifact,
                    admissionConsumer: commit => fresh(() => captured.ExecutableArtifact == artifact
                        ? commit() : KernelResult.Fail(KernelError.StaleGeneration, "Executable local artifact changed.")),
                    sourceCommitConsumer: sourceCommit,
                    completionConsumer: commit => fresh(() => commit(() => sourceCommit(() =>
                        _virtualDomains.CommitStateTransition(captured, VirtualDomainState.Configured, VirtualDomainState.Running))))));
        }
        return TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Configured,
            VirtualDomainState.Running, PlatformVirtualDomainTransition.Start);
    }
    public KernelResult ParkVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability) => TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Running, VirtualDomainState.Parked, PlatformVirtualDomainTransition.Park);
    public KernelResult ResumeVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability) => TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Parked, VirtualDomainState.Running, PlatformVirtualDomainTransition.Resume);

    public KernelResult InjectVirtualEvent(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId eventCapability, KernelEventEndpoint endpoint)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
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
        var composedPublication = RevalidateComposedDomain(owner, virtualDomain: domain);
        if (!composedPublication.IsSuccess)
        {
            _ = _kernelEvents.RollbackExact(owner, staged.Value!);
            return composedPublication;
        }
        var committed = _kernelEvents.CommitExact(owner, staged.Value!);
        return committed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(committed.Error, committed.Message!);
    }

    public KernelResult<VirtualTrapObservation> ObserveVirtualTrap(
        ProcessHandle owner, VirtualDomainHandle domain, CapabilityId trapCapability)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult<VirtualTrapObservation>.Fail(composedUse.Result.Error, composedUse.Result.Message!);
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
        KernelResult<PlatformVirtualTrapEvidence> observed = default;
        var publication = UseVirtualTrapSource(owner, domain, trapCapability, record.Value, child, (fresh, sourceCommit) =>
        {
            observed = PlatformAuthority.ObserveChildTrap(child, fresh, sourceCommit,
                completionConsumer: commit =>
                {
                    // Composition may consult provider state; keep it outside source and owner locks.
                    var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
                    return composed.IsSuccess ? fresh(() => commit(() => sourceCommit(() => KernelResult.Ok()))) : composed;
                });
            return observed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(observed.Error, observed.Message!);
        });
        if (!publication.IsSuccess)
        {
            if (RequiresVirtualDomainQuarantine(publication.Error))
                record.Value.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualTrapObservation>.Fail(publication.Error, publication.Message!);
        }
        return KernelResult<VirtualTrapObservation>.Ok(new(domain, observed.Value!.Sequence,
            (VirtualTrapKind)(int)observed.Value.Kind));
    }

    public KernelResult DestroyVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId configureCapability)
    {
        var record = ResolveVirtualCapability(owner, domain, configureCapability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        lock (_secureExecutionGate)
            if (record.Value!.State is not (VirtualDomainState.Quarantined or VirtualDomainState.Faulted or VirtualDomainState.Closed))
                record.Value.State = VirtualDomainState.Draining;
        var virtualIoDrain = CloseVirtualIoForDomain(owner, domain);
        if (!virtualIoDrain.IsSuccess) return virtualIoDrain;
        var secureGuestDrain = CloseSecureGuestRegionsForVirtualDomain(owner, domain);
        if (!secureGuestDrain.IsSuccess) return secureGuestDrain;
        var guestMappingDrain = CloseGuestMappingsForVirtualTree(record.Value!, owner);
        if (!guestMappingDrain.IsSuccess) return guestMappingDrain;
        var composedDrain = DrainSecureExecutions(owner, virtualDomain: domain);
        if (!composedDrain.IsSuccess) return composedDrain;
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
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, capability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != from) return KernelResult.Fail(KernelError.InvalidTransition, $"Virtual domain cannot transition from {record.Value.State} to {to}.");
        if (record.Value.ModelBinding is { } model)
            return TransitionModelVirtualDomain(owner, domain, capability, record.Value, model,
                from, to, transition, CapabilityRights.Execute);
        if (record.Value.ChildBinding is not { } child)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Virtual domain has no executable child binding.");
        return TransitionChildVirtualDomain(owner, domain, capability, record.Value, child, from, to,
            transition switch
            {
                PlatformVirtualDomainTransition.Start => PlatformChildDomainTransition.Start,
                PlatformVirtualDomainTransition.Park => PlatformChildDomainTransition.Park,
                PlatformVirtualDomainTransition.Resume => PlatformChildDomainTransition.Resume,
                _ => PlatformChildDomainTransition.BeginDrain,
            });
    }

    private KernelResult TransitionChildVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain,
        CapabilityId source, VirtualDomainAuthority.Record record, PlatformChildBinding binding,
        VirtualDomainState from, VirtualDomainState to, PlatformChildDomainTransition? transition)
    {
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "A virtual state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Child process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.State != from || record.ChildBinding != binding)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Child local source state changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(record);
                return lineage.IsSuccess ? commit() : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation,
                transition is null ? CapabilityRights.Configure : CapabilityRights.Execute,
                VirtualizationResourceIds.Domain(domain.DomainId), () => _virtualDomains.CommitEffectAdmission(record, from, commit));
        try
        {
            if (transition is null)
                return Fresh(() => PlatformAuthority.CommitActiveChildDomainConsumer(binding, () =>
                    SourceCommit(() => _virtualDomains.CommitStateTransition(record, from, to))));
            return PlatformAuthority.TransitionChildDomain(binding, transition.Value,
                admissionConsumer: Fresh, sourceCommitConsumer: SourceCommit,
                completionConsumer: commit => Fresh(() => commit(() =>
                    SourceCommit(() => _virtualDomains.CommitStateTransition(record, from, to)))));
        }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    private KernelResult UseVirtualExecutableSource(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId source,
        VirtualDomainAuthority.Record record, PlatformChildBinding child,
        Func<Func<Func<KernelResult>, KernelResult>, Func<Func<KernelResult>, KernelResult>, KernelResult> operation)
    {
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "A virtual state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Executable process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.ChildBinding != child || record.State != VirtualDomainState.Configured)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Executable local owner/from state changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(record);
                return lineage.IsSuccess ? commit() : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation, CapabilityRights.Execute,
                VirtualizationResourceIds.Domain(domain.DomainId), () => _virtualDomains.CommitEffectAdmission(record, VirtualDomainState.Configured, commit));
        try { return operation(Fresh, SourceCommit); }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    private KernelResult UseVirtualTrapSource(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId source,
        VirtualDomainAuthority.Record record, PlatformChildBinding child,
        Func<Func<Func<KernelResult>, KernelResult>, Func<Func<KernelResult>, KernelResult>, KernelResult> operation)
    {
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "A virtual state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Trap process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.ChildBinding != child || record.State != VirtualDomainState.Running)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Trap local owner/from state changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(record);
                return lineage.IsSuccess ? commit() : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation, CapabilityRights.Read,
                VirtualizationResourceIds.Traps(domain.DomainId), () => _virtualDomains.CommitEffectAdmission(record, VirtualDomainState.Running, commit));
        try { return operation(Fresh, SourceCommit); }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    // Admission only; VirtualDomainAuthority remains the local state owner.
    private readonly HashSet<VirtualDomainHandle> _pendingVirtualTransitions = [];

    private KernelResult TransitionModelVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain,
        CapabilityId source, VirtualDomainAuthority.Record record, PlatformAuthorityBridge.VirtualDomainBinding binding,
        VirtualDomainState from, VirtualDomainState to, PlatformVirtualDomainTransition transition, CapabilityRights rights)
    {
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "A model state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Model process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.State != from || record.ModelBinding != binding)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Model local source state changed.");
                return commit();
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation, rights,
                VirtualizationResourceIds.Domain(domain.DomainId), () => _virtualDomains.CommitEffectAdmission(record, from, commit));
        try
        {
            return PlatformAuthority.TransitionVirtualDomain(binding, transition, Fresh, SourceCommit,
                completionConsumer: commit => Fresh(() => commit(() =>
                    SourceCommit(() => _virtualDomains.CommitStateTransition(record, from, to)))));
        }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    private KernelResult<VirtualDomainAuthority.Record> ResolveVirtualCapability(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId capabilityId, string resourceId, CapabilityRights rights)
    {
        var record = _virtualDomains.ResolveAdmission(owner, domain);
        if (!record.IsSuccess) return record;
        var capability = ValidateCapability(owner, capabilityId, rights);
        if (!capability.IsSuccess) return KernelResult<VirtualDomainAuthority.Record>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Virtualization || capability.Value.ResourceId != resourceId)
            return KernelResult<VirtualDomainAuthority.Record>.Fail(KernelError.WrongCapabilityResource, "Capability does not match the exact virtual-domain resource.");
        if (rights != CapabilityRights.Configure)
        {
            var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
            if (!composed.IsSuccess) return KernelResult<VirtualDomainAuthority.Record>.Fail(composed.Error, composed.Message!);
        }
        return record;
    }

    private KernelResult CloseVirtualDomainsForProcess(ProcessHandle owner)
    {
        foreach (var record in _virtualDomains.ForOwner(owner)
                     .Where(static domain => domain.ParentDomain is null && domain.State != VirtualDomainState.Closed)
                     .OrderBy(static domain => domain.Handle.DomainId.Value))
        {
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
                var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping.Mapping);
                if (!secureClose.IsSuccess) { record.State = VirtualDomainState.Quarantined; return secureClose; }
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
            var draining = PlatformAuthority.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain, allowCommittedDrain: true);
            if (!draining.IsSuccess) return draining;
            var closed = PlatformAuthority.CloseChildDomain(child, allowCommittedClosure: true);
            if (!closed.IsSuccess) return closed;
            if (record.ParentDomain is not null)
                return KernelResult.Ok();
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            var rootClosed = PlatformAuthority.RevokeDomain(parent, PlatformIdentity(process.Value!));
            if (rootClosed.IsSuccess) ConsumeTrackedVirtualRootClosure(owner, parent);
            return rootClosed;
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
                var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping.Mapping);
                if (!secureClose.IsSuccess) { child.State = VirtualDomainState.Quarantined; return secureClose; }
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

    private KernelResult CloseGuestMappingsForProcess(ProcessHandle owner)
    {
        foreach (var record in _virtualDomains.ForOwner(owner).Where(static x => x.State != VirtualDomainState.Closed).OrderByDescending(x => x.Handle.DomainId.Value))
        {
            record.State = VirtualDomainState.Draining;
            var close = CloseGuestMappingsForRecord(record, owner);
            if (!close.IsSuccess) { record.State = VirtualDomainState.Quarantined; return close; }
        }
        return KernelResult.Ok();
    }

    private KernelResult CloseGuestMappingsForVirtualTree(VirtualDomainAuthority.Record record, ProcessHandle owner)
    {
        foreach (var child in _virtualDomains.ChildrenOf(record))
        {
            var descendants = CloseGuestMappingsForVirtualTree(child, owner);
            if (!descendants.IsSuccess) return descendants;
        }
        return CloseGuestMappingsForRecord(record, owner);
    }

    private KernelResult CloseGuestMappingsForRecord(VirtualDomainAuthority.Record record, ProcessHandle owner)
    {
        foreach (var mapping in record.Mappings.Values.OrderBy(x => x.Mapping.MappingId.Value).ToArray())
        {
            if (HasVirtualComputePin(mapping.Mapping))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Guest mapping remains pinned by virtualized provider work.");
            var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping.Mapping);
            if (!secureClose.IsSuccess) return secureClose;
            var childPlatformMapping = record.PlatformMappings.ContainsKey(mapping.Mapping.MappingId);
            if (childPlatformMapping)
            {
                var platformClose = CloseChildPlatformMapping(record, owner, mapping.Mapping.MappingId);
                if (!platformClose.IsSuccess) return platformClose;
            }
            var removed = _virtualDomains.RemoveMapping(record, mapping.Mapping);
            if (!removed.IsSuccess) return KernelResult.Fail(removed.Error, removed.Message!);
            if (!childPlatformMapping)
            {
                var process = Processes.Resolve(owner);
                if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                var released = Regions.ReleasePlatformMappingReservation(mapping.Region,
                    new RegionOwner(process.Value!.DomainId, owner.Generation));
                if (!released.IsSuccess) return released;
            }
        }
        return KernelResult.Ok();
    }

    private static PlatformChildAuthorityClass ToPlatformAuthority(VirtualDomainAuthorityClass authority) =>
        (PlatformChildAuthorityClass)(int)authority;

    private static bool RequiresVirtualDomainQuarantine(KernelError error) => error is
        KernelError.PlatformFaulted or KernelError.PlatformBindingRevoked or KernelError.StaleGeneration or
        KernelError.WrongPlatformDomain;
}




