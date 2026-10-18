namespace YAKSys_Hybrid_CPU.Core;

public interface INeutralMappingOperations
{
    NeutralOwnedRegionMapResult MapOwnedRegion(NeutralDomainBindingLease domain, NeutralOwnedRegionSlice slice);
    NeutralOwnedRegionCloseResult CloseOwnedRegionMapping(NeutralOwnedRegionMappingLease lease);
    NeutralOwnedRegionVisibilityResult PrepareOwnedRegionVisibility(NeutralOwnedRegionMappingLease lease, NeutralMemoryVisibilityRequirement requirement);
    NeutralOwnedRegionAcquireResult AcquireOwnedRegionVisibility(NeutralOwnedRegionMappingLease lease, NeutralMemoryAcquireRequirement requirement);
}

public readonly record struct NeutralOriginalMappingCloseRequest(
    Guid InvocationId, Guid RuntimeIncarnation, NeutralOwnedRegionMappingLease Mapping);

public readonly record struct NeutralOriginalMappingCloseResolution(
    NeutralOriginalMappingCloseRequest Request, NeutralOwnedRegionCloseResult Observation);

/// <summary>Optional exact original Close response query; Resolve never closes,
/// repeats effects or interprets absence/lifecycle as original closure.</summary>
public interface INeutralOriginalMappingClosureProvider
{
    Guid MappingClosureIncarnation { get; }
    NeutralOwnedRegionCloseResult CloseOwnedRegionMapping(NeutralOriginalMappingCloseRequest request);
    NeutralOriginalMappingCloseResolution? ResolveOwnedRegionMappingClose(NeutralOriginalMappingCloseRequest request);
}
