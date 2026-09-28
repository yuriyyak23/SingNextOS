namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDomainState
{
    public required NeutralDomainBindingLease Lease { get; init; }
    public NeutralDomainBindingHandle Handle => Lease.Handle;
    public NeutralDomainBindingEpoch Epoch => Lease.Epoch;
    public NeutralExecutionState Execution { get; set; } = NeutralExecutionState.Ready;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
}
