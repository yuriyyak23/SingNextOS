namespace SingPlus.Platform;

[Flags]
public enum PlatformMmioAccess
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
}

public readonly record struct PlatformMmioRegionIdentity(
    string ResourceId,
    long ByteLength);

public readonly record struct PlatformMmioRange(long Offset, long Length);

public readonly record struct PlatformProviderMmioLeaseId(ulong Value);

public readonly record struct PlatformProviderMmioLease(
    PlatformProviderMmioLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDeviceLease DeviceLease,
    PlatformMmioRegionIdentity Region,
    PlatformMmioRange Range,
    PlatformMmioAccess Access);

public static class PlatformMmioLeaseContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformMmioRegionIdentity region,
        PlatformMmioRange range,
        PlatformMmioAccess access)
    {
        if (string.IsNullOrWhiteSpace(region.ResourceId) ||
            region.ResourceId.Length > 128 ||
            region.ByteLength <= 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Platform MMIO region identity must be bounded and have a positive semantic byte extent.");
        }

        if (range.Offset < 0 ||
            range.Length <= 0 ||
            range.Length > region.ByteLength ||
            range.Offset > region.ByteLength - range.Length)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Platform MMIO range must be positive, non-overflowing, and contained in the exact semantic region extent.");
        }

        if (access == PlatformMmioAccess.None ||
            (access & ~(PlatformMmioAccess.Read | PlatformMmioAccess.Write)) != 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Platform MMIO access must be Read, Write, or Read|Write.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateLease(
        PlatformProviderDeviceLease requestedDevice,
        PlatformMmioRegionIdentity requestedRegion,
        PlatformMmioRange requestedRange,
        PlatformMmioAccess requestedAccess,
        PlatformProviderMmioLease lease)
    {
        if (lease.LeaseId.Value == 0 || lease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider MMIO lease identity must be materialized.");
        }

        if (lease.DeviceLease != requestedDevice ||
            lease.Region != requestedRegion ||
            lease.Range != requestedRange ||
            lease.Access != requestedAccess)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider MMIO lease does not match the exact requested device, region, range, and access authority.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformMmioLeaseProvider
{
    PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(
        PlatformProviderDeviceLease deviceLease,
        PlatformMmioRegionIdentity region,
        PlatformMmioRange range,
        PlatformMmioAccess access);

    PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease);
}
