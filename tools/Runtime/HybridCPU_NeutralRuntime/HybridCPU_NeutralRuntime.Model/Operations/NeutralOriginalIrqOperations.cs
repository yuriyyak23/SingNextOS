namespace YAKSys_Hybrid_CPU.Core;
public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalIrqProvider
{
    public NeutralOriginalIrqContainObservation ContainInterrupt(NeutralOriginalIrqContainRequest request)
    {
        lock (_lifecycleGate)
        {
            var refused = new NeutralOriginalIrqContainObservation(request,false);
            if (request.Admission.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Interrupts.TryGetValue(request.Binding.Handle,out var source) || source.Lease != request.Binding) return refused;
            if (source.OriginalContainment is not null) return ResolveIrqContainment(request) ?? refused;
            if (request.InvocationId == Guid.Empty || !OriginalIrqDeliverySource(request.Admission,request.Binding,out var original) || !ReferenceEquals(original,source) ||
                (ulong)source.Polls.Count != source.PollsIssued || (ulong)source.Completions.Count != source.CompletionsIssued ||
                source.Polls.Any(f => !OriginalIrqPollFact(source,f)) || source.Completions.Any(f => ResolveIrqCompletion(f.Request) != f.Observation || f.Observation is null)) return refused;
            var fact = new NeutralOriginalIrqContainFact(request,source,source.OriginalAdmission!);
            source.OriginalContainment = fact; // Identity before retirement at the native effect lock.
            source.Lifecycle = NeutralResourceLifecycle.Revoked;
            source.Delivery = NeutralDeliveryState.Idle;
            var raw = new NeutralOriginalIrqContainObservation(request,true); fact.Observation = raw; return raw;
        }
    }
    public NeutralOriginalIrqContainObservation? ResolveIrqContainment(NeutralOriginalIrqContainRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.Admission.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Interrupts.TryGetValue(request.Binding.Handle,out var source) || source.Lease != request.Binding || source.IsActive ||
                source.OriginalContainment is not { } fact || fact.Request != request || !ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.Source,source) ||
                source.OriginalAdmission is not { } admission || !ReferenceEquals(fact.Admission,admission) || admission.Request != request.Admission ||
                !OriginalIrqSources(source,admission,request.Admission.RuntimeIncarnation,false) || fact.Observation is not { NoFurtherDeliveryGuaranteed: true } raw || raw.Request != request) return null;
            return raw;
        }
    }
    public NeutralOriginalIrqBindObservation BindInterrupt(NeutralOriginalIrqBindRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalIrqBindObservation Refuse(bool source,bool noEffect) => new(request,source,noEffect,new() { Decision = NeutralInterruptBindDecision.Faulted });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Devices.TryGetValue(request.Device.Handle,out var retained))
            {
                if ((ulong)retained.OriginalIrqAdmissions.Count != retained.OriginalIrqIssued || retained.OriginalIrqAdmissions.Any(old => !ReferenceEquals(old.OwnerSelf,old) || !ReferenceEquals(old.Parent,retained))) return Refuse(true,false);
                var previous = retained.OriginalIrqAdmissions.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
                if (previous.Length != 0) return previous.Length == 1 && previous[0].Request == request && previous[0].Observation is { } raw &&
                    (raw.NoEffectGuaranteed && previous[0].Source is null || previous[0].Source is { } source && OriginalIrqSources(source,previous[0],request.RuntimeIncarnation,false)) ? raw : Refuse(true,false);
            }
            var valid = _leases.Validate(request.Device);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } factory || factory.Request.InvocationId != request.DeviceBindInvocationId ||
                !OriginalDeviceSources(valid.State,factory,request.RuntimeIncarnation) || valid.State.OriginalIrqIssued == ulong.MaxValue) return Refuse(true,true);
            var fact = new NeutralOriginalIrqAdmissionFact(request,valid.State,factory);
            valid.State.OriginalIrqAdmissions.Add(fact); valid.State.OriginalIrqIssued++; // Original intent before effect.
            var result = BindInterruptCore(request.Device,request.Source,fact);
            if (result is { IsBound: true,Decision: NeutralInterruptBindDecision.Bound }) { fact.Source = _dependencies.Interrupts[result.Lease.Handle]; fact.Source.OriginalAdmission = fact; }
            var observation = new NeutralOriginalIrqBindObservation(request,true,!result.IsBound && result.Lease == default,result); fact.Observation = observation; return observation;
        }
    }
    public bool ValidateIrqAdmission(NeutralOriginalIrqBindRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Devices.TryGetValue(request.Device.Handle,out var parent)) return false;
            var facts = parent.OriginalIrqAdmissions.Where(fact => fact.Request == request).ToArray();
            return facts.Length == 1 && facts[0].Source is { } source && source.IsActive && _dependencies.Interrupts.TryGetValue(source.Handle,out var current) && ReferenceEquals(current,source) && OriginalIrqSources(source,facts[0],request.RuntimeIncarnation);
        }
    }
    public NeutralOriginalIrqBindObservation? ResolveIrqBind(NeutralOriginalIrqBindRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Devices.TryGetValue(request.Device.Handle,out var parent)) return null;
            var facts = parent.OriginalIrqAdmissions.Where(old => old.Request == request).ToArray();
            if (facts.Length != 1 || !ReferenceEquals(facts[0].OwnerSelf,facts[0]) || !ReferenceEquals(facts[0].Parent,parent) || facts[0].Observation is not { } raw || raw.Request != request) return null;
            if (raw.NoEffectGuaranteed && facts[0].Source is null && !raw.Observation.IsBound && raw.Observation.Lease == default) return raw;
            return facts[0].Source is { } source && _dependencies.Interrupts.TryGetValue(source.Handle,out var route) && ReferenceEquals(route,source) && OriginalIrqSources(source,facts[0],request.RuntimeIncarnation,false) ? raw : null;
        }
    }
    private bool OriginalIrqDeliverySource(NeutralOriginalIrqBindRequest admission,NeutralInterruptLease lease,out NeutralInterruptState? source)
    {
        source = null;
        if (admission.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Interrupts.TryGetValue(lease.Handle,out var owner) || owner.Lease != lease ||
            owner.OriginalAdmission is not { } fact || fact.Request != admission || !owner.IsActive || owner.SourceRevisionExhausted || !OriginalIrqSources(owner,fact,admission.RuntimeIncarnation)) return false;
        source = owner; return true;
    }
    private bool OriginalIrqPollFact(NeutralInterruptState source,NeutralOriginalIrqPollFact fact) => (ulong)source.Polls.Count == source.PollsIssued &&
        !source.Polls.Any(old => !ReferenceEquals(old.OwnerSelf,old) || !ReferenceEquals(old.Source,source)) && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,source) &&
        ReferenceEquals(source.OriginalAdmission,fact.Admission) && source.Polls.Any(old => ReferenceEquals(old,fact)) && fact.Observation is { } raw && raw.Request == fact.Request &&
        OriginalIrqSources(source,fact.Admission,fact.Request.Admission.RuntimeIncarnation,false);
    public NeutralOriginalIrqPollObservation PollInterrupt(NeutralOriginalIrqPollRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalIrqPollObservation Refuse(bool noEffect) => new(request,false,noEffect,new() { Lease = request.Binding,Decision = NeutralInterruptPollDecision.Faulted },0);
            if (!OriginalIrqDeliverySource(request.Admission,request.Binding,out var source) || request.InvocationId == Guid.Empty) return Refuse(true);
            if ((ulong)source!.Polls.Count != source.PollsIssued || source.Polls.Any(f => !ReferenceEquals(f.OwnerSelf,f) || !ReferenceEquals(f.Source,source))) return Refuse(false);
            var prior = source.Polls.Where(f => f.Request.InvocationId == request.InvocationId).ToArray();
            if (prior.Length != 0) return prior.Length == 1 && prior[0].Request == request && OriginalIrqPollFact(source,prior[0]) ? prior[0].Observation!.Value : Refuse(false);
            if (source.PollsIssued == ulong.MaxValue) return Refuse(true);
            var fact = new NeutralOriginalIrqPollFact(request,source,source.OriginalAdmission!); source.Polls.Add(fact); source.PollsIssued++;
            var observation = new NeutralOriginalIrqPollObservation(request,true,false,PollInterrupt(request.Binding),source.SourceRevision); fact.Observation = observation; return observation;
        }
    }
    public NeutralOriginalIrqPollObservation? ResolveIrqPoll(NeutralOriginalIrqPollRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.Admission.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Interrupts.TryGetValue(request.Binding.Handle,out var source) || source.Lease != request.Binding) return null;
            var facts = source.Polls.Where(f => f.Request == request).ToArray();
            return facts.Length == 1 && OriginalIrqPollFact(source,facts[0]) ? facts[0].Observation : null;
        }
    }
    public bool ValidateIrqPoll(NeutralOriginalIrqPollRequest request)
    {
        lock (_lifecycleGate) return OriginalIrqDeliverySource(request.Admission,request.Binding,out var source) && ResolveIrqPoll(request) is { } raw && raw.SourceRevision == source!.SourceRevision &&
            raw.Observation.Sequence == source.Sequence && raw.Observation.DeliveryAvailable == (source.Delivery == NeutralDeliveryState.Pending);
    }
    public NeutralOriginalIrqCompleteObservation CompleteInterruptDelivery(NeutralOriginalIrqCompleteRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalIrqCompleteObservation Refuse(bool noEffect) => new(request,false,noEffect,new() { Decision = NeutralInterruptCompleteDecision.Faulted },0);
            if (request.Admission.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(true);
            if (_dependencies.Interrupts.TryGetValue(request.Binding.Handle,out var retained))
            {
                if ((ulong)retained.Completions.Count != retained.CompletionsIssued || retained.Completions.Any(f => !ReferenceEquals(f.OwnerSelf,f) || !ReferenceEquals(f.Source,retained))) return Refuse(false);
                if (retained.Completions.Any(f => f.Request.InvocationId == request.InvocationId)) return ResolveIrqCompletion(request) ?? Refuse(false);
            }
            if (!OriginalIrqDeliverySource(request.Admission,request.Binding,out var source) || request.InvocationId == Guid.Empty || source!.CompletionsIssued == ulong.MaxValue) return Refuse(true);
            var polls = source.Polls.Where(f => f.Request.InvocationId == request.PollInvocationId).ToArray();
            if (polls.Length != 1 || !OriginalIrqPollFact(source,polls[0]) || !ValidateIrqPoll(polls[0].Request) || polls[0].Observation!.Value.Observation.Sequence != request.Sequence ||
                !polls[0].Observation.Value.Observation.DeliveryAvailable) return Refuse(true);
            var fact = new NeutralOriginalIrqCompleteFact(request,source,source.OriginalAdmission!,polls[0]); source.Completions.Add(fact); source.CompletionsIssued++;
            var result = CompleteInterruptDelivery(request.Binding,request.Sequence);
            var raw = new NeutralOriginalIrqCompleteObservation(request,true,result.Decision != NeutralInterruptCompleteDecision.Completed,result,source.SourceRevision); fact.Observation = raw; return raw;
        }
    }
    public NeutralOriginalIrqCompleteObservation? ResolveIrqCompletion(NeutralOriginalIrqCompleteRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.Admission.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Interrupts.TryGetValue(request.Binding.Handle,out var source) || source.Lease != request.Binding) return null;
            var facts = source.Completions.Where(f => f.Request == request).ToArray();
            if ((ulong)source.Completions.Count != source.CompletionsIssued || source.Completions.Any(f => !ReferenceEquals(f.OwnerSelf,f) || !ReferenceEquals(f.Source,source)) ||
                facts.Length != 1 || !ReferenceEquals(facts[0].OwnerSelf,facts[0]) || !ReferenceEquals(facts[0].Source,source) || !ReferenceEquals(source.OriginalAdmission,facts[0].Admission) ||
                !OriginalIrqPollFact(source,facts[0].Poll) || !OriginalIrqSources(source,facts[0].Admission,request.Admission.RuntimeIncarnation,false)) return null;
            return facts[0].Observation;
        }
    }
    public bool ValidateIrqCompletion(NeutralOriginalIrqCompleteRequest request)
    {
        lock (_lifecycleGate) return OriginalIrqDeliverySource(request.Admission,request.Binding,out var source) && ResolveIrqCompletion(request) is { SourceFenceMatched: true,NoEffectGuaranteed: false } raw &&
            raw.Observation.Decision == NeutralInterruptCompleteDecision.Completed && raw.SourceRevision == source!.SourceRevision && source.Sequence == request.Sequence && source.Delivery == NeutralDeliveryState.Idle;
    }
    public NeutralOriginalIrqCloseObservation CloseInterrupt(NeutralOriginalIrqCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalIrqCloseObservation Refuse(bool source,bool noEffect) => new(request,source,noEffect,new() { Decision = NeutralInterruptCloseDecision.Faulted });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (_dependencies.Interrupts.TryGetValue(request.Binding.Handle,out var retained) && retained.OriginalClose is { } prior && prior.Request.InvocationId == request.InvocationId)
                return prior.Request == request && ReferenceEquals(prior.OwnerSelf,prior) && ReferenceEquals(prior.Source,retained) && prior.Observation is { } raw && OriginalIrqSources(retained,prior.Admission,request.RuntimeIncarnation,false) ? raw : Refuse(true,false);
            var valid = _leases.Validate(request.Binding);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } admission || admission.Request.InvocationId != request.BindInvocationId ||
                !OriginalIrqSources(valid.State,admission,request.RuntimeIncarnation)) return Refuse(true,true);
            if (valid.State.OriginalClose is not null) return Refuse(true,false);
            if (valid.State.Delivery == NeutralDeliveryState.Pending) return new(request,true,true,new() { Decision = NeutralInterruptCloseDecision.PendingDelivery });
            var fact = new NeutralOriginalIrqCloseFact(request,valid.State,admission); valid.State.OriginalClose = fact;
            valid.State.Lifecycle = NeutralResourceLifecycle.Revoked;
            var observation = new NeutralOriginalIrqCloseObservation(request,true,false,new() { Decision = NeutralInterruptCloseDecision.Closed }); fact.Observation = observation; return observation;
        }
    }
    public NeutralOriginalIrqCloseObservation? ResolveIrqClose(NeutralOriginalIrqCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || !_dependencies.Interrupts.TryGetValue(request.Binding.Handle,out var source) || source.Lease != request.Binding || source.IsActive ||
                source.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.Source,source) || !ReferenceEquals(source.OriginalAdmission,fact.Admission) ||
                fact.Admission.Request.InvocationId != request.BindInvocationId || !OriginalIrqSources(source,fact.Admission,request.RuntimeIncarnation,false)) return null;
            return fact.Observation;
        }
    }
    private bool OriginalIrqSources(NeutralInterruptState source,NeutralOriginalIrqAdmissionFact admission,Guid incarnation,bool active = true) =>
        ReferenceEquals(source.OwnerSelf,source) && ReferenceEquals(source.OriginalAdmission,admission) && ReferenceEquals(admission.OwnerSelf,admission) && ReferenceEquals(admission.Source,source) &&
        admission.Request.RuntimeIncarnation == incarnation && admission.Observation is { SourceFenceMatched: true,NoEffectGuaranteed: false } raw && raw.Request == admission.Request &&
        raw.Observation is { IsBound: true,Decision: NeutralInterruptBindDecision.Bound } && raw.Observation.Lease == source.Lease &&
        _dependencies.Devices.TryGetValue(source.ParentDevice,out var parent) && ReferenceEquals(parent,admission.Parent) && (!active || parent.IsActive) &&
        ReferenceEquals(parent.OriginalAdmission,admission.DeviceAdmission) && admission.DeviceAdmission.Request.InvocationId == admission.Request.DeviceBindInvocationId &&
        (ulong)parent.OriginalIrqAdmissions.Count == parent.OriginalIrqIssued && parent.OriginalIrqAdmissions.All(old => ReferenceEquals(old.OwnerSelf,old) && ReferenceEquals(old.Parent,parent)) &&
        parent.OriginalIrqAdmissions.Any(old => ReferenceEquals(old,admission)) && OriginalDeviceSources(parent,admission.DeviceAdmission,incarnation,active);
}
