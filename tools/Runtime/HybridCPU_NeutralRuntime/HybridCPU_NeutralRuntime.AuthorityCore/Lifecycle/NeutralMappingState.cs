namespace YAKSys_Hybrid_CPU.Core;

internal sealed class NeutralMappingState
{
    public required NeutralOwnedRegionMappingLease Lease { get; init; }
    public NeutralOwnedRegionMappingHandle Handle => Lease.Handle;
    public NeutralOwnedRegionMappingEpoch Epoch => Lease.Epoch;
    public NeutralDomainBindingHandle ParentDomain => Lease.DomainLease.Handle;
    public NeutralOwnedRegionSlice Slice => Lease.Slice;
    public NeutralResourceLifecycle Lifecycle { get; set; } = NeutralResourceLifecycle.Active;
    public bool IsActive => Lifecycle == NeutralResourceLifecycle.Active;
    public NeutralOriginalMappingCloseFact? OriginalClose { get; set; }
    public NeutralOriginalMappingAdmissionFact? OriginalAdmission { get; set; }
    public List<NeutralOriginalDsc1CopyFact> OriginalDsc1Copies {get;}=[];
    public ulong OriginalDsc1CopiesIssued {get;set;}
}

internal sealed class NeutralOriginalDsc1CopyFact(object runtime,NeutralMappingState source,NeutralMappingState destination,
    NeutralOriginalMappingAdmissionFact sourceAdmission,NeutralOriginalMappingAdmissionFact destinationAdmission)
{
    public readonly object Runtime=runtime;
    public readonly NeutralMappingState Source=source,Destination=destination;
    public readonly NeutralOriginalMappingAdmissionFact SourceAdmission=sourceAdmission,DestinationAdmission=destinationAdmission;
    public NeutralOriginalDsc1CopyFact? Self;
    public NeutralOriginalDsc1CopyRequest Request=null!;
    public bool Invoked;
    public NeutralOriginalDsc1CopyObservation? Raw;
}

internal sealed class NeutralOriginalMappingAdmissionFact(
    NeutralOriginalOwnedRegionMapRequest request, NeutralMappingState source, NeutralDomainState parent)
{
    public NeutralOriginalOwnedRegionMapRequest Request { get; } = request;
    public NeutralMappingState Source { get; } = source;
    public NeutralDomainState Parent { get; } = parent;
}

internal sealed class NeutralOriginalMappingCloseFact(NeutralOriginalMappingCloseRequest request,
    NeutralMappingState mapping, NeutralDomainState parent)
{
    public NeutralOriginalMappingCloseRequest Request { get; } = request;
    public NeutralMappingState Mapping { get; } = mapping;
    public NeutralDomainState Parent { get; } = parent;
    public NeutralOwnedRegionCloseResult? Observation { get; set; }
}
