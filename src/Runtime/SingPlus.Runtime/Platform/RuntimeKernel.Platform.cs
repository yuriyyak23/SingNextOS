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
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(subject, evidenceCapability, CapabilityRights.Read);
        if (!capability.IsSuccess) return KernelResult<PlatformEvidenceCatalog>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Evidence || capability.Value.ResourceId != EvidenceResourceIds.Read)
            return KernelResult<PlatformEvidenceCatalog>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize evidence reads.");
        KernelResult<SingProcess> process = Processes.Resolve(subject);
        if (!process.IsSuccess) return KernelResult<PlatformEvidenceCatalog>.Fail(process.Error, process.Message!);
        return PlatformAuthority.QueryEvidenceCatalog(binding, PlatformIdentity(process.Value!), virtualDomain);
    }

    public KernelResult<EvidenceRecord> ReadPlatformEvidence(
        ProcessHandle subject, CapabilityId evidenceCapability, PlatformDomainBinding binding,
        EvidenceIdentity identity, EvidenceVisibilityClass visibility, EvidenceFreshness minimumFreshness,
        EvidenceDomainIdentity? virtualDomain = null)
    {
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(subject, evidenceCapability, CapabilityRights.Read);
        if (!capability.IsSuccess) return KernelResult<EvidenceRecord>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Evidence || capability.Value.ResourceId != EvidenceResourceIds.Read)
            return KernelResult<EvidenceRecord>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize evidence reads.");
        KernelResult<SingProcess> process = Processes.Resolve(subject);
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
            KernelResult<SingProcess> resolved = Processes.Resolve(subject);
            if (!resolved.IsSuccess)
                return KernelResult<PlatformDomainBinding>.Fail(resolved.Error, resolved.Message!);
            process = resolved.Value!;
            KernelResult ready = ValidateAttachmentSubject();
            if (!ready.IsSuccess)
                return KernelResult<PlatformDomainBinding>.Fail(ready.Error, ready.Message!);
            KernelResult pending = CommitController(() =>
                _executionTransactions.Contains(subject) || !_pendingPlatformDomainBinds.Add(subject)
                    ? KernelResult.Fail(KernelError.PlatformBindingActive, "A root bind or execution transaction is already in flight for this exact process.")
                    : KernelResult.Ok());
            if (!pending.IsSuccess) return KernelResult<PlatformDomainBinding>.Fail(pending.Error, pending.Message!);
        }

        KernelResult CommitController(Func<KernelResult> commit)
        {
            if (sourceCommit is not null) return sourceCommit(commit);
            if (source is not { } controller) return commit();
            KernelResult<SingProcess> principal = Processes.Resolve(controller.Principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult accepts = EnsureProcessAcceptsNewEffects(principal.Value!);
            if (!accepts.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, accepts.Message!);
            return CapabilityAuthority.CommitSupervisorPlatformBinding(controller.Capability,
                principal.Value!.DomainId, controller.Principal.Generation, commit);
        }

        KernelResult ValidateAttachmentSubject()
        {
            KernelResult<SingProcess> current = Processes.Resolve(subject);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleHandle, "Root bind subject identity changed.");
            KernelResult effect = EnsureProcessAcceptsNewEffects(process);
            if (!effect.IsSuccess) return effect;
            return CanChangePlatformExecutionAttachment(process.State) ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.InvalidTransition,
                    $"A platform execution domain cannot be attached while the process is {process.State}; bind before execution starts.");
        }

        PlatformDomainIdentity identity = PlatformIdentity(process);
        try
        {
            KernelResult<PlatformDomainBinding> binding = PlatformAuthority.BindDomain(identity,
                admissionConsumer: begin =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        KernelResult ready = ValidateAttachmentSubject();
                        return ready.IsSuccess ? begin() : ready;
                    }
                },
                completionConsumer: (published, commit) =>
                {
                    BeforePlatformBindingAttachmentQualificationHook?.Invoke();
                    KernelResult attachmentSource = PlatformAuthority.ValidateRootAttachmentSource(published);
                    lock (_platformMemoryUseGate)
                    {
                        KernelResult ready = ValidateAttachmentSubject();
                        if (ready.IsSuccess) ready = attachmentSource;
                        if (!ready.IsSuccess)
                        {
                            // Known provider lease remains reachable by the actual teardown
                            // consumer, but grants no execution attachment to a stopped subject.
                            TrackPlatformBinding(subject, published, executionAttached: false);
                            return ready;
                        }
                        KernelResult attached = CommitController(() =>
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
                        KernelResult ready = ValidateAttachmentSubject();
                        return ready.IsSuccess ? CommitController(KernelResult.Ok) : ready;
                    }
                });
            if (!binding.IsSuccess && PlatformAuthority.TryGetQuarantinedDomainBinding(identity, out PlatformDomainBinding quarantinedBinding))
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
            KernelResult<SingProcess> resolved = Processes.Resolve(subject);
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
        trackedBefore = _processPlatformBindings.TryGetValue(subject, out PlatformDomainBinding tracked) ? tracked : null;
        if (trackedBefore is { } routed && routed != binding)
        {
                KernelResult exact = PlatformAuthority.CommitActiveDomainConsumer(binding, PlatformIdentity(process), KernelResult.Ok);
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
            PlatformDomainIdentity identity = PlatformIdentity(process);
            KernelResult revoke = PlatformAuthority.RevokeDomain(binding, identity);
        lock (_platformMemoryUseGate)
        {
            if (!revoke.IsSuccess) return revoke;
                KernelResult<SingProcess> current = Processes.Resolve(subject);
            if (!current.IsSuccess || !ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact detach process incarnation changed during provider revoke.");
                PlatformDomainBinding? trackedAfter = _processPlatformBindings.TryGetValue(subject, out PlatformDomainBinding tracked) ? (PlatformDomainBinding?)tracked : null;
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

        KernelResult<SingProcess> resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(resolved.Error, resolved.Message!);

        SingProcess process = resolved.Value!;
        KernelResult effect = BeginPlatformMappingAdmission(owner, process, capabilityId);
        if (!effect.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(effect.Error, effect.Message!);
        try
        {

            PlatformDomainIdentity identity = PlatformIdentity(process);

            KernelResult bindingValidation = PlatformAuthority.ValidateDomain(binding, identity);
        if (!bindingValidation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);

            CapabilityRights requiredRights = CapabilityRights.Map;
        if ((access & PlatformMemoryAccess.Read) != 0) requiredRights |= CapabilityRights.Read;
        if ((access & PlatformMemoryAccess.Write) != 0) requiredRights |= CapabilityRights.Write;

            KernelResult<CapabilityDescriptorV1> capability = CapabilityAuthority.Validate(
            capabilityId,
            process.DomainId,
            owner.Generation,
            requiredRights);

        if (!capability.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(capability.Error, capability.Message!);

            CapabilityDescriptorV1 descriptor = capability.Value!;
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

            RegionOwner ownerIdentity = new RegionOwner(process.DomainId, owner.Generation);
            KernelResult<RegionDescriptor> regionValidation = Regions.Validate(region, ownerIdentity);
        if (!regionValidation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                regionValidation.Error,
                regionValidation.Message!);

            KernelResult<BudgetReservationHandle?> budget = ReserveAttachedBudget(owner,
            [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, checked((ulong)regionValidation.Value!.ByteLength))],
            BudgetReservationLifetime.PlatformMapping);
        if (!budget.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(budget.Error, budget.Message!);
            object? originalBudgetOwner = Budgets.CaptureOriginalMappingReservationOwner(owner, budget.Value);

            KernelResult reservation = Regions.ReservePlatformMapping(region, ownerIdentity);
        if (!reservation.IsSuccess)
        {
            _ = ReleaseAttachedBudget(owner, budget.Value);
            return KernelResult<PlatformRegionMapping>.Fail(
                reservation.Error,
                reservation.Message!);
        }

            PlatformRegionIdentity platformRegion = new PlatformRegionIdentity(
            regionValidation.Value!.Handle,
            regionValidation.Value.Owner,
            regionValidation.Value.ByteLength);

            KernelResult<PlatformRegionMapping> mapping = PlatformAuthority.MapOwnedRegion(
            binding,
            identity,
            capabilityId,
            platformRegion,
            access,
            out bool retainReservation);

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
                    KernelResult<object> bound = PlatformAuthority.BindOriginalMappingBudgetOwner(mapping.Value!, budget.Value is not null, originalBudgetOwner);
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

        KernelResult<SingProcess> resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        SingProcess process = resolved.Value!;
        KernelResult effect = BeginPlatformMappingAdmission(owner, process, capabilityId);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                effect.Error,
                effect.Message!);
        }
        try
        {

            PlatformDomainIdentity identity = PlatformIdentity(process);
            KernelResult bindingValidation = PlatformAuthority.ValidateDomain(binding, identity);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

            CapabilityRights requiredRights = CapabilityRights.Map;
        if ((access & PlatformMemoryAccess.Read) != 0) requiredRights |= CapabilityRights.Read;
        if ((access & PlatformMemoryAccess.Write) != 0) requiredRights |= CapabilityRights.Write;

            KernelResult<CapabilityDescriptorV1> capability = CapabilityAuthority.Validate(
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

            CapabilityDescriptorV1 capabilityDescriptor = capability.Value!;
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

            RegionOwner ownerIdentity = new RegionOwner(process.DomainId, owner.Generation);
            KernelResult<RegionDescriptor> regionValidation = Regions.Validate(region, ownerIdentity);
        if (!regionValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                regionValidation.Error,
                regionValidation.Message!);
        }

            PlatformRegionIdentity platformRegion = new PlatformRegionIdentity(
            regionValidation.Value!.Handle,
            regionValidation.Value.Owner,
            regionValidation.Value.ByteLength);
            PlatformRegionSlice slice = new PlatformRegionSlice(
            platformRegion,
            offset,
            length,
            access);
            PlatformAuthorityResult sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
        if (!sliceValidation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                KernelError.PlatformDenied,
                sliceValidation.Message ?? "The requested region slice is invalid.");
        }

            KernelResult<BudgetReservationHandle?> budget = ReserveAttachedBudget(owner,
            [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, checked((ulong)length))],
            BudgetReservationLifetime.PlatformMapping);
        if (!budget.IsSuccess)
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(budget.Error, budget.Message!);
            object? originalBudgetOwner = Budgets.CaptureOriginalMappingReservationOwner(owner, budget.Value);

            KernelResult reservation = Regions.ReservePlatformMapping(region, ownerIdentity);
        if (!reservation.IsSuccess)
        {
            _ = ReleaseAttachedBudget(owner, budget.Value);
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                reservation.Error,
                reservation.Message!);
        }

            KernelResult<PlatformOwnedRegionSliceMapping> mapping = PlatformAuthority.MapOwnedRegionSlice(
            binding,
            identity,
            capabilityId,
            slice,
            retainFaultedHandleOnMalformed: true,
            out bool retainReservation, out PlatformRegionMapping? faultedMapping);
        if (!mapping.IsSuccess)
        {
            if (!retainReservation)
            {
                _ = Regions.ReleasePlatformMappingReservation(region, ownerIdentity);
                _ = ReleaseAttachedBudget(owner, budget.Value);
            }
            else if (faultedMapping is { } cleanup)
            {
                lock (_platformMemoryUseGate)
                {
                        KernelResult<SingProcess> current = Processes.Resolve(owner);
                    if (current.IsSuccess && ReferenceEquals(current.Value, process))
                    {
                        TrackPlatformMapping(owner, cleanup);
                        lock (Budgets.MappingReservationPublicationGate)
                        {
                                KernelResult<object> bound = PlatformAuthority.BindOriginalMappingBudgetOwner(cleanup, budget.Value is not null, originalBudgetOwner);
                            if (bound.IsSuccess && (budget.Value is null || Budgets.BindOriginalMappingChargeOwnerLocked(originalBudgetOwner, bound.Value!).IsSuccess))
                            {
                                if (budget.Value is { } reservationOwner)
                                    _mappingBudgetReservations.TryAdd(cleanup.MappingId, (owner, reservationOwner));
                            }
                        }
                    }
                }
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
                    KernelResult<object> bound = PlatformAuthority.BindOriginalMappingBudgetOwner(mapping.Value.Mapping, budget.Value is not null, originalBudgetOwner);
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
        KernelResult<SingProcess> resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        SingProcess process = resolved.Value!;
        KernelResult effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(
                effect.Error,
                effect.Message!);
        }

        PlatformDomainIdentity identity = PlatformIdentity(process);
        KernelResult exactMapping = PlatformAuthority.ValidateExactMapping(mapping, identity);
        if (!exactMapping.IsSuccess)
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(exactMapping.Error, exactMapping.Message!);
        KernelResult usability = Regions.ValidatePlatformMappingRegionUsability(mapping.Mapping.Region,
            new RegionOwner(process.DomainId, owner.Generation));
        if (!usability.IsSuccess)
            return KernelResult<PlatformRegionVisibilityEvidence>.Fail(usability.Error, usability.Message!);
        KernelResult<PlatformRegionVisibilityEvidence> evidence = PlatformAuthority.PrepareRegionMappingForConsumer(
            mapping,
            identity,
            consumer,
            requirement);
        if (!evidence.IsSuccess) return evidence;
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult<PlatformRegionVisibilityEvidence>.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult<PlatformRegionVisibilityEvidence>.Fail(KernelError.StaleGeneration, "Visibility publication process incarnation changed.");
            KernelResult ready = EnsureProcessAcceptsNewEffects(process);
            if (!ready.IsSuccess) return KernelResult<PlatformRegionVisibilityEvidence>.Fail(ready.Error, ready.Message!);
            KernelResult<object> regionGate = Regions.GetPlatformMappingCompletionGate(mapping.Mapping.Region,
                new RegionOwner(process.DomainId, owner.Generation));
            if (!regionGate.IsSuccess)
                return KernelResult<PlatformRegionVisibilityEvidence>.Fail(regionGate.Error, regionGate.Message!);
            lock (regionGate.Value!)
            {
                KernelResult valid = PlatformAuthority.ValidateVisibilityPublication(mapping, identity, Regions);
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
            KernelResult<SingProcess> resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        identity = PlatformIdentity(resolved.Value!);
            KernelResult drain = PlatformAuthority.BeginMappingChildDrain(mapping, identity, out release);
        if (!drain.IsSuccess) return drain;
        }

        try
        {
            KernelResult compute = AdvancePlatformDsc1ForMapping(mapping);
        if (!compute.IsSuccess) return compute;

            KernelResult dma = AdvancePlatformDmaGrantsForMapping(mapping);
        if (!dma.IsSuccess) return dma;

            KernelResult<PlatformRegionMappingLifecycle> lifecycle = PlatformAuthority.BeginRegionMappingRevocation(
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
        KernelResult<SingProcess> resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        PlatformDomainIdentity identity = PlatformIdentity(resolved.Value!);
        KernelResult<PlatformRegionMappingLifecycle> lifecycle = PlatformAuthority.ObserveRegionMappingRevocation(mapping, identity);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        return FinalizePlatformRegionMappingClosure(mapping, identity, lifecycle.Value!);
    }

    public KernelResult<PlatformRegionMappingLifecycle> QueryPlatformRegionMappingLifecycle(
        ProcessHandle owner,
        PlatformRegionMapping mapping)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                resolved.Error,
                resolved.Message!);

        PlatformDomainIdentity identity = PlatformIdentity(resolved.Value!);
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

        foreach (PlatformRegionMapping mapping in mappings)
        {
            PlatformDomainIdentity subject = mapping.DomainBinding.Subject;
            KernelResult dma = AdvancePlatformDmaGrantsForMapping(mapping);
            if (!dma.IsSuccess)
            {
                firstFailure ??= dma;
                continue;
            }

            KernelResult<PlatformRegionMappingLifecycle> lifecycle = PlatformAuthority.BeginRegionMappingRevocation(
                mapping,
                subject,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke);

            if (!lifecycle.IsSuccess)
            {
                firstFailure ??= KernelResult.Fail(lifecycle.Error, lifecycle.Message!);
                continue;
            }

            KernelResult finalize = FinalizePlatformRegionMappingClosure(
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
        KernelResult source = PlatformAuthority.ValidateRegionMappingReleaseSource(mapping, identity);
        if (!source.IsSuccess) return source;
        List<BudgetTraceObservation> observations = new List<BudgetTraceObservation>();
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
            KernelResult<SingProcess> current = Processes.Resolve(identity.Process);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (PlatformIdentity(current.Value!) != identity)
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact mapping cleanup process incarnation changed.");
            // The supplied observation is evidence of the provider stage, not a
            // license to release against changed local mapping/reset owners.
            KernelResult<PlatformRegionMappingLifecycle> fresh = PlatformAuthority.QueryRegionMappingLifecycle(mapping, identity);
        if (!fresh.IsSuccess) return KernelResult.Fail(fresh.Error, fresh.Message!);
        lifecycle = fresh.Value!;
        if (lifecycle.PlatformClosure == PlatformExternalClosureState.Faulted && !lifecycle.AdmissionNoEffectProven)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform region mapping closure faulted; local reclaim remains forbidden.");
        }

        if (lifecycle.PlatformClosure != PlatformExternalClosureState.Closed && !lifecycle.AdmissionNoEffectProven)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "The platform region mapping is still draining; local reclaim remains forbidden.");
        }

        if (lifecycle.LocalReservationReleased)
        {
                KernelResult existingBudgetRelease = ReleaseMappingBudget(mapping, observations);
            if (!existingBudgetRelease.IsSuccess) return existingBudgetRelease;
            UntrackPlatformMapping(mapping);
            PlatformAuthority.ForgetExactMappingMetadata(mapping);
            return KernelResult.Ok();
        }

            RegionOwner owner = new RegionOwner(identity.DomainId, identity.ProcessGeneration);
            KernelResult<object> gate = Regions.GetPlatformMappingCompletionGate(mapping.Region, owner);
        if (!gate.IsSuccess) return KernelResult.Fail(gate.Error, gate.Message!);
        lock (gate.Value!)
        {
                KernelResult release = PlatformAuthority.CommitRegionReservationRelease(mapping, identity, Regions);
            if (!release.IsSuccess) return release;
        }

            KernelResult budgetRelease = ReleaseMappingBudget(mapping, observations);
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
