using System.Collections.Immutable;

namespace SingPlus.Runtime;

internal enum SipJobAdmissionParticipantKind
{
    ProcessIdentity = 0,
    ServiceIdentity,
    SessionPin,
    SealPin,
    RegionReadUse,
    OperationAuthority,
    RegionMoveSettlement,
    InvocationSettlement,
    PublicationSettlement,
}

internal enum SipJobAdmissionPhase
{
    ProbeOnly = 0,
    ReversiblePrepare,
    CommitConsumptive,
    PostCommitSettlement,
}

internal sealed record SipJobAdmissionParticipantDescriptor(
    uint Version,
    string ParticipantId,
    SipJobAdmissionParticipantKind Kind,
    SipJobAdmissionPhase Phase,
    string OwnerId,
    string PrepareOperationId,
    string FinalRevalidationOperationId,
    string CommitOperationId,
    string ReleaseBeforeCommitOperationId,
    string ReleaseAfterCommitOperationId);

internal sealed record SipJobSegmentAdmissionDescriptor
{
    internal SipJobSegmentAdmissionDescriptor(
        uint version,
        string segmentId,
        IEnumerable<SipJobAdmissionParticipantDescriptor> participants,
        IEnumerable<string> declaredGateSet)
    {
        Version = version;
        SegmentId = segmentId;
        Participants = participants.ToImmutableArray();
        DeclaredGateSet = declaredGateSet.ToImmutableArray();
    }

    internal uint Version { get; }
    internal string SegmentId { get; }
    internal ImmutableArray<SipJobAdmissionParticipantDescriptor> Participants { get; }
    internal ImmutableArray<string> DeclaredGateSet { get; }
}

internal enum SipJobSegmentAdmissionError
{
    None = 0,
    UnknownVersion,
    Malformed,
    UnsupportedGate,
    UnsupportedParticipant,
    InvalidPhase,
    InvalidOwnerProtocol,
    UnsafeMultipleConsumptiveCommits,
}

internal readonly record struct SipJobSegmentAdmissionFailure(
    SipJobSegmentAdmissionError Error,
    string Detail);

internal sealed record SipJobVerifiedSegmentMetadata(
    string SegmentId,
    ImmutableArray<string> OrderedParticipantIds,
    uint Version);

internal readonly record struct SipJobSegmentAdmissionResult(
    SipJobVerifiedSegmentMetadata? Metadata,
    SipJobSegmentAdmissionFailure? Failure)
{
    internal bool IsSuccess => Metadata is not null && Failure is null;
}

internal static class SipJobSegmentAdmissionVerifier
{
    internal const uint Version = 1;
    internal const string LinearGate = "FG-JOB-LINEAR";
    internal const string MultiSessionGate = "FG-MULTI-SESSION-SEGMENT";

