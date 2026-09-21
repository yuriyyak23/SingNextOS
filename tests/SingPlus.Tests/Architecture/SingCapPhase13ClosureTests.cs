using System.Text.Json;

namespace SingPlus.Tests.Architecture;

public sealed class SingCapPhase13ClosureTests
{
    [Fact]
    public void PerformanceReportContainsEveryRequiredWorkerAndContentionCombination()
    {
        using var document = Read("P13_PERFORMANCE_BASELINE.json");
        var root = document.RootElement;
        Assert.Equal("SingCapPerformanceQualificationV1", root.GetProperty("schema").GetString());
        var rows = root.GetProperty("measurements").EnumerateArray().ToArray();
        foreach (var operation in new[] { "capability-lookup", "region-validate" })
        foreach (var contention in new[] { "shared", "unrelated" })
        foreach (var workers in new[] { 1, 2, 4, 8, 16, 32 })
        {
            var row = Assert.Single(rows, item =>
                item.GetProperty("Operation").GetString() == operation &&
                item.GetProperty("Contention").GetString() == contention &&
                item.GetProperty("Workers").GetInt32() == workers);
            Assert.True(row.GetProperty("ThroughputPerSecond").GetDouble() > 0);
            Assert.True(row.GetProperty("MedianNs").GetDouble() >= 0);
            Assert.True(row.GetProperty("P95Ns").GetDouble() >= row.GetProperty("MedianNs").GetDouble());
            Assert.True(row.GetProperty("P99Ns").GetDouble() >= row.GetProperty("P95Ns").GetDouble());
            Assert.True(row.GetProperty("MaxObservedOperationLatencyNs").GetDouble() >= row.GetProperty("P99Ns").GetDouble());
        }

        var single = root.GetProperty("singleThreadMeasurements").EnumerateArray().ToArray();
        foreach (var operation in new[]
        {
            "single-sip-empty-call",
            "sip-with-4-exact-capabilities",
            "seal-unseal-internal-resolution",
            "region-lexical-borrow-acquire-return",
            "async-lease-revalidation",
            "move-64-byte-region",
            "external-operation-prepare-admit-publish-release",
        })
        {
            var row = Assert.Single(single,
                item => item.GetProperty("Operation").GetString() == operation);
            Assert.True(row.GetProperty("ThroughputPerSecond").GetDouble() > 0);
            Assert.True(row.GetProperty("P99Ns").GetDouble() >= row.GetProperty("MedianNs").GetDouble());
        }
        Assert.True(root.GetProperty("spanAfterAcquisition")
            .GetProperty("operationsPerSecond").GetDouble() > 0);
    }

    [Fact]
    public void ClaimMatrixWithholdsProductionAndHardwareClaims()
    {
        using var document = Read("P13_CLAIM_EVIDENCE_MATRIX.json");
        var root = document.RootElement;
        Assert.Equal("SingCapClaimEvidenceMatrixV1", root.GetProperty("schema").GetString());
        Assert.False(root.GetProperty("productionCandidateGranted").GetBoolean());
        Assert.All(root.GetProperty("features").EnumerateArray(), feature =>
            Assert.Contains(feature.GetProperty("level").GetString(),
                new[] { "ModelOnly", "StaticAdmission", "RuntimeEnforced", "QualifiedManaged" }));
    }

    [Fact]
    public void SecurityMatrixNamesEveryMandatoryCrossAuthorityRace()
    {
        using var document = Read("P13_SECURITY_QUALIFICATION_MATRIX.json");
        var requirements = document.RootElement.GetProperty("categories").EnumerateArray()
            .Select(item => item.GetProperty("requirement").GetString()).ToArray();
        foreach (var required in new[]
        {
            "admission versus Region MOVE reclaim",
            "admission versus session close",
            "seal resolution versus object close service restart",
            "publication versus capability revoke Region reclaim",
            "provider completion versus service restart",
            "adapter callback reentry lock order",
        })
            Assert.Contains(required, requirements);
    }

    [Fact]
    public void DefinitionOfDoneMatrixMapsEveryItemAndDoesNotHideFutureGatedWork()
    {
        using var document = Read("P13_DEFINITION_OF_DONE_LIVE_MATRIX.json");
        var root = document.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(1, 16), items.Select(item => item.GetProperty("number").GetInt32()));
        Assert.All(items, item => Assert.NotEmpty(item.GetProperty("evidence").EnumerateArray()));
        Assert.False(root.GetProperty("singCapMComplete").GetBoolean());
        Assert.Equal("FutureGated", Assert.Single(items,
            item => item.GetProperty("number").GetInt32() == 11).GetProperty("status").GetString());
    }

    [Fact]
    public void InventoryMakesNoNativeIsolatedClaim()
    {
        var path = Path.Combine(RepositoryRoot(), "eng", "singcap-security-profiles-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.DoesNotContain(document.RootElement.GetProperty("profiles").EnumerateArray(),
            profile => profile.GetProperty("profile").GetString() == "NativeIsolated");
    }

    [Fact]
    public void ReleaseGateContainsLockedRestoreNativeAotStressAndPerformanceStages()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "eng", "qualify-singcap-p13.ps1"));
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test $solution", script, StringComparison.Ordinal);
        Assert.Contains("qualify-singcap-p01.ps1", script, StringComparison.Ordinal);
        Assert.Contains("SingPlus.SingCapQualification", script, StringComparison.Ordinal);
        Assert.Contains("@(1, 2, 4, 8, 16, 32)", script, StringComparison.Ordinal);
        Assert.Contains("git diff --check", script, StringComparison.Ordinal);
    }

    private static JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(RepositoryRoot(), "docs", "Completed", "SingCap-Refactoring", "roadmap", name)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
