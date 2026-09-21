using System.Text.Json;
using SingPlus.SingCapQualification;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16ResourcePerformanceQualificationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string RoadmapRoot = Path.Combine(RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new");

    [Fact]
    public async Task MatrixExecutesLiveOwnerContentionAndQuarantinePressureWithoutUnexpectedOutcomes()
    {
        var report = await VNextResourcePerformanceQualification.MeasureAsync(
            new(IterationsPerWorker: 8, WorkerCounts: [1, 2]));

        Assert.Equal("SingNextOS.VNext.ResourceBudgetPerformance.v1", report.Schema);
        Assert.Equal(8, report.Measurements.Count);
        Assert.All(report.Measurements, measurement =>
        {
            Assert.Equal(0, measurement.UnexpectedOutcomes);
            Assert.Equal(measurement.Attempts, measurement.Successes + measurement.ExpectedFailures);
            Assert.True(measurement.P99Nanoseconds > 0);
            Assert.True(measurement.ThroughputPerSecond > 0);
        });

        var denials = report.Measurements.Where(item => item.Operation == "reserve-denied-by-quarantined-external-effect").ToArray();
        Assert.Equal(2, denials.Length);
        Assert.All(denials, item =>
        {
            Assert.Equal(item.Attempts, item.ExpectedFailures);
            Assert.Equal(0, item.Successes);
            Assert.Equal(1UL, item.FinalUsedAmount);
            Assert.Equal(1, item.FinalLiveLeases);
            Assert.Equal("PinnedByExternalEffect", item.FinalPressure);
        });

        Assert.Contains(report.Exclusions, exclusion => exclusion.Contains("No SMT-topology", StringComparison.Ordinal));
        Assert.Contains("AccountingOnly", report.Claim, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductionQualified", report.Claim, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 65)]
    public async Task InvalidMeasurementBoundsFailClosed(int iterations, int workers)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            VNextResourcePerformanceQualification.MeasureAsync(new(iterations, [workers])));
    }

    [Fact]
    public void RecordedReleaseMatrixAndTupleRemainExactAndDoNotOverclaim()
    {
        using var reportDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RoadmapRoot,
            "P16_RESOURCE_BUDGET_PERFORMANCE_20260922.json")));
        var report = reportDocument.RootElement;
        Assert.Equal("SingNextOS.VNext.ResourceBudgetPerformance.v1", report.GetProperty("Schema").GetString());
        Assert.Equal("JIT-host", report.GetProperty("Environment").GetProperty("ExecutionMode").GetString());
        Assert.Equal(20, report.GetProperty("Measurements").GetArrayLength());
        Assert.All(report.GetProperty("Measurements").EnumerateArray(), measurement =>
        {
            Assert.Equal(0, measurement.GetProperty("UnexpectedOutcomes").GetInt32());
            Assert.Equal(measurement.GetProperty("Attempts").GetInt32(),
                measurement.GetProperty("Successes").GetInt32() + measurement.GetProperty("ExpectedFailures").GetInt32());
        });
        Assert.Contains("AccountingOnly", report.GetProperty("Claim").GetString(), StringComparison.Ordinal);
        Assert.Contains(report.GetProperty("Exclusions").EnumerateArray(), item =>
            item.GetString()!.Contains("No SMT-topology", StringComparison.Ordinal));

        using var tupleDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RoadmapRoot,
            "P16_RESOURCE_BUDGET_PERFORMANCE_20260922_TUPLE.json")));
        Assert.Empty(VNextTraceabilityPolicy.ValidateArtifacts(tupleDocument.RootElement.GetProperty("artifacts"),
            path => File.ReadAllBytes(Path.Combine(RepositoryRoot, path))));
        Assert.False(tupleDocument.RootElement.GetProperty("productionQualified").GetBoolean());
        Assert.Equal("Open", tupleDocument.RootElement.GetProperty("phaseDisposition").GetString());
        Assert.Empty(tupleDocument.RootElement.GetProperty("enabledFeatureGates").EnumerateArray());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SingNextOS.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
