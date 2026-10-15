namespace SingPlus.Platform;

/// <summary>Original model operation identity; not permission or domain closure.</summary>
public sealed class PlatformOriginalVirtualDomainTransitionRequest
{
    private readonly PlatformOriginalVirtualDomainTransitionRequest _original;
    public PlatformOriginalVirtualDomainTransitionRequest(PlatformProviderVirtualDomainLease lease,
        PlatformVirtualDomainTransition transition,PlatformProviderIncarnation incarnation)
    {_original=this;Lease=lease;Transition=transition;Incarnation=incarnation;}
    public bool IsOriginal => ReferenceEquals(_original,this);
    public PlatformProviderVirtualDomainLease Lease {get;}
    public PlatformVirtualDomainTransition Transition {get;}
    public PlatformProviderIncarnation Incarnation {get;}
}

/// <summary>Factory-original completed operation/no-effect. The domain can remain live.</summary>
public sealed class PlatformOriginalVirtualDomainTransitionObservation
{
    private readonly PlatformOriginalVirtualDomainTransitionObservation _original;
    public PlatformOriginalVirtualDomainTransitionObservation(PlatformOriginalVirtualDomainTransitionRequest request,
        PlatformAuthorityResult result,ulong revision,bool noEffect)
    {_original=this;Request=request;Result=result;Revision=revision;NoEffect=noEffect;}
    public bool IsOriginal => ReferenceEquals(_original,this);
    public PlatformOriginalVirtualDomainTransitionRequest Request {get;}
    public PlatformAuthorityResult Result {get;}
    public ulong Revision {get;}
    public bool NoEffect {get;}
    public bool NoFurtherTransitionUse => NoEffect && Result.Status==PlatformAuthorityStatus.NotAccepted || !NoEffect && Result.IsSuccess;
}

public interface IPlatformOriginalVirtualDomainTransitionProvider
{
    bool SupportsOriginalVirtualDomainTransitions {get;}
    PlatformOriginalVirtualDomainTransitionObservation? TransitionOriginalVirtualDomain(PlatformOriginalVirtualDomainTransitionRequest request);
    /// <summary>Pure same-original lookup. Null/absence is not no-effect or closure.</summary>
    PlatformOriginalVirtualDomainTransitionObservation? ResolveOriginalVirtualDomainTransition(PlatformOriginalVirtualDomainTransitionRequest request);
}
