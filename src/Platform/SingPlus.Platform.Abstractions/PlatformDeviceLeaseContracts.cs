namespace SingPlus.Platform;

[Flags]
public enum PlatformDeviceRights
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Configure = 1 << 2,
}

public readonly record struct PlatformDeviceIdentity(string ResourceId);
public readonly record struct PlatformProviderDeviceLeaseId(ulong Value);

public readonly record struct PlatformProviderDeviceLease(
    PlatformProviderDeviceLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDomainLease DomainLease,
    PlatformDeviceIdentity Device,
    PlatformDeviceRights Rights);

public static class PlatformDeviceLeaseContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformDeviceIdentity device,
        PlatformDeviceRights rights)
    {
        if (string.IsNullOrWhiteSpace(device.ResourceId) || device.ResourceId.Length > 256)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Platform device identity must be a non-empty bounded semantic resource identifier.");
        }

        if (rights == PlatformDeviceRights.None ||
            (rights & ~(PlatformDeviceRights.Read |
                        PlatformDeviceRights.Write |
                        PlatformDeviceRights.Configure)) != 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Platform device rights must be a non-empty subset of Read, Write, and Configure.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateLease(
        PlatformProviderDomainLease requestedDomain,
        PlatformDeviceIdentity requestedDevice,
        PlatformDeviceRights requestedRights,
        PlatformProviderDeviceLease lease)
    {
        if (lease.LeaseId.Value == 0 || lease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider device lease identity must be materialized.");
        }

        if (lease.DomainLease != requestedDomain)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider device lease belongs to a different platform domain lease.");
        }

        if (lease.Device != requestedDevice || lease.Rights != requestedRights)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider device lease does not match the exact requested device authority.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDeviceLeaseProvider
{
    PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
        PlatformProviderDomainLease domainLease,
        PlatformDeviceIdentity device,
        PlatformDeviceRights rights);

    PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease);
}
