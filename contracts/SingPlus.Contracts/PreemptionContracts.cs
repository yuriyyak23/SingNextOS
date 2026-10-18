using System.Text;

namespace SingPlus.Contracts;

public enum PreemptionClassV1 : byte
{
    NonPreemptible = 1,
    RestartOnly = 2,
    SafePoint = 3,
    StateCapture = 4,
}

public enum PreemptionEffectSemanticsV1 : byte
{
    RequestOnly = 1,
    ExactBeforeEffect = 2,
    ContainedAtSafePoint = 3,
}

/// <summary>Provider guarantee evidence. It never authorizes cancellation, restart, or resume.</summary>
public readonly record struct PreemptionGuaranteeV1(
    ushort Version,
    PreemptionClassV1 Class,
    PreemptionEffectSemanticsV1 EffectSemantics,
    ulong MaximumSafePointLatencyNanoseconds,
    bool CapturedStateSupported)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesPreemption => false;
    public bool AuthorizesRestart => false;
    public bool AuthorizesResume => false;

    public PreemptionGuaranteeV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Class) || !Enum.IsDefined(EffectSemantics))
            throw new NotSupportedException("Preemption guarantee version or class is unsupported.");
        if (Class is PreemptionClassV1.NonPreemptible or PreemptionClassV1.RestartOnly &&
            (MaximumSafePointLatencyNanoseconds != 0 || CapturedStateSupported))
            throw new ArgumentException("Non-preemptible/restart-only guarantees cannot claim safe-point latency or captured state.");
        if (Class is PreemptionClassV1.SafePoint or PreemptionClassV1.StateCapture &&
            MaximumSafePointLatencyNanoseconds == 0)
            throw new ArgumentException("Safe-point classes require a non-zero enforced latency bound.");
        if (CapturedStateSupported != (Class == PreemptionClassV1.StateCapture))
            throw new ArgumentException("Captured-state support is valid only for the StateCapture class.");
        if (EffectSemantics == PreemptionEffectSemanticsV1.ContainedAtSafePoint &&
            Class is not (PreemptionClassV1.SafePoint or PreemptionClassV1.StateCapture))
            throw new ArgumentException("Safe-point containment requires an executable safe-point class.");
        return this;
    }
}

/// <summary>Opaque state correlation only. Captured state and this binding carry no authority.</summary>
public readonly record struct ResumeBindingV1(
    ushort Version,
    string OperationCorrelation,
    string CapturedStateDigest,
    string SemanticBindingDigest,
    ulong OperationGeneration,
    ulong CaptureGeneration,
    ulong ProviderGeneration,
    ulong RuntimeGeneration)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesRegionAccess => false;
    public bool PreservesCapability => false;

    public ResumeBindingV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Resume binding version is unsupported.");
        if (OperationCorrelation is null || OperationCorrelation.Length > 256 ||
            string.IsNullOrWhiteSpace(OperationCorrelation) || OperationCorrelation != OperationCorrelation.Trim() ||
            OperationCorrelation.Any(char.IsControl))
            throw new ArgumentException("Resume operation correlation is not canonical.");
        try { _ = new UTF8Encoding(false, true).GetByteCount(OperationCorrelation); }
        catch (EncoderFallbackException exception)
        { throw new ArgumentException("Resume operation correlation must contain valid Unicode scalar values.", nameof(OperationCorrelation), exception); }
        ValidateDigest(CapturedStateDigest);
        ValidateDigest(SemanticBindingDigest);
        if (OperationGeneration == 0 || CaptureGeneration == 0 || ProviderGeneration == 0 || RuntimeGeneration == 0)
            throw new ArgumentException("Every resume dependency generation must be non-zero.");
        return this;
    }

    private static void ValidateDigest(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Resume binding digest must be canonical SHA-256 hex.");
    }
}

public enum PreemptionLifecycleEventKindV1 : byte
{
    Requested = 1,
    Draining = 2,
    SafePointReached = 3,
    StateCaptured = 4,
    Suspended = 5,
    Resumed = 6,
    Contained = 7,
    Restarted = 8,
}

public readonly record struct PreemptionLifecycleEventV1(
    ushort Version,
    ulong Sequence,
    PreemptionLifecycleEventKindV1 Kind,
    string EvidenceDigest)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;

    public PreemptionLifecycleEventV1 Validate()
    {
        if (Version != CurrentVersion || Sequence == 0 || !Enum.IsDefined(Kind))
            throw new NotSupportedException("Preemption lifecycle event is unsupported.");
        if (EvidenceDigest is null || EvidenceDigest.Length != 64 || EvidenceDigest.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Preemption evidence digest must be canonical SHA-256 hex.");
        return this;
    }
}

public static class PreemptionLifecycleValidatorV1
{
    public static bool IsValidRestartOnly(IEnumerable<PreemptionLifecycleEventV1> events)
    {
        try
        {
            var exact = events.Select(static item => item.Validate()).ToArray();
            PreemptionLifecycleEventKindV1[] expected =
            [PreemptionLifecycleEventKindV1.Requested, PreemptionLifecycleEventKindV1.Draining,
             PreemptionLifecycleEventKindV1.Contained, PreemptionLifecycleEventKindV1.Restarted];
            return exact.Select(static item => item.Kind).SequenceEqual(expected) &&
                   exact.Select(static item => item.Sequence).SequenceEqual([1UL, 2UL, 3UL, 4UL]);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }

    public static bool IsValidSafePoint(IEnumerable<PreemptionLifecycleEventV1> events)
    {
        try
        {
            var exact = events.Select(static item => item.Validate()).ToArray();
            PreemptionLifecycleEventKindV1[] expected =
            [PreemptionLifecycleEventKindV1.Requested, PreemptionLifecycleEventKindV1.Draining,
             PreemptionLifecycleEventKindV1.SafePointReached, PreemptionLifecycleEventKindV1.Contained];
            return exact.Select(static item => item.Kind).SequenceEqual(expected) &&
                   exact.Select(static item => item.Sequence).SequenceEqual([1UL, 2UL, 3UL, 4UL]) &&
                   exact.Select(static item => item.EvidenceDigest).Distinct(StringComparer.Ordinal).Count() == 1;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }
}

public sealed record RestartAdmissionReceiptV1(
    ushort Version,
    ExternalOperationHandle Predecessor,
    ExternalOperationHandle Replacement,
    PreemptionGuaranteeV1 Guarantee,
    ulong ProviderGeneration,
    ulong RuntimeGeneration,
    IReadOnlyList<PreemptionLifecycleEventV1> Lifecycle,
    BudgetReservationHandle? ReplacementBudget = null,
    BudgetReservationState? ReplacementBudgetState = null)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool ProvesPredecessorClosure => false;
    public bool AuthorizesBudgetConsumption => false;
}
