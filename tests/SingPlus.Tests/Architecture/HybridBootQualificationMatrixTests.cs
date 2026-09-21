using System.Text.Json;

namespace SingPlus.Tests.Architecture;

public sealed class HybridBootQualificationMatrixTests
{
    [Fact]
    public void MatrixCoversT001ThroughT050WithoutUnsupportedClaims()
    {
        var root = FindRoot();
        var roadmap = Path.Combine(root, "docs", "Completed", "HybridCPU-v2-Boot-Reset-CXL-Boot-ABI-Roadmap");
        var path = Path.Combine(roadmap, "QUALIFICATION_CLAIM_MATRIX.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rootElement = document.RootElement;
        Assert.Equal("ExcludedByUser", rootElement.GetProperty("qemuDirection").GetString());
        Assert.False(rootElement.GetProperty("hardwareExecuted").GetBoolean());

        var scenarios = rootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(50, scenarios.Length);
        Assert.Equal(Enumerable.Range(1, 50).Select(static i => $"T{i:000}"),
            scenarios.Select(static item => item.GetProperty("id").GetString()));
        Assert.All(scenarios, static item =>
        {
            var claim = item.GetProperty("claimLevel").GetString();
            Assert.Contains(claim, new[] { "ContractOnly", "ModelValidated", "AdapterQualified", "FutureGated" });
            Assert.NotEqual("HardwareValidated", claim);
            Assert.NotEqual("QemuProtocolValidated", claim);
            Assert.NotEmpty(item.GetProperty("reason").GetString()!);
            Assert.NotEmpty(item.GetProperty("gate").GetString()!);
            Assert.NotEmpty(item.GetProperty("tests").EnumerateArray());
            var evidence = item.GetProperty("evidence").GetString();
            Assert.False(string.IsNullOrWhiteSpace(evidence));
        });

        Assert.All(scenarios, item =>
        {
            var evidence = item.GetProperty("evidence").GetString()!;
            Assert.True(File.Exists(Path.Combine(roadmap, evidence)),
                $"{item.GetProperty("id").GetString()} evidence path does not exist: {evidence}");
        });
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
