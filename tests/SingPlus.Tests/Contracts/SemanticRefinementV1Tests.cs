using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class SemanticRefinementV1Tests
{
    [Fact]
    public void MandatoryUnsupportedAndMismatchFailClosedWhileAdvisoryRemainsNonAuthoritative()
    {
        var mandatory = Requirement(IsolationClassV1.DomainSeparated, SemanticRequirementStrengthV1.Mandatory);
        var advisory = Requirement(IsolationClassV1.DomainSeparated, SemanticRequirementStrengthV1.Advisory);

        var unsupported = SemanticRefinementEvaluatorV1.Evaluate(mandatory,
            Guarantee(IsolationClassV1.None, SemanticGuaranteeSupportV1.Unsupported),
            SemanticPartialOrdersV1.IsolationRefines);
        var mismatch = SemanticRefinementEvaluatorV1.Evaluate(mandatory,
            Guarantee(IsolationClassV1.None), SemanticPartialOrdersV1.IsolationRefines);
        var advisoryMismatch = SemanticRefinementEvaluatorV1.Evaluate(advisory,
            Guarantee(IsolationClassV1.None), SemanticPartialOrdersV1.IsolationRefines);

        Assert.Equal(SemanticRefinementStatusV1.MandatoryUnsupported, unsupported.Status);
        Assert.False(unsupported.IsAccepted);
        Assert.Equal(SemanticRefinementStatusV1.ClassMismatch, mismatch.Status);
        Assert.False(mismatch.IsAccepted);
        Assert.Equal(SemanticRefinementStatusV1.AdvisoryUnmet, advisoryMismatch.Status);
        Assert.True(advisoryMismatch.IsAccepted);
        Assert.False(advisoryMismatch.IsSatisfied);
        Assert.False(advisoryMismatch.AuthorizesExecution);
        Assert.False(advisoryMismatch.AuthorizesEffect);
        Assert.False(mandatory.AuthorizesExecution);
        Assert.False(Guarantee(IsolationClassV1.ConfidentialDomain).AuthorizesEffect);
    }

    [Fact]
    public void UnknownVersionStrengthSupportAndClassAlwaysFailClosed()
    {
        var validRequirement = Requirement(IsolationClassV1.DomainSeparated, SemanticRequirementStrengthV1.Advisory);
        var validGuarantee = Guarantee(IsolationClassV1.ConfidentialDomain);

        var decisions = new[]
        {
            SemanticRefinementEvaluatorV1.Evaluate(validRequirement with { Version = 2 }, validGuarantee,
                SemanticPartialOrdersV1.IsolationRefines),
            SemanticRefinementEvaluatorV1.Evaluate(validRequirement with { Strength = (SemanticRequirementStrengthV1)0 }, validGuarantee,
                SemanticPartialOrdersV1.IsolationRefines),
            SemanticRefinementEvaluatorV1.Evaluate(validRequirement, validGuarantee with { Version = 2 },
                SemanticPartialOrdersV1.IsolationRefines),
            SemanticRefinementEvaluatorV1.Evaluate(validRequirement, validGuarantee with { Support = (SemanticGuaranteeSupportV1)0 },
                SemanticPartialOrdersV1.IsolationRefines),
            SemanticRefinementEvaluatorV1.Evaluate(validRequirement with { RequiredClass = (IsolationClassV1)0 }, validGuarantee,
                SemanticPartialOrdersV1.IsolationRefines),
            SemanticRefinementEvaluatorV1.Evaluate(validRequirement, validGuarantee with { ProvidedClass = (IsolationClassV1)byte.MaxValue },
                SemanticPartialOrdersV1.IsolationRefines),
        };

        Assert.All(decisions, decision =>
        {
            Assert.Equal(SemanticRefinementStatusV1.InvalidContract, decision.Status);
            Assert.False(decision.IsAccepted);
        });
    }

    [Fact]
    public void EveryDimensionIsReflexiveForDefinedClassesAndRejectsUnknownValues()
    {
        AssertReflexive(Enum.GetValues<IsolationClassV1>(), SemanticPartialOrdersV1.IsolationRefines);
        AssertReflexive(Enum.GetValues<PublicationEnforcementClassV1>(), SemanticPartialOrdersV1.PublicationRefines);
        AssertReflexive(Enum.GetValues<ReplayClassV1>(), SemanticPartialOrdersV1.ReplayRefines);
        AssertReflexive(Enum.GetValues<DeterminismClassV1>(), SemanticPartialOrdersV1.DeterminismRefines);
        AssertReflexive(Enum.GetValues<CancellationClassV1>(), SemanticPartialOrdersV1.CancellationRefines);
        AssertReflexive(Enum.GetValues<ContainmentClassV1>(), SemanticPartialOrdersV1.ContainmentRefines);
        AssertReflexive(Enum.GetValues<LocalityClassV1>(), SemanticPartialOrdersV1.LocalityRefines);
        AssertReflexive(Enum.GetValues<ResourceAssuranceV1>(), SemanticPartialOrdersV1.ResourceAssuranceRefines);

        Assert.False(SemanticPartialOrdersV1.ReplayRefines((ReplayClassV1)0, ReplayClassV1.None));
        Assert.False(SemanticPartialOrdersV1.ReplayRefines(ReplayClassV1.None, (ReplayClassV1)255));
    }

    [Fact]
    public void EveryDimensionIsTransitiveAcrossExhaustiveDefinedStateSpace()
    {
        AssertTransitive(Enum.GetValues<IsolationClassV1>(), SemanticPartialOrdersV1.IsolationRefines);
        AssertTransitive(Enum.GetValues<PublicationEnforcementClassV1>(), SemanticPartialOrdersV1.PublicationRefines);
        AssertTransitive(Enum.GetValues<ReplayClassV1>(), SemanticPartialOrdersV1.ReplayRefines);
        AssertTransitive(Enum.GetValues<DeterminismClassV1>(), SemanticPartialOrdersV1.DeterminismRefines);
        AssertTransitive(Enum.GetValues<CancellationClassV1>(), SemanticPartialOrdersV1.CancellationRefines);
        AssertTransitive(Enum.GetValues<ContainmentClassV1>(), SemanticPartialOrdersV1.ContainmentRefines);
        AssertTransitive(Enum.GetValues<LocalityClassV1>(), SemanticPartialOrdersV1.LocalityRefines);
        AssertTransitive(Enum.GetValues<ResourceAssuranceV1>(), SemanticPartialOrdersV1.ResourceAssuranceRefines);
    }

    [Fact]
    public void IncomparableReplayAndLocalityBranchesCannotBeLaundered()
    {
        Assert.False(SemanticPartialOrdersV1.ReplayRefines(ReplayClassV1.Idempotent, ReplayClassV1.Deduplicated));
        Assert.False(SemanticPartialOrdersV1.ReplayRefines(ReplayClassV1.Deduplicated, ReplayClassV1.Idempotent));
        Assert.False(SemanticPartialOrdersV1.LocalityRefines(LocalityClassV1.ProviderLocal, LocalityClassV1.ConsumerDomainLocal));
        Assert.False(SemanticPartialOrdersV1.LocalityRefines(LocalityClassV1.ConsumerDomainLocal, LocalityClassV1.ProviderLocal));
    }

    [Fact]
    public void StrongerGuaranteesMonotonicallySatisfyWeakerRequirementsWithoutGrantingAuthority()
    {
        var decision = SemanticRefinementEvaluatorV1.Evaluate(
            Requirement(PublicationEnforcementClassV1.VisibilityFence, SemanticRequirementStrengthV1.Mandatory),
            Guarantee(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
            SemanticPartialOrdersV1.PublicationRefines);

        Assert.Equal(SemanticRefinementStatusV1.Refines, decision.Status);
        Assert.True(decision.IsAccepted);
        Assert.True(decision.IsSatisfied);
        Assert.False(decision.AuthorizesExecution);
        Assert.False(decision.AuthorizesEffect);
    }

    private static SemanticRequirementV1<T> Requirement<T>(T value, SemanticRequirementStrengthV1 strength)
        where T : struct, Enum => new(SemanticRequirementV1<T>.CurrentVersion, strength, value);

    private static SemanticGuaranteeV1<T> Guarantee<T>(
        T value, SemanticGuaranteeSupportV1 support = SemanticGuaranteeSupportV1.Supported)
        where T : struct, Enum => new(SemanticGuaranteeV1<T>.CurrentVersion, support, value);

    private static void AssertReflexive<T>(IReadOnlyList<T> values, Func<T, T, bool> relation)
        where T : struct, Enum => Assert.All(values, value => Assert.True(relation(value, value)));

    private static void AssertTransitive<T>(IReadOnlyList<T> values, Func<T, T, bool> relation)
        where T : struct, Enum
    {
        foreach (var stronger in values)
        foreach (var middle in values)
        foreach (var weaker in values)
            if (relation(stronger, middle) && relation(middle, weaker))
                Assert.True(relation(stronger, weaker), $"Non-transitive relation: {stronger} >= {middle} >= {weaker}.");
    }
}
