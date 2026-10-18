using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

public enum SemanticTraceEventKindV1 : byte
{
    Submit = 1,
    EffectPossible = 2,
    RetireOrComplete = 3,
    Visible = 4,
    Published = 5,
    Quarantined = 6,
    GenerationChanged = 7,
    Settled = 8,
    Released = 9,
    EffectClosedWithoutPublication = 10,
    CancellationRequested = 11,
    ResourceAccountingQuarantined = 12,
    CancelledBeforeSubmit = 13,
    LocalAuthorityReleasedBeforeSubmit = 14,
    LocalAuthorityReleasedWithoutResourceBinding = 15,
    ResourceAccountingQuarantinedBeforeSubmit = 16,
}

public readonly record struct SemanticTraceEventV1(
    ushort Version,
    string OperationCorrelation,
    ulong Sequence,
    SemanticTraceEventKindV1 Kind,
    string Source,
    string GenerationVectorDigest,
    string EvidenceDigest)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
    public bool AuthorizesPublication => false;

    public SemanticTraceEventV1 Validate()
    {
        if (Version != CurrentVersion || Sequence == 0 || !Enum.IsDefined(Kind))
            throw new NotSupportedException("Semantic trace event version, sequence, or kind is unsupported.");
        ValidateToken(OperationCorrelation, nameof(OperationCorrelation));
        ValidateToken(Source, nameof(Source));
        ValidateDigest(GenerationVectorDigest, nameof(GenerationVectorDigest));
        ValidateDigest(EvidenceDigest, nameof(EvidenceDigest));
        return this;
    }

    private static void ValidateToken(string? value, string parameter)
    {
        if (value is null || value.Length > 256 || string.IsNullOrWhiteSpace(value) ||
            value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Trace identity must be a bounded canonical token.", parameter);
        int byteCount;
        try { byteCount = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Trace identity must contain valid Unicode scalar values.", parameter, exception);
        }
        if (byteCount > 256)
            throw new ArgumentException("Trace identity must be a bounded canonical token.", parameter);
    }

    private static void ValidateDigest(string? value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Trace digest must be canonical SHA-256 hex.", parameter);
    }
}

/// <summary>
/// Non-authoritative, best-effort observation sink. Implementations must not block or throw;
/// a rejected observation never changes execution, effect, or publication authority.
/// </summary>
public interface ISemanticTraceSinkV1
{
    bool TryRecord(SemanticTraceEventV1 traceEvent);
}

public enum ProviderTraceDispositionV1 : byte
{
    SecurityRelevant = 1,
    ProviderPrivate = 2,
}

public readonly record struct ProviderTraceEventV1(
    ushort Version,
    string OperationCorrelation,
    ulong Sequence,
    ProviderTraceDispositionV1 Disposition,
    SemanticTraceEventKindV1 SemanticKind,
    string ProviderIdentity,
    string GenerationVectorDigest,
    string EvidenceDigest);

public enum SemanticTraceValidationStatusV1 : byte
{
    Valid = 1,
    InvalidEvent = 2,
    MixedOperation = 3,
    NonMonotonicSequence = 4,
    InvalidLifecycleOrder = 5,
    EventAfterRelease = 6,
    GenerationMismatch = 7,
}

/// <summary>
/// Diagnostic failure identity. InvalidEvent inputs are not canonical or bounded,
/// so their ID identifies the failure position/kind rather than the rejected payload.
/// Exact-input attribution requires separately retained source evidence.
/// </summary>
public sealed record SemanticTraceCounterexampleV1(
    ushort Version,
    SemanticTraceValidationStatusV1 Status,
    int EventIndex,
    SemanticTraceEventKindV1? Previous,
    SemanticTraceEventKindV1 Current,
    string CanonicalId)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
}

public sealed record SemanticTraceValidationResultV1(
    SemanticTraceValidationStatusV1 Status,
    SemanticTraceCounterexampleV1? Counterexample)
{
    public bool IsValid => Status == SemanticTraceValidationStatusV1.Valid;
}

