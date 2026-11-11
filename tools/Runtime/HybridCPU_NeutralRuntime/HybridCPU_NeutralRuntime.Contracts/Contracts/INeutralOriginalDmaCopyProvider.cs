[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HybridCPU_NeutralRuntime.Model")]

namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralOriginalDmaCopyLeg(NeutralOriginalDmaGrantRequest Admission,
    NeutralDmaGrant Grant, NeutralOriginalDmaVisibilityRequest Prepare, NeutralDmaVisibilityCycle Cycle);

/// <summary>Original invocation identity, not permission. Memory is supplied by an
/// independently admitted runtime storage reservation; no native pointers or ISA changes.</summary>
public sealed class NeutralOriginalDmaCopyRequest(Guid invocationId, Guid runtimeIncarnation,
    NeutralOriginalDmaCopyLeg source, NeutralOriginalDmaCopyLeg destination,
    ReadOnlyMemory<byte> sourceBytes, Memory<byte> destinationBytes)
{
    public Guid InvocationId { get; } = invocationId;
    public Guid RuntimeIncarnation { get; } = runtimeIncarnation;
    public NeutralOriginalDmaCopyLeg Source { get; } = source;
    public NeutralOriginalDmaCopyLeg Destination { get; } = destination;
    public int ByteLength { get; } = sourceBytes.Length;
    public ReadOnlyMemory<byte> SourceBytes { get; private set; } = sourceBytes;
    public Memory<byte> DestinationBytes { get; private set; } = destinationBytes;
    internal void ReleaseBytes() { SourceBytes = default; DestinationBytes = default; }
}

public sealed record NeutralOriginalDmaCopyObservation(NeutralOriginalDmaCopyRequest Request,
    bool SourceFenceMatched, bool NoEffectGuaranteed, bool Completed, bool NoFurtherByteUseGuaranteed);

public interface INeutralOriginalDmaCopyProvider : INeutralOriginalDmaVisibilityProvider
{
    NeutralOriginalDmaCopyObservation CopyDma(NeutralOriginalDmaCopyRequest request);
    // Pure exact surviving-original lookup; missing/history loss is unknown.
    NeutralOriginalDmaCopyObservation? ResolveDmaCopy(NeutralOriginalDmaCopyRequest request);
    // Fresh prepared cycles/source fences for final admission publication. This
    // is distinct from the completed lifetime lookup, which needs no new permission.
    NeutralOriginalDmaCopyObservation? ValidateDmaCopy(NeutralOriginalDmaCopyRequest request);
}
