namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Caller-retained original identity before possible custody acceptance.
/// Identity alone grants no mapping or byte permission.</summary>
public sealed class NeutralOriginalDsc1Preparation
{
    public Guid InvocationId { get; } = Guid.NewGuid();
}

public interface INeutralOriginalDsc1PreparationProvider : INeutralOriginalDsc1CopyProvider
{
    NeutralOriginalDsc1CopyRequest? PrepareDsc1Copy(NeutralOriginalDsc1Preparation preparation,
        NeutralOwnedRegionMappingLease sourceMapping, long sourceOffset,
        NeutralOwnedRegionMappingLease destinationMapping, long destinationOffset,
        ReadOnlyMemory<byte> source, Memory<byte> destination);
    NeutralOriginalDsc1CopyRequest? ResolveDsc1Preparation(NeutralOriginalDsc1Preparation preparation,
        NeutralOwnedRegionMappingLease sourceMapping, NeutralOwnedRegionMappingLease destinationMapping);
    NeutralOriginalDsc1CopyObservation? EndDsc1PreparedCustody(NeutralOriginalDsc1CopyRequest request);
}

/// <summary>Opaque custody request prepared by the original native mapping owners.
/// Caller must independently admit compute/Region/budget use before invoking.</summary>
public sealed class NeutralOriginalDsc1CopyRequest
{
    internal NeutralOriginalDsc1CopyRequest(object owner,ReadOnlyMemory<byte> source,Memory<byte> destination)
    {Owner=owner;Source=source;Destination=destination;InvocationId=Guid.NewGuid();ByteLength=source.Length;}
    internal object Owner {get;}
    public Guid InvocationId {get;}
    public int ByteLength {get;}
    internal ReadOnlyMemory<byte> Source {get;private set;}
    internal Memory<byte> Destination {get;private set;}
    internal void ReleaseBytes(){Source=default;Destination=default;}
}
public sealed class NeutralOriginalDsc1CopyObservation
{
    internal NeutralOriginalDsc1CopyObservation(NeutralOriginalDsc1CopyRequest request,bool completed,bool noEffect)
    {Original=this;Request=request;Completed=completed;NoEffectGuaranteed=noEffect;}
    internal NeutralOriginalDsc1CopyObservation Original {get;}
    public bool IsOriginalObservation => ReferenceEquals(Original, this);
    public NeutralOriginalDsc1CopyRequest Request {get;}
    public bool Completed {get;}
    public bool NoEffectGuaranteed {get;}
    public bool NoFurtherByteUseGuaranteed=>true;
}
public interface INeutralOriginalDsc1CopyProvider
{
    NeutralOriginalDsc1CopyRequest? PrepareDsc1Copy(NeutralOwnedRegionMappingLease sourceMapping,long sourceOffset,
        NeutralOwnedRegionMappingLease destinationMapping,long destinationOffset,ReadOnlyMemory<byte> source,Memory<byte> destination);
    NeutralOriginalDsc1CopyObservation? CopyDsc1(NeutralOriginalDsc1CopyRequest request);
    NeutralOriginalDsc1CopyObservation? ResolveDsc1Copy(NeutralOriginalDsc1CopyRequest request);
}
