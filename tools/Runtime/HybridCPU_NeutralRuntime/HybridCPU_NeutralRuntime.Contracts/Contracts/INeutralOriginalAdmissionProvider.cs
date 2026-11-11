namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralOriginalDomainBindRequest(
    Guid InvocationId, Guid RuntimeIncarnation, NeutralDomainProfile Profile);

public readonly record struct NeutralOriginalOwnedRegionMapRequest(
    Guid InvocationId, Guid RuntimeIncarnation, Guid DomainBindInvocationId,
    NeutralDomainBindingLease Domain, NeutralOwnedRegionSlice Slice);

public readonly record struct NeutralOriginalDomainBindObservation(
    NeutralOriginalDomainBindRequest Request, bool SourceFenceMatched, NeutralDomainBindResult Observation)
{
    // Explicit original-owner disposition; generic failure/default lease is not no-effect.
    public bool ProvenNoEffect { get; init; }
}

public readonly record struct NeutralOriginalOwnedRegionMapObservation(
    NeutralOriginalOwnedRegionMapRequest Request, bool SourceFenceMatched, NeutralOwnedRegionMapResult Observation)
{
    public bool ProvenNoEffect { get; init; }
}

/// <summary>Optional producing-owner fence. Source comparison and admission execute
/// under the same lifecycle lock. A false SourceFenceMatched proves this request
/// performed no effect in this runtime; it proves neither closure nor acceptance
/// elsewhere. Missing/malformed responses are unknown. No durable recovery implied.</summary>
public interface INeutralOriginalAdmissionProvider : INeutralOriginalMappingClosureProvider
{
    NeutralOriginalDomainBindObservation Bind(NeutralOriginalDomainBindRequest request);
    NeutralOriginalOwnedRegionMapObservation MapOwnedRegion(NeutralOriginalOwnedRegionMapRequest request);
}

/// <summary>Pure lookup of a surviving original Map invocation; absence is unknown.</summary>
public interface INeutralOriginalMapOutcomeProvider : INeutralOriginalAdmissionProvider
{
    NeutralOriginalOwnedRegionMapObservation? ResolveOriginalOwnedRegionMap(NeutralOriginalOwnedRegionMapRequest request);
}

public interface INeutralOriginalBindOutcomeProvider : INeutralOriginalAdmissionProvider
{
    NeutralOriginalDomainBindObservation? ResolveOriginalDomainBind(NeutralOriginalDomainBindRequest request);
}

public readonly record struct NeutralOriginalDomainCloseRequest(
    Guid InvocationId, Guid RuntimeIncarnation, Guid DomainBindInvocationId, NeutralDomainBindingLease Domain);
public readonly record struct NeutralOriginalDomainCloseObservation(
    NeutralOriginalDomainCloseRequest Request, bool SourceFenceMatched, NeutralDomainCloseResult Observation);

/// <summary>Optional same-original Close fence and surviving outcome lookup. Lookup
/// never closes and absence proves neither no-effect nor closure.</summary>
public interface INeutralOriginalDomainCloseProvider : INeutralOriginalAdmissionProvider
{
    NeutralOriginalDomainCloseObservation Close(NeutralOriginalDomainCloseRequest request);
    NeutralOriginalDomainCloseObservation? ResolveDomainClose(NeutralOriginalDomainCloseRequest request);
}

/// <summary>Cleanup-only continuation of an already retained original root Close.
/// It cannot create intent; pure ResolveDomainClose never performs the effect.</summary>
public interface INeutralOriginalDomainCloseContinuationProvider : INeutralOriginalDomainCloseProvider
{
    NeutralOriginalDomainCloseObservation ContinueOriginalDomainClose(NeutralOriginalDomainCloseRequest request);
}
