using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public PlatformFeatureManifest QueryPlatformFeatures() => PlatformAuthority.FeatureManifest;

    public KernelResult<PlatformDomainBinding> BindPlatformDomain(ProcessHandle subject)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformDomainBinding>.Fail(resolved.Error, resolved.Message!);

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
            return KernelResult<PlatformDomainBinding>.Fail(effect.Error, effect.Message!);

        if (!CanChangePlatformExecutionAttachment(process.State))
        {
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.InvalidTransition,
                $"A platform execution domain cannot be attached while the process is {process.State}; bind before execution starts.");
        }

        var identity = PlatformIdentity(process);
        var binding = PlatformAuthority.BindDomain(identity);
        if (binding.IsSuccess)
            TrackPlatformBinding(subject, binding.Value!);
        else if (PlatformAuthority.TryGetQuarantinedDomainBinding(
                     identity,
                     out var quarantinedBinding))
            TrackPlatformBinding(subject, quarantinedBinding);
        return binding;
    }

    public KernelResult RevokePlatformDomain(
        ProcessHandle subject,
        PlatformDomainBinding binding)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        var process = resolved.Value!;
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

        var identity = PlatformIdentity(process);
        var revoke = PlatformAuthority.RevokeDomain(binding, identity);
        if (revoke.IsSuccess)
            UntrackPlatformBinding(subject, binding);
        return revoke;
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
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(effect.Error, effect.Message!);

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

        var reservation = Regions.ReservePlatformMapping(region, ownerIdentity);
        if (!reservation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                reservation.Error,
                reservation.Message!);

        var platformRegion = new PlatformRegionIdentity(
            regionValidation.Value!.Handle,
            regionValidation.Value.Owner,
            regionValidation.Value.ByteLength);

        var mapping = PlatformAuthority.MapOwnedRegion(
            binding,
            identity,
            capabilityId,
            platformRegion,
            access);

        if (!mapping.IsSuccess)
        {
            _ = Regions.ReleasePlatformMappingReservation(region, ownerIdentity);
            return mapping;
        }

        TrackPlatformMapping(owner, mapping.Value!);
        return mapping;
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
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                effect.Error,
                effect.Message!);
        }

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

        var reservation = Regions.ReservePlatformMapping(region, ownerIdentity);
        if (!reservation.IsSuccess)
        {
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(
                reservation.Error,
                reservation.Message!);
        }

        var mapping = PlatformAuthority.MapOwnedRegionSlice(
            binding,
            identity,
            capabilityId,
            slice);
        if (!mapping.IsSuccess)
        {
            _ = Regions.ReleasePlatformMappingReservation(region, ownerIdentity);
            return mapping;
        }

        TrackPlatformMapping(owner, mapping.Value!.Mapping);
        return mapping;
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
        return PlatformAuthority.PrepareRegionMappingForConsumer(
            mapping,
            identity,
            consumer,
            requirement);
    }

    public KernelResult RevokePlatformRegionMapping(
        ProcessHandle owner,
        PlatformOwnedRegionSliceMapping mapping) =>
        RevokePlatformRegionMapping(owner, mapping.Mapping);

    public KernelResult RevokePlatformRegionMapping(
        ProcessHandle owner,
        PlatformRegionMapping mapping)
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        var dma = AdvancePlatformDmaGrantsForMapping(mapping);
        if (!dma.IsSuccess) return dma;

        var identity = PlatformIdentity(resolved.Value!);
        var lifecycle = PlatformAuthority.BeginRegionMappingRevocation(
            mapping,
            identity,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        return FinalizePlatformRegionMappingClosure(mapping, identity, lifecycle.Value!);
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
        var mappings = PlatformAuthority.BeginCapabilityRevocation(capabilityId);
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

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult FinalizePlatformRegionMappingClosure(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity identity,
        PlatformRegionMappingLifecycle lifecycle)
    {
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
            UntrackPlatformMapping(mapping);
            PlatformAuthority.ForgetExactMappingMetadata(mapping);
            return KernelResult.Ok();
        }

        var bindingValidation = PlatformAuthority.ValidateDomain(
            mapping.DomainBinding,
            identity);
        if (!bindingValidation.IsSuccess) return bindingValidation;

        var owner = new RegionOwner(identity.DomainId, identity.ProcessGeneration);
        var regionValidation = Regions.Validate(mapping.Region, owner);
        if (!regionValidation.IsSuccess)
            return KernelResult.Fail(regionValidation.Error, regionValidation.Message!);

        var release = Regions.ReleasePlatformMappingReservation(mapping.Region, owner);
        if (!release.IsSuccess) return release;

        var markReleased = PlatformAuthority.MarkRegionReservationReleased(mapping, identity);
        if (!markReleased.IsSuccess)
        {
            _ = Regions.ReservePlatformMapping(mapping.Region, owner);
            return markReleased;
        }

        UntrackPlatformMapping(mapping);
        PlatformAuthority.ForgetExactMappingMetadata(mapping);
        return KernelResult.Ok();
    }

    private static bool IsValidPlatformAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;
}
