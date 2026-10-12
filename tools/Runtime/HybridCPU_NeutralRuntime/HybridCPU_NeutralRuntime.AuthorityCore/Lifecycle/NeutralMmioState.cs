namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralMmioState
{
    public NeutralMmioState() { OwnerSelf = this; }
    public NeutralMmioState OwnerSelf { get; }
    public NeutralOriginalMmioAdmissionFact? OriginalAdmission { get; set; }
    public NeutralOriginalMmioCloseFact? OriginalClose { get; set; }
    public bool HasExactOriginalClosure => ReferenceEquals(OwnerSelf,this) && OriginalAdmission is { } admission && ReferenceEquals(admission.OwnerSelf,admission) && ReferenceEquals(admission.Source,this) &&
        OriginalClose is { } close && ReferenceEquals(close.OwnerSelf,close) && ReferenceEquals(close.Source,this) && ReferenceEquals(close.Admission,admission) &&
        close.Request.Mapping == Lease && close.Request.MapInvocationId == admission.Request.InvocationId && close.Observation is { SourceFenceMatched: true,NoEffectGuaranteed: false } raw && raw.Request == close.Request && raw.Observation.Decision == NeutralMmioCloseDecision.Closed;
    public required NeutralMmioLease Lease { get; init; }
    public NeutralMmioLeaseHandle Handle => Lease.Handle;
    public NeutralMmioLeaseEpoch Epoch => Lease.Epoch;
    public NeutralDeviceLeaseHandle ParentDevice => Lease.DeviceLease.Handle;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active && !HasExactOriginalClosure;
}

internal sealed class NeutralOriginalMmioAdmissionFact
{
    public NeutralOriginalMmioAdmissionFact(NeutralOriginalMmioMapRequest request,NeutralDeviceState parent,NeutralOriginalDeviceAdmissionFact factory)
    { OwnerSelf = this; Request = request; Parent = parent; DeviceAdmission = factory; }
    public NeutralOriginalMmioAdmissionFact OwnerSelf { get; }
    public NeutralOriginalMmioMapRequest Request { get; }
    public NeutralDeviceState Parent { get; }
    public NeutralOriginalDeviceAdmissionFact DeviceAdmission { get; }
    public NeutralMmioState? Source { get; set; }
    public NeutralOriginalMmioMapObservation? Observation { get; set; }
}
internal sealed class NeutralOriginalMmioCloseFact
{
    public NeutralOriginalMmioCloseFact(NeutralOriginalMmioCloseRequest request,NeutralMmioState source,NeutralOriginalMmioAdmissionFact admission)
    { OwnerSelf = this; Request = request; Source = source; Admission = admission; }
    public NeutralOriginalMmioCloseFact OwnerSelf { get; }
    public NeutralOriginalMmioCloseRequest Request { get; }
    public NeutralMmioState Source { get; }
    public NeutralOriginalMmioAdmissionFact Admission { get; }
    public NeutralOriginalMmioCloseObservation? Observation { get; set; }
}
