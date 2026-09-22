using System.Text.Json;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16BlockerRemediationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string RoadmapRoot = Path.Combine(
        RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new");

    [Fact]
    public void OrderedMatrixRecordsExactCompletedAndFutureGatedBoundaries()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RoadmapRoot, "P16_BLOCKER_REMEDIATION_20260922.json")));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("P16", root.GetProperty("phase").GetString());
        Assert.Equal("ClosedExactHostContour", root.GetProperty("phaseDisposition").GetString());
        Assert.False(root.GetProperty("productionQualified").GetBoolean());
        Assert.True(root.GetProperty("p17MayStart").GetBoolean());
        Assert.False(root.GetProperty("hybridCpuQualified").GetBoolean());
        Assert.False(root.GetProperty("nativeAotQualified").GetBoolean());
        Assert.Empty(root.GetProperty("enabledByDefault").EnumerateArray());
        Assert.Empty(root.GetProperty("isaChangeCategory").EnumerateArray());

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 8), items.Select(item => item.GetProperty("order").GetInt32()));
        Assert.All(items.Take(6), item =>
            Assert.StartsWith("Complete", item.GetProperty("disposition").GetString(), StringComparison.Ordinal));
        Assert.All(items.Skip(6), item =>
        {
            Assert.Equal("FutureGated", item.GetProperty("disposition").GetString());
            Assert.Equal("OFF", item.GetProperty("gateDefault").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("missingPrerequisite").GetString()));
        });
        Assert.DoesNotContain(items, item => item.GetProperty("claim").GetString() is
            "EnforcedUpperBound" or "GuaranteedReservation" or "ProductionQualified");
    }

    [Fact]
    public void RemediationTuplePinsExecutableEvidenceAndKeepsEveryGateOff()
    {
        using var tuple = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RoadmapRoot, "P16_BLOCKER_REMEDIATION_20260922_TUPLE.json")));
        var root = tuple.RootElement;
        Assert.Equal(VNextTraceabilityPolicy.Baseline, root.GetProperty("normativeBaseline").GetString());
        Assert.Equal("ExecutableAdapter", root.GetProperty("claim").GetString());
        Assert.False(root.GetProperty("productionQualified").GetBoolean());
        Assert.True(root.GetProperty("p17MayStart").GetBoolean());
        Assert.Equal("ClosedExactHostContour", root.GetProperty("phaseDisposition").GetString());
        Assert.Empty(root.GetProperty("enabledFeatureGates").EnumerateArray());
        Assert.Empty(root.GetProperty("isaChangeCategory").EnumerateArray());
        Assert.Empty(VNextTraceabilityPolicy.ValidateArtifacts(root.GetProperty("artifacts"),
            path => File.ReadAllBytes(Path.Combine(RepositoryRoot, path))));

        Assert.All(VNextFeatureGates.Names, gate => Assert.False(VNextFeatureGates.IsEnabled(gate), gate));
        Assert.False(VNextFeatureGates.IsEnabled("FG-VNX-TEMPORAL-UPPER-BOUND"));
        Assert.False(VNextFeatureGates.IsEnabled("FG-VNX-GUARANTEED-RESERVATION"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
