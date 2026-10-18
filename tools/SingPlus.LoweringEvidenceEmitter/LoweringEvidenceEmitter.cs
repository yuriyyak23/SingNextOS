using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SingPlus.Contracts;

namespace SingPlus.LoweringEvidence;

public sealed record LoweringEvidenceManifestV1(
    string CompilerContractVersion,
    IReadOnlyList<LoweringFootprintFactV1> Footprints,
    IReadOnlyList<LoweringAliasFactV1>? AliasFacts = null,
    IReadOnlyList<LoweringOrderingFactV1>? OrderingFacts = null,
    IReadOnlyList<LoweringNumericModeFactV1>? NumericFacts = null,
    IReadOnlyList<LoweringSafePointFactV1>? SafePointMap = null,
    IReadOnlyList<LoweringStaticResourceEstimateFactV1>? StaticResourceEstimates = null);

public readonly record struct LoweringEvidenceEmissionResult(
    string InputIrDigest,
    string OutputDigest,
    string ToolchainDigest,
    string ProducerDigest,
    string EvidenceDigest,
    int MetadataBytes);

public static class LoweringEvidenceEmitter
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static LoweringEvidenceManifestV1 ParseManifest(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > CompilerLoweringEvidenceMetadataCodecV1.MaximumBytes)
            throw new ArgumentException("Lowering fact manifest is empty or exceeds its bound.");
        var manifest = JsonSerializer.Deserialize<LoweringEvidenceManifestV1>(utf8Json, ManifestJson)
            ?? throw new ArgumentException("Lowering fact manifest is empty.");
        _ = CreateEvidence(manifest, ZeroDigest, ZeroDigest, ZeroDigest, ZeroDigest);
        return manifest;
    }

    public static LoweringEvidenceEmissionResult EmitFiles(
        string manifestPath,
        string inputIrPath,
        string outputBinaryOrBundlePath,
        string toolchainPath,
        string producerPath,
        string metadataPath)
    {
        var paths = new[] { manifestPath, inputIrPath, outputBinaryOrBundlePath,
            toolchainPath, producerPath, metadataPath }
            .Select(Path.GetFullPath).ToArray();
        if (paths.Take(5).Distinct(PathComparer).Count() != 5 ||
            paths.Take(5).Contains(paths[5], PathComparer))
            throw new ArgumentException("Manifest, evidence inputs, output artifact, and metadata target must be distinct files.");

        using var manifest = OpenStable(paths[0]);
        using var input = OpenStable(paths[1]);
        using var output = OpenStable(paths[2]);
        using var toolchain = OpenStable(paths[3]);
        using var producer = OpenStable(paths[4]);
        if (manifest.Length > CompilerLoweringEvidenceMetadataCodecV1.MaximumBytes)
            throw new ArgumentException("Lowering fact manifest exceeds its bound.");
        var manifestBytes = new byte[checked((int)manifest.Length)];
        manifest.ReadExactly(manifestBytes);
        var facts = ParseManifest(manifestBytes);
        var inputDigest = Digest(input);
        var outputDigest = Digest(output);
        var toolchainDigest = Digest(toolchain);
        var producerDigest = Digest(producer);
        var evidence = CreateEvidence(facts, toolchainDigest, inputDigest,
            outputDigest, producerDigest);
        var envelope = CompilerLoweringEvidenceEnvelopeV1.Create(evidence);
        var metadata = CompilerLoweringEvidenceMetadataCodecV1.Emit(envelope);
        _ = CompilerLoweringEvidenceMetadataCodecV1.Parse(metadata);
        WriteAtomic(paths[5], metadata);
        return new(inputDigest, outputDigest, toolchainDigest, producerDigest,
            envelope.EvidenceDigest, metadata.Length);
    }

    private static CompilerLoweringEvidenceV1 CreateEvidence(
        LoweringEvidenceManifestV1 manifest,
        string toolchainDigest,
        string inputDigest,
        string outputDigest,
        string producerDigest) => CompilerLoweringEvidenceV1.Create(
            manifest.CompilerContractVersion, toolchainDigest, inputDigest,
            outputDigest, producerDigest,
            manifest.Footprints ?? throw new ArgumentException("Footprints are required."),
            manifest.AliasFacts, manifest.OrderingFacts, manifest.NumericFacts,
            manifest.SafePointMap, manifest.StaticResourceEstimates);

    private static FileStream OpenStable(string path) => new(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private static string Digest(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Metadata target has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private const string ZeroDigest =
        "0000000000000000000000000000000000000000000000000000000000000000";
}
