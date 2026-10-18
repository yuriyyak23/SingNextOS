namespace SingPlus.Platform;
public sealed record PlatformOriginalGuestMapRequest(PlatformGuestRegionMappingRequest MappingRequest);
public interface IPlatformOriginalGuestMapProvider
{
    bool SupportsOriginalGuestMapResolution { get; }
    PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapOriginalGuest(PlatformOriginalGuestMapRequest request);
    PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> ResolveOriginalGuestMap(PlatformOriginalGuestMapRequest originalRequest);
}
