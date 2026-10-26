using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalMmioRegistersProvider
{
    public bool SupportsMmioRegisterBacking => _runtime is INeutralOriginalMmioRegistersProvider;
    private sealed class Mmio32Fact
    {
        public Mmio32Fact(PlatformOriginalMmio32Request request,MmioLeaseRecord source,NeutralOriginalMmio32Request native)
        { Original=this;Request=request;Source=source;Native=native; }
        public Mmio32Fact Original { get; }
        public PlatformOriginalMmio32Request Request { get; }
        public MmioLeaseRecord Source { get; }
        public NeutralOriginalMmio32Request Native { get; }
        public bool Invoked { get; set; }
        public NeutralOriginalMmio32Observation? Raw { get; set; }
        public NeutralOriginalMmio32Observation? Resolution { get; set; }
    }
    private bool Mmio32Sources(MmioLeaseRecord source,bool metadata=false) => ReferenceEquals(source.OwnerSelf,source) &&
        _originalMmioLeases.TryGetValue(source.Lease.LeaseId,out MmioLeaseRecord? original) && ReferenceEquals(original,source) &&
        _mmioLeases.TryGetValue(source.Lease.LeaseId,out MmioLeaseRecord? route) && ReferenceEquals(route,source) && (CurrentMmioSources(source.MapSource) || metadata && HasConsumedNativeMmio(source)) &&
        (ulong)source.Accesses.Count==source.AccessesIssued && source.Accesses.Distinct(ReferenceEqualityComparer.Instance).Count()==source.Accesses.Count &&
        source.Accesses.All(f=>ReferenceEquals(f.Original,f) && ReferenceEquals(f.Source,source) && ReferenceEquals(f.Request.Original,f.Request));
    public PlatformAuthorityResult<PlatformOriginalMmio32Observation> AccessMmio32(PlatformOriginalMmio32Request request)
    {
        if(_runtime is not INeutralOriginalMmioRegistersProvider native)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(PlatformAuthorityStatus.Unsupported,"Native original register producer absent.");
        Mmio32Fact fact;
        lock(_childCreateCommitGate)
        {
            if(!ReferenceEquals(request.Original,request) || !_mmioLeases.TryGetValue(request.Mapping.LeaseId,out MmioLeaseRecord? source) || source.Lease!=request.Mapping ||
                !Mmio32Sources(source) || source.AccessInFlight || source.CloseInFlight || source.CloseUncertain || source.Revoked || source.CleanupOnly ||
                source.AccessesIssued==ulong.MaxValue || source.Accesses.Any(f=>f.Request.InvocationId==request.InvocationId) ||
                source.Accesses.Any(f=>f.Invoked && !KnownNativeMmio32(f)) ||
                source.MapSource.NativeRequest is not {Backing:not null} map)
                return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(PlatformAuthorityStatus.Faulted,"Original register source/pending/history changed; lookup only.");
            fact=new(request,source,new(map.RuntimeIncarnation,map.InvocationId,source.HybridCpuLease,request.Offset,request.Write,request.Value));
            source.Accesses.Add(fact);source.AccessesIssued++;source.AccessInFlight=true;
        }
        try
        {
            PlatformAuthorityResult live =ValidateOriginalNativeMappingSource();if(!live.IsSuccess)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(live.Status,live.Message!);
            lock(_childCreateCommitGate)
            { if(!Mmio32Sources(fact.Source) || fact.Source.Revoked)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(PlatformAuthorityStatus.Faulted,"Register source changed before effect.");fact.Invoked=true; }
            NeutralOriginalMmio32Observation raw =native.AccessMmio32(fact.Native);lock(_childCreateCommitGate)fact.Raw=raw;
            NeutralOriginalMmio32Observation? original =native.ResolveMmio32(fact.Native);lock(_childCreateCommitGate)fact.Resolution=original;
            live=ValidateOriginalNativeMappingSource();if(!live.IsSuccess)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(live.Status,live.Message!);
            lock(_childCreateCommitGate)
            {
                if(original!=raw)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(PlatformAuthorityStatus.Faulted,"Original register producer outcome not confirmed.");
                return ConsumeMmio32(fact);
            }
        }
        finally { lock(_childCreateCommitGate)fact.Source.AccessInFlight=false; }
    }
    public PlatformAuthorityResult<PlatformOriginalMmio32Observation> ResolveMmio32(PlatformOriginalMmio32Request request)
    {
        Mmio32Fact fact;
        lock(_childCreateCommitGate)
        {
            if(!ReferenceEquals(request.Original,request) || !_originalMmioLeases.TryGetValue(request.Mapping.LeaseId,out MmioLeaseRecord? source) ||
                source.AccessInFlight || !Mmio32Sources(source,true))return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(PlatformAuthorityStatus.Faulted,"Original register owner unavailable.");
            Mmio32Fact[] matches =source.Accesses.Where(f=>ReferenceEquals(f.Request,request)).ToArray();
            if(matches.Length!=1)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(PlatformAuthorityStatus.Faulted,"Original register invocation absent/foreign.");fact=matches[0];source.AccessInFlight=true;
        }
        try
        {
            PlatformAuthorityResult live =ValidateOriginalNativeMappingSource();if(!live.IsSuccess)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(live.Status,live.Message!);
            if(fact.Invoked && _runtime is INeutralOriginalMmioRegistersProvider native)
            { NeutralOriginalMmio32Observation? raw =native.ResolveMmio32(fact.Native);lock(_childCreateCommitGate)fact.Resolution=raw; }
            live=ValidateOriginalNativeMappingSource();if(!live.IsSuccess)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(live.Status,live.Message!);
            lock(_childCreateCommitGate)
            {
                if(!fact.Invoked)return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Ok(new(request,true,null));
                return ConsumeMmio32(fact,true);
            }
        }
        finally { lock(_childCreateCommitGate)fact.Source.AccessInFlight=false; }
    }
    private PlatformAuthorityResult<PlatformOriginalMmio32Observation> ConsumeMmio32(Mmio32Fact fact,bool metadata=false)
    {
        if(!Mmio32Sources(fact.Source,metadata) || fact.Resolution is not { } raw || !ReferenceEquals(raw.Request,fact.Native) ||
            raw.NoEffectGuaranteed==raw.Completed || raw.Completed!=(raw.Value is not null) ||
            raw.Completed && (!raw.SourceFenceMatched || fact.Request.Write && raw.Value!=fact.Request.Value))
            return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Fail(PlatformAuthorityStatus.Faulted,"Original register raw missing/foreign/malformed; no repeat.");
        return PlatformAuthorityResult<PlatformOriginalMmio32Observation>.Ok(new(fact.Request,raw.NoEffectGuaranteed,raw.Value));
    }
    private static bool KnownNativeMmio32(Mmio32Fact fact) => fact.Resolution is { } raw && ReferenceEquals(raw.Request,fact.Native) &&
        raw.NoEffectGuaranteed!=raw.Completed && raw.Completed==(raw.Value is not null) &&
        (!raw.Completed || raw.SourceFenceMatched && (!fact.Request.Write || raw.Value==fact.Request.Value));
}
