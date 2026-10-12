namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDomainCloseProvider
{
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
                _dependencies.Domains.Values.Any(x => x.OriginalAdmission?.Request.InvocationId == request.InvocationId))
                return new(request, true, refused);
            var result = Bind(request.Profile);
            if (result is { IsBound: true, Decision: NeutralDomainBindDecision.Bound })
            {
                var source = _dependencies.Domains[result.Lease.Handle];
                source.OriginalAdmission = new(request, source);
            }
            return new(request, true, result);
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
            var result = MapOwnedRegion(request.Domain, request.Slice);
            if (result is { IsMapped: true, Decision: NeutralOwnedRegionMapDecision.Mapped })
            {
                var source = _dependencies.Mappings[result.Lease.Handle];
                source.OriginalAdmission = new(request, source, parent.State);
            }
            return new(request, true, result);
        }
    }
}
