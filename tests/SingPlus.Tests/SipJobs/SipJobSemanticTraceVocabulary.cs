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
    SipJobSemanticEventKind Kind,
    string OpaqueCorrelation,
    ulong Generation,
    SipJobSemanticOutcome Outcome);

internal sealed class SipJobSemanticTrace
{
    private readonly List<SipJobSemanticTraceEvent> _completed = [];

    internal IReadOnlyList<SipJobSemanticTraceEvent> Completed => _completed;

    internal void RecordCompleted(
        SipJobSemanticEventKind kind,
        string opaqueCorrelation,
        ulong generation,
        SipJobSemanticOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opaqueCorrelation);
        _completed.Add(new(kind, opaqueCorrelation, generation, outcome));
    }
}
