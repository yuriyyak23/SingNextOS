namespace YAKSys_Hybrid_CPU.Core;
public sealed record NeutralOriginalChildCreateRequest(NeutralDomainBindingLease Parent, NeutralChildDomainIntent Intent);
public interface INeutralOriginalChildCreateProvider
{
    bool SupportsOriginalChildCreateResolution { get; }
    NeutralVirtualizationResult<NeutralChildDomainLease> CreateOriginalChild(NeutralOriginalChildCreateRequest request);
    NeutralVirtualizationResult<NeutralChildDomainLease> ResolveOriginalChildCreate(NeutralOriginalChildCreateRequest originalRequest);
}
public interface INeutralOriginalExternalParentBindNoEffectProvider
{
    bool HasOriginalExternalParentBindNoEffect(NeutralOriginalChildCreateRequest originalRequest);
}
