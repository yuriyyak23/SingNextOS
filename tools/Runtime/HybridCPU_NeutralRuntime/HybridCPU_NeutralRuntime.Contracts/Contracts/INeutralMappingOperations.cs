namespace YAKSys_Hybrid_CPU.Core;

public interface INeutralMappingOperations
{
    NeutralOwnedRegionMapResult MapOwnedRegion(NeutralDomainBindingLease domain, NeutralOwnedRegionSlice slice);
    NeutralOwnedRegionCloseResult CloseOwnedRegionMapping(NeutralOwnedRegionMappingLease lease);
    NeutralOwnedRegionVisibilityResult PrepareOwnedRegionVisibility(NeutralOwnedRegionMappingLease lease, NeutralMemoryVisibilityRequirement requirement);
    NeutralOwnedRegionAcquireResult AcquireOwnedRegionVisibility(NeutralOwnedRegionMappingLease lease, NeutralMemoryAcquireRequirement requirement);
}
