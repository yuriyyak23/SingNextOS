using SingPlus.Diagnostics;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class SoftwareSecureMemoryTests
{
    public static IEnumerable<object[]> Scenarios => SoftwareSecureMemoryOracle.Scenarios.Select(name => new object[] { name });
    [Theory] [MemberData(nameof(Scenarios))]
    public void OriginalInitializedSourceLoadAndIndependentCleanup(string scenario) => SoftwareSecureMemoryOracle.Run(scenario);
}
