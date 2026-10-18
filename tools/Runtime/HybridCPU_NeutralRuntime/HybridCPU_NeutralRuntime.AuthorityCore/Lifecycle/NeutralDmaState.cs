namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralDmaGrantState
{
    public NeutralDmaGrantState() { OwnerSelf = this; }
    public NeutralDmaGrantState OwnerSelf { get; }
    public NeutralOriginalDmaAdmissionFact? OriginalAdmission { get; set; }
    public NeutralOriginalDmaCloseFact? OriginalClose { get; set; }
    public required NeutralDmaGrant Lease { get; init; }
    public NeutralDmaGrantHandle Handle => Lease.Handle;
    public NeutralDmaGrantEpoch Epoch => Lease.Epoch;
    public NeutralDeviceLeaseHandle ParentDevice => Lease.DeviceLease.Handle;
    public NeutralOwnedRegionMappingHandle ParentMapping => Lease.MappingLease.Handle;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
    private NeutralDmaVisibilityState? _visibility;
    public ulong VisibilityRevision { get; private set; }
    public bool VisibilitySourceExhausted { get; private set; }
    public NeutralDmaVisibilityState? Visibility { get => _visibility; set { if (VisibilityRevision == ulong.MaxValue) VisibilitySourceExhausted = true; else VisibilityRevision++; _visibility = value; } }
    public List<NeutralOriginalDmaVisibilityFact> OriginalVisibilityHistory { get; } = [];
    public ulong OriginalVisibilityIssued { get; set; }
    public List<NeutralOriginalDmaCopyFact> OriginalCopies { get; } = [];
    public ulong OriginalCopiesIssued { get; set; }
    public NeutralOriginalDmaCopyFact? PendingCopy { get; set; }
}

internal sealed class NeutralOriginalDmaCopyFact(NeutralOriginalDmaCopyRequest request,
    NeutralDmaGrantState source, NeutralDmaGrantState destination,
    NeutralOriginalDmaAdmissionFact sourceAdmission, NeutralOriginalDmaAdmissionFact destinationAdmission,
    NeutralDmaVisibilityState sourceCycle, NeutralDmaVisibilityState destinationCycle)
{
    public NeutralOriginalDmaCopyRequest Request { get; } = request;
    public NeutralDmaGrantState Source { get; } = source;
    public NeutralDmaGrantState Destination { get; } = destination;
    public NeutralOriginalDmaAdmissionFact SourceAdmission { get; } = sourceAdmission;
    public NeutralOriginalDmaAdmissionFact DestinationAdmission { get; } = destinationAdmission;
    public NeutralDmaVisibilityState SourceCycle { get; } = sourceCycle;
    public NeutralDmaVisibilityState DestinationCycle { get; } = destinationCycle;
    public ulong SourceCycleRevision { get; } = sourceCycle.Revision;
    public ulong DestinationCycleRevision { get; } = destinationCycle.Revision;
    public ulong SourceVisibilityRevision { get; } = source.VisibilityRevision;
    public ulong DestinationVisibilityRevision { get; } = destination.VisibilityRevision;
    public NeutralOriginalDmaCopyObservation? Observation { get; set; }
    public void EstablishSelf() => OwnerSelf = this;
    public NeutralOriginalDmaCopyFact? OwnerSelf { get; private set; }
    public bool HasSelf => ReferenceEquals(OwnerSelf,this);
}

internal sealed class NeutralDmaVisibilityState
{
    public NeutralDmaVisibilityState() { OwnerSelf = this; }
    public NeutralDmaVisibilityState OwnerSelf { get; }
    private NeutralDmaVisibilityCycle _cycle;
    private bool _acquired;
    public ulong Revision { get; private set; }
    public bool SourceExhausted { get; private set; }
    private void Advance() { if (Revision == ulong.MaxValue) SourceExhausted = true; else Revision++; }
    public required NeutralDmaVisibilityCycle Cycle { get => _cycle; init { Advance(); _cycle = value; } }
    public bool Acquired { get => _acquired; set { Advance(); _acquired = value; } }
}

internal sealed class NeutralOriginalDmaAdmissionFact
{
    public NeutralOriginalDmaAdmissionFact(NeutralOriginalDmaGrantRequest request, NeutralDeviceState device, NeutralMappingState mapping, NeutralDomainState parent)
    { OwnerSelf = this; Request = request; Device = device; Mapping = mapping; Parent = parent; RootAdmission = parent.OriginalAdmission; MappingAdmission = mapping.OriginalAdmission; DeviceAdmission = device.OriginalAdmission; }
    public NeutralOriginalDmaAdmissionFact OwnerSelf { get; }
    public NeutralOriginalDmaGrantRequest Request { get; }
    public NeutralDeviceState Device { get; }
    public NeutralMappingState Mapping { get; }
    public NeutralDomainState Parent { get; }
    public NeutralOriginalDomainAdmissionFact? RootAdmission { get; }
    public NeutralOriginalMappingAdmissionFact? MappingAdmission { get; }
    public NeutralOriginalDeviceAdmissionFact? DeviceAdmission { get; }
    public NeutralDmaGrantState? Source { get; set; }
    public NeutralOriginalDmaGrantObservation? Observation { get; set; }
}
internal sealed class NeutralOriginalDmaCloseFact
{
    public NeutralOriginalDmaCloseFact(NeutralOriginalDmaGrantCloseRequest request, NeutralDmaGrantState source, NeutralOriginalDmaAdmissionFact admission)
    { OwnerSelf = this; Request = request; Source = source; Admission = admission; }
    public NeutralOriginalDmaCloseFact OwnerSelf { get; }
    public NeutralOriginalDmaGrantCloseRequest Request { get; }
    public NeutralDmaGrantState Source { get; }
    public NeutralOriginalDmaAdmissionFact Admission { get; }
    public NeutralOriginalDmaGrantCloseObservation? Observation { get; set; }
}

internal sealed class NeutralOriginalDmaVisibilityFact
{
    public NeutralOriginalDmaVisibilityFact(NeutralOriginalDmaVisibilityRequest request, NeutralDmaGrantState source, NeutralOriginalDmaAdmissionFact admission)
    { OwnerSelf = this; Request = request; Source = source; Admission = admission; }
    public NeutralOriginalDmaVisibilityFact OwnerSelf { get; }
    public NeutralOriginalDmaVisibilityRequest Request { get; }
    public NeutralDmaGrantState Source { get; }
    public NeutralOriginalDmaAdmissionFact Admission { get; }
    public NeutralDmaVisibilityState? ProducedState { get; set; }
    public ulong ProducedRevision { get; set; }
    public ulong GrantVisibilityRevision { get; set; }
    public NeutralOriginalDmaVisibilityObservation? Observation { get; set; }
}
