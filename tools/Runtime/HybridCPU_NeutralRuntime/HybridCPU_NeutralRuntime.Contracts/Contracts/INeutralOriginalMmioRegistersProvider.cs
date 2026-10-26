namespace YAKSys_Hybrid_CPU.Core;
public sealed class NeutralOriginalMmio32Request
{
    public NeutralOriginalMmio32Request(Guid incarnation,Guid mapInvocationId,NeutralMmioLease mapping,long offset,bool write,uint value=0)
    { Original=this;InvocationId=Guid.NewGuid();RuntimeIncarnation=incarnation;MapInvocationId=mapInvocationId;Mapping=mapping;Offset=offset;Write=write;Value=value; }
    internal NeutralOriginalMmio32Request Original { get; }
    public Guid InvocationId { get; }
    public Guid RuntimeIncarnation { get; }
    public Guid MapInvocationId { get; }
    public NeutralMmioLease Mapping { get; }
    public long Offset { get; }
    public bool Write { get; }
    public uint Value { get; }
}
public readonly record struct NeutralOriginalMmio32Observation(NeutralOriginalMmio32Request Request,
    bool SourceFenceMatched,bool NoEffectGuaranteed,bool Completed,uint? Value);
public interface INeutralOriginalMmioRegistersProvider : INeutralOriginalMmioProvider
{
    NeutralOriginalMmio32Observation AccessMmio32(NeutralOriginalMmio32Request request);
    NeutralOriginalMmio32Observation? ResolveMmio32(NeutralOriginalMmio32Request request);
}
