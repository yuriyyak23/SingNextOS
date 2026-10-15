namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade
{
    public int ActiveInterruptLeaseCount { get { lock (_lifecycleGate) return _dependencies.ActiveInterruptCount; } }

    public NeutralInterruptBindResult BindInterrupt(NeutralDeviceLease device, NeutralInterruptSourceIdentity source)
        => BindInterruptCore(device,source,null,out _);

    private NeutralInterruptBindResult BindInterruptCore(NeutralDeviceLease device,NeutralInterruptSourceIdentity source,NeutralOriginalIrqAdmissionFact? intent,out bool noEffect)
    {
        noEffect=false;
        lock (_lifecycleGate)
        {
            var parent = _leases.Validate(device);
            if (!parent.IsValid) {noEffect=true;return new() { Decision = ToInterruptBind(parent.Status) };}
            if (string.IsNullOrWhiteSpace(source.ResourceId) || !Enum.IsDefined(source.Trigger)) {noEffect=true;return new() { Decision = NeutralInterruptBindDecision.InvalidSource };}
            if (!NeutralRuntimeValidation.Has(parent.State!.Rights, NeutralDeviceRights.Configure)) {noEffect=true;return new() { Decision = NeutralInterruptBindDecision.InsufficientDeviceRights };}
            if (_dependencies.HasActiveInterruptBinding(device, source,intent)) {noEffect=true;return new() { Decision = NeutralInterruptBindDecision.AlreadyBound };}
            if (!TryAllocate(ref _nextResource, out var handle)) {noEffect=true;return new() { Decision = NeutralInterruptBindDecision.Faulted, Reason = "Neutral resource handle space is exhausted." };}
            var lease = new NeutralInterruptLease(device, source, new(handle), new(1));
            _dependencies.RegisterInterrupt(lease);
            return new() { IsBound = true, Lease = lease, Decision = NeutralInterruptBindDecision.Bound };
        }
    }

    /// <summary>Signals an idle interrupt. A repeated signal is rejected as AlreadyPending so pending delivery is never overwritten.</summary>
    public NeutralInterruptSignalResult SignalInterrupt(NeutralInterruptLease lease)
    {
        lock (_lifecycleGate)
        {
            var validation = ValidateInterruptLifetime(lease);
            if (!validation.IsValid) return new() { Decision = ToInterruptSignal(validation.Status) };
            var state = validation.State!;
            if (state.OriginalAdmission is { } admission && (state.SourceRevisionExhausted || state.SourceRevision > ulong.MaxValue - 2 ||
                !OriginalIrqSources(state,admission,admission.Request.RuntimeIncarnation)))
                return new() { Decision = NeutralInterruptSignalDecision.Faulted, Reason = "Original IRQ signal source or revision exhausted/lost." };
            if (state.Delivery == NeutralDeliveryState.Pending) return new() { Decision = NeutralInterruptSignalDecision.AlreadyPending, Sequence = state.Sequence };
            if (state.Sequence.Value == ulong.MaxValue) return new() { Decision = NeutralInterruptSignalDecision.Faulted, Sequence = state.Sequence, Reason = "Interrupt delivery sequence space is exhausted." };
            state.Sequence = new(state.Sequence.Value + 1);
            state.Delivery = NeutralDeliveryState.Pending;
            return new() { IsSignaled = true, Decision = NeutralInterruptSignalDecision.Signaled, Sequence = state.Sequence };
        }
    }

    public NeutralInterruptPollResult PollInterrupt(NeutralInterruptLease lease)
    {
        lock (_lifecycleGate)
        {
            var validation = ValidateInterruptLifetime(lease);
            if (!validation.IsValid) return new() { Lease = lease, Decision = ToInterruptPoll(validation.Status) };
            var state = validation.State!;
            var pending = state.Delivery == NeutralDeliveryState.Pending;
            return new() { IsObserved = pending, Lease = state.Lease, DeliveryAvailable = pending, Sequence = state.Sequence, Decision = pending ? NeutralInterruptPollDecision.Observed : NeutralInterruptPollDecision.NoDelivery };
        }
    }

    public NeutralInterruptCompleteResult CompleteInterruptDelivery(NeutralInterruptLease lease, NeutralInterruptDeliverySequence sequence)
    {
        lock (_lifecycleGate)
        {
            var validation = ValidateInterruptLifetime(lease);
            if (!validation.IsValid) return new() { Decision = ToInterruptComplete(validation.Status) };
            var state = validation.State!;
            if (state.Delivery != NeutralDeliveryState.Pending) return new() { Decision = NeutralInterruptCompleteDecision.NoPendingDelivery };
            if (state.Sequence != sequence) return new() { Decision = NeutralInterruptCompleteDecision.WrongSequence };
            state.Delivery = NeutralDeliveryState.Idle;
            return new() { Decision = NeutralInterruptCompleteDecision.Completed };
        }
    }

    public NeutralInterruptCloseResult CloseInterrupt(NeutralInterruptLease lease)
    {
        lock (_lifecycleGate)
        {
            var validation = _leases.Validate(lease);
            if (!validation.IsValid) return new() { Decision = ToInterruptClose(validation.Status) };
            if (validation.State!.Delivery == NeutralDeliveryState.Pending) return new() { Decision = NeutralInterruptCloseDecision.PendingDelivery, Reason = "Pending interrupt delivery must complete before close." };
            validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
            validation.State.Delivery = NeutralDeliveryState.Idle;
            return new() { Decision = NeutralInterruptCloseDecision.Closed };
        }
    }

    private NeutralLeaseValidation<NeutralInterruptState> ValidateInterruptLifetime(NeutralInterruptLease lease)
    {
        var validation = _leases.Validate(lease);
        if (!validation.IsValid) return validation;
        return _leases.Validate(validation.State!.Lease.DeviceLease).IsValid ? validation : new(NeutralLeaseValidationStatus.Faulted, null);
    }

    private static NeutralInterruptBindDecision ToInterruptBind(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralInterruptBindDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralInterruptBindDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralInterruptBindDecision.Revoked, _ => NeutralInterruptBindDecision.Faulted };
    private static NeutralInterruptSignalDecision ToInterruptSignal(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralInterruptSignalDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralInterruptSignalDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralInterruptSignalDecision.Revoked, _ => NeutralInterruptSignalDecision.Faulted };
    private static NeutralInterruptPollDecision ToInterruptPoll(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralInterruptPollDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralInterruptPollDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralInterruptPollDecision.Revoked, _ => NeutralInterruptPollDecision.Faulted };
    private static NeutralInterruptCompleteDecision ToInterruptComplete(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralInterruptCompleteDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralInterruptCompleteDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralInterruptCompleteDecision.Revoked, _ => NeutralInterruptCompleteDecision.Faulted };
    private static NeutralInterruptCloseDecision ToInterruptClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralInterruptCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralInterruptCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralInterruptCloseDecision.Revoked, _ => NeutralInterruptCloseDecision.Faulted };
}
