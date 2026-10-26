namespace SingPlus.Platform;

public sealed record PlatformOriginalNestedDomainRequest(PlatformNestedDomainRequest Request);
public sealed record PlatformOriginalNestedDomainClosure(PlatformOriginalNestedDomainRequest Request, bool NoEffect, bool NoFurtherAdmission, bool NoFurtherChildUse);
public interface IPlatformOriginalNestedDomainProvider
{
    // NotAccepted from pure resolution of the same retained request proves exact
    // no-effect/no-further-admission for that invocation; V1 absence does not.
    bool SupportsOriginalNestedDomains { get; }
    PlatformAuthorityResult<PlatformProviderNestedDomainLease> CreateOriginalNestedDomain(PlatformOriginalNestedDomainRequest request);
    PlatformAuthorityResult<PlatformProviderNestedDomainLease> ResolveOriginalNestedDomain(PlatformOriginalNestedDomainRequest originalRequest);
    PlatformAuthorityResult<PlatformOriginalNestedDomainClosure> ContainOriginalNestedDomain(PlatformOriginalNestedDomainRequest originalRequest);
    PlatformAuthorityResult<PlatformOriginalNestedDomainClosure> ResolveOriginalNestedDomainCleanup(PlatformOriginalNestedDomainRequest originalRequest);
}
