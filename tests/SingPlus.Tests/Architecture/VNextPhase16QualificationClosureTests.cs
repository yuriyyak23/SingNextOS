using System.Security.Cryptography;
using System.Text.Json;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16QualificationClosureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string RoadmapRoot = Path.Combine(RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new");

    [Fact]
    public void MachineReadableTraceabilityCoversEveryInvariantAndReferencesLiveEvidence()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RoadmapRoot, "VNEXT_TRACEABILITY.json")));
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 28).Select(index => $"VNX-{index:000}"),
            rows.Select(row => row.GetProperty("id").GetString()));
        Assert.Equal(28, rows.Select(row => row.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());
        foreach (var row in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("owner").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("implementation").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("tests").GetString()));
            Assert.True(File.Exists(Path.Combine(RoadmapRoot, row.GetProperty("evidence").GetString()!)));
            Assert.True(File.Exists(Path.Combine(RoadmapRoot, row.GetProperty("tuple").GetString()!)));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("exclusions").GetString()));
        }

        var markdown = File.ReadAllText(Path.Combine(RoadmapRoot, "TRACEABILITY_MATRIX.md"));
        Assert.All(rows, row => Assert.Contains($"| {row.GetProperty("id").GetString()} |", markdown, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPhaseTupleIsPinnedAndNoTupleClaimsProductionQualification()
    {
        for (var phase = 0; phase <= 16; phase++)
        {
            var path = Path.Combine(RoadmapRoot, $"P{phase:00}_QUALIFICATION_TUPLE.json");
            Assert.True(File.Exists(path));
            using var tuple = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal($"P{phase:00}", tuple.RootElement.GetProperty("phase").GetString());
            Assert.Equal("6227ea7cf258ef6ffce52001d4d2ffee07355b35",
                tuple.RootElement.GetProperty("normativeBaseline").GetString());
            Assert.NotEqual("ProductionQualified", tuple.RootElement.GetProperty("claim").GetString());
        }
    }

    [Fact]
    public void ManifestHashesAllQualificationInputsAndEveryGateRemainsRollbackSafeOff()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RoadmapRoot, "MANIFEST.json")));
        var entries = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
        foreach (var entry in entries)
        {
            var path = Path.Combine(RoadmapRoot, entry.GetProperty("file").GetString()!);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), bytes.LongLength);
            Assert.Equal(entry.GetProperty("sha256").GetString(),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        Assert.All(VNextFeatureGates.Names, name => Assert.False(VNextFeatureGates.IsEnabled(name)));
    }

    [Fact]
    public void ClaimClosureForbidsProofTransferAndKeepsIsaChangeCategoryEmpty()
    {
        var tuple = File.ReadAllText(Path.Combine(RoadmapRoot, "P16_QUALIFICATION_TUPLE.json"));
        foreach (var exclusion in new[]
                 {
                     "host does not qualify HybridCPU", "one provider does not qualify all providers",
                     "JIT does not qualify NativeAOT", "time does not qualify throughput or occupancy",
                     "upper bound does not qualify guaranteed reservation", "ISA-change category is empty",
                 })
            Assert.Contains(exclusion, tuple, StringComparison.Ordinal);
        Assert.DoesNotContain("\"claim\": \"ProductionQualified\"", tuple, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
