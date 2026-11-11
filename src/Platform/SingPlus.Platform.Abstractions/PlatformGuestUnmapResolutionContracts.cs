namespace SingPlus.Platform;
public interface IPlatformOriginalGuestUnmapProvider
{
    // Pure resolution: NotAccepted proves exact original no-effect; pending/absence is Faulted.
    bool SupportsOriginalGuestUnmapResolution { get; }
    PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> ResolveOriginalGuestUnmap(PlatformProviderGuestRegionMappingLease originalMapping);
}
