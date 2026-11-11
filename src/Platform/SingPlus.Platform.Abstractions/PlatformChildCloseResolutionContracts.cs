namespace SingPlus.Platform;

/// <summary>Pure resolution of the surviving request retained on this original child owner.
/// NotAccepted means exact original no-effect; pending/absence/unknown must return Faulted.</summary>
public interface IPlatformOriginalChildCloseProvider
{
    bool SupportsOriginalChildCloseResolution { get; }
    PlatformAuthorityResult<PlatformChildDomainClosureReceipt> ResolveOriginalChildClose(PlatformProviderChildDomainLease originalChild);
}
