namespace SingPlus.Platform;

public enum PlatformDmaDirection
{
    DeviceReadsMemory = 0,
    DeviceWritesMemory,
    Bidirectional,
}

public readonly record struct PlatformDmaRange(long Offset, long Length)
{
    public bool Fits(long mappingLength) =>
        mappingLength > 0 &&
        Offset >= 0 &&
        Length > 0 &&
        Length <= mappingLength &&
        Offset <= mappingLength - Length;
}

public readonly record struct PlatformDmaGrantRequest(
    PlatformProviderDeviceLease DeviceLease,
    PlatformProviderRegionMappingLease MappingLease,
    PlatformRegionSlice MappingSlice,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction);

public readonly record struct PlatformProviderDmaGrantId(ulong Value);

public readonly record struct PlatformProviderDmaGrant(
    PlatformProviderDmaGrantId GrantId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDeviceLease DeviceLease,
    PlatformProviderRegionMappingLease MappingLease,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction);

/// <summary>
/// DMA authority family contract. Version 1 established exact admission-only grants;
/// version 2 adds grant-scoped non-coherent prepare/acquire visibility cycles. A grant
/// still does not submit a transfer and carries no bus address, IOMMU identity,
/// descriptor/queue identity, or completion authority.
/// </summary>
public static class PlatformDmaGrantContract
{
    public const uint ContractVersion = 2;

    public static PlatformAuthorityResult ValidateRequest(PlatformDmaGrantRequest request)
    {
        if (request.DeviceLease.LeaseId.Value == 0 ||
            request.DeviceLease.Generation.Value == 0 ||
            request.MappingLease.MappingId.Value == 0 ||
            request.MappingLease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA grant admission requires materialized provider device and mapping identities.");
        }

        var slice = PlatformOwnedRegionMappingContract.ValidateSlice(request.MappingSlice);
        if (!slice.IsSuccess) return slice;

        if (request.DeviceLease.DomainLease != request.MappingLease.DomainLease)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "DMA device and region mapping must belong to the exact same provider domain lifetime.");
        }

        if (request.MappingLease.Region != request.MappingSlice.Region ||
            request.MappingLease.Access != request.MappingSlice.Access)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA grant admission must bind the exact provider mapping to its exact region slice.");
        }

        if (!request.Range.Fits(request.MappingSlice.Length))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA grant range must be positive, non-overflowing, and contained in the exact mapped slice.");
        }

        if (!Enum.IsDefined(request.Direction))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA direction is undefined.");
        }

        var requiredDeviceRights = PlatformDeviceRights.Configure;
        var requiredMappingAccess = PlatformMemoryAccess.None;
        switch (request.Direction)
        {
            case PlatformDmaDirection.DeviceReadsMemory:
                requiredDeviceRights |= PlatformDeviceRights.Read;
                requiredMappingAccess |= PlatformMemoryAccess.Read;
                break;
            case PlatformDmaDirection.DeviceWritesMemory:
                requiredDeviceRights |= PlatformDeviceRights.Write;
                requiredMappingAccess |= PlatformMemoryAccess.Write;
                break;
            case PlatformDmaDirection.Bidirectional:
                requiredDeviceRights |= PlatformDeviceRights.Read | PlatformDeviceRights.Write;
                requiredMappingAccess |= PlatformMemoryAccess.Read | PlatformMemoryAccess.Write;
                break;
        }

        if ((request.DeviceLease.Rights & requiredDeviceRights) != requiredDeviceRights)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease lacks Configure plus the rights required by the DMA direction.");
        }

        if ((request.MappingSlice.Access & requiredMappingAccess) != requiredMappingAccess)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The exact mapped-region access does not cover the requested DMA direction.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateGrant(
        PlatformDmaGrantRequest request,
        PlatformProviderDmaGrant grant)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (grant.GrantId.Value == 0 || grant.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider DMA grant identity must be materialized.");
        }

        if (grant.DeviceLease != request.DeviceLease ||
            grant.MappingLease != request.MappingLease ||
            grant.Range != request.Range ||
            grant.Direction != request.Direction)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider DMA grant does not match the exact admission request.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDmaGrantProvider
{
    PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
        PlatformDmaGrantRequest request);

    PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant);
}
