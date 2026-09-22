using System.Text.Json;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase17QualificationClosureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string RoadmapRoot = Path.Combine(
        RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new");

    [Fact]
    public void MigrationMatrixClosesExactHostContourWithoutRemovingCompatibilityPaths()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RoadmapRoot, "P17_MIGRATION_CUTOVER_20260922.json")));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("P17", root.GetProperty("phase").GetString());
        Assert.Equal("ClosedExactHostContour", root.GetProperty("phaseDisposition").GetString());
        Assert.Equal("RuntimeEnforced", root.GetProperty("claim").GetString());
        Assert.False(root.GetProperty("productionQualified").GetBoolean());
        Assert.Empty(root.GetProperty("enabledFeatureGates").EnumerateArray());
        Assert.Empty(root.GetProperty("isaChangeCategory").EnumerateArray());
        Assert.Empty(root.GetProperty("removedCompatibilityPaths").EnumerateArray());

        var cases = root.GetProperty("compatibilityMatrix").EnumerateArray().ToArray();
        Assert.Equal(7, cases.Length);
        Assert.Contains(cases, item => item.GetProperty("disposition").GetString() == "OrdinaryFallback");
        Assert.Contains(cases, item => item.GetProperty("disposition").GetString() == "ResourcePath");
        Assert.Contains(cases, item => item.GetProperty("disposition").GetString() == "Denied");
        Assert.Contains(cases, item => item.GetProperty("disposition").GetString() == "Quarantined");
        Assert.All(cases, item => Assert.False(string.IsNullOrWhiteSpace(
            item.GetProperty("safetyReason").GetString())));

        foreach (var path in root.GetProperty("retainedCompatibilityPaths").EnumerateArray())
            Assert.True(File.Exists(Path.Combine(RepositoryRoot, path.GetString()!)), path.GetString());
    }

    [Fact]
    public void P17TuplePinsExecutableInputsAndWithholdsUnsupportedClaims()
    {
        using var tuple = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RoadmapRoot, "P17_QUALIFICATION_TUPLE.json")));
        var root = tuple.RootElement;
        Assert.Equal(VNextTraceabilityPolicy.Baseline, root.GetProperty("normativeBaseline").GetString());
        Assert.Equal("P17", root.GetProperty("phase").GetString());
        Assert.Equal("ClosedExactHostContour", root.GetProperty("phaseDisposition").GetString());
        Assert.Equal("RuntimeEnforced", root.GetProperty("claim").GetString());
        Assert.False(root.GetProperty("productionQualified").GetBoolean());
        Assert.Empty(root.GetProperty("enabledFeatureGates").EnumerateArray());
        Assert.Empty(root.GetProperty("isaChangeCategory").EnumerateArray());
        Assert.Empty(VNextTraceabilityPolicy.ValidateArtifacts(root.GetProperty("artifacts"),
            path => File.ReadAllBytes(Path.Combine(RepositoryRoot, path))));
        Assert.All(VNextFeatureGates.Names, gate => Assert.False(VNextFeatureGates.IsEnabled(gate), gate));
    }

    [Fact]
    public void TerminalPhaseDocumentNamesRollbackAndNoLiveConsumerBoundary()
    {
        var phase = File.ReadAllText(Path.Combine(RoadmapRoot,
            "PHASE_VNEXT_17_MIGRATION_CLEANUP_AND_CUTOVER.md"));
        Assert.Contains("CLOSED FOR THE EXACT HOST CONTOUR", phase, StringComparison.Ordinal);
        Assert.Contains("no-live-consumer", phase, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Quarantine", phase, StringComparison.Ordinal);
        Assert.Contains("ProductionQualified remains false", phase, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
