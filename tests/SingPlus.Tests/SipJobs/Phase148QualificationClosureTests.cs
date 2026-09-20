using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase148QualificationClosureTests
{
    [Fact]
    public void TupleAndClaimMatrixCloseAllRequirementsWithoutEnablingProductionGate()
    {
        var roadmap = Path.Combine(RepositoryRoot(), "docs", "SingNextOS-post-SingCap-M-SipJob-roadmap");
        using var tuple = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(roadmap, "P14_QUALIFICATION_TUPLE.json")));
        using var matrix = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(roadmap, "P14_CLAIM_TRACEABILITY.json")));
        var markdownRows = File.ReadAllLines(Path.Combine(roadmap, "P14_CLAIM_TRACEABILITY.md"))
            .Where(static line => line.StartsWith("| SJOB-", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(1, matrix.RootElement.GetProperty("schemaVersion").GetInt32());
        var tupleFile = matrix.RootElement.GetProperty("tuple").GetString();
        Assert.Equal("P14_QUALIFICATION_TUPLE.json", tupleFile);
        Assert.True(File.Exists(Path.Combine(roadmap, tupleFile!)));
        Assert.Equal("52ccf45c05498a9143a599bb54499919a2cbcf8c", tuple.RootElement.GetProperty("auditBaselineSha").GetString());
        Assert.Equal(CurrentHead(), tuple.RootElement.GetProperty("singNextOsSourceSha").GetString());
        Assert.Equal("CurrentHeadPlusPreservedDirtyWorktree", tuple.RootElement.GetProperty("sourceDisposition").GetString());
        foreach (var artifact in tuple.RootElement.GetProperty("artifacts").EnumerateObject())
        {
            var relativePath = artifact.Value.GetProperty("path").GetString();
            var expectedHash = artifact.Value.GetProperty("sha256").GetString();
            Assert.False(string.IsNullOrWhiteSpace(relativePath));
            Assert.Matches("^[0-9A-F]{64}$", expectedHash!);
            var artifactPath = Path.GetFullPath(Path.Combine(RepositoryRoot(), relativePath!));
            Assert.StartsWith(RepositoryRoot(), artifactPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(artifactPath), $"Missing qualified artifact {artifact.Name}: {relativePath}");
            using var stream = File.OpenRead(artifactPath);
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(stream)));
        }
        Assert.Empty(tuple.RootElement.GetProperty("enabledFeatureGates").EnumerateArray());
        Assert.Contains("TestOnly", tuple.RootElement.GetProperty("claimLevel").GetString(), StringComparison.Ordinal);
        Assert.Equal("Unclaimed", tuple.RootElement.GetProperty("provider").GetProperty("executionQualification").GetString());
        Assert.All(SipJobFeatureGates.Names, gate => Assert.False(SipJobFeatureGates.IsEnabled(gate)));

        var requirements = matrix.RootElement.GetProperty("requirements").EnumerateArray().ToArray();
        Assert.Equal(16, requirements.Length);
        Assert.Equal(Enumerable.Range(1, 16).Select(index => $"SJOB-{index:D3}"),
            requirements.Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(16, markdownRows.Length);
        Assert.Equal(Enumerable.Range(1, 16).Select(index => $"SJOB-{index:D3}"),
            markdownRows.Select(static row => row.Split('|', StringSplitOptions.TrimEntries)[1]));
        Assert.All(requirements, requirement =>
        {
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("owner").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("implementation").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("testLane").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("evidence").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("claim").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("exclusions").GetString()));
            Assert.DoesNotContain("ProductionCandidate", requirement.GetProperty("claim").GetString(), StringComparison.Ordinal);
            var id = requirement.GetProperty("id").GetString()!;
            var markdownRow = Assert.Single(markdownRows, row => row.Contains($"| {id} |", StringComparison.Ordinal));
            Assert.All(requirement.GetProperty("evidence").GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                evidence =>
                {
                    Assert.True(File.Exists(Path.Combine(roadmap, evidence)),
                        $"{id} references missing evidence {evidence}.");
                    Assert.Contains($"`{evidence}`", markdownRow, StringComparison.Ordinal);
                });
        });

        foreach (var evidence in new[]
        {
            "EVIDENCE_P14_0_ARCHITECTURAL_FREEZE.md", "EVIDENCE_P14_1_PLAN_VERIFIER.md",
            "EVIDENCE_P14_2_DIRECT_SENTRY.md", "EVIDENCE_P14_3_REGION_EDGES.md",
            "EVIDENCE_P14_4_COMPOSED_ADMISSION.md", "EVIDENCE_P14_5A_BARRIERS.md",
            "EVIDENCE_P14_5B_ASYNC.md", "EVIDENCE_P14_5C_READONLY_DAG.md",
            "EVIDENCE_P14_5D_PARALLEL_DAG.md", "EVIDENCE_P14_6_DYNAMIC_BINDING_CACHE.md",
            "EVIDENCE_P14_7_PROVIDER_SCHEDULING.md", "EVIDENCE_P14_8_QUALIFICATION.md"
        }) Assert.True(File.Exists(Path.Combine(roadmap, evidence)), evidence);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string CurrentHead()
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Unable to start local git for qualification tuple validation.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git rev-parse HEAD failed: {error}");
        Assert.Matches("^[0-9a-f]{40}$", output);
        return output;
    }
}
