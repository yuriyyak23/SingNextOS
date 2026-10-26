namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDeviceProvider, INeutralOriginalDeviceLifetimeProvider
{
    private static bool OriginalDeviceHistory(NeutralDomainState parent) => parent.HasIntactOriginalDeviceHistory;
    private static bool OriginalDeviceNoEffect(NeutralOriginalDeviceAdmissionFact fact,NeutralOriginalDeviceBindObservation raw) =>
        fact.NoEffect && raw.NoEffectGuaranteed && raw.SourceFenceMatched && raw.Request==fact.Request && fact.Source is null &&
        raw.Observation is {IsBound:false} result && result.Lease==default && Enum.IsDefined(result.Decision) && result.Decision!=NeutralDeviceBindDecision.Bound;
    public NeutralOriginalDeviceBindObservation BindDevice(NeutralOriginalDeviceBindRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalDeviceBindObservation Refuse(bool matched,bool noEffect) => new(request,matched,noEffect,new() { Decision = NeutralDeviceBindDecision.Faulted });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Domains.TryGetValue(request.Domain.Handle,out NeutralDomainState? retained))
            {
                if (!OriginalDeviceHistory(retained)) return Refuse(true,false);
                NeutralOriginalDeviceAdmissionFact[] old = retained.OriginalDeviceAdmissions.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
                if (old.Length != 0) return old.Length == 1 && old[0].Request == request && old[0].Observation is { } known &&
                    (OriginalDeviceNoEffect(old[0],known) || old[0].Source is { } source && OriginalDeviceSources(source,old[0],request.RuntimeIncarnation,false)) ? known : Refuse(true,false);
            }
            NeutralLeaseValidation<NeutralDomainState> valid = _leases.Validate(request.Domain);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } root ||
                !ReferenceEquals(root.Source,valid.State) || root.Request.InvocationId != request.DomainBindInvocationId || root.Request.RuntimeIncarnation != request.RuntimeIncarnation || valid.State.OriginalDevicesIssued == ulong.MaxValue) return Refuse(true,true);
            NeutralOriginalDeviceAdmissionFact fact = new NeutralOriginalDeviceAdmissionFact(request,valid.State,root);
            valid.State.OriginalDeviceAdmissions.Add(fact); valid.State.OriginalDevicesIssued++; // Before possible device effect.
            NeutralDeviceBindResult result = BindDeviceCore(request.Domain,request.Device,request.Rights,fact,out bool noEffect);
            fact.NoEffect=noEffect;
            if (result is { IsBound: true, Decision: NeutralDeviceBindDecision.Bound })
            { fact.Source = _dependencies.Devices[result.Lease.Handle]; fact.Source.OriginalAdmission = fact; }
            NeutralOriginalDeviceBindObservation raw = new NeutralOriginalDeviceBindObservation(request,true,noEffect,result);
            fact.Observation = raw; return raw;
        }
    }

    public NeutralOriginalDeviceBindObservation? ResolveDeviceBind(NeutralOriginalDeviceBindRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty || !_dependencies.Domains.TryGetValue(request.Domain.Handle,out NeutralDomainState? parent) ||
                parent.Lease!=request.Domain || !OriginalDeviceHistory(parent)) return null;
            NeutralOriginalDeviceAdmissionFact[] history = parent.OriginalDeviceAdmissions.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
            if (history.Length != 1 || history[0].Request != request || !ReferenceEquals(history[0].OwnerSelf,history[0]) || !ReferenceEquals(history[0].Parent,parent) || history[0].Observation is not { } raw || raw.Request != request) return null;
            if (OriginalDeviceNoEffect(history[0],raw)) return raw;
            return history[0].Source is { } source && _dependencies.Devices.TryGetValue(source.Handle,out NeutralDeviceState? routed) && ReferenceEquals(source,routed) &&
                OriginalDeviceSources(source,history[0],request.RuntimeIncarnation,false) ? raw : null;
        }
    }

    public NeutralOriginalDeviceCloseObservation CloseDevice(NeutralOriginalDeviceCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalDeviceCloseObservation Refuse(bool matched,bool noEffect) => new(request,matched,noEffect,new() { Decision = NeutralDeviceCloseDecision.Faulted });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Devices.TryGetValue(request.Device.Handle,out NeutralDeviceState? retained) && retained.OriginalClose is { } previous && previous.Request.InvocationId == request.InvocationId)
                return previous.Request == request && ReferenceEquals(previous.OwnerSelf,previous) && ReferenceEquals(previous.Source,retained) &&
                    previous.Observation is { } raw && OriginalDeviceSources(retained,previous.Admission,request.RuntimeIncarnation,false) ? raw : Refuse(true,false);
            NeutralLeaseValidation<NeutralDeviceState> valid = _leases.Validate(request.Device);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } admission ||
                admission.Request.InvocationId != request.DeviceBindInvocationId || !OriginalDeviceSources(valid.State,admission,request.RuntimeIncarnation)) return Refuse(true,true);
            if (valid.State.OriginalClose is not null) return Refuse(true,false);
            if (_dependencies.HasActiveDependents(request.Device))
                return new(request,true,true,new() { Decision = NeutralDeviceCloseDecision.ActiveDependents });
            NeutralOriginalDeviceCloseFact fact = new NeutralOriginalDeviceCloseFact(request,valid.State,admission);
            valid.State.OriginalClose = fact; // Intent before mutation, source and parent validation under same gate.
            valid.State.Lifecycle = NeutralResourceLifecycle.Revoked;
            NeutralOriginalDeviceCloseObservation closed = new NeutralOriginalDeviceCloseObservation(request,true,false,new() { Decision = NeutralDeviceCloseDecision.Closed });
            fact.Observation = closed;
            valid.State.LifetimeClosure = valid.State.OriginalLifetimeClosure = new(admission.Observation!.Value.Observation, true);
            return closed;
        }
    }

    public NeutralOriginalDeviceCloseObservation? ResolveDeviceClose(NeutralOriginalDeviceCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty || !_dependencies.Devices.TryGetValue(request.Device.Handle,out NeutralDeviceState? source) ||
                source.Lease != request.Device || source.IsActive || source.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.OwnerSelf,fact) ||
                !ReferenceEquals(fact.Source,source) || !ReferenceEquals(source.OriginalAdmission,fact.Admission) || fact.Admission.Request.InvocationId != request.DeviceBindInvocationId ||
                !OriginalDeviceSources(source,fact.Admission,request.RuntimeIncarnation,false)) return null;
            return fact.Observation;
        }
    }

    public NeutralOriginalDeviceLifetimeClosure? ResolveOriginalDeviceLifetime(NeutralDeviceBindResult originalAdmission)
    {
        if (originalAdmission is null) return null;
        lock (_lifecycleGate)
        {
            if (!_dependencies.Devices.TryGetValue(originalAdmission.Lease.Handle, out NeutralDeviceState? source) ||
                source.OriginalAdmission is not { Observation: { } bind } admission || !ReferenceEquals(bind.Observation, originalAdmission) ||
                !OriginalDeviceSources(source, admission, MappingClosureIncarnation, false) || !source.HasExactOriginalClosure ||
                _dependencies.HasActiveDependents(source.Lease) || source.LifetimeClosure is not { NoFurtherUse: true } raw ||
                !ReferenceEquals(raw, source.OriginalLifetimeClosure) || !ReferenceEquals(raw.Admission, originalAdmission)) return null;
            return raw;
        }
    }

    private bool OriginalDeviceSources(NeutralDeviceState source,NeutralOriginalDeviceAdmissionFact admission,Guid incarnation,bool active = true) =>
        ReferenceEquals(source.OwnerSelf,source) && ReferenceEquals(source.OriginalAdmission,admission) && ReferenceEquals(admission.OwnerSelf,admission) && ReferenceEquals(admission.Source,source) &&
        admission.Request.RuntimeIncarnation == incarnation && admission.Observation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw && raw.Request == admission.Request &&
        raw.Observation is { IsBound: true, Decision: NeutralDeviceBindDecision.Bound } && raw.Observation.Lease == source.Lease &&
        _dependencies.Domains.TryGetValue(source.ParentDomain,out NeutralDomainState? parent) && ReferenceEquals(parent,admission.Parent) && ReferenceEquals(source.OriginalParent,parent) &&
        (!active || parent.IsActive) && ReferenceEquals(parent.OriginalAdmission,admission.RootAdmission) && ReferenceEquals(admission.RootAdmission.Source,parent) &&
        admission.RootAdmission.Request.InvocationId == admission.Request.DomainBindInvocationId && parent.OriginalDeviceAdmissions.Any(old => ReferenceEquals(old,admission));
}
