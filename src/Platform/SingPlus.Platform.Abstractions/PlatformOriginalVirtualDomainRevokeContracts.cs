namespace SingPlus.Platform;

public sealed class PlatformOriginalVirtualDomainRevokeRequest
{
    private readonly PlatformOriginalVirtualDomainRevokeRequest _original;
    public PlatformOriginalVirtualDomainRevokeRequest(PlatformProviderVirtualDomainLease lease,PlatformProviderIncarnation incarnation)
    {_original=this;Lease=lease;Incarnation=incarnation;}
    public bool IsOriginal=>ReferenceEquals(_original,this);
    public PlatformProviderVirtualDomainLease Lease {get;}
    public PlatformProviderIncarnation Incarnation {get;}
}

/// <summary>Original model lifetime closure, independently of transition completion.</summary>
public sealed class PlatformOriginalVirtualDomainRevokeObservation
{
    private readonly PlatformOriginalVirtualDomainRevokeObservation _original;
    public PlatformOriginalVirtualDomainRevokeObservation(PlatformOriginalVirtualDomainRevokeRequest request,bool noFurtherDomainUse)
    {_original=this;Request=request;NoFurtherDomainUse=noFurtherDomainUse;}
    public bool IsOriginal=>ReferenceEquals(_original,this);
    public PlatformOriginalVirtualDomainRevokeRequest Request {get;}
    public bool NoFurtherDomainUse {get;}
}

public interface IPlatformOriginalVirtualDomainRevokeProvider
{
    bool SupportsOriginalVirtualDomainRevocation {get;}
    PlatformOriginalVirtualDomainRevokeObservation? RevokeOriginalVirtualDomain(PlatformOriginalVirtualDomainRevokeRequest request);
    /// <summary>Pure factory-original lookup; null/absence is not closure.</summary>
    PlatformOriginalVirtualDomainRevokeObservation? ResolveOriginalVirtualDomainRevoke(PlatformOriginalVirtualDomainRevokeRequest request);
}
