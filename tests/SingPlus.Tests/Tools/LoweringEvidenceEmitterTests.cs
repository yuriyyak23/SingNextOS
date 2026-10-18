using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;
using SingPlus.LoweringEvidence;
using SingPlus.Runtime;

namespace SingPlus.Tests.Tools;

public sealed class LoweringEvidenceEmitterTests
{
    [Fact]
    public void PostLoweringFilesProduceCanonicalExactOutputMetadata()
    {
        using var files = TestFiles.Create();

        var result = LoweringEvidenceEmitter.EmitFiles(files.Manifest, files.Input,
            files.Output, files.Toolchain, files.Producer, files.Metadata);
        var metadata = File.ReadAllBytes(files.Metadata);
        var envelope = CompilerLoweringEvidenceMetadataCodecV1.Parse(metadata);

        Assert.Equal(Digest(File.ReadAllBytes(files.Output)), result.OutputDigest);
        Assert.Equal(result.OutputDigest, envelope.Evidence.OutputBinaryOrBundleDigest);
        Assert.Equal(result.InputIrDigest, envelope.Evidence.InputIrDigest);
        Assert.Equal(result.ToolchainDigest, envelope.Evidence.ToolchainDigest);
        Assert.Equal(result.ProducerDigest, envelope.Evidence.ProducerDigest);
        Assert.Single(envelope.Evidence.SafePointMap);
        Assert.Equal("managed-sp-0", envelope.Evidence.SafePointMap[0].SafePointIdentity);
        Assert.Single(envelope.Evidence.StaticResourceEstimates);
        Assert.False(envelope.Evidence.StaticResourceEstimates[0].ReservesResources);
        Assert.Equal(metadata.Length, result.MetadataBytes);
        Assert.False(envelope.AuthorizesExecution);
    }

    [Fact]
    public void OutputMutationDoesNotMatchPreviouslyEmittedEvidence()
    {
        using var files = TestFiles.Create();
        var result = LoweringEvidenceEmitter.EmitFiles(files.Manifest, files.Input,
            files.Output, files.Toolchain, files.Producer, files.Metadata);

        File.AppendAllBytes(files.Output, [0xFF]);

        Assert.NotEqual(result.OutputDigest, Digest(File.ReadAllBytes(files.Output)));
    }

    [Fact]
    public void ManifestRejectsUnknownFieldsAndMetadataPathAliasing()
    {
        using var files = TestFiles.Create();
        var unknown = Encoding.UTF8.GetBytes(
            "{\"CompilerContractVersion\":\"compiler-contract-v6\",\"Footprints\":[],\"Authority\":true}");

        Assert.ThrowsAny<Exception>(() => LoweringEvidenceEmitter.ParseManifest(unknown));
        Assert.Throws<ArgumentException>(() => LoweringEvidenceEmitter.EmitFiles(
            files.Manifest, files.Input, files.Output, files.Toolchain, files.Producer,
            files.Output));
    }

    [Fact]
    public void EmittedSidecarIsAcceptedByRuntimeOnlyAfterLiveChecks()
    {
        using var files = TestFiles.Create();
        var emitted = LoweringEvidenceEmitter.EmitFiles(files.Manifest, files.Input,
            files.Output, files.Toolchain, files.Producer, files.Metadata);
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var liveChecks = 0;

        var admitted = verifier.VerifyMetadataForAdmission(
            File.ReadAllBytes(files.Metadata), File.ReadAllBytes(files.Output),
            new("compiler-contract-v6", emitted.ToolchainDigest,
                emitted.ProducerDigest, 1, true,
                CompilerLoweringEvidenceMetadataCodecV1.Parse(
                    File.ReadAllBytes(files.Metadata)).Evidence.StaticFactSetDigest),
            () => { liveChecks++; return KernelResult.Ok(); },
            () => { liveChecks++; return KernelResult.Ok(); });

        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.True(admitted.Value!.EvidenceAccepted);
        Assert.False(admitted.Value.AuthorizesExecution);
        Assert.Equal(2, liveChecks);
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class TestFiles : IDisposable
    {
        private TestFiles(string root)
        {
            Root = root;
            Manifest = Path.Combine(root, "facts.json");
            Input = Path.Combine(root, "input.ir");
            Output = Path.Combine(root, "output.bin");
            Toolchain = Path.Combine(root, "compiler.bin");
            Producer = Path.Combine(root, "emitter.bin");
            Metadata = Path.Combine(root, "output.pcl");
        }

        public string Root { get; }
        public string Manifest { get; }
        public string Input { get; }
        public string Output { get; }
        public string Toolchain { get; }
        public string Producer { get; }
        public string Metadata { get; }

        public static TestFiles Create()
        {
            var files = new TestFiles(Path.Combine(Path.GetTempPath(),
                $"singplus-pcl-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(files.Root);
            File.WriteAllText(files.Manifest,
                """
                {
                  "CompilerContractVersion": "compiler-contract-v6",
                  "Footprints": [
                    { "Symbol": "input", "Offset": 0, "Length": 64, "Access": 1 },
                    { "Symbol": "output", "Offset": 0, "Length": 32, "Access": 2 }
                  ],
                  "AliasFacts": [
                    { "LeftSymbol": "input", "RightSymbol": "output", "Disjoint": true }
                  ],
                  "OrderingFacts": [
                    { "ConstraintIdentity": "publish", "Preserved": true, "FenceEmitted": true }
                  ],
                  "NumericFacts": [
                    { "OperationClass": "vector-add", "RoundingMode": "nearest-even", "OverflowMode": "wrap", "VectorWidthBits": 128 }
                  ],
                  "SafePointMap": [
                    { "SafePointIdentity": "managed-sp-0", "EncodedAddress": 0, "LiveStateDigest": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" }
                  ],
                  "StaticResourceEstimates": [
                    { "ResourceIdentity": "canonical-instruction-count", "UpperBound": 3, "Unit": "instructions" }
                  ]
                }
                """, new UTF8Encoding(false));
            File.WriteAllBytes(files.Input, [1, 2, 3]);
            File.WriteAllBytes(files.Output, [4, 5, 6]);
            File.WriteAllBytes(files.Toolchain, [7, 8, 9]);
            File.WriteAllBytes(files.Producer, [10, 11, 12]);
            return files;
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
