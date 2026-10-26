using SingPlus.SingCapQualification;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16ControlledTopologyQualificationTests
{
    [Fact]
    public void CurrentHostAffinitySmokeEitherQualifiesExactlyOrExplainsUnavailability()
    {
        VNextControlledTopologyReport report = VNextControlledTopologyQualification.Measure(100);
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

}
