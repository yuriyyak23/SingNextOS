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
        public NeutralOriginalDsc1Preparation? Preparation;
        public NeutralOriginalDsc1CopyObservation? Raw;
        public PlatformOriginalDsc1CopyBackingObservation? Published;
        public bool PrepareAttempted,Invoked,InFlight,SourceLost;
    }
    private static bool Dsc1BackingHistory(ProviderMappingRecord mapping)=>(ulong)mapping.Dsc1Copies.Count==mapping.Dsc1CopiesIssued &&
        mapping.Dsc1Copies.All(f=>f is not null && ReferenceEquals(f.Self,f) && f.Request is not null && (ReferenceEquals(f.Source,mapping)||ReferenceEquals(f.Destination,mapping))) &&
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
            if(!_providerMappings.TryGetValue(request.Request.Source.Mapping.MappingId,out ProviderMappingRecord? source) ||
                !_providerMappings.TryGetValue(request.Request.Destination.Mapping.MappingId,out ProviderMappingRecord? destination) ||
                !Dsc1BackingHistory(source)||!Dsc1BackingHistory(destination))return Fail();
            Dsc1BackingFact? prior =source.Dsc1Copies.SingleOrDefault(f=>f.Request.InvocationId==request.InvocationId);
            if(prior is not null)
            {
                if(!ReferenceEquals(prior.Request,request) || !ReferenceEquals(prior.Source,source) ||
                    !ReferenceEquals(prior.Destination,destination) ||
                    source.Dsc1Copies.Count(f=>ReferenceEquals(f,prior))!=1 ||
                    destination.Dsc1Copies.Count(f=>ReferenceEquals(f,prior))!=1)return Fail();
                fact=prior;
            }
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
            if((fact.SourceLost && !lookup) || fact.InFlight)return Fail();
            if(fact.Published is { } published)return PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation>.Ok(published);
            fact.InFlight=true;
        }
        try
        {
            if(!Fresh())return Fail();
            if(fact.Native is null)
            {
                lock(_childCreateCommitGate)
                {
                    if (!lookup && fact.PrepareAttempted)return Fail();
                    if(!fact.PrepareAttempted)
                    {
                        if(lookup)return Fail();
                        fact.Preparation = native is INeutralOriginalDsc1PreparationProvider ? new() : null;
                        fact.PrepareAttempted=true;
                    }
                }
                NeutralOriginalDsc1CopyRequest? prepared;
                if (lookup)
                    prepared = native is INeutralOriginalDsc1PreparationProvider recovery && fact.Preparation is { } identity
                        ? recovery.ResolveDsc1Preparation(identity, fact.Source.HybridCpuLease, fact.Destination.HybridCpuLease) : null;
                else if (fact.Preparation is { } identity && native is INeutralOriginalDsc1PreparationProvider original)
                    prepared = original.PrepareDsc1Copy(identity, fact.Source.HybridCpuLease,request.Request.Source.Offset,
                        fact.Destination.HybridCpuLease,request.Request.Destination.Offset,request.Source,request.Destination);
                else
                    prepared=native.PrepareDsc1Copy(fact.Source.HybridCpuLease,request.Request.Source.Offset,
                        fact.Destination.HybridCpuLease,request.Request.Destination.Offset,request.Source,request.Destination);
                lock(_childCreateCommitGate){fact.Native=prepared;if(prepared is not null)request.Drop();}
                if(prepared is null)return Fail();
                if (lookup && !fact.Invoked && native is INeutralOriginalDsc1PreparationProvider cleanup)
                {
                    NeutralOriginalDsc1CopyObservation? closed = cleanup.EndDsc1PreparedCustody(prepared);
                    lock(_childCreateCommitGate) fact.Raw = closed;
                }
            }
            if(!Fresh())return Fail();
            bool invoke;
            lock(_childCreateCommitGate)
            {
                if((fact.SourceLost && !lookup) || !Current()){fact.SourceLost=true;return Fail();}
                invoke=!lookup&&!fact.Invoked;if(invoke)fact.Invoked=true;
            }
            if(invoke){try{ NeutralOriginalDsc1CopyObservation? raw =native.CopyDsc1(fact.Native);lock(_childCreateCommitGate){if(raw is { IsOriginalObservation:true })fact.Raw=raw;}}catch(Exception e)when(e is not StackOverflowException){} }
            NeutralOriginalDsc1CopyObservation? observed =native.ResolveDsc1Copy(fact.Native);
            lock(_childCreateCommitGate)
            {
                if(observed is not { IsOriginalObservation:true })return Fail();
                if(fact.Raw is null)fact.Raw=observed;
                else if(!ReferenceEquals(fact.Raw,observed))return Fail();
            }
            if(!Fresh())return Fail();
            lock(_childCreateCommitGate)
            {
                NeutralOriginalDsc1CopyObservation raw =fact.Raw;
                if((fact.SourceLost && !lookup) || !Current() || raw is not { IsOriginalObservation:true } || !ReferenceEquals(raw.Request,fact.Native) ||
                    raw.Completed==raw.NoEffectGuaranteed || !raw.NoFurtherByteUseGuaranteed)return Fail();
                return PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation>.Ok(fact.Published=new(request,raw.Completed,raw.NoEffectGuaranteed,true));
            }
        }
        catch(Exception e)when(e is not StackOverflowException){return Fail();}
        finally{lock(_childCreateCommitGate)fact.InFlight=false;}
        bool Current()=>Dsc1BackingHistory(fact.Source)&&Dsc1BackingHistory(fact.Destination) &&
            fact.Source.Dsc1Copies.Any(f=>ReferenceEquals(f,fact)) && fact.Destination.Dsc1Copies.Any(f=>ReferenceEquals(f,fact)) &&
            ValidateProviderMapping(request.Request.Source.Mapping,null).IsSuccess && ValidateProviderMapping(request.Request.Destination.Mapping,null).IsSuccess;
        bool Fresh(){ PlatformAuthorityResult source =ValidateOriginalNativeMappingSource();lock(_childCreateCommitGate){ bool current =source.IsSuccess&&Current();if(!current)fact.SourceLost=true;return current&&(lookup||!fact.SourceLost);}}
    }
}