    internal static SipJobSegmentAdmissionResult Verify(SipJobSegmentAdmissionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Participants.Any(static item => item is null))
            return Fail(SipJobSegmentAdmissionError.Malformed, "Admission participants cannot contain null entries.");
        if (descriptor.Version != Version || descriptor.Participants.Any(static item => item.Version != Version))
            return Fail(SipJobSegmentAdmissionError.UnknownVersion, "Unsupported segment or participant version.");
        if (string.IsNullOrWhiteSpace(descriptor.SegmentId) || descriptor.SegmentId != descriptor.SegmentId.Trim() ||
            descriptor.Participants.Length == 0 ||
            descriptor.Participants.Any(static item => string.IsNullOrWhiteSpace(item.ParticipantId) ||
                item.ParticipantId != item.ParticipantId.Trim()))
            return Fail(SipJobSegmentAdmissionError.Malformed, "Segment and participant identities must be canonical.");
        if (descriptor.Participants.Select(static item => item.ParticipantId).Distinct(StringComparer.Ordinal).Count() != descriptor.Participants.Length)
            return Fail(SipJobSegmentAdmissionError.Malformed, "Participant identities must be unique.");
        if (descriptor.DeclaredGateSet is not [LinearGate, MultiSessionGate])
            return Fail(SipJobSegmentAdmissionError.UnsupportedGate, "Composed segments require the exact closed gate set.");
        if (descriptor.Participants.Any(static item => !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Phase)))
            return Fail(SipJobSegmentAdmissionError.UnsupportedParticipant, "Unknown admission participant vocabulary.");

        var lastPhase = SipJobAdmissionPhase.ProbeOnly;
        foreach (var participant in descriptor.Participants)
        {
            if (participant.Phase < lastPhase)
                return Fail(SipJobSegmentAdmissionError.InvalidPhase, "Participants must follow probe, reversible prepare, consumptive commit, settlement order.");
            lastPhase = participant.Phase;
            if (!ValidOwnerProtocol(participant))
                return Fail(SipJobSegmentAdmissionError.InvalidOwnerProtocol, $"Participant '{participant.ParticipantId}' does not name exact owner semantics.");
        }

        if (descriptor.Participants.Count(static item => item.Phase == SipJobAdmissionPhase.CommitConsumptive) > 1)
            return Fail(SipJobSegmentAdmissionError.UnsafeMultipleConsumptiveCommits,
                "Independent consumptive commits require an ordinary SIP boundary or an owner-defined reservation protocol.");

        return new(new(descriptor.SegmentId,
            descriptor.Participants.Select(static item => item.ParticipantId).ToImmutableArray(), descriptor.Version), null);
    }

    private static bool ValidOwnerProtocol(SipJobAdmissionParticipantDescriptor item) => item.Kind switch
    {
        SipJobAdmissionParticipantKind.ProcessIdentity when item.Phase == SipJobAdmissionPhase.ProbeOnly =>
            Exact(item, "ProcessRegistry", "Resolve", "Resolve", "None", "None", "None"),
        SipJobAdmissionParticipantKind.ServiceIdentity when item.Phase == SipJobAdmissionPhase.ProbeOnly =>
            Exact(item, "ServiceRegistry", "Resolve", "Resolve", "None", "None", "None"),
        SipJobAdmissionParticipantKind.SessionPin when item.Phase == SipJobAdmissionPhase.ReversiblePrepare =>
            Exact(item, "EndpointSessionRegistry", "AcquirePin", "RevalidatePin", "None", "ReleasePin", "ReleasePin"),
        SipJobAdmissionParticipantKind.SealPin when item.Phase == SipJobAdmissionPhase.ReversiblePrepare =>
            Exact(item, "SealedObjectAuthority", "AcquirePin", "Revalidate", "None", "DisposePin", "DisposePin"),
        SipJobAdmissionParticipantKind.RegionReadUse when item.Phase == SipJobAdmissionPhase.ReversiblePrepare =>
            Exact(item, "RegionAuthority", "AcquireUse", "ValidateUse", "None", "ReleaseUse", "ReleaseUse"),
        SipJobAdmissionParticipantKind.OperationAuthority when item.Phase == SipJobAdmissionPhase.CommitConsumptive =>
            Exact(item, "CapabilityAuthority", "None", "Validate", "AcquireOperationAuthority", "None", "ReleaseLeaseNoRefund"),
        SipJobAdmissionParticipantKind.RegionMoveSettlement when item.Phase == SipJobAdmissionPhase.PostCommitSettlement =>
            Exact(item, "RegionAuthority", "None", "Validate", "Transfer", "None", "NoImplicitInverseMove"),
        SipJobAdmissionParticipantKind.InvocationSettlement when item.Phase == SipJobAdmissionPhase.PostCommitSettlement =>
            Exact(item, "EndpointSessionInvocationRegistry", "None", "Resolve", "CompleteInline", "None", "OwnerTerminal"),
        SipJobAdmissionParticipantKind.PublicationSettlement when item.Phase == SipJobAdmissionPhase.PostCommitSettlement =>
            Exact(item, "ResponseRegistry", "None", "Resolve", "Publish", "None", "OwnerTerminal"),
        _ => false,
    };

    private static bool Exact(
        SipJobAdmissionParticipantDescriptor item,
        string owner,
        string prepare,
        string revalidate,
        string commit,
        string before,
        string after) =>
        item.OwnerId == owner && item.PrepareOperationId == prepare &&
        item.FinalRevalidationOperationId == revalidate && item.CommitOperationId == commit &&
        item.ReleaseBeforeCommitOperationId == before && item.ReleaseAfterCommitOperationId == after;

    private static SipJobSegmentAdmissionResult Fail(SipJobSegmentAdmissionError error, string detail) =>
        new(null, new(error, detail));
}
