using SingPlus.Platform;

namespace SingPlus.Platform.Host;

public sealed partial class HostPlatformAuthorityProvider : IPlatformOriginalVirtualDomainTransitionProvider,IPlatformOriginalVirtualDomainRevokeProvider,IPlatformProviderIncarnationSource
{
    private sealed class VirtualDomainRecord
    {
        public VirtualDomainRecord(PlatformProviderVirtualDomainLease lease){Original=this;Lease=lease;}
        public readonly VirtualDomainRecord Original;
        public PlatformProviderVirtualDomainLease Lease { get; }
        public PlatformVirtualDomainTransition? LastTransition { get; set; }
        public bool Closed;
        public ulong Revision;
        public readonly Dictionary<PlatformOriginalVirtualDomainTransitionRequest,ModelTransitionFact> Transitions=new(ReferenceEqualityComparer.Instance);
        public ulong TransitionsIssued;
        public ModelRevokeFact? Revoke;
    }

    private sealed class ModelTransitionFact
    {
        public ModelTransitionFact(PlatformOriginalVirtualDomainTransitionRequest request,VirtualDomainRecord owner)
        {OriginalSelf=this;Request=request;Owner=owner;}
        public readonly ModelTransitionFact OriginalSelf;
        public PlatformOriginalVirtualDomainTransitionRequest Request {get;}
        public VirtualDomainRecord Owner {get;}
        public PlatformOriginalVirtualDomainTransitionObservation? Raw;
        public PlatformOriginalVirtualDomainTransitionObservation? OriginalRaw;
        public ulong ProducedRevision;
    }

    private static long _modelIncarnations;
    private readonly PlatformProviderIncarnation _modelIncarnation=CreateModelIncarnation();
    private static PlatformProviderIncarnation CreateModelIncarnation()
    {
        var value=Interlocked.Increment(ref _modelIncarnations);
        return value>0 ? new((ulong)value) : throw new InvalidOperationException("Model incarnation space exhausted.");
    }
    public PlatformProviderIncarnation CurrentIncarnation=>_modelIncarnation;
    public bool SupportsOriginalVirtualDomainTransitions=>true;
    public int OriginalVirtualDomainTransitionEffectCount {get;private set;}
    private sealed class ModelRevokeFact
    {
        public ModelRevokeFact(VirtualDomainRecord owner,PlatformOriginalVirtualDomainRevokeRequest request)
        {OriginalSelf=this;Owner=owner;Request=request;}
        public readonly ModelRevokeFact OriginalSelf;
        public readonly VirtualDomainRecord Owner;
        public readonly PlatformOriginalVirtualDomainRevokeRequest Request;
        public PlatformOriginalVirtualDomainRevokeObservation? Raw;
        public PlatformOriginalVirtualDomainRevokeObservation? OriginalRaw;
    }
    public bool SupportsOriginalVirtualDomainRevocation=>true;

    public PlatformOriginalVirtualDomainRevokeObservation? RevokeOriginalVirtualDomain(PlatformOriginalVirtualDomainRevokeRequest request)
    {
        lock(_virtualDomainGate)
        {
            if(request is not {IsOriginal:true} || request.Incarnation!=CurrentIncarnation || !ModelInventory() ||
                !_virtualDomains.TryGetValue(request.Lease.LeaseId,out var owner) || owner.Lease!=request.Lease || !TransitionHistory(owner))return null;
            if(owner.Revoke is { } prior)return ReferenceEquals(prior.Request,request) ? ReadOriginalModelRevoke(owner,prior) : null;
            if(owner.Closed || RevokeVirtualDomainCallCount==int.MaxValue)return null;
            var fact=new ModelRevokeFact(owner,request);owner.Revoke=fact; // Original before effect.
            RevokeVirtualDomainCallCount++;owner.Closed=true;
            fact.Raw=new(request,true); // Raw before return/callbacks.
            fact.OriginalRaw=fact.Raw;
            return fact.Raw;
        }
    }
    public PlatformOriginalVirtualDomainRevokeObservation? ResolveOriginalVirtualDomainRevoke(PlatformOriginalVirtualDomainRevokeRequest request)
    {
        lock(_virtualDomainGate)
            return request is {IsOriginal:true} && request.Incarnation==CurrentIncarnation && ModelInventory() &&
                _virtualDomains.TryGetValue(request.Lease.LeaseId,out var owner) && owner.Lease==request.Lease &&
                TransitionHistory(owner) && owner.Revoke is { } fact && ReferenceEquals(fact.Request,request) ? ReadOriginalModelRevoke(owner,fact) : null;
    }
    private static PlatformOriginalVirtualDomainRevokeObservation? ReadOriginalModelRevoke(VirtualDomainRecord owner,ModelRevokeFact fact)
        =>ReferenceEquals(fact.OriginalSelf,fact) && ReferenceEquals(fact.Owner,owner) && owner.Closed &&
            fact.Raw is {IsOriginal:true,NoFurtherDomainUse:true} raw && ReferenceEquals(raw,fact.OriginalRaw) && ReferenceEquals(raw.Request,fact.Request) ? raw : null;

