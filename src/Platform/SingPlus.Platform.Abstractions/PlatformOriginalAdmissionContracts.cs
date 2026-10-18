namespace SingPlus.Platform;

/// <summary>Pure surviving-original root Close lookup. Failure never establishes
/// no-effect or closure and never authorizes another revoke invocation.</summary>
public interface IPlatformOriginalDomainClosureProvider
{
    PlatformAuthorityResult ResolveDomainClosure(PlatformProviderDomainLease lease);
}

public readonly record struct PlatformDomainBindRequest(Guid InvocationId, PlatformDomainIdentity Subject);
public readonly record struct PlatformOwnedRegionMapRequest(Guid InvocationId, PlatformProviderDomainLease Domain, PlatformRegionSlice Slice);
public readonly record struct PlatformDomainBindResolution(PlatformDomainBindRequest Request, PlatformProviderDomainLease Lease)
{
    // Exact original invocation proof; no lease, permission or closure is created.
    public bool ProvenNoEffect { get; init; }
}
public readonly record struct PlatformOwnedRegionMapResolution(PlatformOwnedRegionMapRequest Request, PlatformProviderOwnedRegionMapping Mapping)
{
    public bool ProvenNoEffect { get; init; }
}

/// <summary>Optional surviving-original admission resolution. Request keys are
/// retained before effect. Resolve never repeats an effect, treats absence as
/// closure or grants permission; success exposes exact retained cleanup facts
/// at their original owner/source. Unknown/lost facts remain unresolved.</summary>
public interface IPlatformOriginalAdmissionResolutionProvider : IPlatformAuthorityProvider, IPlatformOwnedRegionMappingProvider
{
    PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainBindRequest request);
    PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(PlatformOwnedRegionMapRequest request);
    PlatformAuthorityResult<PlatformDomainBindResolution> ResolveDomainBind(PlatformDomainBindRequest request);
    PlatformAuthorityResult<PlatformOwnedRegionMapResolution> ResolveOwnedRegionMap(PlatformOwnedRegionMapRequest request);
}
