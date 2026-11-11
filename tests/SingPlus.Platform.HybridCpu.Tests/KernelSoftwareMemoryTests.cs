using SingPlus.Diagnostics;
namespace SingPlus.Platform.HybridCpu.Tests;
public sealed class KernelSoftwareMemoryTests
{
    public static IEnumerable<object[]> Scenarios => KernelSoftwareMemoryOracle.Scenarios.Select(name => new object[] { name });
    [Theory][MemberData(nameof(Scenarios))]
    public void OriginalSemanticInitializedLoadAndSeparatePublication(string scenario) => KernelSoftwareMemoryOracle.Run(scenario);
}
