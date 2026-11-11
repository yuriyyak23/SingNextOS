namespace SingPlus.Platform;

public sealed class PlatformOriginalModelCreateRequest(PlatformDomainIdentity owner,PlatformVirtualDomainProfile profile)
{
    public PlatformDomainIdentity Owner { get; }=owner;
    public PlatformVirtualDomainProfile Profile { get; }=profile;
}
public sealed record PlatformOriginalModelCreateObservation(PlatformOriginalModelCreateRequest Request,
    PlatformAuthorityResult<PlatformProviderVirtualDomainLease> Outcome);

public interface IPlatformOriginalModelCreateProvider
{
    PlatformOriginalModelCreateObservation? CreateOriginalModelDomain(PlatformOriginalModelCreateRequest request);
    PlatformOriginalModelCreateObservation? QueryOriginalModelCreate(PlatformOriginalModelCreateRequest request);
}
