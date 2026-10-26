namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralOriginalDmaVisibilityRequest(Guid InvocationId, Guid RuntimeIncarnation,
    Guid DmaBindInvocationId, NeutralDmaGrant Grant, bool Acquire, NeutralDmaVisibilityCycle ExpectedCycle);
public readonly record struct NeutralOriginalDmaVisibilityObservation(NeutralOriginalDmaVisibilityRequest Request,
    bool SourceFenceMatched, bool NoEffectGuaranteed, NeutralDmaPrepareResult? Prepare, NeutralDmaAcquireResult? Acquire);

/// <summary>Original visibility source/parent/cycle checks and effect under the existing
/// producing lifecycle gate. Exact original grant Close serializes with all visibility
/// invocations and prohibits subsequent visibility use, including lost visibility replies.
/// This guarantees neither physical DMA containment nor closure of transfer submissions.</summary>
public interface INeutralOriginalDmaVisibilityProvider : INeutralOriginalDmaGrantProvider
{
    NeutralOriginalDmaVisibilityObservation? ResolveDmaVisibility(NeutralOriginalDmaVisibilityRequest request); // Pure original current cycle lookup; absence is unknown.
    NeutralOriginalDmaVisibilityObservation PrepareDmaVisibility(NeutralOriginalDmaVisibilityRequest request);
    NeutralOriginalDmaVisibilityObservation AcquireDmaVisibility(NeutralOriginalDmaVisibilityRequest request);
}
