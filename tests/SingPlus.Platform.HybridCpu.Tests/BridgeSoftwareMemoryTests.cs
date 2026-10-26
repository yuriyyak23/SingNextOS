using SingPlus.Diagnostics;
namespace SingPlus.Platform.HybridCpu.Tests;
public sealed class BridgeSoftwareMemoryTests
{
    public static IEnumerable<object[]> Scenarios=>BridgeSoftwareMemoryOracle.Scenarios.Select(name=>new object[]{name});
    [Theory][MemberData(nameof(Scenarios))]
    public void OriginalBridgeInitializedLoadAndIndependentCleanup(string scenario)=>BridgeSoftwareMemoryOracle.Run(scenario);
}
