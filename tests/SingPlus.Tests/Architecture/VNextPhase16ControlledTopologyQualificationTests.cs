using System.Text.Json;
using SingPlus.SingCapQualification;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16ControlledTopologyQualificationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void PinnedReportUsesPhysicalCoreMasksAndExactAffinityScenarios()
    {
        var path = Path.Combine(RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new",
            "P16_CONTROLLED_TOPOLOGY_20260922.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Assert.Equal("SingNextOS.VNext.ControlledTopology.v1", root.GetProperty("Schema").GetString());
        Assert.True(root.GetProperty("Qualified").GetBoolean());
        Assert.Equal(8, root.GetProperty("PhysicalCoreMasks").GetArrayLength());
        var measurements = root.GetProperty("Measurements").EnumerateArray().ToArray();
        Assert.Equal(["same-core-smt-siblings", "separate-physical-cores"],
            measurements.Select(item => item.GetProperty("Scenario").GetString()));
        Assert.All(measurements, item =>
        {
            Assert.Equal(0UL, item.GetProperty("FinalUsedAmount").GetUInt64());
            Assert.All(item.GetProperty("WorkerErrors").EnumerateArray(), error => Assert.Equal(0, error.GetInt32()));
            Assert.True(item.GetProperty("TotalOperations").GetInt32() > 0);
        });
        Assert.Contains("AccountingOnly", root.GetProperty("Claim").GetString(), StringComparison.Ordinal);
        Assert.Contains(root.GetProperty("Exclusions").EnumerateArray(), item =>
            item.GetString()!.Contains("GuaranteedReservation", StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentHostAffinitySmokeEitherQualifiesExactlyOrExplainsUnavailability()
    {
        var report = VNextControlledTopologyQualification.Measure(100);
        if (!report.Qualified)
        {
            Assert.Empty(report.Measurements);
            Assert.Contains("No topology claim was promoted", report.Exclusions);
            return;
        }
        Assert.Equal(2, report.Measurements.Count);
        Assert.All(report.Measurements, measurement =>
        {
            Assert.Equal(0UL, measurement.FinalUsedAmount);
            Assert.All(measurement.WorkerErrors, error => Assert.Equal(0, error));
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
