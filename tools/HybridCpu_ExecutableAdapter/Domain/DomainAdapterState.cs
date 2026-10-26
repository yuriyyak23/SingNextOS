using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Domain;

/// <summary>
/// Provisional domain state shape. The external lease remains generic until the
/// HybridCPU-v2 facade publishes its versioned lease contract.
/// </summary>
internal sealed class DomainAdapterState<TExternalDomainLease>
    where TExternalDomainLease : struct
{
    public required NeutralDomainBindingLease NeutralLease { get; init; }
    public TExternalDomainLease? ExternalLease { get; set; }
    public AdapterResourceLifecycle Lifecycle { get; set; } = AdapterResourceLifecycle.Opening;
    public NeutralExecutionState ExecutionState { get; set; } = NeutralExecutionState.Ready;
    public AdapterOperationIdentity? Reservation { get; set; }
}
