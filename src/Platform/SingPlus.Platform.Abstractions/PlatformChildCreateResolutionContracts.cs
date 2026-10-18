namespace SingPlus.Platform;
public sealed record PlatformOriginalChildCreateRequest(PlatformProviderDomainLease Parent, PlatformChildDomainIntent Intent);
public interface IPlatformOriginalChildCreateProvider
{
    bool SupportsOriginalChildCreateResolution { get; }
    PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateOriginalChild(PlatformOriginalChildCreateRequest request, out bool refusedBeforeEffect);
    PlatformAuthorityResult<PlatformProviderChildDomainLease> ResolveOriginalChildCreate(PlatformOriginalChildCreateRequest originalRequest);
}
