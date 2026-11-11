using SingPlus.Platform;
namespace SingPlus.Platform.Host;

public sealed partial class HostPlatformAuthorityProvider : IPlatformOriginalModelCreateProvider
{
    private sealed class ModelCreateFact
    {
        public ModelCreateFact(PlatformOriginalModelCreateRequest request){Original=this;Request=request;}
        public readonly ModelCreateFact Original;
        public readonly PlatformOriginalModelCreateRequest Request;
        public PlatformOriginalModelCreateObservation? Raw;
        public PlatformOriginalModelCreateObservation? OriginalRaw;
        public VirtualDomainRecord? CreatedOwner;
    }
    private readonly Dictionary<PlatformOriginalModelCreateRequest,ModelCreateFact> _modelCreates=new(ReferenceEqualityComparer.Instance);
    private ulong _modelCreatesIssued;
    private bool ModelCreateHistory() => _modelCreatesIssued==(ulong)_modelCreates.Count &&
        _modelCreates.All(pair=>ReferenceEquals(pair.Key,pair.Value.Request) && ReferenceEquals(pair.Value.Original,pair.Value) &&
            pair.Value.Raw is { } raw && ReferenceEquals(raw,pair.Value.OriginalRaw) && ReferenceEquals(raw.Request,pair.Key) &&
            (!raw.Outcome.IsSuccess || pair.Value.CreatedOwner is { } owner && ReferenceEquals(owner.Original,owner) &&
                owner.Lease==raw.Outcome.Value && _virtualDomains.TryGetValue(owner.Lease.LeaseId,out VirtualDomainRecord? routed) && ReferenceEquals(routed,owner)));
    public PlatformOriginalModelCreateObservation? CreateOriginalModelDomain(PlatformOriginalModelCreateRequest request)
    {
        lock(_virtualDomainGate)
        {
            if(_modelCreates.ContainsKey(request))return null; // Query only; never invoke an original twice.
            if(_modelCreatesIssued==ulong.MaxValue)return null;
            bool history =ModelCreateHistory();
            ModelCreateFact fact =new ModelCreateFact(request);_modelCreates.Add(request,fact);
            _modelCreatesIssued++;
            PlatformAuthorityResult<PlatformProviderVirtualDomainLease> result =history ? CreateVirtualDomainCore(request.Owner,request.Profile) :
                PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted,"Original model factory history unavailable; no effect invoked.");
            fact.Raw=new(request,result);
            fact.OriginalRaw=fact.Raw;
            if(result.IsSuccess)fact.CreatedOwner=_virtualDomains[result.Value.LeaseId];
            return fact.Raw;
        }
    }
    public PlatformOriginalModelCreateObservation? QueryOriginalModelCreate(PlatformOriginalModelCreateRequest request)
    {
        lock(_virtualDomainGate)
        {
            if(!_modelCreates.TryGetValue(request,out ModelCreateFact? fact) || !ReferenceEquals(fact.Original,fact) ||
                !ReferenceEquals(fact.Request,request) || fact.Raw is not { } raw || !ReferenceEquals(raw,fact.OriginalRaw) || !ReferenceEquals(raw.Request,request))return null;
            if(raw.Outcome.IsSuccess && (!_virtualDomains.TryGetValue(raw.Outcome.Value.LeaseId,out VirtualDomainRecord? owner) ||
                !ReferenceEquals(owner.Original,owner) || !ReferenceEquals(owner,fact.CreatedOwner) || owner.Lease!=raw.Outcome.Value))return null;
            return raw;
        }
    }
}
