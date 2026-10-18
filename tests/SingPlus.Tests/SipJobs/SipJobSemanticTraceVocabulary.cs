namespace SingPlus.Tests.SipJobs;

// Qualification-only vocabulary. It records completed owner linearizations and
// carries opaque value identities only. It is deliberately not a runtime owner.
internal enum SipJobSemanticEventKind
{
    SubjectGenerationValidated = 0,
    SessionPinned,
    SessionGenerationRevalidated,
    OperationAuthorityCommitted,
    SealPinnedOrValidated,
    RegionUseAcquired,
    RegionUseReleased,
    RegionOwnershipTransferred,
    ProtocolTransitionCommitted,
    InvocationStateTransitioned,
    CancellationLinearized,
    ImplementationEntered,
    ImplementationCompleted,
    ImplementationFaulted,
    ExternalOperationSubmitted,
    ProviderCompletionObserved,
    VisibilityConfirmed,
    OutputValidated,
    ResponsePublished,
    OwnershipSettled,
    ReleaseCompleted,
}

internal enum SipJobSemanticOutcome
{
    Succeeded = 0,
    Denied,
    Faulted,
    Cancelled,
}

internal readonly record struct SipJobSemanticTraceEvent(
    uint Version,
    SipJobSemanticEventKind Kind,
    string OpaqueCorrelation,
    ulong Generation,
    SipJobSemanticOutcome Outcome,
    string? ExternalOperationIdentity,
    string? SemanticBindingIdentity);

internal enum SipJobSemanticTraceError
{
    None = 0,
    UnknownVersion,
    UnknownEvent,
    UnknownOutcome,
    NonCanonicalIdentity,
    MissingExternalIdentity,
    UnexpectedExternalIdentity,
    TraceMismatch,
}

internal readonly record struct SipJobSemanticTraceComparison(
    SipJobSemanticTraceError Error,
    int ComparedEvents,
    string? Detail)
{
    internal bool IsEquivalent => Error == SipJobSemanticTraceError.None;
}

internal sealed class SipJobSemanticTrace
{
    internal const uint Version = 1;
    private readonly List<SipJobSemanticTraceEvent> _completed = [];

    internal IReadOnlyList<SipJobSemanticTraceEvent> Completed => _completed;

    internal void RecordCompleted(
        SipJobSemanticEventKind kind,
        string opaqueCorrelation,
        ulong generation,
        SipJobSemanticOutcome outcome,
        string? externalOperationIdentity = null,
        string? semanticBindingIdentity = null)
    {
        var candidate = new SipJobSemanticTraceEvent(
            Version, kind, opaqueCorrelation, generation, outcome,
            externalOperationIdentity, semanticBindingIdentity);
        var error = Validate(candidate);
        if (error != SipJobSemanticTraceError.None)
            throw new ArgumentException($"Non-canonical semantic trace event: {error}.");
        _completed.Add(candidate);
    }

    internal static SipJobSemanticTraceError Validate(SipJobSemanticTraceEvent item)
    {
        if (item.Version != Version) return SipJobSemanticTraceError.UnknownVersion;
        if (!Enum.IsDefined(item.Kind)) return SipJobSemanticTraceError.UnknownEvent;
        if (!Enum.IsDefined(item.Outcome)) return SipJobSemanticTraceError.UnknownOutcome;
        if (!Canonical(item.OpaqueCorrelation) || item.Generation == 0)
            return SipJobSemanticTraceError.NonCanonicalIdentity;

        var requiresExternalIdentity = item.Kind is
            SipJobSemanticEventKind.ExternalOperationSubmitted or
            SipJobSemanticEventKind.ProviderCompletionObserved or
            SipJobSemanticEventKind.VisibilityConfirmed or
            SipJobSemanticEventKind.OutputValidated or
            SipJobSemanticEventKind.ResponsePublished or
            SipJobSemanticEventKind.OwnershipSettled or
            SipJobSemanticEventKind.ReleaseCompleted;
        if (requiresExternalIdentity &&
            (!Canonical(item.ExternalOperationIdentity) || !Canonical(item.SemanticBindingIdentity)))
            return SipJobSemanticTraceError.MissingExternalIdentity;
        if (!requiresExternalIdentity &&
            (item.ExternalOperationIdentity is not null || item.SemanticBindingIdentity is not null))
            return SipJobSemanticTraceError.UnexpectedExternalIdentity;
        return SipJobSemanticTraceError.None;
    }

    internal static SipJobSemanticTraceComparison CompareAuthorityVisible(
        IReadOnlyList<SipJobSemanticTraceEvent> ordinary,
        IReadOnlyList<SipJobSemanticTraceEvent> fused)
    {
        ArgumentNullException.ThrowIfNull(ordinary);
        ArgumentNullException.ThrowIfNull(fused);
        if (ordinary.Count != fused.Count)
            return new(SipJobSemanticTraceError.TraceMismatch, Math.Min(ordinary.Count, fused.Count),
                "Authority-visible event counts differ after internal stuttering is removed.");
        for (var index = 0; index < ordinary.Count; index++)
        {
            var leftError = Validate(ordinary[index]);
            var rightError = Validate(fused[index]);
            if (leftError != SipJobSemanticTraceError.None || rightError != SipJobSemanticTraceError.None)
                return new(leftError != SipJobSemanticTraceError.None ? leftError : rightError, index,
                    "A trace contains a non-canonical authority-visible event.");
            if (ordinary[index] != fused[index])
                return new(SipJobSemanticTraceError.TraceMismatch, index,
                    "Owner event, exact generation, outcome, operation identity or binding identity differs.");
        }
        return new(SipJobSemanticTraceError.None, ordinary.Count, null);
    }

    private static bool Canonical(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim();
}
