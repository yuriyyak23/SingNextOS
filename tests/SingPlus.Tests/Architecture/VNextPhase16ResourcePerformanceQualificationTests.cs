using SingPlus.SingCapQualification;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16ResourcePerformanceQualificationTests
{
    [Fact]
    public async Task MatrixExecutesLiveOwnerContentionAndQuarantinePressureWithoutUnexpectedOutcomes()
    {
        VNextResourcePerformanceReport report = await VNextResourcePerformanceQualification.MeasureAsync(
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

        VNextResourceMeasurement[] denials = report.Measurements.Where(item => item.Operation == "reserve-denied-by-quarantined-external-effect").ToArray();
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

}
