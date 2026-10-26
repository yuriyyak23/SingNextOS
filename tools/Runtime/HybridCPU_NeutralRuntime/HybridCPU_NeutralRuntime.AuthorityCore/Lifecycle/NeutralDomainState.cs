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
    public bool HasIntactOriginalDeviceHistory => (ulong)OriginalDeviceAdmissions.Count == OriginalDevicesIssued &&
        OriginalDeviceAdmissions.Distinct(ReferenceEqualityComparer.Instance).Count() == OriginalDeviceAdmissions.Count &&
        OriginalDeviceAdmissions.All(fact => fact is not null && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Parent,this) &&
            ReferenceEquals(fact.RootAdmission,OriginalAdmission) && ReferenceEquals(fact.RootAdmission.Original,fact.RootAdmission) &&
            ReferenceEquals(fact.RootAdmission.Source,this) && fact.Request.Domain == Lease &&
            fact.Request.DomainBindInvocationId == fact.RootAdmission.Request.InvocationId && fact.Request.RuntimeIncarnation == fact.RootAdmission.Request.RuntimeIncarnation &&
            (fact.Source is null || ReferenceEquals(fact.Source.OriginalParent,this) && ReferenceEquals(fact.Source.OriginalAdmission,fact) && fact.Source.Lease.DomainLease == Lease)) &&
        OriginalDeviceAdmissions.Select(fact => fact.Request.InvocationId).Distinct().Count() == OriginalDeviceAdmissions.Count;
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
    public NeutralOriginalDomainAdmissionFact? Admission { get; } = source.OriginalAdmission;
    public NeutralOriginalDomainCloseRequest Request { get; } = request;
    public NeutralDomainState Source { get; } = source;
    public NeutralOriginalDomainCloseObservation? Observation { get; set; }
    public NeutralDomainCloseResult? OriginalResult { get; set; }
    public void EstablishOriginal() => _original = this;
    private NeutralOriginalDomainCloseFact? _original;
    public bool IsOriginal => ReferenceEquals(_original,this);
}

internal sealed class NeutralOriginalDomainAdmissionFact
{
    public NeutralOriginalDomainAdmissionFact(NeutralOriginalDomainBindRequest request, NeutralDomainState source)
    { Request=request; Source=source; Original=this; }
    public NeutralOriginalDomainAdmissionFact Original { get; }
    public NeutralOriginalDomainBindRequest Request { get; }
    public NeutralDomainState Source { get; }
}
