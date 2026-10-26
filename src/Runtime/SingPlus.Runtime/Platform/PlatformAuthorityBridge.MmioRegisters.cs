using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;
public sealed partial class PlatformAuthorityBridge
{
    internal bool SupportsMmioRegisterBacking => _provider is IPlatformOriginalMmioRegistersProvider {SupportsMmioRegisterBacking:true};
    private sealed class OriginalMmio32Fact
    {
        public OriginalMmio32Fact(PlatformMmio32Invocation invocation,MmioLeaseRecord source)
        { Original=this;Invocation=invocation;Source=source;Factory=source.OriginalMap!;Revision=source.SourceRevision;Request=new(source.ProviderLease,invocation.Offset,invocation.Write,invocation.Value); }
        public OriginalMmio32Fact Original { get; }
        public PlatformMmio32Invocation Invocation { get; }
        public MmioLeaseRecord Source { get; }
        public OriginalMmioMapInvocation Factory { get; }
        public ulong Revision { get; }
        public PlatformOriginalMmio32Request Request { get; }
        public bool Invoked { get; set; }
        public PlatformAuthorityResult<PlatformOriginalMmio32Observation>? Raw { get; set; }
        public PlatformAuthorityResult<PlatformOriginalMmio32Observation>? Resolution { get; set; }
    }
    private bool Mmio32Owners(MmioLeaseRecord source,PlatformDomainIdentity subject) => ReferenceEquals(source.OwnerSelf,source) && source.Lease.DeviceLease.DomainBinding.Subject==subject &&
        _originalMmioOwners.TryGetValue(source.Lease.LeaseId,out MmioLeaseRecord? original) && ReferenceEquals(original,source) &&
        _mmioLeases.TryGetValue(source.Lease.LeaseId,out MmioLeaseRecord? route) && ReferenceEquals(route,source) &&
        (ulong)source.Accesses.Count==source.AccessesIssued && source.Accesses.Distinct(ReferenceEqualityComparer.Instance).Count()==source.Accesses.Count &&
        source.Accesses.All(f=>ReferenceEquals(f.Original,f) && ReferenceEquals(f.Source,source) && ReferenceEquals(f.Factory,source.OriginalMap) && ReferenceEquals(f.Invocation.Original,f.Invocation));
    internal KernelResult<uint> AccessMmio32(PlatformMmio32Invocation invocation,PlatformDomainIdentity subject,
        Func<CapabilityId,Func<KernelResult>,KernelResult> authorize,RegionAuthority regions)
    {
        if(_provider is not IPlatformOriginalMmioRegistersProvider provider)return KernelResult<uint>.Fail(KernelError.PlatformUnsupported,"Register producer absent.");
        OriginalMmio32Fact fact=null!;
        KernelResult Begin()
        {
            lock(_secureDomainLifecycleGate)
            {
                KernelResult valid =ValidateMmioLease(invocation.Mapping,subject);if(!valid.IsSuccess)return valid;
                MmioLeaseRecord source =_mmioLeases[invocation.Mapping.LeaseId];
                if(!ReferenceEquals(invocation.Original,invocation) || !Mmio32Owners(source,subject) || source.AccessInFlight || source.ClosureInFlight ||
                    source.Accesses.Any(f=>f.Invoked && !KnownMmio32(f)) ||
                    source.AccessesIssued==ulong.MaxValue || source.Accesses.Any(f=>ReferenceEquals(f.Invocation,invocation)) || source.OriginalMap?.Payload is null)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original register pending/history/backing unavailable; lookup only.");
                fact=new(invocation,source);source.Accesses.Add(fact);source.AccessesIssued++;source.AccessInFlight=true;return KernelResult.Ok();
            }
        }
        KernelResult begin =Begin();if(!begin.IsSuccess)return KernelResult<uint>.Fail(begin.Error,begin.Message!);
        try
        {
            PlatformProviderIncarnation incarnation =CurrentProviderIncarnation();
            KernelResult admitted =authorize(fact.Factory.Capability,()=>
            {
                lock(_secureDomainLifecycleGate)
                {
                    KernelResult valid =ValidateMmioLease(invocation.Mapping,subject);if(!valid.IsSuccess)return valid;
                    MmioBufferPayload payload =fact.Factory.Payload!;
                    if(!Mmio32Owners(fact.Source,subject) || !OriginalMmioMapSources(fact.Factory,incarnation) || fact.Source.SourceRevision!=fact.Revision ||
                        !ReferenceEquals(payload.AdmissionOwner,fact.Factory) || payload.Use is not { } use || payload.Reservation?.IsActive!=true)
                        return KernelResult.Fail(KernelError.StaleGeneration,"Original register source/backing changed before effect.");
                    KernelResult<RegionUseDescriptor> backing =regions.ValidateUse(use,payload.Owner);if(!backing.IsSuccess)return KernelResult.Fail(backing.Error,backing.Message!);
                    fact.Invoked=true;return KernelResult.Ok();
                }
            });
            if(!admitted.IsSuccess)return KernelResult<uint>.Fail(admitted.Error,admitted.Message!);
            PlatformAuthorityResult<PlatformOriginalMmio32Observation> raw =provider.AccessMmio32(fact.Request);lock(_secureDomainLifecycleGate)fact.Raw=raw;
            incarnation=CurrentProviderIncarnation();
            KernelResult publication =authorize(fact.Factory.Capability,()=>
            {
                lock(_secureDomainLifecycleGate)
                {
                    if(!Mmio32Owners(fact.Source,subject) || !OriginalMmioMapSources(fact.Factory,incarnation) || fact.Source.SourceRevision!=fact.Revision)
                        return KernelResult.Fail(KernelError.StaleGeneration,"Register source changed after effect; retain original raw.");
                    MmioBufferPayload payload =fact.Factory.Payload!; KernelResult<RegionUseDescriptor> use =regions.ValidateUse(payload.Use!.Value,payload.Owner);
                    if(!use.IsSuccess)return KernelResult.Fail(use.Error,use.Message!);
                    KernelResult<uint> value =DecodeMmio32(fact,raw);return value.IsSuccess?KernelResult.Ok():KernelResult.Fail(value.Error,value.Message!);
                }
            });
            return publication.IsSuccess?DecodeMmio32(fact,raw):KernelResult<uint>.Fail(publication.Error,publication.Message!);
        }
        catch(Exception e) when(e is not StackOverflowException){return KernelResult<uint>.Fail(KernelError.PlatformFaulted,"Original register outcome retained; lookup only: "+e.Message);}
        finally { lock(_secureDomainLifecycleGate)fact.Source.AccessInFlight=false; }
    }
    internal KernelResult<uint> ResolveMmio32(PlatformMmio32Invocation invocation,PlatformDomainIdentity subject)
    {
        if(_provider is not IPlatformOriginalMmioRegistersProvider provider)return KernelResult<uint>.Fail(KernelError.PlatformUnsupported,"Register lookup absent.");
        OriginalMmio32Fact fact;
        lock(_secureDomainLifecycleGate)
        {
            if(!ReferenceEquals(invocation.Original,invocation) || !_originalMmioOwners.TryGetValue(invocation.Mapping.LeaseId,out MmioLeaseRecord? source) ||
                source.AccessInFlight || !Mmio32Owners(source,subject))return KernelResult<uint>.Fail(KernelError.PlatformFaulted,"Register original owner absent/foreign.");
            OriginalMmio32Fact[] facts =source.Accesses.Where(f=>ReferenceEquals(f.Invocation,invocation)).ToArray();if(facts.Length!=1)return KernelResult<uint>.Fail(KernelError.PlatformFaulted,"Original register invocation absent/foreign.");
            fact=facts[0];source.AccessInFlight=true;
        }
        try
        {
            if(!fact.Invoked)return KernelResult<uint>.Fail(KernelError.PlatformDenied,"Original register invocation had no provider effect.");
            PlatformProviderIncarnation source =CurrentProviderIncarnation();
            PlatformAuthorityResult<PlatformOriginalMmio32Observation> raw =provider.ResolveMmio32(fact.Request);lock(_secureDomainLifecycleGate)fact.Resolution=raw;
            PlatformProviderIncarnation current =CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
            {
                if(!Mmio32Owners(fact.Source,subject) || source.Value==0 || source!=current ||
                    current!=fact.Factory.Source.ProviderIncarnation || !ReferenceEquals(fact.Factory,fact.Source.OriginalMap))
                    return KernelResult<uint>.Fail(KernelError.StaleGeneration,"Original register lookup source changed.");
                return DecodeMmio32(fact,raw);
            }
        }
        catch(Exception e) when(e is not StackOverflowException){return KernelResult<uint>.Fail(KernelError.PlatformFaulted,e.Message);}
        finally { lock(_secureDomainLifecycleGate)fact.Source.AccessInFlight=false; }
    }
    private static KernelResult<uint> DecodeMmio32(OriginalMmio32Fact fact,PlatformAuthorityResult<PlatformOriginalMmio32Observation> raw)
    {
        if(!raw.IsSuccess || !ReferenceEquals(raw.Value.Request,fact.Request) || raw.Value.NoEffectGuaranteed==(raw.Value.Value is not null) ||
            !raw.Value.NoEffectGuaranteed && fact.Invocation.Write && raw.Value.Value!=fact.Invocation.Value)
            return KernelResult<uint>.Fail(KernelError.PlatformFaulted,"Original register raw missing/foreign/malformed.");
        return raw.Value.NoEffectGuaranteed?KernelResult<uint>.Fail(KernelError.PlatformDenied,"Exact original register no-effect."):KernelResult<uint>.Ok(raw.Value.Value!.Value);
    }
    private static bool KnownMmio32(OriginalMmio32Fact fact)
    {
        PlatformAuthorityResult<PlatformOriginalMmio32Observation>? raw =fact.Resolution ?? fact.Raw;
        return raw is {IsSuccess:true} value && ReferenceEquals(value.Value.Request,fact.Request) &&
            value.Value.NoEffectGuaranteed!=(value.Value.Value is not null) &&
            (value.Value.NoEffectGuaranteed || !fact.Invocation.Write || value.Value.Value==fact.Invocation.Value);
    }
}
