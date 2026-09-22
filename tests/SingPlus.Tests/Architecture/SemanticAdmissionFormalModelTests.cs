using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class SemanticAdmissionFormalModelTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void GlobalStateIsOnlyAProductViewOfExistingOwnersAndClaims()
    {
        var model = ReadModel();

        Assert.Contains("OwnerStateProduct ==", model, StringComparison.Ordinal);
        Assert.Contains("GlobalState ==", model, StringComparison.Ordinal);
        foreach (var owner in new[] { "capability", "session", "region", "budget", "provider", "operation" })
            Assert.Contains($"{owner}", model, StringComparison.Ordinal);

        Assert.Contains("This is a specification view, never a runtime ledger or authority", model,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalStateManager", model, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalActionsAndInvariantsCoverExactSentryAndAmbiguityBoundary()
    {
        var model = ReadModel();
        string[] requiredSymbols =
        [
            "DriftCapability", "DriftSession", "DriftRegion", "DriftLease", "DriftProvider",
            "ProviderAdmission", "AcceptRefinement", "AcceptRuntimeLegality", "FinalRevalidate",
            "StartSubmitWinner", "RecordPossibleSubmit", "PreSubmitFailure",
            "ProviderAmbiguousFailure", "AtMostOneSubmitWinner", "IrreversibleRequiresAllGates",
            "NoCompensationAfterPossibleSubmit", "AmbiguousPossibleSubmitIsPinned",
        ];

        foreach (var symbol in requiredSymbols)
            Assert.Contains($"{symbol} ==", model, StringComparison.Ordinal);

        var configuration = File.ReadAllText(Path.Combine(FormalDirectory(), "SemanticAdmissionSentry.cfg"));
        foreach (var invariant in new[]
                 {
                     "TypeOK", "AtMostOneSubmitWinner", "IrreversibleRequiresAllGates",
                     "NoCompensationAfterPossibleSubmit", "AmbiguousPossibleSubmitIsPinned",
                 })
            Assert.Contains($"INVARIANT {invariant}", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcreteResourceAdmissionOrdersOneWinnerBeforePossibleSubmitAndProviderCallback()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot,
            "src", "Runtime", "SingPlus.Runtime", "VNext", "ResourceAdmissionProtocol.cs"));

        var winner = source.IndexOf("if (!commit.TryStartSubmit())", StringComparison.Ordinal);
        var durablePossibleSubmit = source.IndexOf("ResourceBudgetRecoveryTransition.PossibleSubmit", winner,
            StringComparison.Ordinal);
        var operationSubmitted = source.IndexOf("ExternalOperations.RecordSubmission", winner,
            StringComparison.Ordinal);
        var callbackBoundary = source.IndexOf("ResourceAdmissionQualificationPoint.BeforeProviderCallback", winner,
            StringComparison.Ordinal);
        var providerCall = source.IndexOf("var provider = providerSubmit();", winner, StringComparison.Ordinal);

        Assert.True(winner >= 0);
        Assert.True(durablePossibleSubmit > winner);
        Assert.True(operationSubmitted > durablePossibleSubmit);
        Assert.True(callbackBoundary > operationSubmitted);
        Assert.True(providerCall > callbackBoundary);
    }

    [Fact]
    public void ConcreteQualificationPointsRemainRepresentedByExecutableRaceTests()
    {
        var testSource = File.ReadAllText(Path.Combine(RepositoryRoot,
            "tests", "SingPlus.Tests", "Runtime", "VNextPhase04CrossOwnerAdmissionTests.cs"));

        foreach (var point in Enum.GetNames<ResourceAdmissionQualificationPoint>())
            Assert.Contains($"ResourceAdmissionQualificationPoint.{point}", testSource, StringComparison.Ordinal);

        Assert.Contains("ProviderFailureAfterPossibleSubmitQuarantinesWithoutRefund", testSource,
            StringComparison.Ordinal);
        Assert.Contains("ProviderCallbackRunsWithoutOwnerLocksAndDuplicateSubmitIsDenied", testSource,
            StringComparison.Ordinal);
    }

    private static string ReadModel() =>
        File.ReadAllText(Path.Combine(FormalDirectory(), "SemanticAdmissionSentry.tla"));

    private static string FormalDirectory() => Path.Combine(RepositoryRoot,
        "docs", "singnextos_semantic_codesign_refactoring", "formal");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
