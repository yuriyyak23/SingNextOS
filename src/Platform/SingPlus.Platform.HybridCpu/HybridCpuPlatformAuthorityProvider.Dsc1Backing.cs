using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalDsc1CopyBackingProvider
{
    private sealed class Dsc1BackingFact(PlatformOriginalDsc1CopyBackingRequest request,ProviderMappingRecord source,ProviderMappingRecord destination)
    {
        public readonly PlatformOriginalDsc1CopyBackingRequest Request=request;
        public readonly ProviderMappingRecord Source=source,Destination=destination;
        public Dsc1BackingFact? Self;
        public NeutralOriginalDsc1CopyRequest? Native;
        public NeutralOriginalDsc1CopyObservation? Raw;
        public PlatformOriginalDsc1CopyBackingObservation? Published;
        public bool Invoked,InFlight,SourceLost;
    }
    private static bool Dsc1BackingHistory(ProviderMappingRecord mapping)=>(ulong)mapping.Dsc1Copies.Count==mapping.Dsc1CopiesIssued &&
        mapping.Dsc1Copies.All(f=>ReferenceEquals(f.Self,f) && (ReferenceEquals(f.Source,mapping)||ReferenceEquals(f.Destination,mapping))) &&
        mapping.Dsc1Copies.Select(f=>f.Request.InvocationId).Distinct().Count()==mapping.Dsc1Copies.Count;
    public PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation> CopyDsc1Backing(PlatformOriginalDsc1CopyBackingRequest request)=>RunDsc1Backing(request,false);
    public PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation> ResolveDsc1Backing(PlatformOriginalDsc1CopyBackingRequest request)=>RunDsc1Backing(request,true);
    private PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation> RunDsc1Backing(PlatformOriginalDsc1CopyBackingRequest request,bool lookup)
    {
        PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation> Fail()=>PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation>.Fail(PlatformAuthorityStatus.Faulted,"Original DSC1 backing retained; unknown invocation is never repeated.");
        if(request is null || _runtime is not INeutralOriginalDsc1CopyProvider native)return Fail();
        Dsc1BackingFact fact;
        lock(_childCreateCommitGate)
        {
            if(!_providerMappings.TryGetValue(request.Request.Source.Mapping.MappingId,out var source) ||
                !_providerMappings.TryGetValue(request.Request.Destination.Mapping.MappingId,out var destination) ||
                !Dsc1BackingHistory(source)||!Dsc1BackingHistory(destination))return Fail();
            var prior=source.Dsc1Copies.SingleOrDefault(f=>f.Request.InvocationId==request.InvocationId);
            if(prior is not null){if(!ReferenceEquals(prior.Request,request))return Fail();fact=prior;}
            else
            {
                if(lookup || ReferenceEquals(source,destination) || !PlatformDsc1ComputeContract.ValidateRequest(request.Request).IsSuccess ||
                    request.Request.DomainLease!=source.Mapping.Lease.DomainLease || request.Request.DomainLease!=destination.Mapping.Lease.DomainLease ||
                    request.ByteLength!=request.Request.Source.Length || request.ByteLength!=request.Destination.Length ||
                    source.Dsc1Copies.Count>=256 || destination.Dsc1Copies.Count>=256 ||
                    !ValidateProviderMapping(request.Request.Source.Mapping,null).IsSuccess || !ValidateProviderMapping(request.Request.Destination.Mapping,null).IsSuccess)return Fail();
                source.Dsc1Copies.EnsureCapacity(source.Dsc1Copies.Count+1);destination.Dsc1Copies.EnsureCapacity(destination.Dsc1Copies.Count+1);
                fact=new(request,source,destination);fact.Self=fact;
                source.Dsc1Copies.Add(fact);destination.Dsc1Copies.Add(fact);source.Dsc1CopiesIssued++;destination.Dsc1CopiesIssued++;
            }
            if(fact.SourceLost || fact.InFlight)return Fail();
            if(fact.Published is { } published)return PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation>.Ok(published);
            fact.InFlight=true;
        }
        try
        {
            if(!Fresh())return Fail();
            if(fact.Native is null)
            {
                if(lookup)return Fail();
                var prepared=native.PrepareDsc1Copy(fact.Source.HybridCpuLease,request.Request.Source.Offset,
                    fact.Destination.HybridCpuLease,request.Request.Destination.Offset,request.Source,request.Destination);
                lock(_childCreateCommitGate){fact.Native=prepared;if(prepared is not null)request.Drop();}
                if(prepared is null)return Fail();
            }
            if(!Fresh())return Fail();
            bool invoke;
            lock(_childCreateCommitGate){invoke=!lookup&&!fact.Invoked;if(invoke)fact.Invoked=true;}
            if(invoke){try{var raw=native.CopyDsc1(fact.Native);lock(_childCreateCommitGate)fact.Raw=raw;}catch(Exception e)when(e is not StackOverflowException){} }
            var observed=native.ResolveDsc1Copy(fact.Native);
            lock(_childCreateCommitGate)
            {
                if(fact.Raw is null)fact.Raw=observed;
                else if(!ReferenceEquals(fact.Raw,observed))return Fail();
            }
            if(!Fresh())return Fail();
            lock(_childCreateCommitGate)
            {
                var raw=fact.Raw;
                if(fact.SourceLost || !Current() || raw is null || !ReferenceEquals(raw.Request,fact.Native) ||
                    raw.Completed==raw.NoEffectGuaranteed || !raw.NoFurtherByteUseGuaranteed)return Fail();
                return PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation>.Ok(fact.Published=new(request,raw.Completed,raw.NoEffectGuaranteed,true));
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return Fail();}
        finally{lock(_childCreateCommitGate)fact.InFlight=false;}
        bool Current()=>Dsc1BackingHistory(fact.Source)&&Dsc1BackingHistory(fact.Destination) &&
            fact.Source.Dsc1Copies.Any(f=>ReferenceEquals(f,fact)) && fact.Destination.Dsc1Copies.Any(f=>ReferenceEquals(f,fact)) &&
            ValidateProviderMapping(request.Request.Source.Mapping,null).IsSuccess && ValidateProviderMapping(request.Request.Destination.Mapping,null).IsSuccess;
        bool Fresh(){var source=ValidateOriginalNativeMappingSource();lock(_childCreateCommitGate){if(!source.IsSuccess||!Current())fact.SourceLost=true;return !fact.SourceLost;}}
    }
}
