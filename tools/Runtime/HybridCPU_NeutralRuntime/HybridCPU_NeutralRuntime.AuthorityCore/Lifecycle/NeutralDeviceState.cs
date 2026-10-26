namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDeviceState
{
    public NeutralDeviceState() { OwnerSelf = this; }
    public NeutralDeviceState OwnerSelf { get; }
    public NeutralDomainState? OriginalParent { get; init; }
    public NeutralOriginalDeviceAdmissionFact? OriginalAdmission { get; set; }
    public NeutralOriginalDeviceCloseFact? OriginalClose { get; set; }
    public NeutralOriginalDeviceLifetimeClosure? LifetimeClosure { get; set; }
    public NeutralOriginalDeviceLifetimeClosure? OriginalLifetimeClosure { get; set; }
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
    public bool HasIntactOriginalLoanHistory => ReferenceEquals(OwnerSelf,this) &&
        (ulong)OriginalDeviceLoans.Count == OriginalDeviceLoansIssued &&
        OriginalDeviceLoans.All(fact => fact is not null && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,this) &&
            ReferenceEquals(fact.Admission,OriginalAdmission) && (fact.WasAdmitted
                ? fact.Loan is not null && ReferenceEquals(fact.Loan,fact.OriginalLoan) && ReferenceEquals(fact.Loan.Request,fact.Request)
                : fact.HasExactNoEffect) &&
            fact.Request.Device == Lease && fact.Request.DeviceAdmission == fact.Admission.Request) &&
        OriginalDeviceLoans.Distinct(ReferenceEqualityComparer.Instance).Count() == OriginalDeviceLoans.Count &&
        OriginalDeviceLoans.Where(fact => fact.WasAdmitted).Select(fact => fact.Loan).Distinct(ReferenceEqualityComparer.Instance).Count() == OriginalDeviceLoans.Count(fact => fact.WasAdmitted) &&
        OriginalDeviceLoans.Select(fact => fact.Request.InvocationId).Distinct().Count() == OriginalDeviceLoans.Count;
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

internal sealed class NeutralOriginalDeviceLoanFact
{
    public NeutralOriginalDeviceLoanFact(NeutralOriginalDeviceLoanRequest request, NeutralDeviceState source, NeutralOriginalDeviceAdmissionFact admission, bool admitted = true)
    {
        Request = request; Source = source; Admission = admission; WasAdmitted = admitted;
        Loan = admitted ? new(request) : null; OriginalLoan = Loan;
        NoEffect = admitted ? null : new(request, true, true); OriginalNoEffect = NoEffect;
    }
    public NeutralOriginalDeviceLoanFact? OwnerSelf { get; private set; }
    public void EstablishSelf() => OwnerSelf = this;
    public NeutralOriginalDeviceLoanRequest Request { get; }
    public NeutralDeviceState Source { get; }
    public NeutralOriginalDeviceAdmissionFact Admission { get; }
    public bool WasAdmitted { get; }
    public NeutralOriginalDeviceLoan? Loan { get; }
    public NeutralOriginalDeviceLoan? OriginalLoan { get; }
    public NeutralOriginalDeviceLoanNoEffect? NoEffect { get; set; }
    public NeutralOriginalDeviceLoanNoEffect? OriginalNoEffect { get; }
    public bool HasExactNoEffect => !WasAdmitted && Loan is null && OriginalLoan is null &&
        NoEffect is { NoEffect: true, NoFurtherAdmission: true } raw && ReferenceEquals(raw, OriginalNoEffect) && ReferenceEquals(raw.Request, Request);
    public NeutralOriginalDeviceLoanClosure? Closure { get; set; }
    public NeutralOriginalDeviceLoanClosure? OriginalClosure { get; set; }
    public bool QueryInFlight { get; set; }
    public bool Consumed { get; set; }
    public bool IsConsumed => ReferenceEquals(OwnerSelf, this) && (HasExactNoEffect || WasAdmitted && Loan is not null && ReferenceEquals(Loan, OriginalLoan) && Consumed &&
        Closure is { NoFurtherUse: true } closure && ReferenceEquals(closure, OriginalClosure) &&
        ReferenceEquals(closure.Loan, Loan) && ReferenceEquals(Loan.Request, Request));
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
