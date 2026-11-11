namespace SingPlus.Platform;
public sealed class PlatformOriginalMmio32Request
{
    public PlatformOriginalMmio32Request(PlatformProviderMmioLease mapping,long offset,bool write,uint value=0)
    { Original=this;InvocationId=Guid.NewGuid();Mapping=mapping;Offset=offset;Write=write;Value=value; }
    internal PlatformOriginalMmio32Request Original { get; }
    public Guid InvocationId { get; }
    public PlatformProviderMmioLease Mapping { get; }
    public long Offset { get; }
    public bool Write { get; }
    public uint Value { get; }
}
public readonly record struct PlatformOriginalMmio32Observation(PlatformOriginalMmio32Request Request,bool NoEffectGuaranteed,uint? Value);
public interface IPlatformOriginalMmioRegistersProvider : IPlatformOriginalMmioProvider
{
    bool SupportsMmioRegisterBacking { get; }
    PlatformAuthorityResult<PlatformOriginalMmio32Observation> AccessMmio32(PlatformOriginalMmio32Request request);
    PlatformAuthorityResult<PlatformOriginalMmio32Observation> ResolveMmio32(PlatformOriginalMmio32Request request);
}
