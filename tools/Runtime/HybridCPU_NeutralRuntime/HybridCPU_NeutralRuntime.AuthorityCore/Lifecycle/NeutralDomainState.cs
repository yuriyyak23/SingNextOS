namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDomainState
{
    public required NeutralDomainBindingLease Lease { get; init; }
    public NeutralDomainBindingHandle Handle => Lease.Handle;
    public NeutralDomainBindingEpoch Epoch => Lease.Epoch;
    public NeutralExecutionState Execution { get; set; } = NeutralExecutionState.Ready;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
    public NeutralOriginalDomainAdmissionFact? OriginalAdmission { get; set; }
    public NeutralOriginalDomainCloseFact? OriginalClose { get; set; }
    public List<NeutralOriginalDeviceAdmissionFact> OriginalDeviceAdmissions { get; } = [];
    public ulong OriginalDevicesIssued { get; set; }
}

internal sealed class NeutralOriginalDomainCloseFact(NeutralOriginalDomainCloseRequest request, NeutralDomainState source)
{
    public NeutralOriginalDomainCloseRequest Request { get; } = request;
    public NeutralDomainState Source { get; } = source;
    public NeutralOriginalDomainCloseObservation? Observation { get; set; }
}

internal sealed class NeutralOriginalDomainAdmissionFact(
    NeutralOriginalDomainBindRequest request, NeutralDomainState source)
{
    public NeutralOriginalDomainBindRequest Request { get; } = request;
    public NeutralDomainState Source { get; } = source;
}
