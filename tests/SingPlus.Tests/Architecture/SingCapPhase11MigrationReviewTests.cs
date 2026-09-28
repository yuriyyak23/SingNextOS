using System.Text.Json;

namespace SingPlus.Tests.Architecture;

public sealed class SingCapPhase11MigrationReviewTests
{
    [Fact]
    public void ConfusedDeputyReviewIsCompleteAndDeterministicForMigratedFamilies()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "docs", "Completed", "SingCap-Refactoring", "roadmap", "P11_CONFUSED_DEPUTY_REVIEW.json")));
        var model = document.RootElement;
        Assert.Equal("singcap-confused-deputy-review-v1", model.GetProperty("schema").GetString());
        var methods = model.GetProperty("methods").EnumerateArray().ToArray();
        Assert.Contains(methods, item => item.GetProperty("method").GetString()!.StartsWith("File.", StringComparison.Ordinal));
        Assert.Contains(methods, item => item.GetProperty("method").GetString()!.StartsWith("Network.", StringComparison.Ordinal));
        Assert.Contains(methods, item => item.GetProperty("method").GetString()!.StartsWith("Process.", StringComparison.Ordinal));
        string[] fields = ["caller", "object", "authority", "targetSubject", "delegation", "externalEffect", "regionChange", "failureClosure", "participants", "revocationPolicy", "lockOrder", "compensation"];
        foreach (var method in methods)
            foreach (var field in fields)
                Assert.False(string.IsNullOrWhiteSpace(method.GetProperty(field).GetString()), $"{method.GetProperty("method").GetString()} lacks {field}");
        Assert.Contains(model.GetProperty("legacySurfaces").EnumerateArray(), item => item.GetString()!.Contains("non-ManagedCap", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
