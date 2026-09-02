using SingPlus.Contracts;

namespace SingPlus.Platform;

[Flags]
public enum PlatformAuthorityFeatures
{
    None = 0,
    NeutralDomainBinding = 1 << 0,
    DirectOwnedRegionMapping = 1 << 1
}

public enum PlatformAuthorityStatus
{
    Success = 0,
    Unavailable,
    Unsupported,
    Denied,
    Stale,
    Revoked,
    WrongDomain,
    Faulted
}

[Flags]
public enum PlatformMemoryAccess
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1
}

public enum PlatformRegionRevocationPolicy
{
    DrainBeforeRevoke = 0
}

public readonly record struct PlatformDomainIdentity(DomainId DomainId, ulong ProcessGeneration);

public readonly record struct PlatformRegionIdentity(
    RegionHandle Handle,
    RegionOwner Owner,
    long ByteLength);

public readonly record struct PlatformProviderDomainLeaseId(ulong Value);
public readonly record struct PlatformProviderRegionMappingId(ulong Value);
public readonly record struct PlatformProviderLeaseGeneration(ulong Value);

public readonly record struct PlatformProviderDomainLease(
    PlatformProviderDomainLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformDomainIdentity Subject);

public readonly record struct PlatformProviderRegionMappingLease(
    PlatformProviderRegionMappingId MappingId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDomainLease DomainLease,
    PlatformRegionIdentity Region,
    PlatformMemoryAccess Access);

public sealed record PlatformProviderDescriptor(
    string ProviderId,
    uint ContractVersion,
    PlatformAuthorityFeatures Features);

public readonly record struct PlatformAuthorityResult(
    PlatformAuthorityStatus Status,
    string? Message)
{
    public bool IsSuccess => Status == PlatformAuthorityStatus.Success;

    public static PlatformAuthorityResult Ok() => new(PlatformAuthorityStatus.Success, null);

    public static PlatformAuthorityResult Fail(PlatformAuthorityStatus status, string message)
    {
        if (status == PlatformAuthorityStatus.Success)
            throw new ArgumentOutOfRangeException(nameof(status));

        return new PlatformAuthorityResult(status, message);
    }
}

public readonly record struct PlatformAuthorityResult<T>(
    PlatformAuthorityStatus Status,
    T? Value,
    string? Message)
{
    public bool IsSuccess => Status == PlatformAuthorityStatus.Success;

    public static PlatformAuthorityResult<T> Ok(T value) =>
        new(PlatformAuthorityStatus.Success, value, null);

    public static PlatformAuthorityResult<T> Fail(PlatformAuthorityStatus status, string message)
    {
        if (status == PlatformAuthorityStatus.Success)
            throw new ArgumentOutOfRangeException(nameof(status));

        return new PlatformAuthorityResult<T>(status, default, message);
    }
}

public interface IPlatformAuthorityProvider
{
    PlatformProviderDescriptor Descriptor { get; }

    PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject);

    PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease);

    PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
        PlatformProviderDomainLease domainLease,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access);

    PlatformAuthorityResult RevokeRegionMapping(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy);
}
