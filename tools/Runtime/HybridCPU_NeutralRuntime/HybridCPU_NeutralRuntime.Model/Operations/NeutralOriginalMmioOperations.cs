namespace YAKSys_Hybrid_CPU.Core;
public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalMmioProvider
{
    public NeutralOriginalMmioMapObservation MapMmio(NeutralOriginalMmioMapRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalMmioMapObservation Refuse(bool source,bool noEffect) => new(request,source,noEffect,new() { Decision = NeutralMmioMapDecision.Faulted });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Devices.TryGetValue(request.Device.Handle,out var retained))
            {
                if ((ulong)retained.OriginalMmioAdmissions.Count != retained.OriginalMmioIssued || retained.OriginalMmioAdmissions.Any(old => !ReferenceEquals(old.OwnerSelf,old) || !ReferenceEquals(old.Parent,retained))) return Refuse(true,false);
                var previous = retained.OriginalMmioAdmissions.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
                if (previous.Length != 0) return previous.Length == 1 && previous[0].Request == request && previous[0].Observation is { } raw &&
                    (raw.NoEffectGuaranteed && previous[0].Source is null || previous[0].Source is { } source && OriginalMmioSources(source,previous[0],request.RuntimeIncarnation,false)) ? raw : Refuse(true,false);
            }
            var valid = _leases.Validate(request.Device);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } factory || factory.Request.InvocationId != request.DeviceBindInvocationId ||
                !OriginalDeviceSources(valid.State,factory,request.RuntimeIncarnation) || valid.State.OriginalMmioIssued == ulong.MaxValue) return Refuse(true,true);
            var fact = new NeutralOriginalMmioAdmissionFact(request,valid.State,factory);
            valid.State.OriginalMmioAdmissions.Add(fact); valid.State.OriginalMmioIssued++; // Original intent before effect.
            var result = MapMmioCore(request.Device,request.Region,request.Range,request.Access,fact);
            if (result is { IsMapped: true,Decision: NeutralMmioMapDecision.Mapped }) { fact.Source = _dependencies.Mmio[result.Lease.Handle]; fact.Source.OriginalAdmission = fact; }
            var observation = new NeutralOriginalMmioMapObservation(request,true,!result.IsMapped && result.Lease == default,result); fact.Observation = observation; return observation;
        }
    }
    public bool ValidateMmioAdmission(NeutralOriginalMmioMapRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Devices.TryGetValue(request.Device.Handle,out var parent)) return false;
            var facts = parent.OriginalMmioAdmissions.Where(fact => fact.Request == request).ToArray();
            return facts.Length == 1 && facts[0].Source is { } source && source.IsActive && _dependencies.Mmio.TryGetValue(source.Handle,out var current) && ReferenceEquals(current,source) && OriginalMmioSources(source,facts[0],request.RuntimeIncarnation);
        }
    }
    public NeutralOriginalMmioMapObservation? ResolveMmioMap(NeutralOriginalMmioMapRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Devices.TryGetValue(request.Device.Handle,out var parent)) return null;
            var facts = parent.OriginalMmioAdmissions.Where(old => old.Request == request).ToArray();
            if (facts.Length != 1 || !ReferenceEquals(facts[0].OwnerSelf,facts[0]) || !ReferenceEquals(facts[0].Parent,parent) || facts[0].Observation is not { } raw || raw.Request != request) return null;
            if (raw.NoEffectGuaranteed && facts[0].Source is null && !raw.Observation.IsMapped && raw.Observation.Lease == default) return raw;
            return facts[0].Source is { } source && _dependencies.Mmio.TryGetValue(source.Handle,out var route) && ReferenceEquals(route,source) && OriginalMmioSources(source,facts[0],request.RuntimeIncarnation,false) ? raw : null;
        }
    }
    public NeutralOriginalMmioCloseObservation CloseMmio(NeutralOriginalMmioCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalMmioCloseObservation Refuse(bool source,bool noEffect) => new(request,source,noEffect,new() { Decision = NeutralMmioCloseDecision.Faulted });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Mmio.TryGetValue(request.Mapping.Handle,out var retained) && retained.OriginalClose is { } prior && prior.Request.InvocationId == request.InvocationId)
                return prior.Request == request && ReferenceEquals(prior.OwnerSelf,prior) && ReferenceEquals(prior.Source,retained) && prior.Observation is { } raw && OriginalMmioSources(retained,prior.Admission,request.RuntimeIncarnation,false) ? raw : Refuse(true,false);
            var valid = _leases.Validate(request.Mapping);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } admission || admission.Request.InvocationId != request.MapInvocationId ||
                !OriginalMmioSources(valid.State,admission,request.RuntimeIncarnation)) return Refuse(true,true);
            if (valid.State.OriginalClose is not null) return Refuse(true,false);
            var fact = new NeutralOriginalMmioCloseFact(request,valid.State,admission); valid.State.OriginalClose = fact;
            valid.State.Lifecycle = NeutralResourceLifecycle.Revoked;
            var observation = new NeutralOriginalMmioCloseObservation(request,true,false,new() { Decision = NeutralMmioCloseDecision.Closed }); fact.Observation = observation; return observation;
        }
    }
    public NeutralOriginalMmioCloseObservation? ResolveMmioClose(NeutralOriginalMmioCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Mmio.TryGetValue(request.Mapping.Handle,out var source) || source.Lease != request.Mapping || source.IsActive ||
                source.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.Source,source) || !ReferenceEquals(source.OriginalAdmission,fact.Admission) ||
                fact.Admission.Request.InvocationId != request.MapInvocationId || !OriginalMmioSources(source,fact.Admission,request.RuntimeIncarnation,false)) return null;
            return fact.Observation;
        }
    }
    private bool OriginalMmioSources(NeutralMmioState source,NeutralOriginalMmioAdmissionFact admission,Guid incarnation,bool active = true) =>
        ReferenceEquals(source.OwnerSelf,source) && ReferenceEquals(source.OriginalAdmission,admission) && ReferenceEquals(admission.OwnerSelf,admission) && ReferenceEquals(admission.Source,source) &&
        admission.Request.RuntimeIncarnation == incarnation && admission.Observation is { SourceFenceMatched: true,NoEffectGuaranteed: false } raw && raw.Request == admission.Request &&
        raw.Observation is { IsMapped: true,Decision: NeutralMmioMapDecision.Mapped } && raw.Observation.Lease == source.Lease &&
        _dependencies.Devices.TryGetValue(source.ParentDevice,out var parent) && ReferenceEquals(parent,admission.Parent) && (!active || parent.IsActive) &&
        ReferenceEquals(parent.OriginalAdmission,admission.DeviceAdmission) && admission.DeviceAdmission.Request.InvocationId == admission.Request.DeviceBindInvocationId &&
        parent.OriginalMmioAdmissions.Any(old => ReferenceEquals(old,admission)) && OriginalDeviceSources(parent,admission.DeviceAdmission,incarnation,active);
}
