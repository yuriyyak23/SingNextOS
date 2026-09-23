using System.Text.Json;
using System.Security.Cryptography;
using Xunit;

namespace SingPlus.Tests.Boot;

public sealed class DirectBootQualificationArtifactTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void P15_00_BaselineKeepsObservedAndQualifiedHybridCpuRevisionsDistinct()
    {
        using var baseline = Read("DirectSingNextBootBaselineV2.json");
        var root = baseline.RootElement;

        Assert.Equal("direct-singnext-boot-baseline-v2", root.GetProperty("schema").GetString());
        var hybridCpu = root.GetProperty("repositories").GetProperty("hybridCpu");
        var observed = hybridCpu.GetProperty("observedHead").GetString();
        var qualified = hybridCpu.GetProperty("qualifiedRevision").GetString();
        Assert.Equal("794c4a53494f503855ac8cf209efab23fde083b2", observed);
        Assert.Equal("9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9", qualified);
        Assert.NotEqual(observed, qualified);
        Assert.False(hybridCpu.GetProperty("revisionEquivalentToQualified").GetBoolean());
        Assert.Equal("ContractOnly", root.GetProperty("maximumSupportedClaim").GetString());
    }

    [Fact]
    public void P15_12_QualificationFaultMatrixHasStableCompleteScenarioRecords()
    {
        using var qualification = Read("DirectSingNextBootQualificationV1.json");
        var root = qualification.RootElement;
        Assert.Equal("direct-singnext-boot-qualification-v1", root.GetProperty("schema").GetString());

        var records = root.GetProperty("faultMatrix").EnumerateArray().ToArray();
        Assert.Equal(records.Length, records.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
        for (var number = 1; number <= 26; number++)
        {
            var id = $"P15-F{number:00}";
            var record = Assert.Single(records, item => item.GetProperty("id").GetString() == id);
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("owner").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("environment").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("terminalState").GetString()));
            var status = record.GetProperty("status").GetString();
            Assert.Contains(status, new[] { "Passed", "ExternalBlocked", "Unavailable" });
            if (status == "Passed")
                Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("test").GetString()));
        }

        Assert.Equal("AdapterQualified", root.GetProperty("maximumSupportedClaim").GetString());
        Assert.Contains("local deterministic", root.GetProperty("maximumSupportedClaimScope").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("hybridCpu").GetProperty("requalified").GetBoolean());
    }

    [Fact]
    public void P15_14_RecordedBinaryAndRoadmapHashesMatchExactBytes()
    {
        using var qualification = Read("DirectSingNextBootQualificationV1.json");
        foreach (var artifact in qualification.RootElement.GetProperty("artifactHashes").EnumerateArray())
        {
            var relative = artifact.GetProperty("path").GetString()!;
            Assert.Equal(artifact.GetProperty("sha256").GetString(), Hash(relative), ignoreCase: true);
        }

        var roadmapRoot = Path.Combine(Root, "docs", "Completed", "P15_Direct_SingNext_Roadmap_v3");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(roadmapRoot, "MANIFEST.json")));
        foreach (var checksum in manifest.RootElement.GetProperty("checksums").EnumerateObject())
        {
            var expected = checksum.Value.GetString()!;
            Assert.StartsWith("sha256:", expected, StringComparison.Ordinal);
            Assert.Equal(expected[7..], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(roadmapRoot, checksum.Name)))), ignoreCase: true);
        }
    }

    private static JsonDocument Read(string fileName) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
        Root, "artifacts", "direct-singnext-boot", fileName)));

    private static string Hash(string relative) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)))));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