/// <summary>
/// Validates observation metadata before private-event erasure. ProviderIdentity
/// is a source label, not an authenticated provider/dependency binding; callers
/// must retain exact source evidence separately for qualification attribution.
/// </summary>
public static class SemanticTraceProjectionV1
{
    public static IReadOnlyList<SemanticTraceEventV1> Project(IEnumerable<ProviderTraceEventV1> providerTrace)
    {
        ArgumentNullException.ThrowIfNull(providerTrace);
        var projected = new List<SemanticTraceEventV1>();
        string? lastGenerationDigest = null;
        string? operation = null;
        ulong sequence = 0;
        foreach (var item in providerTrace)
        {
            if (item.Version != SemanticTraceEventV1.CurrentVersion || !Enum.IsDefined(item.Disposition))
                throw new NotSupportedException("Provider trace event version or disposition is unsupported.");
            // Validate metadata even for erased observations: private labels cannot hide
            // a semantic transition, a different operation, or a reordered event.
            _ = new SemanticTraceEventV1(item.Version, item.OperationCorrelation, item.Sequence,
                SemanticTraceEventKindV1.Submit, item.ProviderIdentity,
                item.GenerationVectorDigest, item.EvidenceDigest).Validate();
            if (operation is not null && !string.Equals(operation, item.OperationCorrelation, StringComparison.Ordinal))
                throw new InvalidOperationException("A provider-private trace event cannot conceal another operation.");
            if (item.Sequence <= sequence)
                throw new InvalidOperationException("Provider trace sequence must be strictly monotonic before projection.");
            operation = item.OperationCorrelation;
            sequence = item.Sequence;
            if (item.Disposition == ProviderTraceDispositionV1.ProviderPrivate)
            {
                if (item.SemanticKind != default)
                    throw new InvalidOperationException(
                        "A provider-private trace event cannot conceal a semantic lifecycle transition.");
                if (lastGenerationDigest is not null &&
                    !string.Equals(lastGenerationDigest, item.GenerationVectorDigest,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "A provider-private trace event cannot conceal a generation change.");
                lastGenerationDigest ??= item.GenerationVectorDigest;
                continue;
            }
            if (lastGenerationDigest is not null &&
                !string.Equals(lastGenerationDigest, item.GenerationVectorDigest,
                    StringComparison.Ordinal) && item.SemanticKind != SemanticTraceEventKindV1.GenerationChanged)
                throw new InvalidOperationException(
                    "Provider-private trace events cannot conceal a generation change before a semantic event.");
            var semanticEvent = new SemanticTraceEventV1(SemanticTraceEventV1.CurrentVersion,
                item.OperationCorrelation, item.Sequence, item.SemanticKind, item.ProviderIdentity,
                item.GenerationVectorDigest, item.EvidenceDigest).Validate();
            projected.Add(semanticEvent);
            lastGenerationDigest = semanticEvent.GenerationVectorDigest;
        }
        return new ReadOnlyCollection<SemanticTraceEventV1>(projected);
    }
}

public static class SemanticTraceValidatorV1
{
    public static SemanticTraceValidationResultV1 Validate(IEnumerable<SemanticTraceEventV1> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var events = trace.ToArray();
        if (events.Length == 0)
            return Fail(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, 0, null, default);

        string? operation = null;
        string? generationVectorDigest = null;
        var observedGenerationDigests = new HashSet<string>(StringComparer.Ordinal);
        ulong sequence = 0;
        SemanticTraceEventKindV1? previous = null;
        var state = LifecycleState.Initial;
        var completionObserved = false;
        var accountingQuarantineObserved = false;
        var generationDriftObserved = false;
        for (var index = 0; index < events.Length; index++)
        {
            SemanticTraceEventV1 current;
            try { current = events[index].Validate(); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            { return Fail(SemanticTraceValidationStatusV1.InvalidEvent, index, previous, events[index].Kind); }

            operation ??= current.OperationCorrelation;
            if (!string.Equals(operation, current.OperationCorrelation, StringComparison.Ordinal))
                return Fail(SemanticTraceValidationStatusV1.MixedOperation, index, previous, current.Kind, current);
            if (current.Sequence <= sequence)
                return Fail(SemanticTraceValidationStatusV1.NonMonotonicSequence, index, previous, current.Kind, current);
            if (state is LifecycleState.Released or LifecycleState.NonResourceLocallyReleased ||
                state == LifecycleState.PreSubmitReleased && current.Kind != SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit)
                return Fail(SemanticTraceValidationStatusV1.EventAfterRelease, index, previous, current.Kind, current);

            if (generationVectorDigest is not null)
            {
                var changed = !string.Equals(generationVectorDigest,
                    current.GenerationVectorDigest, StringComparison.Ordinal);
                if (changed != (current.Kind == SemanticTraceEventKindV1.GenerationChanged) ||
                    (changed && observedGenerationDigests.Contains(current.GenerationVectorDigest)))
                    return Fail(SemanticTraceValidationStatusV1.GenerationMismatch,
                        index, previous, current.Kind, current);
            }

            if ((current.Kind == SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding &&
                 (accountingQuarantineObserved || generationDriftObserved)) ||
                (current.Kind == SemanticTraceEventKindV1.RetireOrComplete && completionObserved) ||
                !TryAdvance(state, current.Kind, out state))
                return Fail(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, index, previous, current.Kind, current);
            if (current.Kind == SemanticTraceEventKindV1.RetireOrComplete) completionObserved = true;
            if (current.Kind is SemanticTraceEventKindV1.ResourceAccountingQuarantined or
                SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit) accountingQuarantineObserved = true;
            if (current.Kind == SemanticTraceEventKindV1.GenerationChanged) generationDriftObserved = true;
            generationVectorDigest = current.GenerationVectorDigest;
            observedGenerationDigests.Add(current.GenerationVectorDigest);
            sequence = current.Sequence;
            previous = current.Kind;
        }
        return new(SemanticTraceValidationStatusV1.Valid, null);
    }

    private static bool TryAdvance(LifecycleState state, SemanticTraceEventKindV1 kind, out LifecycleState next)
    {
        next = state;
        if (kind == SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit)
            // Local pre-submit release does not terminate independent accounting.
            return state is LifecycleState.Initial or LifecycleState.PreSubmitCancelled or LifecycleState.PreSubmitReleased;
        if (kind == SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding)
            return state is LifecycleState.Published or LifecycleState.Quarantined or LifecycleState.EffectClosed &&
                   Set(LifecycleState.NonResourceLocallyReleased, out next);
        if (kind == SemanticTraceEventKindV1.CancelledBeforeSubmit)
            return state is LifecycleState.Initial or LifecycleState.PreSubmitCancelled &&
                   Set(LifecycleState.PreSubmitCancelled, out next);
        if (kind == SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit)
            return state == LifecycleState.PreSubmitCancelled &&
                   Set(LifecycleState.PreSubmitReleased, out next);
        // This separate local branch cannot acquire post-submit effect/closure facts.
        if (state is LifecycleState.PreSubmitCancelled or LifecycleState.PreSubmitReleased)
            return false;
        if (kind == SemanticTraceEventKindV1.ResourceAccountingQuarantined)
        {
            // Accounting ambiguity is independent of effect containment. Observe
            // it without restoring any lifecycle/generation/closure permission.
            return state is not (LifecycleState.Initial or LifecycleState.Submitted or
                LifecycleState.Settled or LifecycleState.QuarantinedSettled or
                LifecycleState.EffectClosedSettled or LifecycleState.GenerationDriftedSettled or
                LifecycleState.GenerationDriftedAfterPublicationSettled or
                LifecycleState.QuarantinedAfterPublicationSettled or LifecycleState.CompletedSettled or
                LifecycleState.VisibleSettled or LifecycleState.Released);
        }
        if (kind == SemanticTraceEventKindV1.GenerationChanged)
        {
            // Submit and EffectPossible are one irreversible owner boundary.
            // A drift marker between them would allow a changed generation to
            // inherit a submit without accounting for its possible effect.
            if (state is LifecycleState.Initial or LifecycleState.Submitted) return false;
            if (state is LifecycleState.Settled or LifecycleState.QuarantinedAfterPublicationSettled or
                LifecycleState.GenerationDriftedAfterPublicationSettled)
                return Set(LifecycleState.GenerationDriftedAfterPublicationSettled, out next);
            if (state is LifecycleState.QuarantinedSettled or LifecycleState.EffectClosedSettled or
                LifecycleState.GenerationDriftedSettled or LifecycleState.CompletedSettled or LifecycleState.VisibleSettled)
                return Set(LifecycleState.GenerationDriftedSettled, out next);
            if (state is LifecycleState.Published or LifecycleState.Settled or
                LifecycleState.GenerationDriftedAfterPublication or
                LifecycleState.QuarantinedAfterPublication or
                LifecycleState.QuarantinedAfterPublicationSettled)
                next = LifecycleState.GenerationDriftedAfterPublication;
            else if (state is LifecycleState.EffectPossible or LifecycleState.CancellationPending or
                     LifecycleState.Completed or LifecycleState.Visible or LifecycleState.GenerationDrifted or
                     LifecycleState.Quarantined or LifecycleState.QuarantinedSettled or LifecycleState.EffectClosed or
                     LifecycleState.EffectClosedSettled)
                next = LifecycleState.GenerationDrifted;
            return true;
        }
        if (kind == SemanticTraceEventKindV1.Quarantined)
        {
            if (state is LifecycleState.Initial or LifecycleState.Submitted) return false;
            if (state is LifecycleState.Settled or LifecycleState.QuarantinedAfterPublicationSettled or
                LifecycleState.GenerationDriftedAfterPublicationSettled)
                return Set(LifecycleState.QuarantinedAfterPublicationSettled, out next);
            if (state is LifecycleState.QuarantinedSettled or LifecycleState.EffectClosedSettled or
                LifecycleState.GenerationDriftedSettled or LifecycleState.CompletedSettled or LifecycleState.VisibleSettled)
                return Set(LifecycleState.QuarantinedSettled, out next);
            next = state is LifecycleState.Published or LifecycleState.Settled or LifecycleState.GenerationDriftedAfterPublication or
                LifecycleState.QuarantinedAfterPublication or LifecycleState.QuarantinedAfterPublicationSettled
                ? LifecycleState.QuarantinedAfterPublication
                : LifecycleState.Quarantined;
            return true;
        }
        if (kind == SemanticTraceEventKindV1.EffectClosedWithoutPublication)
            return state switch
            {
                LifecycleState.Quarantined => Set(LifecycleState.EffectClosed, out next),
                LifecycleState.QuarantinedSettled => Set(LifecycleState.EffectClosedSettled, out next),
                _ => false,
            };
        if (kind == SemanticTraceEventKindV1.CancellationRequested)
            return state == LifecycleState.EffectPossible &&
                   Set(LifecycleState.CancellationPending, out next);
        if (state is LifecycleState.GenerationDrifted or LifecycleState.GenerationDriftedAfterPublication or
            LifecycleState.GenerationDriftedSettled or LifecycleState.GenerationDriftedAfterPublicationSettled)
            return false;
        if (state == LifecycleState.QuarantinedAfterPublication)
            return kind == SemanticTraceEventKindV1.Settled &&
                   Set(LifecycleState.QuarantinedAfterPublicationSettled, out next);
        if (state == LifecycleState.QuarantinedAfterPublicationSettled)
            return false;
        if (state == LifecycleState.Quarantined)
            return kind switch
            {
                // Late completion observes an existing effect; it does not clear
                // quarantine or establish visibility, publication or closure.
                SemanticTraceEventKindV1.RetireOrComplete => true,
                SemanticTraceEventKindV1.Settled => Set(LifecycleState.QuarantinedSettled, out next),
                SemanticTraceEventKindV1.Released => false,
                _ => false,
            };
        if (state == LifecycleState.QuarantinedSettled)
            return false;
        if (state == LifecycleState.EffectClosed)
            return kind == SemanticTraceEventKindV1.Settled &&
                   Set(LifecycleState.EffectClosedSettled, out next);
        if (state == LifecycleState.EffectClosedSettled)
            return kind == SemanticTraceEventKindV1.Released &&
                   Set(LifecycleState.Released, out next);
        return (state, kind) switch
        {
            (LifecycleState.Initial, SemanticTraceEventKindV1.Submit) => Set(LifecycleState.Submitted, out next),
            (LifecycleState.Submitted, SemanticTraceEventKindV1.EffectPossible) => Set(LifecycleState.EffectPossible, out next),
            (LifecycleState.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete) => Set(LifecycleState.Completed, out next),
            (LifecycleState.CancellationPending, SemanticTraceEventKindV1.RetireOrComplete) => Set(LifecycleState.Completed, out next),
            (LifecycleState.Completed, SemanticTraceEventKindV1.Visible) => Set(LifecycleState.Visible, out next),
            // Accounting settlement is independent from visibility/publication.
            // It cannot permit release until publication or exact no-publication closure.
            (LifecycleState.Completed, SemanticTraceEventKindV1.Settled) => Set(LifecycleState.CompletedSettled, out next),
            (LifecycleState.Visible, SemanticTraceEventKindV1.Settled) => Set(LifecycleState.VisibleSettled, out next),
            (LifecycleState.CompletedSettled, SemanticTraceEventKindV1.Visible) => Set(LifecycleState.VisibleSettled, out next),
            (LifecycleState.VisibleSettled, SemanticTraceEventKindV1.Published) => Set(LifecycleState.Settled, out next),
            (LifecycleState.Visible, SemanticTraceEventKindV1.Published) => Set(LifecycleState.Published, out next),
            (LifecycleState.Published, SemanticTraceEventKindV1.Settled) => Set(LifecycleState.Settled, out next),
            (LifecycleState.Settled, SemanticTraceEventKindV1.Released) => Set(LifecycleState.Released, out next),
            _ => false,
        };
    }

    private static bool Set(LifecycleState value, out LifecycleState target)
    {
        target = value;
        return true;
    }

    private static SemanticTraceValidationResultV1 Fail(
        SemanticTraceValidationStatusV1 status,
        int index,
        SemanticTraceEventKindV1? previous,
        SemanticTraceEventKindV1 current,
        SemanticTraceEventV1? offendingEvent = null)
    {
        var payload = $"1|{(byte)status}|{index}|{(byte?)previous ?? 0}|{(byte)current}";
        if (offendingEvent is { } item)
        {
            // A valid event has bounded canonical tokens. Bind the counterexample
            // to the actual observation without exposing its identity in the result.
            payload += FormattableString.Invariant(
                $"|event/v1|{Encoding.UTF8.GetByteCount(item.OperationCorrelation)}:{item.OperationCorrelation}|{item.Sequence}|{Encoding.UTF8.GetByteCount(item.Source)}:{item.Source}|{item.GenerationVectorDigest}|{item.EvidenceDigest}");
        }
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return new(status, new SemanticTraceCounterexampleV1(1, status, index, previous, current, id));
    }

    private enum LifecycleState : byte
    {
        Initial,
        Submitted,
        EffectPossible,
        CancellationPending,
        Completed,
        Visible,
        Published,
        Settled,
        Quarantined,
        QuarantinedSettled,
        EffectClosed,
        EffectClosedSettled,
        GenerationDrifted,
        GenerationDriftedAfterPublication,
        QuarantinedAfterPublication,
        QuarantinedAfterPublicationSettled,
        Released,
        GenerationDriftedSettled,
        GenerationDriftedAfterPublicationSettled,
        CompletedSettled,
        VisibleSettled,
        PreSubmitCancelled,
        PreSubmitReleased,
        NonResourceLocallyReleased,
    }
}

public enum SemanticTraceComparisonStatusV1 : byte
{
    Equivalent = 1,
    InvalidReference = 2,
    InvalidCandidate = 3,
    LengthMismatch = 4,
    EventMismatch = 5,
}

public sealed record SemanticTraceDifferenceV1(
    ushort Version,
    SemanticTraceComparisonStatusV1 Status,
    int EventIndex,
    SemanticTraceEventKindV1? Reference,
    SemanticTraceEventKindV1? Candidate,
    string ReferenceSourceTupleDigest,
    string CandidateSourceTupleDigest,
    string CanonicalId)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesPublication => false;
}

public sealed record SemanticTraceComparisonResultV1(
    SemanticTraceComparisonStatusV1 Status,
    SemanticTraceDifferenceV1? Difference)
{
    public bool IsEquivalent => Status == SemanticTraceComparisonStatusV1.Equivalent;
}

/// <summary>
/// Compares the allowed lifecycle projection, not provider-private timing or evidence detail.
/// Counterexamples are bound to the two caller-supplied source/dependency tuple digests.
/// </summary>
public static class SemanticTraceDifferentialV1
{
    public static SemanticTraceComparisonResultV1 CompareAllowedProjection(
        IEnumerable<SemanticTraceEventV1> reference,
        IEnumerable<SemanticTraceEventV1> candidate,
        string referenceSourceTupleDigest,
        string candidateSourceTupleDigest)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateDigest(referenceSourceTupleDigest);
        ValidateDigest(candidateSourceTupleDigest);
        var left = reference.ToArray();
        var right = candidate.ToArray();
        var leftValidation = SemanticTraceValidatorV1.Validate(left);
        if (!leftValidation.IsValid)
            return Fail(SemanticTraceComparisonStatusV1.InvalidReference,
                leftValidation.Counterexample?.EventIndex ?? 0, null, null,
                leftValidation.Counterexample?.CanonicalId);
        var rightValidation = SemanticTraceValidatorV1.Validate(right);
        if (!rightValidation.IsValid)
            return Fail(SemanticTraceComparisonStatusV1.InvalidCandidate,
                rightValidation.Counterexample?.EventIndex ?? 0, null, null,
                rightValidation.Counterexample?.CanonicalId);
        var count = Math.Min(left.Length, right.Length);
        for (var index = 0; index < count; index++)
            if (left[index].Kind != right[index].Kind)
                return Fail(SemanticTraceComparisonStatusV1.EventMismatch, index,
                    left[index].Kind, right[index].Kind);
        if (left.Length != right.Length)
            return Fail(SemanticTraceComparisonStatusV1.LengthMismatch, count,
                count < left.Length ? left[count].Kind : null,
                count < right.Length ? right[count].Kind : null);
        return new(SemanticTraceComparisonStatusV1.Equivalent, null);

        SemanticTraceComparisonResultV1 Fail(SemanticTraceComparisonStatusV1 status, int index,
            SemanticTraceEventKindV1? referenceKind, SemanticTraceEventKindV1? candidateKind,
            string? validationCounterexampleId = null)
        {
            var payload = $"1|{(byte)status}|{index}|{(byte?)referenceKind ?? 0}|{(byte?)candidateKind ?? 0}|{referenceSourceTupleDigest}|{candidateSourceTupleDigest}";
            if (validationCounterexampleId is not null)
                payload += $"|{validationCounterexampleId}";
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            return new(status, new SemanticTraceDifferenceV1(1, status, index, referenceKind,
                candidateKind, referenceSourceTupleDigest, candidateSourceTupleDigest, id));
        }
    }

    private static void ValidateDigest(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Source tuple digest must be canonical SHA-256 hex.");
    }
}
