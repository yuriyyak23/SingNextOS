namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralDomainRuntime
{
    private ulong _nextHandle = 1;
    public int ActiveBindingCount => _dependencies.ActiveDomainCount;

    public NeutralDomainBindResult Bind(NeutralDomainProfile profile)
    {
        if (profile != NeutralDomainProfile.OrdinaryService) return new(false, NeutralDomainBindDecision.UnsupportedProfile, default, "Unsupported neutral domain profile.");
        if (!TryAllocate(ref _nextHandle, out var handle)) return new(false, NeutralDomainBindDecision.Faulted, default, "Neutral domain handle space is exhausted.");
        var lease = new NeutralDomainBindingLease(new(handle), new(1));
        _dependencies.RegisterDomain(lease);
        return NeutralDomainBindResult.Bound(lease);
    }

    public NeutralDomainCloseResult Close(NeutralDomainBindingLease lease)
    {
        var validation = _leases.Validate(lease);
        if (!validation.IsValid) return new(ToDomainClose(validation.Status), "Neutral domain lease is not live.");
        if (_dependencies.HasActiveDependents(lease)) return new(NeutralDomainCloseDecision.ActiveDependents, "Neutral domain has active dependent resources.");
        validation.State!.Lifecycle = NeutralResourceLifecycle.Revoked;
        return new(NeutralDomainCloseDecision.Closed, string.Empty);
    }

    public NeutralExecutionTransitionResult TransitionExecution(NeutralDomainBindingLease lease, NeutralExecutionTransition transition)
    {
        var validation = _leases.Validate(lease);
        if (!validation.IsValid) return Failed(lease, transition, ToTransition(validation.Status), "Neutral domain lease is not live.");
        var domain = validation.State!;
        var next = (domain.Execution, transition) switch
        {
            (NeutralExecutionState.Ready, NeutralExecutionTransition.Start) => NeutralExecutionState.Running,
            (NeutralExecutionState.Running, NeutralExecutionTransition.Park) => NeutralExecutionState.Parked,
            (NeutralExecutionState.Parked, NeutralExecutionTransition.Resume) => NeutralExecutionState.Running,
            _ => (NeutralExecutionState?)null,
        };
        if (next is null) return Failed(lease, transition, NeutralExecutionTransitionDecision.InvalidTransition, "Invalid neutral execution transition.");
        domain.Execution = next.Value;
        return NeutralExecutionTransitionResult.Success(lease, transition, domain.Execution);
    }

    private static NeutralExecutionTransitionResult Failed(NeutralDomainBindingLease lease, NeutralExecutionTransition transition, NeutralExecutionTransitionDecision decision, string reason) => new(false, decision, lease, transition, default, reason);
    private static NeutralDomainCloseDecision ToDomainClose(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralDomainCloseDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralDomainCloseDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralDomainCloseDecision.Revoked, _ => NeutralDomainCloseDecision.Faulted };
    private static NeutralExecutionTransitionDecision ToTransition(NeutralLeaseValidationStatus status) => status switch { NeutralLeaseValidationStatus.NotFound => NeutralExecutionTransitionDecision.NotFound, NeutralLeaseValidationStatus.Stale => NeutralExecutionTransitionDecision.Stale, NeutralLeaseValidationStatus.Revoked => NeutralExecutionTransitionDecision.Revoked, _ => NeutralExecutionTransitionDecision.Faulted };
}
