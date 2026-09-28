namespace SingPlus.HybridCpuQualification;

// Cold, internal semantic model. The report schema stays the same; preview only adds compile-time exhaustiveness.
#if SINGPLUS_PREVIEW_LANGUAGE
internal closed record class StageOutcome;
#else
internal abstract record class StageOutcome;
#endif

internal sealed record class StageValidated : StageOutcome;
internal sealed record class StageExternalBlocked : StageOutcome
{
    internal StageExternalBlocked(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }
    internal string Reason { get; }
}

internal sealed record class StageNotProduced : StageOutcome
{
    internal StageNotProduced(string prerequisite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prerequisite);
        Prerequisite = prerequisite;
    }
    internal string Prerequisite { get; }
}

internal sealed record class StageNotAttempted : StageOutcome
{
    internal StageNotAttempted(string prerequisite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prerequisite);
        Prerequisite = prerequisite;
    }
    internal string Prerequisite { get; }
}

internal static class StageOutcomeModel
{
    internal static StageOutcome Parse(string outcome, string? reason) => outcome switch
    {
        "Validated" when reason is null => new StageValidated(),
        "ExternalBlocked" when !string.IsNullOrWhiteSpace(reason) => new StageExternalBlocked(reason),
        "NotProduced" when !string.IsNullOrWhiteSpace(reason) => new StageNotProduced(reason),
        "NotAttempted" when !string.IsNullOrWhiteSpace(reason) => new StageNotAttempted(reason),
        _ => throw new ArgumentException("Invalid qualification stage outcome/reason combination.")
    };

    internal static (string Outcome, string? Reason) Canonical(StageOutcome stage) => stage switch
    {
        StageValidated => ("Validated", null),
        StageExternalBlocked blocked => ("ExternalBlocked", blocked.Reason),
        StageNotProduced missing => ("NotProduced", missing.Prerequisite),
        StageNotAttempted skipped => ("NotAttempted", skipped.Prerequisite),
#if !SINGPLUS_PREVIEW_LANGUAGE
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
#endif
    };
}
