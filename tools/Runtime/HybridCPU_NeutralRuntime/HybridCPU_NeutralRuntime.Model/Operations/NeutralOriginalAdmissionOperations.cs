namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDomainCloseContinuationProvider, INeutralOriginalMapOutcomeProvider,INeutralOriginalBindOutcomeProvider
{
    private sealed class OriginalBindInvocation
    {
        public OriginalBindInvocation(NeutralOriginalDomainBindRequest request){Original=this;Request=request;}
        public OriginalBindInvocation Original {get;}
        public NeutralOriginalDomainBindRequest Request {get;}
        public NeutralOriginalDomainBindObservation? Raw;
        public NeutralOriginalDomainAdmissionFact? Admission;
        public bool NoEffect;
    }
    private readonly List<OriginalBindInvocation> _originalBindInvocations=[];
    private ulong _originalBindsIssued;
    NeutralOriginalDomainCloseObservation INeutralOriginalDomainCloseProvider.Close(NeutralOriginalDomainCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralDomainCloseResult refused = new NeutralDomainCloseResult(NeutralDomainCloseDecision.Faulted, "Original Close source/owner unavailable.");
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return new(request, false, refused);
            NeutralLeaseValidation<NeutralDomainState> parent = _leases.Validate(request.Domain);
            if (request.InvocationId == Guid.Empty || !parent.IsValid ||
                parent.State!.OriginalAdmission is not { } admission || !ReferenceEquals(admission.Source, parent.State) ||
                admission.Request.InvocationId != request.DomainBindInvocationId ||
                admission.Request.RuntimeIncarnation != request.RuntimeIncarnation || parent.State.OriginalClose is not null)
                return new(request, true, refused);
            NeutralOriginalDomainCloseFact fact = new NeutralOriginalDomainCloseFact(request, parent.State);
            fact.EstablishOriginal();
            parent.State.OriginalClose = fact;
            return ContinueRootCloseLocked(fact);
        }
    }

    private bool OriginalRootCloseSource(NeutralOriginalDomainCloseFact fact) => fact.IsOriginal &&
        fact.Request.RuntimeIncarnation == MappingClosureIncarnation && fact.Request.InvocationId != Guid.Empty &&
        _dependencies.Domains.TryGetValue(fact.Request.Domain.Handle,out NeutralDomainState? source) && ReferenceEquals(source,fact.Source) && source.Lease == fact.Request.Domain &&
        ReferenceEquals(source.OriginalClose,fact) && fact.Admission is { } admission && ReferenceEquals(source.OriginalAdmission,admission) &&
        ReferenceEquals(admission.Original,admission) && ReferenceEquals(admission.Source,source) &&
        admission.Request.RuntimeIncarnation == MappingClosureIncarnation && admission.Request.InvocationId == fact.Request.DomainBindInvocationId;

    private NeutralOriginalDomainCloseObservation ContinueRootCloseLocked(NeutralOriginalDomainCloseFact fact)
    {
        NeutralOriginalDomainCloseRequest request = fact.Request;
        if (!OriginalRootCloseSource(fact)) return new(request,true,new(NeutralDomainCloseDecision.Faulted,"Original retained root Close source unavailable."));
        if (fact.Observation is { } closed)
            return !fact.Source.IsActive && !_dependencies.HasActiveDependents(request.Domain) && ReferenceEquals(closed.Observation,fact.OriginalResult) &&
                closed.Request == request && closed.SourceFenceMatched && closed.Observation.Decision == NeutralDomainCloseDecision.Closed
                ? closed : new(request,true,new(NeutralDomainCloseDecision.Faulted,"Original terminal root closure custody unavailable."));
        if (!fact.Source.IsActive || fact.OriginalResult is not null) return new(request,true,new(NeutralDomainCloseDecision.Faulted,"Root state is not original pending Close."));
        if (_dependencies.HasActiveDependents(request.Domain)) return new(request,true,new(NeutralDomainCloseDecision.ActiveDependents,"Original root has active dependents."));
        fact.Source.Lifecycle = NeutralResourceLifecycle.Revoked;
        NeutralDomainCloseResult result = new NeutralDomainCloseResult(NeutralDomainCloseDecision.Closed,string.Empty);
        fact.OriginalResult = result;
        NeutralOriginalDomainCloseObservation raw = new NeutralOriginalDomainCloseObservation(request,true,result);
        fact.Observation = raw;
        return raw;
    }

    public NeutralOriginalDomainCloseObservation ContinueOriginalDomainClose(NeutralOriginalDomainCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Domains.TryGetValue(request.Domain.Handle,out NeutralDomainState? source) ||
                source.OriginalClose is not { } fact || fact.Request != request)
                return new(request,request.RuntimeIncarnation == MappingClosureIncarnation,new(NeutralDomainCloseDecision.Faulted,"Original root Close intent missing; no new effect."));
            return ContinueRootCloseLocked(fact);
        }
    }

    public NeutralOriginalDomainCloseObservation? ResolveDomainClose(NeutralOriginalDomainCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty ||
                !_dependencies.Domains.TryGetValue(request.Domain.Handle, out NeutralDomainState? source) || source.Lease != request.Domain ||
                source.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.Source, source) ||
                source.OriginalAdmission is not { } admission || !ReferenceEquals(admission.Source, source) ||
                admission.Request.InvocationId != request.DomainBindInvocationId || source.IsActive ||
                _dependencies.HasActiveDependents(request.Domain) || !OriginalRootCloseSource(fact) ||
                fact.Observation is not { } raw || raw.Request != request || !raw.SourceFenceMatched ||
                !ReferenceEquals(raw.Observation,fact.OriginalResult) || raw.Observation.Decision != NeutralDomainCloseDecision.Closed) return null;
            return fact.Observation;
        }
    }

    public NeutralOriginalDomainBindObservation Bind(NeutralOriginalDomainBindRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralDomainBindResult refused = new NeutralDomainBindResult(false, NeutralDomainBindDecision.Faulted, default,
                "Original Bind source/key is unavailable or already invoked.");
            if (request.RuntimeIncarnation != MappingClosureIncarnation)
                return new(request, false, refused);
            if (request.InvocationId == Guid.Empty ||
                _originalBindsIssued!=(ulong)_originalBindInvocations.Count || _originalBindsIssued==ulong.MaxValue ||
                _originalBindInvocations.Any(f=>!ReferenceEquals(f.Original,f) || f.Request.InvocationId==request.InvocationId) ||
                _dependencies.Domains.Values.Any(x => x.OriginalAdmission?.Request.InvocationId == request.InvocationId))
                return new(request, true, refused);
            OriginalBindInvocation invocation =new OriginalBindInvocation(request);
            _originalBindInvocations.Add(invocation);_originalBindsIssued++;
            NeutralDomainBindResult result = BindCore(request.Profile,out bool noEffect);
            invocation.NoEffect=noEffect;
            if (result is { IsBound: true, Decision: NeutralDomainBindDecision.Bound })
            {
                NeutralDomainState source = _dependencies.Domains[result.Lease.Handle];
                source.OriginalAdmission = new(request, source);
                invocation.Admission=source.OriginalAdmission;
            }
            NeutralOriginalDomainBindObservation raw =new NeutralOriginalDomainBindObservation(request,true,result){ProvenNoEffect=noEffect};
            invocation.Raw=raw;return raw;
        }
    }

    public NeutralOriginalDomainBindObservation? ResolveOriginalDomainBind(NeutralOriginalDomainBindRequest request)
    {
        lock(_lifecycleGate)
        {
            if(request.RuntimeIncarnation!=MappingClosureIncarnation || request.InvocationId==Guid.Empty ||
                _originalBindsIssued!=(ulong)_originalBindInvocations.Count ||
                _originalBindInvocations.Any(f=>!ReferenceEquals(f.Original,f)) ||
                _originalBindInvocations.Distinct(ReferenceEqualityComparer.Instance).Count()!=_originalBindInvocations.Count)return null;
            OriginalBindInvocation[] facts =_originalBindInvocations.Where(f=>f.Request.InvocationId==request.InvocationId).ToArray();
            if(facts.Length!=1 || facts[0].Request!=request || facts[0].Raw is not { } raw || raw.Request!=request)return null;
            if(raw.ProvenNoEffect!=facts[0].NoEffect || raw.ProvenNoEffect &&
                (!raw.SourceFenceMatched || raw.Observation.IsBound || raw.Observation.Lease!=default || facts[0].Admission is not null ||
                raw.Observation.Decision is not (NeutralDomainBindDecision.Faulted or NeutralDomainBindDecision.UnsupportedProfile)))return null;
            if(raw.Observation.IsBound)
            {
                NeutralOriginalDomainAdmissionFact? fact =facts[0].Admission;
                if(fact is null || !ReferenceEquals(fact.Original,fact) || fact.Request!=request ||
                    !_dependencies.Domains.TryGetValue(raw.Observation.Lease.Handle,out NeutralDomainState? source) ||
                    !ReferenceEquals(source,fact.Source) || !ReferenceEquals(source.OriginalAdmission,fact) || source.Lease!=raw.Observation.Lease)return null;
            }
            return raw;
        }
    }

    public NeutralOriginalOwnedRegionMapObservation MapOwnedRegion(NeutralOriginalOwnedRegionMapRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOwnedRegionMapResult refused = new NeutralOwnedRegionMapResult { Decision = NeutralOwnedRegionMapDecision.Faulted,
                Reason = "Original Map source/key/parent is unavailable or already invoked." };
            if (request.RuntimeIncarnation != MappingClosureIncarnation)
                return new(request, false, refused);
            NeutralLeaseValidation<NeutralDomainState> parent = _leases.Validate(request.Domain);
            if (request.InvocationId == Guid.Empty || request.DomainBindInvocationId == Guid.Empty || !parent.IsValid ||
                parent.State!.OriginalAdmission is not { } original || !ReferenceEquals(original.Source, parent.State) ||
                original.Request.InvocationId != request.DomainBindInvocationId ||
                original.Request.RuntimeIncarnation != request.RuntimeIncarnation ||
                _dependencies.Mappings.Values.Any(x => x.OriginalAdmission?.Request.InvocationId == request.InvocationId))
                return new(request, true, refused);
            if(parent.State.OriginalMapsIssued!=(ulong)parent.State.OriginalMaps.Count ||
                parent.State.OriginalMapsIssued==ulong.MaxValue || parent.State.OriginalMaps.Any(f=>!ReferenceEquals(f.Original,f) ||
                    !ReferenceEquals(f.ParentAdmission,parent.State.OriginalAdmission) || f.Request.InvocationId==request.InvocationId) ||
                parent.State.OriginalMaps.Distinct(ReferenceEqualityComparer.Instance).Count()!=parent.State.OriginalMaps.Count)
                return new(request,true,refused);
            NeutralOriginalMapInvocation invocation =new NeutralOriginalMapInvocation(request,parent.State);
            parent.State.OriginalMaps.Add(invocation);parent.State.OriginalMapsIssued++;
            NeutralOwnedRegionMapResult result = MapOwnedRegionCore(request.Domain, request.Slice,out bool noEffect);
            invocation.NoEffect=noEffect;
            if (result is { IsMapped: true, Decision: NeutralOwnedRegionMapDecision.Mapped })
            {
                NeutralMappingState source = _dependencies.Mappings[result.Lease.Handle];
                source.OriginalAdmission = new(request, source, parent.State);
                invocation.Admission=source.OriginalAdmission;
            }
            NeutralOriginalOwnedRegionMapObservation raw =new NeutralOriginalOwnedRegionMapObservation(request,true,result){ProvenNoEffect=noEffect};
            invocation.Raw=raw;
            return raw;
        }
    }

    public NeutralOriginalOwnedRegionMapObservation? ResolveOriginalOwnedRegionMap(NeutralOriginalOwnedRegionMapRequest request)
    {
        lock(_lifecycleGate)
        {
            if(request.RuntimeIncarnation!=MappingClosureIncarnation || request.InvocationId==Guid.Empty ||
                !_dependencies.Domains.TryGetValue(request.Domain.Handle,out NeutralDomainState? parent) || parent.Lease!=request.Domain ||
                parent.OriginalAdmission is not { } root || !ReferenceEquals(root.Original,root) || !ReferenceEquals(root.Source,parent) ||
                root.Request.InvocationId!=request.DomainBindInvocationId || root.Request.RuntimeIncarnation!=request.RuntimeIncarnation ||
                parent.OriginalMapsIssued!=(ulong)parent.OriginalMaps.Count ||
                parent.OriginalMaps.Any(f=>!ReferenceEquals(f.Original,f) || !ReferenceEquals(f.Parent,parent) || !ReferenceEquals(f.ParentAdmission,root)) ||
                parent.OriginalMaps.Distinct(ReferenceEqualityComparer.Instance).Count()!=parent.OriginalMaps.Count)return null;
            NeutralOriginalMapInvocation[] facts =parent.OriginalMaps.Where(f=>f.Request.InvocationId==request.InvocationId).ToArray();
            if(facts.Length!=1 || facts[0].Request!=request || facts[0].Raw is not { } raw || raw.Request!=request)return null;
            if(raw.ProvenNoEffect!=facts[0].NoEffect || raw.ProvenNoEffect &&
                (!raw.SourceFenceMatched || raw.Observation.IsMapped || raw.Observation.Lease!=default ||
                raw.Observation.Decision!=NeutralOwnedRegionMapDecision.Faulted || facts[0].Admission is not null))return null;
            if(raw.Observation.IsMapped)
            {
                NeutralOriginalMappingAdmissionFact? admission =facts[0].Admission;
                if(admission is null || admission.Request!=request || !ReferenceEquals(admission.Original,admission) || !ReferenceEquals(admission.Parent,parent) ||
                    !_dependencies.Mappings.TryGetValue(raw.Observation.Lease.Handle,out NeutralMappingState? mapping) ||
                    !ReferenceEquals(mapping,admission.Source) || !ReferenceEquals(mapping.OriginalAdmission,admission) || mapping.Lease!=raw.Observation.Lease)return null;
            }
            return raw;
        }
    }
}
