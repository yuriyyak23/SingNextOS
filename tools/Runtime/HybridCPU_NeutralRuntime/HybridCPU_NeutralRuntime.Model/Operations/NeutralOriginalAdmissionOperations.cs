namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDomainCloseProvider, INeutralOriginalMapOutcomeProvider,INeutralOriginalBindOutcomeProvider
{
    private sealed class OriginalBindInvocation
    {
        public OriginalBindInvocation(NeutralOriginalDomainBindRequest request){Original=this;Request=request;}
        public OriginalBindInvocation Original {get;}
        public NeutralOriginalDomainBindRequest Request {get;}
        public NeutralOriginalDomainBindObservation? Raw;
        public NeutralOriginalDomainAdmissionFact? Admission;
    }
    private readonly List<OriginalBindInvocation> _originalBindInvocations=[];
    private ulong _originalBindsIssued;
    NeutralOriginalDomainCloseObservation INeutralOriginalDomainCloseProvider.Close(NeutralOriginalDomainCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            var refused = new NeutralDomainCloseResult(NeutralDomainCloseDecision.Faulted, "Original Close source/owner unavailable.");
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return new(request, false, refused);
            var parent = _leases.Validate(request.Domain);
            if (request.InvocationId == Guid.Empty || !parent.IsValid ||
                parent.State!.OriginalAdmission is not { } admission || !ReferenceEquals(admission.Source, parent.State) ||
                admission.Request.InvocationId != request.DomainBindInvocationId ||
                admission.Request.RuntimeIncarnation != request.RuntimeIncarnation || parent.State.OriginalClose is not null)
                return new(request, true, refused);
            if (_dependencies.HasActiveDependents(request.Domain))
                return new(request, true, new(NeutralDomainCloseDecision.ActiveDependents, "Original root has active dependents."));
            var fact = new NeutralOriginalDomainCloseFact(request, parent.State);
            parent.State.OriginalClose = fact;
            parent.State.Lifecycle = NeutralResourceLifecycle.Revoked;
            var result = new NeutralOriginalDomainCloseObservation(request, true, new(NeutralDomainCloseDecision.Closed, string.Empty));
            fact.Observation = result;
            return result;
        }
    }

    public NeutralOriginalDomainCloseObservation? ResolveDomainClose(NeutralOriginalDomainCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty ||
                !_dependencies.Domains.TryGetValue(request.Domain.Handle, out var source) || source.Lease != request.Domain ||
                source.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.Source, source) ||
                source.OriginalAdmission is not { } admission || !ReferenceEquals(admission.Source, source) ||
                admission.Request.InvocationId != request.DomainBindInvocationId || source.IsActive ||
                _dependencies.HasActiveDependents(request.Domain)) return null;
            return fact.Observation;
        }
    }

    public NeutralOriginalDomainBindObservation Bind(NeutralOriginalDomainBindRequest request)
    {
        lock (_lifecycleGate)
        {
            var refused = new NeutralDomainBindResult(false, NeutralDomainBindDecision.Faulted, default,
                "Original Bind source/key is unavailable or already invoked.");
            if (request.RuntimeIncarnation != MappingClosureIncarnation)
                return new(request, false, refused);
            if (request.InvocationId == Guid.Empty ||
                _originalBindsIssued!=(ulong)_originalBindInvocations.Count || _originalBindsIssued==ulong.MaxValue ||
                _originalBindInvocations.Any(f=>!ReferenceEquals(f.Original,f) || f.Request.InvocationId==request.InvocationId) ||
                _dependencies.Domains.Values.Any(x => x.OriginalAdmission?.Request.InvocationId == request.InvocationId))
                return new(request, true, refused);
            var invocation=new OriginalBindInvocation(request);
            _originalBindInvocations.Add(invocation);_originalBindsIssued++;
            var result = Bind(request.Profile);
            if (result is { IsBound: true, Decision: NeutralDomainBindDecision.Bound })
            {
                var source = _dependencies.Domains[result.Lease.Handle];
                source.OriginalAdmission = new(request, source);
                invocation.Admission=source.OriginalAdmission;
            }
            var raw=new NeutralOriginalDomainBindObservation(request,true,result);
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
            var facts=_originalBindInvocations.Where(f=>f.Request.InvocationId==request.InvocationId).ToArray();
            if(facts.Length!=1 || facts[0].Request!=request || facts[0].Raw is not { } raw || raw.Request!=request)return null;
            if(raw.Observation.IsBound)
            {
                var fact=facts[0].Admission;
                if(fact is null || !ReferenceEquals(fact.Original,fact) || fact.Request!=request ||
                    !_dependencies.Domains.TryGetValue(raw.Observation.Lease.Handle,out var source) ||
                    !ReferenceEquals(source,fact.Source) || !ReferenceEquals(source.OriginalAdmission,fact) || source.Lease!=raw.Observation.Lease)return null;
            }
            return raw;
        }
    }

    public NeutralOriginalOwnedRegionMapObservation MapOwnedRegion(NeutralOriginalOwnedRegionMapRequest request)
    {
        lock (_lifecycleGate)
        {
            var refused = new NeutralOwnedRegionMapResult { Decision = NeutralOwnedRegionMapDecision.Faulted,
                Reason = "Original Map source/key/parent is unavailable or already invoked." };
            if (request.RuntimeIncarnation != MappingClosureIncarnation)
                return new(request, false, refused);
            var parent = _leases.Validate(request.Domain);
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
            var invocation=new NeutralOriginalMapInvocation(request,parent.State);
            parent.State.OriginalMaps.Add(invocation);parent.State.OriginalMapsIssued++;
            var result = MapOwnedRegion(request.Domain, request.Slice);
            if (result is { IsMapped: true, Decision: NeutralOwnedRegionMapDecision.Mapped })
            {
                var source = _dependencies.Mappings[result.Lease.Handle];
                source.OriginalAdmission = new(request, source, parent.State);
                invocation.Admission=source.OriginalAdmission;
            }
            var raw=new NeutralOriginalOwnedRegionMapObservation(request,true,result);
            invocation.Raw=raw;
            return raw;
        }
    }

    public NeutralOriginalOwnedRegionMapObservation? ResolveOriginalOwnedRegionMap(NeutralOriginalOwnedRegionMapRequest request)
    {
        lock(_lifecycleGate)
        {
            if(request.RuntimeIncarnation!=MappingClosureIncarnation || request.InvocationId==Guid.Empty ||
                !_dependencies.Domains.TryGetValue(request.Domain.Handle,out var parent) || parent.Lease!=request.Domain ||
                parent.OriginalAdmission is not { } root || !ReferenceEquals(root.Original,root) || !ReferenceEquals(root.Source,parent) ||
                root.Request.InvocationId!=request.DomainBindInvocationId || root.Request.RuntimeIncarnation!=request.RuntimeIncarnation ||
                parent.OriginalMapsIssued!=(ulong)parent.OriginalMaps.Count ||
                parent.OriginalMaps.Any(f=>!ReferenceEquals(f.Original,f) || !ReferenceEquals(f.Parent,parent) || !ReferenceEquals(f.ParentAdmission,root)) ||
                parent.OriginalMaps.Distinct(ReferenceEqualityComparer.Instance).Count()!=parent.OriginalMaps.Count)return null;
            var facts=parent.OriginalMaps.Where(f=>f.Request.InvocationId==request.InvocationId).ToArray();
            if(facts.Length!=1 || facts[0].Request!=request || facts[0].Raw is not { } raw || raw.Request!=request)return null;
            if(raw.Observation.IsMapped)
            {
                var admission=facts[0].Admission;
                if(admission is null || admission.Request!=request || !ReferenceEquals(admission.Original,admission) || !ReferenceEquals(admission.Parent,parent) ||
                    !_dependencies.Mappings.TryGetValue(raw.Observation.Lease.Handle,out var mapping) ||
                    !ReferenceEquals(mapping,admission.Source) || !ReferenceEquals(mapping.OriginalAdmission,admission) || mapping.Lease!=raw.Observation.Lease)return null;
            }
            return raw;
        }
    }
}
