using SingPlus.Diagnostics;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class PlatformSoftwareMemoryTests
{
    public static IEnumerable<object[]> Scenarios => PlatformSoftwareMemoryOracle.Scenarios.Select(name => new object[] {name});
    [Theory] [MemberData(nameof(Scenarios))]
    public void OriginalPlatformInitializedSourceLoadAndCleanup(string scenario) => PlatformSoftwareMemoryOracle.Run(scenario);
}
