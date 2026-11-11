namespace YAKSys_Hybrid_CPU.Core;
public interface INeutralOriginalGuestUnmapProvider
{
    bool SupportsOriginalGuestUnmapResolution { get; }
    NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> ResolveOriginalGuestUnmap(NeutralGuestMappingLease originalMapping);
}
