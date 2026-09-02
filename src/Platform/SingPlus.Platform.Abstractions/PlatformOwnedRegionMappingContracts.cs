namespace SingPlus.Platform;

public readonly record struct PlatformRegionSlice(
    PlatformRegionIdentity Region,
    long Offset,
    long Length,
    PlatformMemoryAccess Access)
{
    public bool IsWholeRegion =>
        Offset == 0 && Length == Region.ByteLength;
}

public readonly record struct PlatformProviderOwnedRegionMapping(
    PlatformProviderRegionMappingLease Lease,
    PlatformRegionSlice Slice);

public static class PlatformOwnedRegionMappingContract
{
    public const uint ContractVersion = 2;

    public static PlatformAuthorityResult ValidateSlice(PlatformRegionSlice slice)
    {
        if (slice.Region.Handle.RegionId.Value == 0 ||
            slice.Region.Handle.Generation.Value == 0 ||
            slice.Region.Owner.ProcessGeneration == 0 ||
            slice.Region.ByteLength <= 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Owned-region slices require a materialized region identity, owner generation, and positive byte length.");
        }

        if (!IsValidAccess(slice.Access))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Owned-region slice access must be Read, Write, or Read|Write.");
        }

        if (slice.Offset < 0 ||
            slice.Length <= 0 ||
            slice.Offset > slice.Region.ByteLength - slice.Length)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Owned-region slice range is outside the exact region bounds or overflows them.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateResult(
        PlatformProviderDomainLease expectedDomain,
        PlatformRegionSlice expectedSlice,
        PlatformProviderOwnedRegionMapping result)
    {
        var sliceValidation = ValidateSlice(expectedSlice);
        if (!sliceValidation.IsSuccess) return sliceValidation;

        if (result.Lease.MappingId.Value == 0 ||
            result.Lease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider mapping identities must be non-zero.");
        }

        if (result.Lease.DomainLease.LeaseId != expectedDomain.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider mapping belongs to a different domain lease.");
        }

        if (result.Lease.DomainLease.Generation != expectedDomain.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider mapping domain generation is stale.");
        }

        if (result.Lease.DomainLease.Subject != expectedDomain.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider mapping belongs to a different local subject.");
        }

        if (result.Lease.Region != expectedSlice.Region ||
            result.Lease.Access != expectedSlice.Access ||
            result.Slice != expectedSlice)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider mapping did not commit the exact requested region slice and access.");
        }

        return PlatformAuthorityResult.Ok();
    }

    private static bool IsValidAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;
}

public interface IPlatformOwnedRegionMappingProvider
{
    PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
        PlatformProviderDomainLease domainLease,
        PlatformRegionSlice slice);
}
