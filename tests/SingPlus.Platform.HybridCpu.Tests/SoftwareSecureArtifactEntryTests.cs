using SingPlus.Diagnostics;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class SoftwareSecureArtifactEntryTests
{
    public static IEnumerable<object[]> Schedules => SoftwareSecureArtifactOracle.Schedules.Select(s => new object[] { s });
    [Theory]
    [MemberData(nameof(Schedules))]
    public void PublicArtifactEntry_UsesOriginalNativeOwnersAndIndependentCleanup(string schedule) => SoftwareSecureArtifactOracle.Run(schedule);
}
