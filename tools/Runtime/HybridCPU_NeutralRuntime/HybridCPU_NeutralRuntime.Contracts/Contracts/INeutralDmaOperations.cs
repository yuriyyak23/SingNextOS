namespace YAKSys_Hybrid_CPU.Core;

public interface INeutralDmaOperations
{
    NeutralDmaGrantResult BindDmaGrant(NeutralDeviceLease device, NeutralOwnedRegionMappingLease mapping, NeutralDmaRange range, NeutralDmaDirection direction);
    NeutralDmaGrantCloseResult CloseDmaGrant(NeutralDmaGrant grant);
    NeutralDmaPrepareResult PrepareDmaVisibility(NeutralDmaGrant grant);
    NeutralDmaAcquireResult AcquireDmaVisibility(NeutralDmaGrant grant);
}
