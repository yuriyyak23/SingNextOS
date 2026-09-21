using System.Text.Json;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16FullSuiteRemediationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CompletedRoadmapConsumersAndSecurityInventoryAreBytePinned()
    {
        var tuplePath = Path.Combine(RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new",
            "P16_FULL_SUITE_REMEDIATION_20260922_TUPLE.json");
        using var tuple = JsonDocument.Parse(File.ReadAllBytes(tuplePath));
        Assert.Equal("P16", tuple.RootElement.GetProperty("phase").GetString());
        Assert.Equal("Open", tuple.RootElement.GetProperty("phaseDisposition").GetString());
        Assert.False(tuple.RootElement.GetProperty("productionQualified").GetBoolean());
        Assert.Empty(tuple.RootElement.GetProperty("enabledFeatureGates").EnumerateArray());
        Assert.Empty(VNextTraceabilityPolicy.ValidateArtifacts(tuple.RootElement.GetProperty("artifacts"),
            path => File.ReadAllBytes(Path.Combine(RepositoryRoot, path))));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
