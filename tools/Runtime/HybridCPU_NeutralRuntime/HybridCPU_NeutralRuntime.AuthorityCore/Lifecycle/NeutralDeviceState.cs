namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDeviceState
{
    public NeutralDeviceState() { OwnerSelf = this; }
    public NeutralDeviceState OwnerSelf { get; }
    public NeutralDomainState? OriginalParent { get; init; }
    public NeutralOriginalDeviceAdmissionFact? OriginalAdmission { get; set; }
    public NeutralOriginalDeviceCloseFact? OriginalClose { get; set; }
    public List<NeutralOriginalIrqAdmissionFact> OriginalIrqAdmissions { get; } = [];
    public ulong OriginalIrqIssued { get; set; }
    public List<NeutralOriginalMmioAdmissionFact> OriginalMmioAdmissions { get; } = [];
    public ulong OriginalMmioIssued { get; set; }
    public List<NeutralOriginalDmaAdmissionFact> OriginalDmaAdmissions { get; } = [];
    public ulong OriginalDmaIssued { get; set; }
    public bool HasIntactOriginalDmaHistory => ReferenceEquals(OwnerSelf,this) && (ulong)OriginalDmaAdmissions.Count == OriginalDmaIssued &&
        OriginalDmaAdmissions.All(fact => fact is not null && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Device,this) &&
            fact.Request.Device == Lease && (fact.Observation is null || fact.Observation.Value.Request == fact.Request)) &&
        OriginalDmaAdmissions.Distinct(ReferenceEqualityComparer.Instance).Count() == OriginalDmaAdmissions.Count &&
        OriginalDmaAdmissions.Select(fact => fact.Request.InvocationId).Distinct().Count() == OriginalDmaAdmissions.Count;
    public List<NeutralOriginalDeviceLoanFact> OriginalDeviceLoans { get; } = [];
    public ulong OriginalDeviceLoansIssued { get; set; }
    public required NeutralDeviceLease Lease { get; init; }
    public NeutralDeviceLeaseHandle Handle => Lease.Handle;
    public NeutralDeviceLeaseEpoch Epoch => Lease.Epoch;
    public NeutralDomainBindingHandle ParentDomain => Lease.DomainLease.Handle;
    public NeutralDeviceIdentity Identity => Lease.Device;
    public NeutralDeviceRights Rights => Lease.Rights;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool HasExactOriginalClosure => ReferenceEquals(OwnerSelf,this) && OriginalAdmission is { } admission && ReferenceEquals(admission.OwnerSelf,admission) && ReferenceEquals(admission.Source,this) &&
        OriginalClose is { } close && ReferenceEquals(close.OwnerSelf,close) && ReferenceEquals(close.Source,this) && ReferenceEquals(close.Admission,admission) &&
        close.Request.Device == Lease && close.Request.DeviceBindInvocationId == admission.Request.InvocationId &&
        close.Observation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw && raw.Request == close.Request && raw.Observation.Decision == NeutralDeviceCloseDecision.Closed;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active && !HasExactOriginalClosure;
}

internal sealed class NeutralOriginalDeviceLoanFact(NeutralOriginalDeviceLoanRequest request,
    NeutralDeviceState source, NeutralOriginalDeviceAdmissionFact admission)
{
    public NeutralOriginalDeviceLoanFact? OwnerSelf { get; private set; }
    public void EstablishSelf() => OwnerSelf = this;
    public NeutralOriginalDeviceLoanRequest Request { get; } = request;
    public NeutralDeviceState Source { get; } = source;
    public NeutralOriginalDeviceAdmissionFact Admission { get; } = admission;
    public NeutralOriginalDeviceLoan Loan { get; } = new(request);
    public NeutralOriginalDeviceLoanClosure? Closure { get; set; }
    public bool QueryInFlight { get; set; }
    public bool Consumed { get; set; }
    public bool IsConsumed => ReferenceEquals(OwnerSelf, this) && Consumed &&
        Closure is { NoFurtherUse: true } closure && ReferenceEquals(closure.Loan, Loan) && ReferenceEquals(Loan.Request, Request);
}

internal sealed class NeutralOriginalDeviceAdmissionFact
{
    public NeutralOriginalDeviceAdmissionFact(NeutralOriginalDeviceBindRequest request, NeutralDomainState parent, NeutralOriginalDomainAdmissionFact root)
    { OwnerSelf = this; Request = request; Parent = parent; RootAdmission = root; }
    public NeutralOriginalDeviceAdmissionFact OwnerSelf { get; }
    public NeutralOriginalDeviceBindRequest Request { get; }
    public NeutralDomainState Parent { get; }
    public NeutralOriginalDomainAdmissionFact RootAdmission { get; }
    public NeutralDeviceState? Source { get; set; }
    public NeutralOriginalDeviceBindObservation? Observation { get; set; }
    public bool NoEffect { get; set; }
}
internal sealed class NeutralOriginalDeviceCloseFact
{
    public NeutralOriginalDeviceCloseFact(NeutralOriginalDeviceCloseRequest request, NeutralDeviceState source, NeutralOriginalDeviceAdmissionFact admission)
    { OwnerSelf = this; Request = request; Source = source; Admission = admission; }
    public NeutralOriginalDeviceCloseFact OwnerSelf { get; }
    public NeutralOriginalDeviceCloseRequest Request { get; }
    public NeutralDeviceState Source { get; }
    public NeutralOriginalDeviceAdmissionFact Admission { get; }
    public NeutralOriginalDeviceCloseObservation? Observation { get; set; }
}
