namespace YAKSys_Hybrid_CPU.Core;

public enum NeutralDomainProfile { OrdinaryService }
public enum NeutralExecutionTransition { Start, Park, Resume }
public enum NeutralExecutionState { Ready, Running, Parked }
public enum NeutralExecutionTransitionDecision { Transitioned, InvalidTransition, Revoked, Stale, NotFound, Faulted }
public enum NeutralDomainBindDecision { Bound, UnsupportedProfile, Faulted }
public enum NeutralDomainCloseDecision { Closed, Revoked, ActiveDependents, Stale, NotFound, Faulted }

public sealed record NeutralDomainCloseResult(NeutralDomainCloseDecision Decision, string Reason)
{
    public bool IsClosed => Decision is NeutralDomainCloseDecision.Closed or NeutralDomainCloseDecision.Revoked;
}

public readonly record struct NeutralDomainBindingHandle(ulong Value);
public readonly record struct NeutralDomainBindingEpoch(ulong Value);
public readonly record struct NeutralDomainBindingLease(NeutralDomainBindingHandle Handle, NeutralDomainBindingEpoch Epoch);

public readonly record struct NeutralDomainBindResult(bool IsBound, NeutralDomainBindDecision Decision, NeutralDomainBindingLease Lease, string Reason)
{
    public static NeutralDomainBindResult Bound(NeutralDomainBindingLease lease) => new(true, NeutralDomainBindDecision.Bound, lease, string.Empty);
}

public readonly record struct NeutralExecutionTransitionResult(bool IsTransitioned, NeutralExecutionTransitionDecision Decision, NeutralDomainBindingLease Lease, NeutralExecutionTransition Transition, NeutralExecutionState State, string Reason)
{
    public static NeutralExecutionTransitionResult Success(NeutralDomainBindingLease lease, NeutralExecutionTransition transition, NeutralExecutionState state) => new(true, NeutralExecutionTransitionDecision.Transitioned, lease, transition, state, string.Empty);
}
