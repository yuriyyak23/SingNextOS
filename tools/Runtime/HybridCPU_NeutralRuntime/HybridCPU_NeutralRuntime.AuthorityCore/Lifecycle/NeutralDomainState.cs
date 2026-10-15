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
    public List<NeutralOriginalMapInvocation> OriginalMaps { get; } = [];
    public ulong OriginalMapsIssued { get; set; }
}

internal sealed class NeutralOriginalMapInvocation
{
    public NeutralOriginalMapInvocation(NeutralOriginalOwnedRegionMapRequest request,NeutralDomainState parent)
    {Original=this;Request=request;Parent=parent;ParentAdmission=parent.OriginalAdmission;}
    public NeutralOriginalMapInvocation Original { get; }
    public NeutralOriginalOwnedRegionMapRequest Request { get; }
    public NeutralDomainState Parent { get; }
    public NeutralOriginalDomainAdmissionFact? ParentAdmission { get; }
    public NeutralOriginalOwnedRegionMapObservation? Raw { get; set; }
    public NeutralOriginalMappingAdmissionFact? Admission { get; set; }
    public bool NoEffect { get; set; }
}

internal sealed class NeutralOriginalDomainCloseFact(NeutralOriginalDomainCloseRequest request, NeutralDomainState source)
{
    public NeutralOriginalDomainCloseRequest Request { get; } = request;
    public NeutralDomainState Source { get; } = source;
    public NeutralOriginalDomainCloseObservation? Observation { get; set; }
}

internal sealed class NeutralOriginalDomainAdmissionFact
{
    public NeutralOriginalDomainAdmissionFact(NeutralOriginalDomainBindRequest request, NeutralDomainState source)
    { Request=request; Source=source; Original=this; }
    public NeutralOriginalDomainAdmissionFact Original { get; }
    public NeutralOriginalDomainBindRequest Request { get; }
    public NeutralDomainState Source { get; }
}
