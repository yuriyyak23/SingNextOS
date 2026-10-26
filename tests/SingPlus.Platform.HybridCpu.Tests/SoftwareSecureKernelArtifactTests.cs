using SingPlus.Diagnostics;
namespace SingPlus.Platform.HybridCpu.Tests;
public sealed class SoftwareSecureKernelArtifactTests
{
    public static IEnumerable<object[]> Schedules => KernelArtifactScenarioOracle.Schedules.Select(s=>new object[]{s});
    [Theory] [MemberData(nameof(Schedules))]
    public void OriginalArtifactEntry_UsesSemanticOwnersRetirePublicationAndCleanup(string schedule) => KernelArtifactScenarioOracle.Run(schedule);
}