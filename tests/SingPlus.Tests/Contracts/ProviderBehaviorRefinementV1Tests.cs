using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class ProviderBehaviorRefinementV1Tests
{
    [Fact]
    public void ExactNumericBehaviorRefinesWithoutAuthorizingExecution()
    {
        var result = ProviderBehaviorRefinementEvaluatorV1.Evaluate(Requirements(), Guarantees());

        Assert.True(result.IsAccepted);
        Assert.All(result.Decisions, decision => Assert.True(decision.IsSatisfied));
        Assert.False(result.AuthorizesExecution);
        Assert.False(result.AuthorizesEffect);
    }

    [Fact]
    public void RoundingPartialProgressAndPrecisionCannotBeLaundered()
    {
        var rounding = ProviderBehaviorRefinementEvaluatorV1.Evaluate(Requirements(),
            Guarantees() with { RoundingOverflow = Supported(RoundingOverflowClassV1.None) });
        var partial = ProviderBehaviorRefinementEvaluatorV1.Evaluate(Requirements(),
            Guarantees() with { PartialProgress = Supported(PartialProgressClassV1.MayExposePartialProgress) });
        var precision = ProviderBehaviorRefinementEvaluatorV1.Evaluate(Requirements(),
            Guarantees() with { NumericPrecision = Supported(NumericPrecisionClassV1.Binary64) });

        Assert.All(new[] { rounding, partial, precision }, result => Assert.False(result.IsAccepted));
    }

    [Fact]
    public void UnsupportedMandatoryReplayIsolationAdjacentSemanticsFailClosed()
    {
        var result = ProviderBehaviorRefinementEvaluatorV1.Evaluate(Requirements(),
            Guarantees() with
            {
                Replay = Unsupported(ReplayClassV1.None),
                Ordering = Unsupported(DeterminismClassV1.None),
            });

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Decisions, decision =>
            decision.Status == SemanticRefinementStatusV1.MandatoryUnsupported);
    }

    [Fact]
    public void UnknownVersionEnumAndNoncanonicalExecutionClassFailClosed()
    {
        var unknownVersion = ProviderBehaviorRefinementEvaluatorV1.Evaluate(
            Requirements() with { Version = 2 }, Guarantees());
        var unknownClass = ProviderBehaviorRefinementEvaluatorV1.Evaluate(Requirements(),
            Guarantees() with { Atomicity = Supported((AtomicityClassV1)255) });
        var noncanonicalExecutionClass = ProviderBehaviorRefinementEvaluatorV1.Evaluate(Requirements(),
            Guarantees() with { ExecutionClass = " matrix-tile " });

        Assert.All(new[] { unknownVersion, unknownClass, noncanonicalExecutionClass }, result =>
        {
            Assert.False(result.IsAccepted);
            Assert.Contains(result.Decisions, decision =>
                decision.Status == SemanticRefinementStatusV1.InvalidContract);
        });
    }

    private static ProviderBehaviorRequirementsV1 Requirements() => new(1,
        Required(NumericPrecisionClassV1.Binary32),
        Required(RoundingOverflowClassV1.Ieee754NearestEven),
        Required(AtomicityClassV1.WholeOutput),
        Required(DeterminismClassV1.StableOrdering),
        Required(PartialProgressClassV1.WithheldUntilComplete),
        Required(FailureHandlingClassV1.FailClosedExactGeneration),
        Required(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
        Required(CancellationClassV1.ExactAcknowledgement),
        Required(ReplayClassV1.Deduplicated),
        Required(MeasurementClassV1.BoundUsageEvidence));

    private static ProviderBehaviorGuaranteesV1 Guarantees() => new(1, "matrix-tile/binary32",
        Supported(NumericPrecisionClassV1.Binary32),
        Supported(RoundingOverflowClassV1.Ieee754NearestEven),
        Supported(AtomicityClassV1.WholeOutput),
        Supported(DeterminismClassV1.BitExact),
        Supported(PartialProgressClassV1.WithheldUntilComplete),
        Supported(FailureHandlingClassV1.FailClosedExactGeneration),
        Supported(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
        Supported(CancellationClassV1.ExactAcknowledgement),
        Supported(ReplayClassV1.Deduplicated),
        Supported(MeasurementClassV1.BoundUsageEvidence));

    private static SemanticRequirementV1<T> Required<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Mandatory, value);

    private static SemanticGuaranteeV1<T> Supported<T>(T value) where T : struct, Enum =>
        new(1, SemanticGuaranteeSupportV1.Supported, value);

    private static SemanticGuaranteeV1<T> Unsupported<T>(T value) where T : struct, Enum =>
        new(1, SemanticGuaranteeSupportV1.Unsupported, value);
}
