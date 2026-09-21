using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16RemainingContourTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string RoadmapRoot = Path.Combine(RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new");

    [Fact]
    public void EveryUnqualifiedContourNamesOwnerPrerequisiteUnsafeBypassAndClaimCeiling()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RoadmapRoot,
            "P16_REMAINING_CONTOURS_20260922.json")));
        var root = document.RootElement;
        Assert.Equal("P16", root.GetProperty("phase").GetString());
        Assert.Equal("Open", root.GetProperty("phaseDisposition").GetString());
        Assert.Equal("PartialCoverage", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("productionQualified").GetBoolean());
        Assert.False(root.GetProperty("p17MayStart").GetBoolean());

        string[] expectedIds = [
            "P16-GAP-PROVIDER-RESOURCE", "P16-GAP-CONTROLLED-SMT", "P16-GAP-DURABLE-RESTART",
            "P16-GAP-LIVE-GATE-ROLLBACK", "P16-GAP-SIPJOB-DIFFERENTIAL", "P16-GAP-TEMPORAL-CLAIMS"
        ];
        var contours = root.GetProperty("contours").EnumerateArray().ToArray();
        Assert.Equal(expectedIds, contours.Select(item => item.GetProperty("id").GetString()));
        Assert.All(contours, contour =>
        {
            Assert.Equal("FutureGated", contour.GetProperty("disposition").GetString());
            foreach (var field in new[] { "owner", "missingPrerequisite", "currentEvidence", "unsafeBypass", "claimCeiling" })
                Assert.False(string.IsNullOrWhiteSpace(contour.GetProperty(field).GetString()), $"{contour.GetProperty("id").GetString()} lacks {field}.");
            Assert.DoesNotContain("ProductionQualified", contour.GetProperty("claimCeiling").GetString(), StringComparison.Ordinal);
            foreach (var gate in contour.GetProperty("gates").EnumerateArray().Select(item => item.GetString()!))
            {
                Assert.True(VNextFeatureGates.TryResolve(gate, out _), gate);
                Assert.False(VNextFeatureGates.IsEnabled(gate), gate);
            }
        });
    }

    [Fact]
    public void LocalProviderPackageIsExactButHasNoResourceUsageContract()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RoadmapRoot,
            "P16_REMAINING_CONTOURS_20260922.json")));
        var artifact = document.RootElement.GetProperty("localProviderArtifact");
        var path = Path.Combine(RepositoryRoot, artifact.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(artifact.GetProperty("bytes").GetInt64(), bytes.LongLength);
        Assert.Equal(artifact.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.False(artifact.GetProperty("sourceShaLocallyVerified").GetBoolean());

        using var archive = ZipFile.OpenRead(path);
        var readme = new StreamReader(Assert.Single(archive.Entries, entry => entry.FullName == "README.md").Open()).ReadToEnd();
        Assert.Contains("reconciliation/closure belongs to the provider", readme, StringComparison.Ordinal);
        var publicTypes = typeof(Hc.ExternalOperationRequest).Assembly.GetExportedTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var absent in new[] { "ExternalResourceEnvelope", "ExternalResourceUsageEvidence", "ExternalBudgetLease", "ExternalResourceAmount" })
            Assert.DoesNotContain(absent, publicTypes);
    }

    [Fact]
    public void RemainingContourTuplePinsTheReviewedInputs()
    {
        using var tuple = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RoadmapRoot,
            "P16_REMAINING_CONTOURS_20260922_TUPLE.json")));
        Assert.Empty(VNextTraceabilityPolicy.ValidateArtifacts(tuple.RootElement.GetProperty("artifacts"),
            path => File.ReadAllBytes(Path.Combine(RepositoryRoot, path))));
        Assert.Equal("Open", tuple.RootElement.GetProperty("phaseDisposition").GetString());
        Assert.False(tuple.RootElement.GetProperty("productionQualified").GetBoolean());
        Assert.False(tuple.RootElement.GetProperty("p17MayStart").GetBoolean());
        Assert.Empty(tuple.RootElement.GetProperty("enabledFeatureGates").EnumerateArray());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
