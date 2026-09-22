namespace SingPlus.Contracts;

public enum NumericPrecisionClassV1 : byte
{
    None = 1,
    Binary32 = 2,
    Binary64 = 3,
}

public enum RoundingOverflowClassV1 : byte
{
    None = 1,
    Ieee754NearestEven = 2,
}

public enum AtomicityClassV1 : byte
{
    None = 1,
    WholeOutput = 2,
}

public enum PartialProgressClassV1 : byte
{
    MayExposePartialProgress = 1,
    WithheldUntilComplete = 2,
}

/// <summary>
/// Immutable semantic requirements for provider-local behavior. This value is not
/// admission, runtime legality, effect authority, or permission to replay work.
/// </summary>
public sealed record ProviderBehaviorRequirementsV1(
    ushort Version,
    SemanticRequirementV1<NumericPrecisionClassV1> NumericPrecision,
    SemanticRequirementV1<RoundingOverflowClassV1> RoundingOverflow,
    SemanticRequirementV1<AtomicityClassV1> Atomicity,
    SemanticRequirementV1<DeterminismClassV1> Ordering,
    SemanticRequirementV1<PartialProgressClassV1> PartialProgress,
    SemanticRequirementV1<FailureHandlingClassV1> Failure,
    SemanticRequirementV1<PublicationEnforcementClassV1> VisibilityPublication,
    SemanticRequirementV1<CancellationClassV1> Cancellation,
    SemanticRequirementV1<ReplayClassV1> Replay,
    SemanticRequirementV1<MeasurementClassV1> Measurement)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

/// <summary>Provider behavior claims. Unsupported dimensions carry no proof.</summary>
public sealed record ProviderBehaviorGuaranteesV1(
    ushort Version,
    string ExecutionClass,
    SemanticGuaranteeV1<NumericPrecisionClassV1> NumericPrecision,
    SemanticGuaranteeV1<RoundingOverflowClassV1> RoundingOverflow,
    SemanticGuaranteeV1<AtomicityClassV1> Atomicity,
    SemanticGuaranteeV1<DeterminismClassV1> Ordering,
    SemanticGuaranteeV1<PartialProgressClassV1> PartialProgress,
    SemanticGuaranteeV1<FailureHandlingClassV1> Failure,
    SemanticGuaranteeV1<PublicationEnforcementClassV1> VisibilityPublication,
    SemanticGuaranteeV1<CancellationClassV1> Cancellation,
    SemanticGuaranteeV1<ReplayClassV1> Replay,
    SemanticGuaranteeV1<MeasurementClassV1> Measurement)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

public sealed record ProviderBehaviorRefinementV1(
    ushort Version,
    IReadOnlyList<SemanticRefinementDecisionV1> Decisions)
{
    public const ushort CurrentVersion = 1;
    public bool IsAccepted => Version == CurrentVersion && Decisions is not null &&
                              Decisions.Count == 10 && Decisions.All(static decision => decision.IsAccepted);
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

public static class ProviderBehaviorRefinementEvaluatorV1
{
    public static ProviderBehaviorRefinementV1 Evaluate(
        ProviderBehaviorRequirementsV1 requirements,
        ProviderBehaviorGuaranteesV1 guarantees)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(guarantees);
        if (requirements.Version != ProviderBehaviorRequirementsV1.CurrentVersion ||
            guarantees.Version != ProviderBehaviorGuaranteesV1.CurrentVersion ||
            string.IsNullOrWhiteSpace(guarantees.ExecutionClass) ||
            guarantees.ExecutionClass != guarantees.ExecutionClass.Trim())
            return Invalid();

        return new ProviderBehaviorRefinementV1(ProviderBehaviorRefinementV1.CurrentVersion,
        [
            Compare(requirements.NumericPrecision, guarantees.NumericPrecision, NumericPrecisionRefines),
            Compare(requirements.RoundingOverflow, guarantees.RoundingOverflow, ExactOrNone),
            Compare(requirements.Atomicity, guarantees.Atomicity, AtomicityRefines),
            Compare(requirements.Ordering, guarantees.Ordering, SemanticPartialOrdersV1.DeterminismRefines),
            Compare(requirements.PartialProgress, guarantees.PartialProgress, PartialProgressRefines),
            Compare(requirements.Failure, guarantees.Failure, FailureRefines),
            Compare(requirements.VisibilityPublication, guarantees.VisibilityPublication, SemanticPartialOrdersV1.PublicationRefines),
            Compare(requirements.Cancellation, guarantees.Cancellation, SemanticPartialOrdersV1.CancellationRefines),
            Compare(requirements.Replay, guarantees.Replay, SemanticPartialOrdersV1.ReplayRefines),
            Compare(requirements.Measurement, guarantees.Measurement, ExactOrNone),
        ]);
    }

    private static ProviderBehaviorRefinementV1 Invalid() => new(
        ProviderBehaviorRefinementV1.CurrentVersion,
        Enumerable.Repeat(new SemanticRefinementDecisionV1(1, SemanticRefinementStatusV1.InvalidContract), 10).ToArray());

    private static SemanticRefinementDecisionV1 Compare<T>(SemanticRequirementV1<T> requirement,
        SemanticGuaranteeV1<T> guarantee, Func<T, T, bool> relation) where T : struct, Enum =>
        SemanticRefinementEvaluatorV1.Evaluate(requirement, guarantee, relation);

    private static bool NumericPrecisionRefines(NumericPrecisionClassV1 provided, NumericPrecisionClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, NumericPrecisionClassV1.None) => true,
            (NumericPrecisionClassV1.Binary32, NumericPrecisionClassV1.Binary32) => true,
            (NumericPrecisionClassV1.Binary64, NumericPrecisionClassV1.Binary64) => true,
            _ => false,
        };

    private static bool AtomicityRefines(AtomicityClassV1 provided, AtomicityClassV1 required) =>
        Defined(provided, required) && (required == AtomicityClassV1.None || provided == required);

    private static bool PartialProgressRefines(PartialProgressClassV1 provided, PartialProgressClassV1 required) =>
        Defined(provided, required) && (provided == required ||
            provided == PartialProgressClassV1.WithheldUntilComplete &&
            required == PartialProgressClassV1.MayExposePartialProgress);

    private static bool FailureRefines(FailureHandlingClassV1 provided, FailureHandlingClassV1 required) =>
        Defined(provided, required) && (required == FailureHandlingClassV1.None || provided == required);

    private static bool ExactOrNone<T>(T provided, T required) where T : struct, Enum =>
        Defined(provided, required) && (Convert.ToUInt64(required) == 1 || EqualityComparer<T>.Default.Equals(provided, required));

    private static bool Defined<T>(T left, T right) where T : struct, Enum =>
        Enum.IsDefined(left) && Enum.IsDefined(right);
}
