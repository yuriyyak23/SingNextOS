using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformDomainBinding> BindPlatformDomain(ProcessHandle subject)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformDomainBinding>.Fail(resolved.Error, resolved.Message!);

        var identity = new PlatformDomainIdentity(resolved.Value!.DomainId, subject.Generation);
        return PlatformAuthority.BindDomain(identity);
    }

    public KernelResult RevokePlatformDomain(
        ProcessHandle subject,
        PlatformDomainBinding binding)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        var identity = new PlatformDomainIdentity(resolved.Value!.DomainId, subject.Generation);
        return PlatformAuthority.RevokeDomain(binding, identity);
    }

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
        var identity = new PlatformDomainIdentity(process.DomainId, owner.Generation);

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
            platformRegion,
            access);

        if (!mapping.IsSuccess)
        {
            _ = Regions.ReleasePlatformMappingReservation(region, ownerIdentity);
            return mapping;
        }

        return mapping;
    }

    public KernelResult RevokePlatformRegionMapping(
        ProcessHandle owner,
        PlatformRegionMapping mapping)
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        var process = resolved.Value!;
        var identity = new PlatformDomainIdentity(process.DomainId, owner.Generation);
        var validation = PlatformAuthority.ValidateMapping(mapping, identity);
        if (!validation.IsSuccess) return validation;

        var revoke = PlatformAuthority.RevokeRegionMapping(mapping, identity);
        if (!revoke.IsSuccess) return revoke;

        return Regions.ReleasePlatformMappingReservation(
            mapping.Region,
            new RegionOwner(process.DomainId, owner.Generation));
    }

    private static bool IsValidPlatformAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;
}
