using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16QualificationClosureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string RoadmapRoot = Path.Combine(RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new");

    [Fact]
    public void EveryTraceabilityTestReferenceResolvesToExecutableXunitTest()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RoadmapRoot, "VNEXT_TRACEABILITY.json")));
        var types = typeof(VNextPhase16QualificationClosureTests).Assembly.GetTypes();
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        foreach (var reference in row.GetProperty("tests").GetString()!.Split(';', StringSplitOptions.TrimEntries))
        {
            var methods = types.SelectMany(type => type.GetMethods()
                .Where(method => method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length != 0)
                .Where(method => reference == $"{type.FullName}.{method.Name}"));
            Assert.True(methods.Any(), $"{row.GetProperty("id").GetString()}: missing executable test '{reference}'.");
        }
    }

    [Fact]
    public void MachineReadableTraceabilityCoversEveryInvariantAndReferencesLiveEvidence()
    {
        var errors = Validate(ReadTraceability(), File.ReadAllText(Path.Combine(RoadmapRoot, "TRACEABILITY_MATRIX.md")));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Theory]
    [InlineData("EnforcedUpperBound")]
    [InlineData("GuaranteedReservation")]
    [InlineData("ProductionQualified")]
    [InlineData("NativeAOT")]
    [InlineData("HybridCPU")]
    [InlineData("Throughput")]
    public void TraceabilityRejectsUnsupportedClaimsAndContourTransfer(string mutation)
    {
        var node = JsonNode.Parse(ReadTraceability())!;
        var row = node["rows"]![0]!;
        row[mutation is "NativeAOT" or "HybridCPU" or "Throughput" ? "contour" : "claim"] = mutation;
        var json = node.ToJsonString();
        Assert.NotEmpty(Validate(json, Render(json)));
    }

    [Theory]
    [InlineData("missing-test")]
    [InlineData("class-without-method")]
    [InlineData("missing-source")]
    [InlineData("path-traversal")]
    [InlineData("absolute-path")]
    [InlineData("stale-tuple-hash")]
    [InlineData("stale-evidence-hash")]
    [InlineData("unknown-field")]
    [InlineData("missing-field")]
    [InlineData("null-field")]
    [InlineData("unknown-schema")]
    [InlineData("duplicate-invariant")]
    [InlineData("false-closure")]
    [InlineData("unknown-lane")]
    public void TraceabilityRejectsMalformedStaleOrUnresolvableInputs(string mutation)
    {
        var node = JsonNode.Parse(ReadTraceability())!;
        var row = (JsonObject)node["rows"]![0]!;
        switch (mutation)
        {
            case "missing-test": row["tests"] = "SingPlus.Tests.Missing.Test"; break;
            case "class-without-method": row["tests"] = typeof(VNextPhase00ArchitecturalFreezeTests).FullName; break;
            case "missing-source": row["implementation"] = "src/missing.cs"; break;
            case "path-traversal": row["implementation"] = "../outside.cs"; break;
            case "absolute-path": row["implementation"] = "C:/outside.cs"; break;
            case "stale-tuple-hash": row["tupleSha256"] = new string('0', 64); break;
            case "stale-evidence-hash": row["evidenceSha256"] = new string('0', 64); break;
            case "unknown-field": row["authorized"] = true; break;
            case "missing-field": row.Remove("tests"); break;
            case "null-field": row["tests"] = null; break;
            case "unknown-schema": node["schemaVersion"] = 99; break;
            case "duplicate-invariant": row["id"] = "VNX-002"; break;
            case "false-closure": node["status"] = "Closed"; break;
            case "unknown-lane": row["ciLane"] = "eng/missing.ps1"; break;
        }
        var errors = Validate(node.ToJsonString(), File.ReadAllText(Path.Combine(RoadmapRoot, "TRACEABILITY_MATRIX.md")));
        Assert.Contains(errors, error => error != "Markdown and JSON traceability differ.");
    }

    [Fact]
    public void TraceabilityRejectsDuplicateJsonFieldsAndMarkdownClaimDrift()
    {
        var json = ReadTraceability();
        var markdown = File.ReadAllText(Path.Combine(RoadmapRoot, "TRACEABILITY_MATRIX.md"));
        Assert.NotEmpty(Validate(json.Replace("\"schemaVersion\": 2,", "\"schemaVersion\": 2, \"schemaVersion\": 2,", StringComparison.Ordinal), markdown));
        Assert.NotEmpty(Validate(json, markdown.Replace("StaticAdmission", "ProductionQualified", StringComparison.Ordinal)));
    }

    [Fact]
    public void CurrentRevalidationTuplePinsSourcesTestsAndLocalPackageWithoutClaimingClosure()
    {
        using var tuple = JsonDocument.Parse(File.ReadAllText(Path.Combine(RoadmapRoot, "P16_REVALIDATION_20260921_TUPLE.json")));
        var root = tuple.RootElement;
        Assert.Equal(VNextTraceabilityPolicy.Baseline, root.GetProperty("normativeBaseline").GetString());
        Assert.Equal("Open", root.GetProperty("phaseDisposition").GetString());
        Assert.False(root.GetProperty("productionQualified").GetBoolean());
        Assert.False(root.GetProperty("p17MayStart").GetBoolean());
        Assert.Empty(root.GetProperty("enabledFeatureGates").EnumerateArray());
        Assert.Empty(root.GetProperty("isaChangeCategory").EnumerateArray());
        var errors = VNextTraceabilityPolicy.ValidateArtifacts(root.GetProperty("artifacts"),
            path => File.ReadAllBytes(Path.Combine(RepositoryRoot, path)));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        var paths = root.GetProperty("artifacts").EnumerateArray().Select(item => item.GetProperty("path").GetString()).ToHashSet(StringComparer.Ordinal);
        using var traceability = JsonDocument.Parse(ReadTraceability());
        foreach (var row in traceability.RootElement.GetProperty("rows").EnumerateArray())
            Assert.All(row.GetProperty("implementation").GetString()!.Split("; "), path => Assert.Contains(path, paths));
        Assert.Contains(".packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg", paths);
        Assert.Contains("tests/SingPlus.Tests/Architecture/VNextPhase16QualificationClosureTests.cs", paths);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("bytes")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public void SourceTupleRejectsChangedBytesAndDuplicateOrEmptyArtifactSets(string mutation)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var array = new JsonArray(new JsonObject
        {
            ["path"] = "src/fixture.cs", ["bytes"] = bytes.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        });
        switch (mutation)
        {
            case "hash": array[0]!["sha256"] = new string('0', 64); break;
            case "bytes": array[0]!["bytes"] = 4; break;
            case "duplicate": array.Add(array[0]!.DeepClone()); break;
            case "empty": array.Clear(); break;
        }
        using var document = JsonDocument.Parse(array.ToJsonString());
        Assert.NotEmpty(VNextTraceabilityPolicy.ValidateArtifacts(document.RootElement, _ => bytes));
    }

    private static string ReadTraceability() => File.ReadAllText(Path.Combine(RoadmapRoot, "VNEXT_TRACEABILITY.json"));

    private static string Render(string json)
    {
        using var document = JsonDocument.Parse(json);
        return VNextTraceabilityPolicy.Render(document.RootElement);
    }

    private static IReadOnlyList<string> Validate(string json, string markdown)
    {
        var methods = typeof(VNextPhase16QualificationClosureTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods().Where(method =>
                    method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length != 0)
                .Select(method => $"{type.FullName}.{method.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        return VNextTraceabilityPolicy.Validate(json, markdown, methods,
            path => File.ReadAllBytes(Path.Combine(RepositoryRoot, path)));
    }

    [Fact]
    public void EveryPhaseTupleIsPinnedAndNoTupleClaimsProductionQualification()
    {
        for (var phase = 0; phase <= 17; phase++)
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
