using System.Text.Json;
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

        Assert.Equal(1, matrix.RootElement.GetProperty("schemaVersion").GetInt32());
        var tupleFile = matrix.RootElement.GetProperty("tuple").GetString();
        Assert.Equal("P14_QUALIFICATION_TUPLE.json", tupleFile);
        Assert.True(File.Exists(Path.Combine(roadmap, tupleFile!)));
        Assert.Equal("52ccf45c05498a9143a599bb54499919a2cbcf8c", tuple.RootElement.GetProperty("singNextOsSourceSha").GetString());
        Assert.Empty(tuple.RootElement.GetProperty("enabledFeatureGates").EnumerateArray());
        Assert.Contains("TestOnly", tuple.RootElement.GetProperty("claimLevel").GetString(), StringComparison.Ordinal);
        Assert.Equal("Unclaimed", tuple.RootElement.GetProperty("provider").GetProperty("executionQualification").GetString());
        Assert.All(SipJobFeatureGates.Names, gate => Assert.False(SipJobFeatureGates.IsEnabled(gate)));

        var requirements = matrix.RootElement.GetProperty("requirements").EnumerateArray().ToArray();
        Assert.Equal(16, requirements.Length);
        Assert.Equal(Enumerable.Range(1, 16).Select(index => $"SJOB-{index:D3}"),
            requirements.Select(item => item.GetProperty("id").GetString()));
        Assert.All(requirements, requirement =>
        {
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("owner").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("implementation").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("testLane").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("evidence").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("claim").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(requirement.GetProperty("exclusions").GetString()));
            Assert.All(requirement.GetProperty("evidence").GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                evidence => Assert.True(File.Exists(Path.Combine(roadmap, evidence)),
                    $"{requirement.GetProperty("id").GetString()} references missing evidence {evidence}."));
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
}
