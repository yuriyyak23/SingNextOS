using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class SemanticAdmissionFormalModelTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ConcreteResourceAdmissionOrdersOneWinnerBeforePossibleSubmitAndProviderCallback()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot,
            "src", "Runtime", "SingPlus.Runtime", "VNext", "ResourceAdmissionProtocol.cs"));

        int winner = source.IndexOf("if (!commit.TryStartSubmit())", StringComparison.Ordinal);
        int durablePossibleSubmit = source.IndexOf("ResourceBudgetRecoveryTransition.PossibleSubmit", winner,
            StringComparison.Ordinal);
        int operationSubmitted = source.IndexOf("ExternalOperations.RecordSubmission", winner,
            StringComparison.Ordinal);
        int callbackBoundary = source.IndexOf("ResourceAdmissionQualificationPoint.BeforeProviderCallback", winner,
            StringComparison.Ordinal);
        int dispatchValidation = source.IndexOf("RevalidateResourceDispatch(commit, submitted.Value!)", callbackBoundary,
            StringComparison.Ordinal);
        int helper = source.IndexOf("private KernelResult RevalidateResourceDispatch", StringComparison.Ordinal);
        int ownerRead = source.IndexOf("liveOwner = ExternalOperations.Query", helper, StringComparison.Ordinal);
        int budgetRead = source.IndexOf("liveBudget = Budgets.Query", helper, StringComparison.Ordinal);
        int resourceRead = source.IndexOf("liveResource = ExternalOperations.QueryResourceBinding", helper,
            StringComparison.Ordinal);
        int providerCall = source.IndexOf("providerSubmit(submitted.Value!)", callbackBoundary, StringComparison.Ordinal);

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
        string testSource = File.ReadAllText(Path.Combine(RepositoryRoot,
            "tests", "SingPlus.Tests", "Runtime", "VNextPhase04CrossOwnerAdmissionTests.cs"));

        foreach (string point in Enum.GetNames<ResourceAdmissionQualificationPoint>())
            Assert.Contains($"ResourceAdmissionQualificationPoint.{point}", testSource, StringComparison.Ordinal);

        Assert.Contains("ProviderFailureAfterPossibleSubmitQuarantinesWithoutRefund", testSource,
            StringComparison.Ordinal);
        Assert.Contains("ProviderCallbackRunsWithoutOwnerLocksAndDuplicateSubmitIsDenied", testSource,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
