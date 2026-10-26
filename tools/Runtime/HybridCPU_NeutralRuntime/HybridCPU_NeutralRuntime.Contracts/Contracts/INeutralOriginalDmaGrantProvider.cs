namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralOriginalDmaGrantRequest(Guid InvocationId, Guid RuntimeIncarnation,
    Guid DomainBindInvocationId, Guid MappingAdmissionInvocationId, NeutralDeviceLease Device,
    NeutralOwnedRegionMappingLease Mapping, NeutralDmaRange Range, NeutralDmaDirection Direction);
public readonly record struct NeutralOriginalDmaGrantObservation(NeutralOriginalDmaGrantRequest Request,
    bool SourceFenceMatched, bool NoEffectGuaranteed, NeutralDmaGrantResult Observation);
public readonly record struct NeutralOriginalDmaGrantCloseRequest(Guid InvocationId, Guid RuntimeIncarnation,
    Guid DmaBindInvocationId, NeutralDmaGrant Grant);
public readonly record struct NeutralOriginalDmaGrantCloseObservation(NeutralOriginalDmaGrantCloseRequest Request,
    bool SourceFenceMatched, bool NoEffectGuaranteed, NeutralDmaGrantCloseResult Observation);

/// <summary>Optional producing-owner original DMA fence. Request intent and exact
/// parent/source validation precede effect under one lifecycle gate. NoEffectGuaranteed
/// proves zero effect of this request only. Pure lookup never closes; missing is unknown.</summary>
public interface INeutralOriginalDmaGrantProvider : INeutralOriginalDomainCloseProvider
{
    NeutralOriginalDmaGrantObservation BindDmaGrant(NeutralOriginalDmaGrantRequest request);
    NeutralOriginalDmaGrantObservation? ResolveDmaGrantBind(NeutralOriginalDmaGrantRequest request);
    NeutralOriginalDmaGrantCloseObservation CloseDmaGrant(NeutralOriginalDmaGrantCloseRequest request);
    NeutralOriginalDmaGrantCloseObservation? ResolveDmaGrantClose(NeutralOriginalDmaGrantCloseRequest request);
}
