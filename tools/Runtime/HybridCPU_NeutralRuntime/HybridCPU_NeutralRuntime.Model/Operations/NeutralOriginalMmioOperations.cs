namespace YAKSys_Hybrid_CPU.Core;
public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalMmioProvider
{
    private bool OriginalMmioHistory(NeutralDeviceState parent) =>
        (ulong)parent.OriginalMmioAdmissions.Count == parent.OriginalMmioIssued &&
        parent.OriginalMmioAdmissions.Distinct(ReferenceEqualityComparer.Instance).Count() == parent.OriginalMmioAdmissions.Count &&
        parent.OriginalMmioAdmissions.All(f => ReferenceEquals(f.OwnerSelf,f) && ReferenceEquals(f.Parent,parent) &&
            ReferenceEquals(f.DeviceAdmission,parent.OriginalAdmission) && f.Request.Device == parent.Lease &&
            f.Request.DeviceBindInvocationId == f.DeviceAdmission.Request.InvocationId &&
            OriginalDeviceSources(parent,f.DeviceAdmission,f.Request.RuntimeIncarnation,false));
    private static bool OriginalMmioNoEffect(NeutralOriginalMmioAdmissionFact fact,NeutralOriginalMmioMapObservation raw) =>
        fact.NoEffect && raw.NoEffectGuaranteed && raw.SourceFenceMatched && raw.Request == fact.Request && fact.Source is null &&
        raw.Observation is { IsMapped: false } result && result.Lease == default && Enum.IsDefined(result.Decision) && result.Decision != NeutralMmioMapDecision.Mapped;
    public NeutralOriginalMmioMapObservation MapMmio(NeutralOriginalMmioMapRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalMmioMapObservation Refuse(bool source,bool noEffect)
            {
                if(noEffect && request.Backing is {Owner:null} unadmitted)unadmitted.Release();
                return new(request,source,noEffect,new() { Decision = NeutralMmioMapDecision.Faulted });
            }
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Devices.TryGetValue(request.Device.Handle,out NeutralDeviceState? retained))
            {
                if (!OriginalMmioHistory(retained)) return Refuse(true,false);
                NeutralOriginalMmioAdmissionFact[] previous = retained.OriginalMmioAdmissions.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
                if (previous.Length != 0) return previous.Length == 1 && previous[0].Request == request && previous[0].Observation is { } raw &&
                    (OriginalMmioNoEffect(previous[0],raw) || previous[0].Source is { } source && OriginalMmioSources(source,previous[0],request.RuntimeIncarnation,false)) ? raw : Refuse(true,false);
            }
            NeutralLeaseValidation<NeutralDeviceState> valid = _leases.Validate(request.Device);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } factory || factory.Request.InvocationId != request.DeviceBindInvocationId ||
                !OriginalDeviceSources(valid.State,factory,request.RuntimeIncarnation) || valid.State.OriginalMmioIssued == ulong.MaxValue) return Refuse(true,true);
            NeutralOriginalMmioAdmissionFact fact = new NeutralOriginalMmioAdmissionFact(request,valid.State,factory);
            valid.State.OriginalMmioAdmissions.Add(fact); valid.State.OriginalMmioIssued++; // Original intent before effect.
            NeutralMmioMapResult result = MapMmioCore(request.Device,request.Region,request.Range,request.Access,fact,out bool noEffect);
            fact.NoEffect = noEffect;
            if(noEffect && request.Backing is { } refusedBacking && (refusedBacking.Owner is null || ReferenceEquals(refusedBacking.Owner,fact))) refusedBacking.Release();
            if (result is { IsMapped: true,Decision: NeutralMmioMapDecision.Mapped }) { fact.Source = _dependencies.Mmio[result.Lease.Handle]; fact.Source.OriginalAdmission = fact; }
            NeutralOriginalMmioMapObservation observation = new NeutralOriginalMmioMapObservation(request,true,noEffect,result); fact.Observation = observation; return observation;
        }
    }
    public bool ValidateMmioAdmission(NeutralOriginalMmioMapRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Devices.TryGetValue(request.Device.Handle,out NeutralDeviceState? parent)) return false;
            NeutralOriginalMmioAdmissionFact[] facts = parent.OriginalMmioAdmissions.Where(fact => fact.Request == request).ToArray();
            return facts.Length == 1 && facts[0].Source is { } source && source.IsActive && _dependencies.Mmio.TryGetValue(source.Handle,out NeutralMmioState? current) && ReferenceEquals(current,source) && OriginalMmioSources(source,facts[0],request.RuntimeIncarnation);
        }
    }
    public NeutralOriginalMmioMapObservation? ResolveMmioMap(NeutralOriginalMmioMapRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty || !_dependencies.Devices.TryGetValue(request.Device.Handle,out NeutralDeviceState? parent) ||
                parent.Lease != request.Device || !OriginalMmioHistory(parent)) return null;
            NeutralOriginalMmioAdmissionFact[] facts = parent.OriginalMmioAdmissions.Where(old => old.Request == request).ToArray();
            if (facts.Length != 1 || !ReferenceEquals(facts[0].OwnerSelf,facts[0]) || !ReferenceEquals(facts[0].Parent,parent) || facts[0].Observation is not { } raw || raw.Request != request) return null;
            if (OriginalMmioNoEffect(facts[0],raw)) return raw;
            return facts[0].Source is { } source && _dependencies.Mmio.TryGetValue(source.Handle,out NeutralMmioState? route) && ReferenceEquals(route,source) && OriginalMmioSources(source,facts[0],request.RuntimeIncarnation,false) ? raw : null;
        }
    }
    public NeutralOriginalMmioCloseObservation CloseMmio(NeutralOriginalMmioCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalMmioCloseObservation Refuse(bool source,bool noEffect) => new(request,source,noEffect,new() { Decision = NeutralMmioCloseDecision.Faulted });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Mmio.TryGetValue(request.Mapping.Handle,out NeutralMmioState? retained) && retained.OriginalClose is { } prior && prior.Request.InvocationId == request.InvocationId)
                return prior.Request == request && ReferenceEquals(prior.OwnerSelf,prior) && ReferenceEquals(prior.Source,retained) && prior.Observation is { } raw && OriginalMmioSources(retained,prior.Admission,request.RuntimeIncarnation,false) ? raw : Refuse(true,false);
            NeutralLeaseValidation<NeutralMmioState> valid = _leases.Validate(request.Mapping);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } admission || admission.Request.InvocationId != request.MapInvocationId ||
                !OriginalMmioSources(valid.State,admission,request.RuntimeIncarnation)) return Refuse(true,true);
            if (valid.State.OriginalClose is not null) return Refuse(true,false);
            if(!IntactMmioAccesses(valid.State))return Refuse(true,false);
            NeutralOriginalMmioCloseFact fact = new NeutralOriginalMmioCloseFact(request,valid.State,admission); valid.State.OriginalClose = fact;
            valid.State.Lifecycle = NeutralResourceLifecycle.Revoked;
            admission.Request.Backing?.Release();
            NeutralOriginalMmioCloseObservation observation = new NeutralOriginalMmioCloseObservation(request,true,false,new() { Decision = NeutralMmioCloseDecision.Closed }); fact.Observation = observation; return observation;
        }
    }
    public NeutralOriginalMmioCloseObservation? ResolveMmioClose(NeutralOriginalMmioCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Mmio.TryGetValue(request.Mapping.Handle,out NeutralMmioState? source) || source.Lease != request.Mapping || source.IsActive ||
                source.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.Source,source) || !ReferenceEquals(source.OriginalAdmission,fact.Admission) ||
                fact.Admission.Request.InvocationId != request.MapInvocationId || !OriginalMmioSources(source,fact.Admission,request.RuntimeIncarnation,false)) return null;
            return fact.Observation;
        }
    }
    private bool OriginalMmioSources(NeutralMmioState source,NeutralOriginalMmioAdmissionFact admission,Guid incarnation,bool active = true) =>
        ReferenceEquals(source.OwnerSelf,source) && ReferenceEquals(source.OriginalAdmission,admission) && ReferenceEquals(admission.OwnerSelf,admission) && ReferenceEquals(admission.Source,source) &&
        (admission.Request.Backing is null || ReferenceEquals(admission.Request.Backing.Original,admission.Request.Backing) && ReferenceEquals(admission.Request.Backing.Owner,admission) && (!active || admission.Request.Backing.IsActive)) &&
        admission.Request.RuntimeIncarnation == incarnation && admission.Observation is { SourceFenceMatched: true,NoEffectGuaranteed: false } raw && raw.Request == admission.Request &&
        raw.Observation is { IsMapped: true,Decision: NeutralMmioMapDecision.Mapped } && raw.Observation.Lease == source.Lease &&
        _dependencies.Devices.TryGetValue(source.ParentDevice,out NeutralDeviceState? parent) && ReferenceEquals(parent,admission.Parent) && (!active || parent.IsActive) &&
        ReferenceEquals(parent.OriginalAdmission,admission.DeviceAdmission) && admission.DeviceAdmission.Request.InvocationId == admission.Request.DeviceBindInvocationId &&
        parent.OriginalMmioAdmissions.Any(old => ReferenceEquals(old,admission)) && OriginalDeviceSources(parent,admission.DeviceAdmission,incarnation,active);
}