    private readonly object _virtualDomainGate = new();
    private readonly Dictionary<PlatformProviderVirtualDomainLeaseId, VirtualDomainRecord> _virtualDomains = [];
    private ulong _nextVirtualDomainId = 1;
    public int CreateVirtualDomainCallCount { get; private set; }
    public int RevokeVirtualDomainCallCount { get; private set; }

    public PlatformAuthorityResult<PlatformProviderVirtualDomainLease> CreateVirtualDomain(PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile)
    {
        lock(_virtualDomainGate)
            return ModelCreateHistory() ? CreateVirtualDomainCore(owner,profile) :
                PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted,"Original model factory history unavailable before V1 effect.");
    }

    private PlatformAuthorityResult<PlatformProviderVirtualDomainLease> CreateVirtualDomainCore(PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile)
    {
        lock (_virtualDomainGate)
        {
        CreateVirtualDomainCallCount++;
        if (!PlatformDomainContract.ValidateSubject(owner).IsSuccess || profile.VirtualProcessorCount <= 0 || profile.MaximumGuestMemoryBytes <= 0)
            return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(PlatformAuthorityStatus.Denied, "Virtual-domain owner and profile must be bounded and materialized.");
        if (_nextVirtualDomainId == ulong.MaxValue) return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Model-domain identity space is exhausted before effect.");
        if(!ModelInventory())return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(PlatformAuthorityStatus.Faulted,"Original model inventory changed.");
        var lease = new PlatformProviderVirtualDomainLease(new PlatformProviderVirtualDomainLeaseId(_nextVirtualDomainId++), new PlatformProviderLeaseGeneration(1), owner);
        _virtualDomains.Add(lease.LeaseId, new VirtualDomainRecord(lease));
        return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Ok(lease);
        }
    }

    public PlatformAuthorityResult TransitionVirtualDomain(PlatformProviderVirtualDomainLease lease, PlatformVirtualDomainTransition transition)
    {
        lock (_virtualDomainGate)
        {
        if (!ModelInventory() || !_virtualDomains.TryGetValue(lease.LeaseId, out var record) || record.Lease != lease || record.Closed)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-domain lease is stale.");
        if(record.Revoke is not null)return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Original model revoke forbids new V1 use.");
        if(!TransitionHistory(record))return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Original model transition history unavailable before V1 effect.");
        if(!Enum.IsDefined(transition) || record.Revision==ulong.MaxValue)return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Model transition outside bounds before effect.");
        record.LastTransition = transition;record.Revision++;
        return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult RevokeVirtualDomain(PlatformProviderVirtualDomainLease lease)
    {
        lock (_virtualDomainGate)
        {
        RevokeVirtualDomainCallCount++;
        if (!ModelInventory() || !_virtualDomains.TryGetValue(lease.LeaseId, out var record) || record.Lease != lease || record.Closed)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-domain lease is stale.");
        record.Closed=true; // Original completion history survives lawful routing retirement.
        return PlatformAuthorityResult.Ok();
        }
    }

    private bool ModelInventory()=>_nextVirtualDomainId>0 && (ulong)_virtualDomains.Count==_nextVirtualDomainId-1 &&
        _virtualDomains.All(pair=>pair.Key==pair.Value.Lease.LeaseId && ReferenceEquals(pair.Value.Original,pair.Value));
    private static bool TransitionHistory(VirtualDomainRecord owner)=>owner.TransitionsIssued==(ulong)owner.Transitions.Count &&
        owner.Transitions.All(pair=>ReferenceEquals(pair.Key,pair.Value.Request) && pair.Key.IsOriginal &&
            ReferenceEquals(pair.Value.OriginalSelf,pair.Value) && ReferenceEquals(pair.Value.Owner,owner) &&
            pair.Value.Raw is {IsOriginal:true,NoFurtherTransitionUse:true} raw && ReferenceEquals(raw,pair.Value.OriginalRaw) && ReferenceEquals(raw.Request,pair.Key) &&
            raw.Revision==pair.Value.ProducedRevision && pair.Value.ProducedRevision<=owner.Revision);

    public PlatformOriginalVirtualDomainTransitionObservation? TransitionOriginalVirtualDomain(PlatformOriginalVirtualDomainTransitionRequest request)
    {
        lock(_virtualDomainGate)
        {
            if(request is null || !request.IsOriginal || request.Incarnation!=CurrentIncarnation || !ModelInventory() ||
                !_virtualDomains.TryGetValue(request.Lease.LeaseId,out var owner) || owner.Lease!=request.Lease || !TransitionHistory(owner))return null;
            if(owner.Transitions.TryGetValue(request,out var prior))return ReadOriginalModelTransition(owner,prior);
            if(owner.Revoke is not null)return null; // Historical query remains separate; no new use after original revoke.
            if(owner.TransitionsIssued==ulong.MaxValue)return null;
            var fact=new ModelTransitionFact(request,owner);
            owner.Transitions.Add(request,fact);owner.TransitionsIssued++; // Original before possible effect.
            var noEffect=owner.Closed || !Enum.IsDefined(request.Transition) || owner.Revision==ulong.MaxValue || OriginalVirtualDomainTransitionEffectCount==int.MaxValue;
            if(!noEffect){owner.LastTransition=request.Transition;owner.Revision++;OriginalVirtualDomainTransitionEffectCount++;}
            fact.Raw=new(request,noEffect ? PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Original model transition refused before effect.") : PlatformAuthorityResult.Ok(),owner.Revision,noEffect);
            fact.ProducedRevision=owner.Revision;
            fact.OriginalRaw=fact.Raw;
            return fact.Raw;
        }
    }

    public PlatformOriginalVirtualDomainTransitionObservation? ResolveOriginalVirtualDomainTransition(PlatformOriginalVirtualDomainTransitionRequest request)
    {
        lock(_virtualDomainGate)
        {
            return request is {IsOriginal:true} && request.Incarnation==CurrentIncarnation && ModelInventory() &&
                _virtualDomains.TryGetValue(request.Lease.LeaseId,out var owner) && owner.Lease==request.Lease &&
                TransitionHistory(owner) && owner.Transitions.TryGetValue(request,out var fact) ? ReadOriginalModelTransition(owner,fact) : null;
        }
    }
    private static PlatformOriginalVirtualDomainTransitionObservation? ReadOriginalModelTransition(VirtualDomainRecord owner,ModelTransitionFact fact)
        => ReferenceEquals(fact.OriginalSelf,fact) && ReferenceEquals(fact.Owner,owner) && fact.Raw is {IsOriginal:true} raw && ReferenceEquals(raw,fact.OriginalRaw) &&
            ReferenceEquals(raw.Request,fact.Request) && raw.Revision==fact.ProducedRevision && fact.ProducedRevision<=owner.Revision && raw.NoFurtherTransitionUse ? raw : null;
}
