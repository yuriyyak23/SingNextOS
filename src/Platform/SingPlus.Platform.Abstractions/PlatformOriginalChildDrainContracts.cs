namespace SingPlus.Platform;

public sealed record PlatformOriginalChildDrainRequest(PlatformProviderChildDomainLease Child);
public sealed record PlatformOriginalChildDrainObservation
{
    public PlatformOriginalChildDrainObservation(PlatformOriginalChildDrainRequest request, PlatformAuthorityResult result)
    { Request = request; Result = result; OriginalOwner = this; }
    public PlatformOriginalChildDrainRequest Request { get; }
    public PlatformAuthorityResult Result { get; }
    public PlatformOriginalChildDrainObservation OriginalOwner { get; }
}
public interface IPlatformOriginalChildDrainProvider
{
    bool SupportsOriginalChildDrain { get; }
    PlatformAuthorityResult BeginOriginalChildDrain(PlatformOriginalChildDrainRequest request);
    PlatformAuthorityResult ResolveOriginalChildDrain(PlatformOriginalChildDrainRequest originalRequest);
    PlatformOriginalChildDrainObservation? ObserveOriginalChildDrain(PlatformOriginalChildDrainRequest originalRequest);
}
