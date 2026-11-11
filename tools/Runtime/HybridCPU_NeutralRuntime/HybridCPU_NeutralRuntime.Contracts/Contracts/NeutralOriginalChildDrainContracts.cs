namespace YAKSys_Hybrid_CPU.Core;

public sealed record NeutralOriginalChildDrainRequest(NeutralChildDomainLease Child);
public sealed record NeutralOriginalChildDrainObservation
{
    public NeutralOriginalChildDrainObservation(NeutralOriginalChildDrainRequest request, NeutralVirtualizationResult result)
    { Request = request; Result = result; OriginalOwner = this; }
    public NeutralOriginalChildDrainRequest Request { get; }
    public NeutralVirtualizationResult Result { get; }
    public NeutralOriginalChildDrainObservation OriginalOwner { get; }
}
public interface INeutralOriginalChildDrainProvider
{
    bool SupportsOriginalChildDrain { get; }
    NeutralOriginalChildDrainObservation? BeginOriginalChildDrain(NeutralOriginalChildDrainRequest request);
    NeutralOriginalChildDrainObservation? ResolveOriginalChildDrain(NeutralOriginalChildDrainRequest originalRequest);
}
