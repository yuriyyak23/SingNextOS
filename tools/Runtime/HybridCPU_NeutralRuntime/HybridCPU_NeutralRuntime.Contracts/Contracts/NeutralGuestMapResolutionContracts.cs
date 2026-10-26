namespace YAKSys_Hybrid_CPU.Core;
public sealed record NeutralOriginalGuestMapRequest(NeutralGuestMappingRequest MappingRequest);
public interface INeutralOriginalGuestMapProvider
{
    bool SupportsOriginalGuestMapResolution { get; }
    NeutralVirtualizationResult<NeutralGuestMappingLease> MapOriginalGuest(NeutralOriginalGuestMapRequest request);
    NeutralVirtualizationResult<NeutralGuestMappingLease> ResolveOriginalGuestMap(NeutralOriginalGuestMapRequest originalRequest);
}
