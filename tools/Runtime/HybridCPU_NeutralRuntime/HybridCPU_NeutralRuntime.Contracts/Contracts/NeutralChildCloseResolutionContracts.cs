namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Pure lookup of the request retained by this original child owner; absence is not closure.</summary>
public interface INeutralOriginalChildCloseProvider
{
    bool SupportsOriginalChildCloseResolution { get; }
    NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> ResolveOriginalChildClose(NeutralChildDomainLease originalChild);
}
