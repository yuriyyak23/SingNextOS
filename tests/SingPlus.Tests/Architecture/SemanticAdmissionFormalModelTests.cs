using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class SemanticAdmissionFormalModelTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

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
        var dispatchValidation = source.IndexOf("RevalidateResourceDispatch(commit, submitted.Value!)", callbackBoundary,
            StringComparison.Ordinal);
        var helper = source.IndexOf("private KernelResult RevalidateResourceDispatch", StringComparison.Ordinal);
        var ownerRead = source.IndexOf("var liveOwner = ExternalOperations.Query", helper, StringComparison.Ordinal);
        var budgetRead = source.IndexOf("var liveBudget = Budgets.Query", helper, StringComparison.Ordinal);
        var resourceRead = source.IndexOf("var liveResource = ExternalOperations.QueryResourceBinding", helper,
            StringComparison.Ordinal);
        var providerCall = source.IndexOf("providerSubmit(submitted.Value!)", callbackBoundary, StringComparison.Ordinal);

        Assert.True(winner >= 0);
        Assert.True(durablePossibleSubmit > winner);
        Assert.True(operationSubmitted > durablePossibleSubmit);
        Assert.True(callbackBoundary > operationSubmitted);
        Assert.True(dispatchValidation > callbackBoundary);
        Assert.True(providerCall > dispatchValidation);
        Assert.True(helper >= 0);
        Assert.True(ownerRead > helper);
        Assert.True(budgetRead > ownerRead);
        Assert.True(resourceRead > budgetRead);
        Assert.Contains("liveOwner.Value.Binding == binding", source[helper..]);
        Assert.Contains("liveResource.Value.State == ExternalResourceBindingState.Consuming", source[helper..]);
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
