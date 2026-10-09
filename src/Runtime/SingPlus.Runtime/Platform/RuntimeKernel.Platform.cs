using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public PlatformFeatureManifest QueryPlatformFeatures() => PlatformAuthority.FeatureManifest;

    public PlatformFeatureManifest RefreshPlatformFeatures() => PlatformAuthority.RefreshFeatureManifest();

    public KernelResult<PlatformEvidenceCatalog> QueryPlatformEvidenceCatalog(
        ProcessHandle subject, CapabilityId evidenceCapability, PlatformDomainBinding binding, EvidenceDomainIdentity? virtualDomain = null)
    {
        var capability = ValidateCapability(subject, evidenceCapability, CapabilityRights.Read);
        if (!capability.IsSuccess) return KernelResult<PlatformEvidenceCatalog>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Evidence || capability.Value.ResourceId != EvidenceResourceIds.Read)
            return KernelResult<PlatformEvidenceCatalog>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize evidence reads.");
        var process = Processes.Resolve(subject);
        if (!process.IsSuccess) return KernelResult<PlatformEvidenceCatalog>.Fail(process.Error, process.Message!);
        return PlatformAuthority.QueryEvidenceCatalog(binding, PlatformIdentity(process.Value!), virtualDomain);
    }

    public KernelResult<EvidenceRecord> ReadPlatformEvidence(
        ProcessHandle subject, CapabilityId evidenceCapability, PlatformDomainBinding binding,
        EvidenceIdentity identity, EvidenceVisibilityClass visibility, EvidenceFreshness minimumFreshness,
        EvidenceDomainIdentity? virtualDomain = null)
    {
        var capability = ValidateCapability(subject, evidenceCapability, CapabilityRights.Read);
        if (!capability.IsSuccess) return KernelResult<EvidenceRecord>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Evidence || capability.Value.ResourceId != EvidenceResourceIds.Read)
            return KernelResult<EvidenceRecord>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize evidence reads.");
        var process = Processes.Resolve(subject);
        if (!process.IsSuccess) return KernelResult<EvidenceRecord>.Fail(process.Error, process.Message!);
        return PlatformAuthority.ReadEvidence(binding, PlatformIdentity(process.Value!), virtualDomain, identity, visibility, minimumFreshness);
    }

    public KernelResult<PlatformDomainBinding> BindPlatformDomain(ProcessHandle subject)
        => BindPlatformDomainCore(subject, executionAttached: true);

    public KernelResult<PlatformDomainBinding> BindPlatformAuthorityDomain(ProcessHandle subject)
        => BindPlatformDomainCore(subject, executionAttached: false);

    internal Action? BeforePlatformBindingAttachmentQualificationHook { get; set; }

    private KernelResult<PlatformDomainBinding> BindPlatformDomainCore(ProcessHandle subject, bool executionAttached,
        (ProcessHandle Principal, CapabilityId Capability)? source = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null)
    {
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var resolved = Processes.Resolve(subject);
            if (!resolved.IsSuccess)
                return KernelResult<PlatformDomainBinding>.Fail(resolved.Error, resolved.Message!);
            process = resolved.Value!;
            var ready = ValidateAttachmentSubject();
            if (!ready.IsSuccess)
                return KernelResult<PlatformDomainBinding>.Fail(ready.Error, ready.Message!);
            var pending = CommitController(() =>
                _executionTransactions.Contains(subject) || !_pendingPlatformDomainBinds.Add(subject)
                    ? KernelResult.Fail(KernelError.PlatformBindingActive, "A root bind or execution transaction is already in flight for this exact process.")
                    : KernelResult.Ok());
            if (!pending.IsSuccess) return KernelResult<PlatformDomainBinding>.Fail(pending.Error, pending.Message!);
        }

        KernelResult CommitController(Func<KernelResult> commit)
        {
            if (sourceCommit is not null) return sourceCommit(commit);
            if (source is not { } controller) return commit();
            var principal = Processes.Resolve(controller.Principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            var accepts = EnsureProcessAcceptsNewEffects(principal.Value!);
            if (!accepts.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, accepts.Message!);
            return CapabilityAuthority.CommitSupervisorPlatformBinding(controller.Capability,
                principal.Value!.DomainId, controller.Principal.Generation, commit);
        }

        KernelResult ValidateAttachmentSubject()
        {
            var current = Processes.Resolve(subject);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleHandle, "Root bind subject identity changed.");
            var effect = EnsureProcessAcceptsNewEffects(process);
            if (!effect.IsSuccess) return effect;
            return CanChangePlatformExecutionAttachment(process.State) ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.InvalidTransition,
                    $"A platform execution domain cannot be attached while the process is {process.State}; bind before execution starts.");
        }

        var identity = PlatformIdentity(process);
        try
        {
            var binding = PlatformAuthority.BindDomain(identity,
                admissionConsumer: begin =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        var ready = ValidateAttachmentSubject();
                        return ready.IsSuccess ? begin() : ready;
                    }
                },
                completionConsumer: (published, commit) =>
                {
                    BeforePlatformBindingAttachmentQualificationHook?.Invoke();
                    var attachmentSource = PlatformAuthority.ValidateRootAttachmentSource(published);
                    lock (_platformMemoryUseGate)
                    {
                        var ready = ValidateAttachmentSubject();
                        if (ready.IsSuccess) ready = attachmentSource;
                        if (!ready.IsSuccess)
                        {
                            // Known provider lease remains reachable by the actual teardown
                            // consumer, but grants no execution attachment to a stopped subject.
                            TrackPlatformBinding(subject, published, executionAttached: false);
                            return ready;
                        }
                        var attached = CommitController(() =>
                        {
                            return commit(() =>
                            {
                                TrackPlatformBinding(subject, published, executionAttached);
                                return KernelResult.Ok();
                            });
                        });
                        if (!attached.IsSuccess)
                            TrackPlatformBinding(subject, published, executionAttached: false);
                        return attached;
                    }
                }, sourceCommitConsumer: CommitController, invocationConsumer: () =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        var ready = ValidateAttachmentSubject();
                        return ready.IsSuccess ? CommitController(KernelResult.Ok) : ready;
                    }
                });
            if (!binding.IsSuccess && PlatformAuthority.TryGetQuarantinedDomainBinding(identity, out var quarantinedBinding))
                TrackPlatformBinding(subject, quarantinedBinding, executionAttached: false);
            return binding;
        }
        finally
        {
            lock (_platformMemoryUseGate) _pendingPlatformDomainBinds.Remove(subject);
        }
    }

    public KernelResult RevokePlatformDomain(
        ProcessHandle subject,
        PlatformDomainBinding binding)
    {
        SingProcess process;
        PlatformDomainBinding? trackedBefore;
        lock (_platformMemoryUseGate)
        {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        process = resolved.Value!;
        if (!CanChangePlatformExecutionAttachment(process.State))
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                $"A platform execution domain cannot be detached while the process is {process.State}; post-start closure must use process teardown.");
        }

        if (PlatformAuthority.HasActiveDeviceLeases(binding))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingActive,
                "Platform device leases must close before the platform domain binding.");
        }

        if (PlatformAuthority.HasActiveDsc1Operations(binding))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingActive,
                "DSC1 operations must reach provider closure and release their local buffer reservations before the platform domain binding.");
        }

        if (_pendingPlatformDomainBinds.Contains(subject) || _executionTransactions.Contains(subject))
            return KernelResult.Fail(KernelError.PlatformBindingActive, "An execution/attachment transaction is already pending.");
        trackedBefore = _processPlatformBindings.TryGetValue(subject, out var tracked) ? tracked : null;
        if (trackedBefore is { } routed && routed != binding)
        {
            var exact = PlatformAuthority.CommitActiveDomainConsumer(binding, PlatformIdentity(process), KernelResult.Ok);
            // Preserve V1 idempotence for an exact closed old binding; it cannot untrack a distinct live replacement.
            if (exact.Error != KernelError.PlatformBindingRevoked || routed.BindingId == binding.BindingId)
                return exact.IsSuccess || exact.Error == KernelError.PlatformBindingRevoked
                    ? KernelResult.Fail(KernelError.PlatformFaulted,
                        "A foreign kernel root correlation cannot be discharged by this binding's closure.")
                    : exact;
        }
        _executionTransactions.Add(subject);
        }
        try
        {
        var identity = PlatformIdentity(process);
        var revoke = PlatformAuthority.RevokeDomain(binding, identity);
        lock (_platformMemoryUseGate)
        {
            if (!revoke.IsSuccess) return revoke;
            var current = Processes.Resolve(subject);
            if (!current.IsSuccess || !ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact detach process incarnation changed during provider revoke.");
            var trackedAfter = _processPlatformBindings.TryGetValue(subject, out var tracked) ? (PlatformDomainBinding?)tracked : null;
            if (trackedAfter != trackedBefore)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Detach binding correlation changed; exact cleanup remains owned by its consumer.");
            if (!CanChangePlatformExecutionAttachment(process.State))
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "Process exit began during detach; accepted provider closure remains available to process teardown.");
            UntrackPlatformBinding(subject, binding);
            return KernelResult.Ok();
        }
        }
        finally { lock (_platformMemoryUseGate) _executionTransactions.Remove(subject); }
    }

    private static bool CanChangePlatformExecutionAttachment(ProcessState state) =>
        state is ProcessState.Created or ProcessState.Admitted;

    public KernelResult<PlatformRegionMapping> MapPlatformOwnedRegion(
        ProcessHandle owner,
        PlatformDomainBinding binding,
        CapabilityId capabilityId,
        RegionHandle region,
        PlatformMemoryAccess access)
    {
        if (!IsValidPlatformAccess(access))
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformDenied,
                "Platform memory access must be Read, Write, or Read|Write.");

        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(resolved.Error, resolved.Message!);

        var process = resolved.Value!;
        var effect = BeginPlatformMappingAdmission(owner, process, capabilityId);
        if (!effect.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(effect.Error, effect.Message!);
        try
        {

        var identity = PlatformIdentity(process);

        var bindingValidation = PlatformAuthority.ValidateDomain(binding, identity);
        if (!bindingValidation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);

        var requiredRights = CapabilityRights.Map;
        if ((access & PlatformMemoryAccess.Read) != 0) requiredRights |= CapabilityRights.Read;
        if ((access & PlatformMemoryAccess.Write) != 0) requiredRights |= CapabilityRights.Write;

        var capability = CapabilityAuthority.Validate(
            capabilityId,
            process.DomainId,
            owner.Generation,
            requiredRights);

        if (!capability.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(capability.Error, capability.Message!);

        var descriptor = capability.Value!;
        if (descriptor.ResourceKind != ResourceKind.MemoryRegion ||
            !string.Equals(
                descriptor.ResourceId,
                CapabilityResourceIds.MemoryRegion(region.RegionId),
                StringComparison.Ordinal))
        {
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.WrongCapabilityResource,
                "The local capability does not authorize this memory region.");
        }

        var ownerIdentity = new RegionOwner(process.DomainId, owner.Generation);
        var regionValidation = Regions.Validate(region, ownerIdentity);
        if (!regionValidation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                regionValidation.Error,
                regionValidation.Message!);

        var budget = ReserveAttachedBudget(owner,
            [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, checked((ulong)regionValidation.Value!.ByteLength))],
            BudgetReservationLifetime.PlatformMapping);
        if (!budget.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(budget.Error, budget.Message!);
        var originalBudgetOwner = Budgets.CaptureOriginalMappingReservationOwner(owner, budget.Value);

        var reservation = Regions.ReservePlatformMapping(region, ownerIdentity);
        if (!reservation.IsSuccess)
        {
            _ = ReleaseAttachedBudget(owner, budget.Value);
            return KernelResult<PlatformRegionMapping>.Fail(
                reservation.Error,
                reservation.Message!);
        }

        var platformRegion = new PlatformRegionIdentity(
            regionValidation.Value!.Handle,
            regionValidation.Value.Owner,
            regionValidation.Value.ByteLength);

        var mapping = PlatformAuthority.MapOwnedRegion(
            binding,
            identity,
            capabilityId,
            platformRegion,
            access,
            out var retainReservation);

        if (!mapping.IsSuccess)
        {
            if (!retainReservation)
            {
                _ = Regions.ReleasePlatformMappingReservation(region, ownerIdentity);
                _ = ReleaseAttachedBudget(owner, budget.Value);
            }
            return mapping;
        }

        lock (_platformMemoryUseGate)
        {
        TrackPlatformMapping(owner, mapping.Value!);
        if (!EnsureProcessAcceptsNewEffects(process).IsSuccess ||
            !Regions.ValidatePlatformMappingRegionUsability(region, ownerIdentity).IsSuccess)
            _ = PlatformAuthority.RevokeExactMappingLocalAuthorization(mapping.Value!, identity);
        lock (Budgets.MappingReservationPublicationGate)
        {
            var bound = PlatformAuthority.BindOriginalMappingBudgetOwner(mapping.Value!, budget.Value is not null, originalBudgetOwner);
            if (!bound.IsSuccess || budget.Value is not null && !Budgets.BindOriginalMappingChargeOwnerLocked(originalBudgetOwner, bound.Value!).IsSuccess ||
                !Budgets.ValidateMappingReservationPublicationLocked(owner, budget.Value).IsSuccess)
                _ = PlatformAuthority.RevokeExactMappingLocalAuthorization(mapping.Value!, identity);
            if (budget.Value is { } budgetReservation)
                _mappingBudgetReservations.Add(mapping.Value.MappingId, (owner, budgetReservation));
        }
        // Preserve the receipt for closure without restoring revoked permission.
        if (!CapabilityAuthority.Validate(capabilityId, process.DomainId, owner.Generation, requiredRights).IsSuccess)
            _ = PlatformAuthority.BeginCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
        return mapping;
        }
        }
        finally { EndPlatformMappingAdmission(owner, capabilityId); }
    }

    public KernelResult<PlatformOwnedRegionSliceMapping> MapPlatformOwnedRegionSlice(
        ProcessHandle owner,
        PlatformDomainBinding binding,
        CapabilityId capabilityId,
        RegionHandle region,
        long offset,
        long length,
        PlatformMemoryAccess access)
    {
        if (!IsValidPlatformAccess(access))
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.PlatformDenied,
                "Platform memory access must be Read, Write, or Read|Write.");
        }

        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = BeginPlatformMappingAdmission(owner, process, capabilityId);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                effect.Error,
                effect.Message!);
        }
        try
        {

        var identity = PlatformIdentity(process);
        var bindingValidation = PlatformAuthority.ValidateDomain(binding, identity);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        var requiredRights = CapabilityRights.Map;
        if ((access & PlatformMemoryAccess.Read) != 0) requiredRights |= CapabilityRights.Read;
        if ((access & PlatformMemoryAccess.Write) != 0) requiredRights |= CapabilityRights.Write;

        var capability = CapabilityAuthority.Validate(
            capabilityId,
            process.DomainId,
            owner.Generation,
            requiredRights);
        if (!capability.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                capability.Error,
                capability.Message!);
        }

        var capabilityDescriptor = capability.Value!;
        if (capabilityDescriptor.ResourceKind != ResourceKind.MemoryRegion ||
            !string.Equals(
                capabilityDescriptor.ResourceId,
                CapabilityResourceIds.MemoryRegion(region.RegionId),
                StringComparison.Ordinal))
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.WrongCapabilityResource,
                "The local capability does not authorize this memory region.");
        }

        var ownerIdentity = new RegionOwner(process.DomainId, owner.Generation);
        var regionValidation = Regions.Validate(region, ownerIdentity);
        if (!regionValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                regionValidation.Error,
                regionValidation.Message!);
        }

        var platformRegion = new PlatformRegionIdentity(
            regionValidation.Value!.Handle,
            regionValidation.Value.Owner,
            regionValidation.Value.ByteLength);
        var slice = new PlatformRegionSlice(
            platformRegion,
            offset,
            length,
            access);
        var sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
        if (!sliceValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.PlatformDenied,
                sliceValidation.Message ?? "The requested region slice is invalid.");
        }

        var budget = ReserveAttachedBudget(owner,
            [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, checked((ulong)length))],
            BudgetReservationLifetime.PlatformMapping);
        if (!budget.IsSuccess)
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(budget.Error, budget.Message!);
        var originalBudgetOwner = Budgets.CaptureOriginalMappingReservationOwner(owner, budget.Value);

        var reservation = Regions.ReservePlatformMapping(region, ownerIdentity);
        if (!reservation.IsSuccess)
        {
            _ = ReleaseAttachedBudget(owner, budget.Value);
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                reservation.Error,
                reservation.Message!);
        }

        var mapping = PlatformAuthority.MapOwnedRegionSlice(
            binding,
            identity,
            capabilityId,
            slice,
            retainFaultedHandleOnMalformed: true,
            out var retainReservation);
        if (!mapping.IsSuccess)
        {
            if (!retainReservation)
            {
                _ = Regions.ReleasePlatformMappingReservation(region, ownerIdentity);
                _ = ReleaseAttachedBudget(owner, budget.Value);
            }
            return mapping;
        }

        lock (_platformMemoryUseGate)
        {
        TrackPlatformMapping(owner, mapping.Value!.Mapping);
        if (!EnsureProcessAcceptsNewEffects(process).IsSuccess ||
            !Regions.ValidatePlatformMappingRegionUsability(region, ownerIdentity).IsSuccess)
            _ = PlatformAuthority.RevokeExactMappingLocalAuthorization(mapping.Value.Mapping, identity);
        lock (Budgets.MappingReservationPublicationGate)
        {
            var bound = PlatformAuthority.BindOriginalMappingBudgetOwner(mapping.Value.Mapping, budget.Value is not null, originalBudgetOwner);
            if (!bound.IsSuccess || budget.Value is not null && !Budgets.BindOriginalMappingChargeOwnerLocked(originalBudgetOwner, bound.Value!).IsSuccess ||
                !Budgets.ValidateMappingReservationPublicationLocked(owner, budget.Value).IsSuccess)
                _ = PlatformAuthority.RevokeExactMappingLocalAuthorization(mapping.Value.Mapping, identity);
            if (budget.Value is { } budgetReservation)
                _mappingBudgetReservations.Add(mapping.Value.Mapping.MappingId, (owner, budgetReservation));
        }
        // Preserve the receipt for closure without restoring revoked permission.
        if (!CapabilityAuthority.Validate(capabilityId, process.DomainId, owner.Generation, requiredRights).IsSuccess)
            _ = PlatformAuthority.BeginCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
        return mapping;
        }
        }
        finally { EndPlatformMappingAdmission(owner, capabilityId); }
    }

    public KernelResult<PlatformRegionVisibilityEvidence> PreparePlatformRegionMappingForConsumer(
        ProcessHandle owner,
        PlatformOwnedRegionSliceMapping mapping,
        PlatformMemoryConsumerClass consumer,
        PlatformMemoryVisibilityRequirement requirement)
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                effect.Error,
                effect.Message!);
        }

        var identity = PlatformIdentity(process);
        var exactMapping = PlatformAuthority.ValidateExactMapping(mapping, identity);
        if (!exactMapping.IsSuccess)
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(exactMapping.Error, exactMapping.Message!);
        var usability = Regions.ValidatePlatformMappingRegionUsability(mapping.Mapping.Region,
            new RegionOwner(process.DomainId, owner.Generation));
        if (!usability.IsSuccess)
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(usability.Error, usability.Message!);
        var evidence = PlatformAuthority.PrepareRegionMappingForConsumer(
            mapping,
            identity,
            consumer,
            requirement);
        if (!evidence.IsSuccess) return evidence;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult<PlatformRegionVisibilityEvidence>.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult<PlatformRegionVisibilityEvidence>.Fail(KernelError.StaleGeneration, "Visibility publication process incarnation changed.");
            var ready = EnsureProcessAcceptsNewEffects(process);
            if (!ready.IsSuccess) return KernelResult<PlatformRegionVisibilityEvidence>.Fail(ready.Error, ready.Message!);
            var regionGate = Regions.GetPlatformMappingCompletionGate(mapping.Mapping.Region,
                new RegionOwner(process.DomainId, owner.Generation));
            if (!regionGate.IsSuccess)
                return KernelResult<PlatformRegionVisibilityEvidence>.Fail(regionGate.Error, regionGate.Message!);
            lock (regionGate.Value!)
            {
                var valid = PlatformAuthority.ValidateVisibilityPublication(mapping, identity, Regions);
                return valid.IsSuccess ? evidence : KernelResult<PlatformRegionVisibilityEvidence>.Fail(valid.Error, valid.Message!);
            }
        }
    }

    public KernelResult RevokePlatformRegionMapping(
        ProcessHandle owner,
        PlatformOwnedRegionSliceMapping mapping) =>
        RevokePlatformRegionMapping(owner, mapping.Mapping);

    public KernelResult RevokePlatformRegionMapping(
        ProcessHandle owner,
        PlatformRegionMapping mapping)
    {
        PlatformDomainIdentity identity;
        Action release;
        lock (_platformMemoryUseGate)
        {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        identity = PlatformIdentity(resolved.Value!);
        var drain = PlatformAuthority.BeginMappingChildDrain(mapping, identity, out release);
        if (!drain.IsSuccess) return drain;
        }

        try
        {
        var compute = AdvancePlatformDsc1ForMapping(mapping);
        if (!compute.IsSuccess) return compute;

        var dma = AdvancePlatformDmaGrantsForMapping(mapping);
        if (!dma.IsSuccess) return dma;

        var lifecycle = PlatformAuthority.BeginRegionMappingRevocation(
            mapping,
            identity,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        return FinalizePlatformRegionMappingClosure(mapping, identity, lifecycle.Value!);
        }
        finally { release(); }
    }

    public KernelResult ObservePlatformRegionMappingRevocation(
        ProcessHandle owner,
        PlatformRegionMapping mapping)
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        var identity = PlatformIdentity(resolved.Value!);
        var lifecycle = PlatformAuthority.ObserveRegionMappingRevocation(mapping, identity);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        return FinalizePlatformRegionMappingClosure(mapping, identity, lifecycle.Value!);
    }

    public KernelResult<PlatformRegionMappingLifecycle> QueryPlatformRegionMappingLifecycle(
        ProcessHandle owner,
        PlatformRegionMapping mapping)
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                resolved.Error,
                resolved.Message!);

        var identity = PlatformIdentity(resolved.Value!);
        return PlatformAuthority.QueryRegionMappingLifecycle(mapping, identity);
    }

    internal KernelResult CascadePlatformCapabilityRevocation(CapabilityId capabilityId)
    {
        IReadOnlyList<PlatformRegionMapping> mappings;
        bool pending;
        lock (_platformMemoryUseGate)
        {
            mappings = PlatformAuthority.BeginCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
            pending = _pendingPlatformMappingCapabilities.Any(id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
        }
        KernelResult? firstFailure = null;

        foreach (var mapping in mappings)
        {
            var subject = mapping.DomainBinding.Subject;
            var dma = AdvancePlatformDmaGrantsForMapping(mapping);
            if (!dma.IsSuccess)
            {
                firstFailure ??= dma;
                continue;
            }

            var lifecycle = PlatformAuthority.BeginRegionMappingRevocation(
                mapping,
                subject,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke);

            if (!lifecycle.IsSuccess)
            {
                firstFailure ??= KernelResult.Fail(lifecycle.Error, lifecycle.Message!);
                continue;
            }

            var finalize = FinalizePlatformRegionMappingClosure(
                mapping,
                subject,
                lifecycle.Value!);
            if (!finalize.IsSuccess)
                firstFailure ??= finalize;
        }

        return firstFailure ?? (pending
            ? KernelResult.Fail(KernelError.PlatformBindingDraining, "An admitted mapping must settle before capability closure.")
            : KernelResult.Ok());
    }

    private KernelResult FinalizePlatformRegionMappingClosure(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity identity,
        PlatformRegionMappingLifecycle lifecycle)
    {
        var source = PlatformAuthority.ValidateRegionMappingReleaseSource(mapping, identity);
        if (!source.IsSuccess) return source;
        var observations = new List<BudgetTraceObservation>();
        try { return FinalizePlatformRegionMappingClosureLocal(mapping, identity, lifecycle, observations); }
        finally { EmitBudgetObservations(observations); }
    }

    // Trusted local composition only; caller must perform source observation
    // outside any outer owner gate before entering this callback-free commit.
    private KernelResult FinalizePlatformRegionMappingClosureLocal(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity identity,
        PlatformRegionMappingLifecycle lifecycle,
        List<BudgetTraceObservation> observations)
    {
        lock (_platformMemoryUseGate)
        {
        var current = Processes.Resolve(identity.Process);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (PlatformIdentity(current.Value!) != identity)
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact mapping cleanup process incarnation changed.");
        // The supplied observation is evidence of the provider stage, not a
        // license to release against changed local mapping/reset owners.
        var fresh = PlatformAuthority.QueryRegionMappingLifecycle(mapping, identity);
        if (!fresh.IsSuccess) return KernelResult.Fail(fresh.Error, fresh.Message!);
        lifecycle = fresh.Value!;
        if (lifecycle.PlatformClosure == PlatformExternalClosureState.Faulted)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform region mapping closure faulted; local reclaim remains forbidden.");
        }

        if (lifecycle.PlatformClosure != PlatformExternalClosureState.Closed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "The platform region mapping is still draining; local reclaim remains forbidden.");
        }

        if (lifecycle.LocalReservationReleased)
        {
            var existingBudgetRelease = ReleaseMappingBudget(mapping, observations);
            if (!existingBudgetRelease.IsSuccess) return existingBudgetRelease;
            UntrackPlatformMapping(mapping);
            PlatformAuthority.ForgetExactMappingMetadata(mapping);
            return KernelResult.Ok();
        }

        var bindingValidation = PlatformAuthority.ValidateDomain(
            mapping.DomainBinding,
            identity);
        if (!bindingValidation.IsSuccess) return bindingValidation;

        var owner = new RegionOwner(identity.DomainId, identity.ProcessGeneration);
        var gate = Regions.GetPlatformMappingCompletionGate(mapping.Region, owner);
        if (!gate.IsSuccess) return KernelResult.Fail(gate.Error, gate.Message!);
        lock (gate.Value!)
        {
            var release = PlatformAuthority.CommitRegionReservationRelease(mapping, identity, Regions);
            if (!release.IsSuccess) return release;
        }

        var budgetRelease = ReleaseMappingBudget(mapping, observations);
        if (!budgetRelease.IsSuccess) return budgetRelease;

        UntrackPlatformMapping(mapping);
        PlatformAuthority.ForgetExactMappingMetadata(mapping);
        return KernelResult.Ok();
        }
    }

    private static bool IsValidPlatformAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;
}
