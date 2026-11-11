namespace YAKSys_Hybrid_CPU.Core;
internal enum NeutralDeliveryState { Idle, Pending }


internal sealed class NeutralInterruptState
{
    public NeutralInterruptState() { OwnerSelf = this; }
    public NeutralInterruptState OwnerSelf { get; }
    public NeutralOriginalIrqAdmissionFact? OriginalAdmission { get; set; }
    public NeutralOriginalIrqCloseFact? OriginalClose { get; set; }
    public NeutralOriginalIrqContainFact? OriginalContainment { get; set; }
    public bool HasExactOriginalClosure => ReferenceEquals(OwnerSelf,this) && OriginalAdmission is { } admission && ReferenceEquals(admission.OwnerSelf,admission) && ReferenceEquals(admission.Source,this) &&
        (OriginalContainment is { } contained && ReferenceEquals(contained.OwnerSelf,contained) && ReferenceEquals(contained.Source,this) && ReferenceEquals(contained.Admission,admission) &&
         contained.Request.Binding == Lease && contained.Request.Admission == admission.Request && contained.Observation is { NoFurtherDeliveryGuaranteed: true } proof && proof.Request == contained.Request ||
        OriginalClose is { } close && ReferenceEquals(close.OwnerSelf,close) && ReferenceEquals(close.Source,this) && ReferenceEquals(close.Admission,admission) &&
        close.Request.Binding == Lease && close.Request.BindInvocationId == admission.Request.InvocationId && close.Observation is { SourceFenceMatched: true,NoEffectGuaranteed: false } raw && raw.Request == close.Request && raw.Observation.Decision == NeutralInterruptCloseDecision.Closed);
    private NeutralDeliveryState _delivery;
    private NeutralInterruptDeliverySequence _sequence;
    public ulong SourceRevision { get; private set; }
    public bool SourceRevisionExhausted { get; private set; }
    private void Changed() { if (SourceRevision == ulong.MaxValue) SourceRevisionExhausted = true; else SourceRevision++; }
    public NeutralDeliveryState Delivery { get => _delivery; set { Changed(); _delivery = value; } }
    public NeutralInterruptDeliverySequence Sequence { get => _sequence; set { Changed(); _sequence = value; } }
    public List<NeutralOriginalIrqPollFact> Polls { get; } = [];
    public ulong PollsIssued { get; set; }
    public List<NeutralOriginalIrqCompleteFact> Completions { get; } = [];
    public ulong CompletionsIssued { get; set; }
    public List<NeutralOriginalIrqSignalFact> Signals {get;}=[];
    public ulong SignalsIssued {get;set;}
    public required NeutralInterruptLease Lease { get; init; }
    public NeutralInterruptLeaseHandle Handle => Lease.Handle;
    public NeutralInterruptLeaseEpoch Epoch => Lease.Epoch;
    public NeutralDeviceLeaseHandle ParentDevice => Lease.DeviceLease.Handle;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active && !HasExactOriginalClosure;
}

internal sealed class NeutralOriginalIrqSignalFact(NeutralInterruptState source,NeutralOriginalIrqAdmissionFact admission,object runtimeOwner)
{
    public NeutralOriginalIrqSignalFact? Self;
    public NeutralOriginalIrqSignalRequest Request=null!;
    public readonly object RuntimeOwner=runtimeOwner;
    public readonly NeutralInterruptState Source=source;
    public readonly NeutralOriginalIrqAdmissionFact Admission=admission;
    public bool Invoked;
    public NeutralOriginalIrqSignalObservation? Raw;
}

internal sealed class NeutralOriginalIrqContainFact
{
    public NeutralOriginalIrqContainFact(NeutralOriginalIrqContainRequest request,NeutralInterruptState source,NeutralOriginalIrqAdmissionFact admission)
    { OwnerSelf = this; Request = request; Source = source; Admission = admission; }
    public NeutralOriginalIrqContainFact OwnerSelf { get; }
    public NeutralOriginalIrqContainRequest Request { get; }
    public NeutralInterruptState Source { get; }
    public NeutralOriginalIrqAdmissionFact Admission { get; }
    public NeutralOriginalIrqContainObservation? Observation { get; set; }
}

internal sealed class NeutralOriginalIrqAdmissionFact
{
    public NeutralOriginalIrqAdmissionFact(NeutralOriginalIrqBindRequest request,NeutralDeviceState parent,NeutralOriginalDeviceAdmissionFact factory)
    { OwnerSelf = this; Request = request; Parent = parent; DeviceAdmission = factory; }
    public NeutralOriginalIrqAdmissionFact OwnerSelf { get; }
    public NeutralOriginalIrqBindRequest Request { get; }
    public NeutralDeviceState Parent { get; }
    public NeutralOriginalDeviceAdmissionFact DeviceAdmission { get; }
    public NeutralInterruptState? Source { get; set; }
    public NeutralOriginalIrqBindObservation? Observation { get; set; }
    public bool NoEffect { get; set; }
}
internal sealed class NeutralOriginalIrqCloseFact
{
    public NeutralOriginalIrqCloseFact(NeutralOriginalIrqCloseRequest request,NeutralInterruptState source,NeutralOriginalIrqAdmissionFact admission)
    { OwnerSelf = this; Request = request; Source = source; Admission = admission; }
    public NeutralOriginalIrqCloseFact OwnerSelf { get; }
    public NeutralOriginalIrqCloseRequest Request { get; }
    public NeutralInterruptState Source { get; }
    public NeutralOriginalIrqAdmissionFact Admission { get; }
    public NeutralOriginalIrqCloseObservation? Observation { get; set; }
}

internal sealed class NeutralOriginalIrqPollFact
{
    public NeutralOriginalIrqPollFact(NeutralOriginalIrqPollRequest request,NeutralInterruptState source,NeutralOriginalIrqAdmissionFact admission) { OwnerSelf = this; Request = request; Source = source; Admission = admission; }
    public NeutralOriginalIrqPollFact OwnerSelf { get; }
    public NeutralOriginalIrqPollRequest Request { get; }
    public NeutralInterruptState Source { get; }
    public NeutralOriginalIrqAdmissionFact Admission { get; }
    public NeutralOriginalIrqPollObservation? Observation { get; set; }
}
internal sealed class NeutralOriginalIrqCompleteFact
{
    public NeutralOriginalIrqCompleteFact(NeutralOriginalIrqCompleteRequest request,NeutralInterruptState source,NeutralOriginalIrqAdmissionFact admission,NeutralOriginalIrqPollFact poll) { OwnerSelf = this; Request = request; Source = source; Admission = admission; Poll = poll; }
    public NeutralOriginalIrqCompleteFact OwnerSelf { get; }
    public NeutralOriginalIrqCompleteRequest Request { get; }
    public NeutralInterruptState Source { get; }
    public NeutralOriginalIrqAdmissionFact Admission { get; }
    public NeutralOriginalIrqPollFact Poll { get; }
    public NeutralOriginalIrqCompleteObservation? Observation { get; set; }
}
