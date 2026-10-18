using SingPlus.Contracts;

namespace SingPlus.Platform;

public readonly record struct PlatformProviderSecureDomainId(ulong Value);
public readonly record struct PlatformProviderSecureDomainGeneration(ulong Value);
public readonly record struct PlatformProviderSecureDomainLease(
    PlatformProviderSecureDomainId DomainId,
    PlatformProviderSecureDomainGeneration Generation,
    PlatformProviderDomainLease Parent,
    IReadOnlyList<SecureProperty> ProvenProperties);
public readonly record struct PlatformSecureDomainRequest(PlatformProviderDomainLease Parent, SecureDomainProfile Profile);
public readonly record struct PlatformProviderSecureRegionBindingId(ulong Value);
public readonly record struct PlatformProviderSecureRegionBindingGeneration(ulong Value);
public readonly record struct PlatformProviderSecureRegionBinding(
    PlatformProviderSecureRegionBindingId BindingId,
    PlatformProviderSecureRegionBindingGeneration Generation,
    PlatformProviderSecureDomainLease Domain,
    PlatformProviderRegionMappingLease Mapping,
    PlatformSecureRegionClass RegionClass);
public enum PlatformSecureRegionClass { Private = 0, Shared }
public enum PlatformSecureDomainTransition { Start = 0, Park, Resume, BeginDrain }
public readonly record struct PlatformSecureRegionClosureReceipt(PlatformProviderSecureRegionBinding Binding, bool Closed);
public readonly record struct PlatformSecureDomainTransitionReceipt(
    PlatformProviderSecureDomainLease Domain,
    PlatformSecureDomainTransition Transition,
    bool Accepted);
public readonly record struct PlatformSecureDomainClosureReceipt(PlatformProviderSecureDomainLease Domain, bool Closed);

public static class PlatformSecureComputeContract { public const uint ContractVersion = 1; }

public interface IPlatformSecureComputeProvider
{
    PlatformAuthorityResult<PlatformProviderSecureDomainLease> CreateSecureDomain(PlatformSecureDomainRequest request);
    PlatformAuthorityResult<PlatformProviderSecureRegionBinding> BindSecureRegion(PlatformProviderSecureDomainLease domain, PlatformProviderRegionMappingLease mapping, PlatformSecureRegionClass regionClass);
    PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> UnbindSecureRegion(PlatformProviderSecureRegionBinding binding);
    PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> TransitionSecureDomain(PlatformProviderSecureDomainLease domain, PlatformSecureDomainTransition transition);
    PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> RevokeSecureDomain(PlatformProviderSecureDomainLease domain);
}
