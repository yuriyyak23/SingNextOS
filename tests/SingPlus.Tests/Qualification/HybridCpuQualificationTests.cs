using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Admission;
using SingPlus.HybridCpuQualification;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Qualification;

public sealed class HybridCpuQualificationTests
{
    [Fact]
    [Trait("Category", "Qualification")]
    [Trait("Category", "Determinism")]
    public void ValidEvidenceProducesIdenticalExternalBlockedReportsWithExactHashes()
    {
        using var fixture = QualificationFixture.Create();

        var first = QualificationRecorder.RecordExternalBlocked(fixture.Inputs);
        var second = QualificationRecorder.RecordExternalBlocked(fixture.Inputs);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal(
            new[]
            {
                "SchemaId", "Outcome", "Inputs", "ReproductionCommands", "Artifacts", "Admission",
                "Reproducibility", "Stages", "ToolchainIdentity", "AotCommand", "ImagePath", "ImageDigest",
                "IseCommand", "IseResult",
            },
            root.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(QualificationRecorder.Schema, root.GetProperty("SchemaId").GetString());
        Assert.Equal("ExternalBlocked", root.GetProperty("Outcome").GetString());

        var inputs = root.GetProperty("Inputs");
        Assert.Equal(fixture.SingRevision, inputs.GetProperty("SingNextOsRevision").GetString());
        Assert.Equal(fixture.HybridRevision, inputs.GetProperty("HybridCpuRevision").GetString());
        Assert.Equal(fixture.HybridRevision, inputs.GetProperty("ExpectedHybridCpuRevision").GetString());
        Assert.Equal(fixture.HybridTree, inputs.GetProperty("HybridCpuTree").GetString());
        Assert.Equal("10.0.204", inputs.GetProperty("SingNextOsRequestedSdkVersion").GetString());
        Assert.Equal("10.0.204", inputs.GetProperty("ActualDotNetSdkVersion").GetString());
        Assert.Equal("10.0.201", inputs.GetProperty("HybridCpuRequestedSdkVersion").GetString());
        Assert.Equal(
            QualificationRecorder.AuditedHybridCpuCompilerContractVersion,
            inputs.GetProperty("HybridCpuCompilerContractVersion").GetInt32());
        Assert.Equal(QualificationRecorder.KernelEntryPoint, inputs.GetProperty("KernelEntryPoint").GetString());

        var commands = root.GetProperty("ReproductionCommands").EnumerateArray().ToArray();
        Assert.Equal(
            new[]
            {
                "DotNetSdkIdentity", "BootRestore", "RecorderRestore", "RecorderBuild",
                "FirstPassClean", "FirstPassBuild", "FirstPassAdmission",
                "PreserveFirstPassKernel", "PreserveFirstPassBoot", "PreserveFirstPassAdmission",
                "SecondPassClean", "SecondPassBuild", "SecondPassAdmission",
            },
            commands.Select(static command => command.GetProperty("Stage").GetString()));
        Assert.All(commands, static command =>
        {
            Assert.True(command.TryGetProperty("Stage", out _));
            Assert.True(command.TryGetProperty("Argv", out var argv));
            Assert.Equal(JsonValueKind.Array, argv.ValueKind);
            Assert.False(command.TryGetProperty("ExitCode", out _));
        });

        var artifacts = root.GetProperty("Artifacts").EnumerateArray().ToArray();
        Assert.Equal(new[] { "KernelAssembly", "HostBootAssembly", "AdmissionProof" },
            artifacts.Select(static artifact => artifact.GetProperty("Kind").GetString()));
        Assert.Equal(
            new[]
            {
                "src/Kernel/SingPlus.Kernel/bin/Release/net10.0/SingPlus.Kernel.dll",
                "src/Kernel/Boot/SingPlus.Boot/bin/Release/net10.0/SingPlus.Boot.dll",
                "artifacts/hybridcpu-aot-qualification/SingPlusAdmissionProofV1.json",
            },
            artifacts.Select(static artifact => artifact.GetProperty("Path").GetString()));
        Assert.Equal(fixture.KernelDigest, artifacts[0].GetProperty("Sha256").GetString());
        Assert.Equal(fixture.BootDigest, artifacts[1].GetProperty("Sha256").GetString());
        Assert.Equal(fixture.ProofDigest, artifacts[2].GetProperty("Sha256").GetString());

        var admission = root.GetProperty("Admission");
        Assert.Equal(fixture.KernelDigest, admission.GetProperty("AssemblyDigest").GetString());
        Assert.Equal(fixture.AdmissionSemanticDigest, admission.GetProperty("ProofDigest").GetString());
        Assert.Equal(0, admission.GetProperty("ForbiddenOperationCount").GetInt32());

        var reproducibility = root.GetProperty("Reproducibility");
        Assert.Equal(2, reproducibility.GetProperty("ComparedArtifactSets").GetInt32());
        Assert.Equal("ByteIdentical", reproducibility.GetProperty("Comparison").GetString());
        Assert.Equal(fixture.KernelDigest, reproducibility.GetProperty("KernelAssemblySha256").GetString());
        Assert.Equal(fixture.BootDigest, reproducibility.GetProperty("HostBootAssemblySha256").GetString());
        Assert.Equal(fixture.ProofDigest, reproducibility.GetProperty("AdmissionProofSha256").GetString());

        var stages = root.GetProperty("Stages").EnumerateArray().ToArray();
        AssertStage(stages[0], "LocalArtifacts", "Validated", null);
        AssertStage(stages[1], "LocalAdmissionProof", "Validated", null);
        AssertStage(stages[2], "LocalArtifactComparison", "Validated", null);
        AssertStage(
            stages[3],
            "ManagedAssemblyToHybridCpuAot",
            "ExternalBlocked",
            QualificationRecorder.ExternalBlockReason);
        AssertStage(stages[4], "ImageGeneration", "NotProduced", "ManagedAssemblyToHybridCpuAot");
        AssertStage(stages[5], "IseLoaderAcceptance", "NotAttempted", "ImageGeneration");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ToolchainIdentity").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("AotCommand").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ImagePath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ImageDigest").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("IseCommand").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("IseResult").ValueKind);
    }

    [Theory]
    [InlineData("kernel")]
    [InlineData("boot")]
    [InlineData("proof")]
    [Trait("Category", "Qualification")]
    public void MissingRequiredArtifactIsRejected(string artifact)
    {
        using var fixture = QualificationFixture.Create();
        var missingPath = artifact switch
        {
            "kernel" => fixture.Inputs.KernelAssemblyPath,
            "boot" => fixture.Inputs.BootAssemblyPath,
            "proof" => fixture.Inputs.AdmissionProofPath,
            _ => throw new InvalidOperationException("Unexpected test artifact."),
        };
        File.Delete(missingPath);

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void AdmissionAssemblyDigestMustMatchCurrentKernelBytes()
    {
        using var fixture = QualificationFixture.Create();
        fixture.WriteProof(assemblyDigest: new string('0', 64));

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("AssemblyDigest does not match", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kernel")]
    [InlineData("boot")]
    [Trait("Category", "Qualification")]
    public void ArbitraryBytesCannotQualifyAsManagedAssemblies(string artifact)
    {
        using var fixture = QualificationFixture.Create();
        var path = artifact == "kernel"
            ? fixture.Inputs.KernelAssemblyPath
            : fixture.Inputs.BootAssemblyPath;
        File.WriteAllBytes(path, "not-a-managed-assembly"u8.ToArray());

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("managed PE assembly", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kernel")]
    [InlineData("boot")]
    [InlineData("proof")]
    [Trait("Category", "Qualification")]
    public void TamperedFirstPassArtifactCannotPassByteComparison(string artifact)
    {
        using var fixture = QualificationFixture.Create();
        var path = artifact switch
        {
            "kernel" => fixture.Inputs.FirstPassKernelAssemblyPath,
            "boot" => fixture.Inputs.FirstPassBootAssemblyPath,
            "proof" => fixture.Inputs.FirstPassAdmissionProofPath,
            _ => throw new InvalidOperationException("Unexpected test artifact."),
        };
        File.AppendAllText(path, "tampered", new UTF8Encoding(false));

        Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void AdmissionProofWithViolationsIsRejected()
    {
        using var fixture = QualificationFixture.Create();
        fixture.WriteProof(
            forbiddenOperationCount: 0,
            violationsJson: "[{\"Method\":\"Fixture::Root\",\"Operation\":\"newobj\",\"Detail\":\"object\"}]");

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("Violations must be an empty array", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void SyntacticallyValidButForgedProofDigestIsRejected()
    {
        using var fixture = QualificationFixture.Create();
        fixture.WriteProof(proofDigest: new string('0', 64));

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("ProofDigest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void MalformedAdmissionProofIsRejected()
    {
        using var fixture = QualificationFixture.Create();
        File.WriteAllText(fixture.Inputs.AdmissionProofPath, "{not-json", new UTF8Encoding(false));

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void HybridCpuRevisionMismatchIsRejected()
    {
        using var fixture = QualificationFixture.Create();
        var forgedRevision = new string(
            fixture.HybridRevision[0] == '0' ? '1' : '0',
            fixture.HybridRevision.Length);

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(
                fixture.Inputs with { ExpectedHybridCpuRevision = forgedRevision }));

        Assert.Contains("does not match expected revision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void MalformedHybridCpuRevisionIsRejectedBeforeEvidenceClassification()
    {
        using var fixture = QualificationFixture.Create();

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(
                fixture.Inputs with { ExpectedHybridCpuRevision = "not-an-exact-revision" }));

        Assert.Contains("exact 40-character Git revision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void ExpectedSdkMustMatchTheObservedSdk()
    {
        using var fixture = QualificationFixture.Create();

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(
                fixture.Inputs with { DotNetSdkVersion = "10.0.205" }));

        Assert.Contains("does not match observed version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void UnexpectedHybridCpuCompilerContractVersionIsRejected()
    {
        using var fixture = QualificationFixture.Create();
        var changedRevision = fixture.CommitHybridCompilerContractVersion(7);

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(
                fixture.Inputs with { ExpectedHybridCpuRevision = changedRevision }));

        Assert.Contains("does not match audited version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void DirtyTrackedWorktreeCannotBeReportedAsItsCommittedRevision()
    {
        using var fixture = QualificationFixture.Create();
        File.AppendAllText(
            Path.Combine(fixture.Inputs.SingRepositoryRoot, "marker.txt"),
            "-dirty",
            new UTF8Encoding(false));

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("worktree must be clean", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void NonignoredUntrackedSourceCannotBeReportedAsCommittedEvidence()
    {
        using var fixture = QualificationFixture.Create();
        File.WriteAllText(
            Path.Combine(fixture.Inputs.SingRepositoryRoot, "untracked-source.cs"),
            "public static class UntrackedSource { }",
            new UTF8Encoding(false));

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(fixture.Inputs));

        Assert.Contains("worktree must be clean", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kernel")]
    [InlineData("boot")]
    [InlineData("proof")]
    [Trait("Category", "Qualification")]
    public void ArbitraryArtifactPathsCannotBeRelabeledAsCanonicalSingNextOsOutputs(string artifact)
    {
        using var fixture = QualificationFixture.Create();
        var sourcePath = artifact switch
        {
            "kernel" => fixture.Inputs.KernelAssemblyPath,
            "boot" => fixture.Inputs.BootAssemblyPath,
            "proof" => fixture.Inputs.AdmissionProofPath,
            _ => throw new InvalidOperationException("Unexpected test artifact."),
        };
        var arbitraryPath = Path.Combine(fixture.Root, $"renamed-{artifact}-hybridcpu.img");
        File.Copy(sourcePath, arbitraryPath);
        var inputs = artifact switch
        {
            "kernel" => fixture.Inputs with { KernelAssemblyPath = arbitraryPath },
            "boot" => fixture.Inputs with { BootAssemblyPath = arbitraryPath },
            "proof" => fixture.Inputs with { AdmissionProofPath = arbitraryPath },
            _ => throw new InvalidOperationException("Unexpected test artifact."),
        };

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.RecordExternalBlocked(inputs));

        Assert.Contains("canonical", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void QualificationReportCannotOverwriteAnArbitraryPath()
    {
        using var fixture = QualificationFixture.Create();
        var arbitraryPath = Path.Combine(fixture.Root, "SingPlus.Kernel.dll");

        var error = Assert.Throws<QualificationException>(() =>
            QualificationRecorder.ResolveCanonicalReportPath(
                fixture.Inputs.SingRepositoryRoot,
                arbitraryPath));

        Assert.Contains("canonical", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Qualification")]
    public void CallerCannotSupplyForgedDownstreamSuccessOrRenameInputsAsHybridImage()
    {
        using var fixture = QualificationFixture.Create();
        var inputNames = typeof(QualificationInputs).GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        Assert.DoesNotContain(inputNames, static name =>
            name.Contains("Outcome", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Toolchain", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Image", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ise", StringComparison.OrdinalIgnoreCase));

        var report = QualificationRecorder.RecordExternalBlocked(fixture.Inputs);
        using var document = JsonDocument.Parse(report);
        var root = document.RootElement;
        Assert.Equal("ExternalBlocked", root.GetProperty("Outcome").GetString());
        Assert.DoesNotContain(
            root.GetProperty("Artifacts").EnumerateArray(),
            static artifact => artifact.GetProperty("Kind").GetString()!.Contains(
                "Image",
                StringComparison.OrdinalIgnoreCase));
        Assert.All(
            root.GetProperty("Stages").EnumerateArray().Skip(3),
            static stage => Assert.NotEqual("Succeeded", stage.GetProperty("Outcome").GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ImageDigest").ValueKind);
    }

    private static void AssertStage(
        JsonElement stage,
        string expectedName,
        string expectedOutcome,
        string? expectedReason)
    {
        Assert.Equal(expectedName, stage.GetProperty("Name").GetString());
        Assert.Equal(expectedOutcome, stage.GetProperty("Outcome").GetString());
        if (expectedReason is null)
            Assert.Equal(JsonValueKind.Null, stage.GetProperty("Reason").ValueKind);
        else
            Assert.Equal(expectedReason, stage.GetProperty("Reason").GetString());
    }

    private sealed class QualificationFixture : IDisposable
    {
        private QualificationFixture(string root)
        {
            Root = root;
            var singRoot = Path.Combine(root, "SingNextOS");
            var hybridRoot = Path.Combine(root, "HybridCPU-v2");
            SingRevision = CreateRepository(singRoot, "sing-next-os");
            HybridRevision = CreateRepository(hybridRoot, "hybrid-cpu-v2");

            var kernelPath = Path.Combine(
                singRoot,
                "src", "Kernel", "SingPlus.Kernel", "bin", "Release", "net10.0", "SingPlus.Kernel.dll");
            var bootPath = Path.Combine(
                singRoot,
                "src", "Kernel", "Boot", "SingPlus.Boot", "bin", "Release", "net10.0", "SingPlus.Boot.dll");
            var proofPath = Path.Combine(
                singRoot,
                "artifacts", "hybridcpu-aot-qualification", "SingPlusAdmissionProofV1.json");
            var firstPassRoot = Path.Combine(
                singRoot,
                "artifacts", "hybridcpu-aot-qualification", "pass1");
            var firstPassKernelPath = Path.Combine(firstPassRoot, "SingPlus.Kernel.dll");
            var firstPassBootPath = Path.Combine(firstPassRoot, "SingPlus.Boot.dll");
            var firstPassProofPath = Path.Combine(firstPassRoot, "SingPlusAdmissionProofV1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(kernelPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(bootPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(proofPath)!);
            Directory.CreateDirectory(firstPassRoot);
            EmitManagedAssembly(
                kernelPath,
                "SingPlus.Kernel",
                "namespace SingPlus.Kernel; public static class KernelEntryPoint { public static int Run() => 42; }");
            EmitManagedAssembly(
                bootPath,
                "SingPlus.Boot",
                "namespace SingPlus.Boot; public static class Program { public static int Main() => 0; }");
            KernelDigest = Sha256(File.ReadAllBytes(kernelPath));
            BootDigest = Sha256(File.ReadAllBytes(bootPath));
            Inputs = new QualificationInputs(
                singRoot,
                hybridRoot,
                HybridRevision,
                "10.0.204",
                kernelPath,
                bootPath,
                proofPath,
                firstPassKernelPath,
                firstPassBootPath,
                firstPassProofPath);
            WriteAdmissionProof();
            File.Copy(kernelPath, firstPassKernelPath);
            File.Copy(bootPath, firstPassBootPath);
            File.Copy(proofPath, firstPassProofPath);
        }

        public string Root { get; }
        public string SingRevision { get; }
        public string HybridRevision { get; }
        public string HybridTree => RunGit(Inputs.HybridCpuRepositoryRoot, "rev-parse", "--verify", "HEAD^{tree}");
        public string KernelDigest { get; }
        public string BootDigest { get; }
        public string ProofDigest => Sha256(File.ReadAllBytes(Inputs.AdmissionProofPath));
        public string AdmissionSemanticDigest
        {
            get
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(Inputs.AdmissionProofPath));
                return document.RootElement.GetProperty("ProofDigest").GetString()!;
            }
        }
        public QualificationInputs Inputs { get; }

        public string CommitHybridCompilerContractVersion(int version)
        {
            var relativePath = Path.Combine(
                "HybridCPU_ISE", "CloseToHSL", "Core", "Contracts", "CompilerContract.cs");
            File.WriteAllText(
                Path.Combine(Inputs.HybridCpuRepositoryRoot, relativePath),
                $"public static class CompilerContract {{ public const int Version = {version}; }}\n",
                new UTF8Encoding(false));
            RunGit(Inputs.HybridCpuRepositoryRoot, "add", relativePath);
            RunGit(
                Inputs.HybridCpuRepositoryRoot,
                "-c", "user.name=SingPlus Qualification Tests",
                "-c", "user.email=qualification@example.invalid",
                "commit", "--quiet", "--no-gpg-sign", "-m", "change contract version");
            return RunGit(Inputs.HybridCpuRepositoryRoot, "rev-parse", "--verify", "HEAD^{commit}");
        }

        public static QualificationFixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "singplus-hybridcpu-qualification-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new QualificationFixture(root);
        }

        public void WriteProof(
            int forbiddenOperationCount = 0,
            string violationsJson = "[]",
            string? proofDigest = null,
            string? assemblyDigest = null)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Inputs.AdmissionProofPath));
            var root = document.RootElement;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("SchemaId", root.GetProperty("SchemaId").GetString());
                writer.WriteString("Root", root.GetProperty("Root").GetString());
                writer.WriteString("Profile", root.GetProperty("Profile").GetString());
                writer.WriteString(
                    "AssemblyDigest",
                    assemblyDigest ?? root.GetProperty("AssemblyDigest").GetString());
                writer.WriteNumber("ReachableMethodCount", root.GetProperty("ReachableMethodCount").GetInt32());
                writer.WriteNumber("ForbiddenOperationCount", forbiddenOperationCount);
                writer.WriteString("DependencyDigest", root.GetProperty("DependencyDigest").GetString());
                writer.WriteString("RulesetDigest", root.GetProperty("RulesetDigest").GetString());
                writer.WriteString("ProofDigest", proofDigest ?? root.GetProperty("ProofDigest").GetString());
                writer.WritePropertyName("Violations");
                using var violations = JsonDocument.Parse(violationsJson);
                violations.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                writer.Flush();
            }

            File.WriteAllBytes(Inputs.AdmissionProofPath, stream.ToArray());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string CreateRepository(string path, string marker)
        {
            Directory.CreateDirectory(path);
            RunGit(path, "init", "--quiet");
            File.WriteAllText(Path.Combine(path, "marker.txt"), marker, new UTF8Encoding(false));
            var sdkVersion = marker == "sing-next-os" ? "10.0.204" : "10.0.201";
            File.WriteAllText(
                Path.Combine(path, "global.json"),
                $"{{\"sdk\":{{\"version\":\"{sdkVersion}\"}}}}",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(path, ".gitignore"),
                "bin/\nobj/\nartifacts/\n",
                new UTF8Encoding(false));
            if (marker == "hybrid-cpu-v2")
            {
                var contractPath = Path.Combine(
                    path, "HybridCPU_ISE", "CloseToHSL", "Core", "Contracts", "CompilerContract.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(contractPath)!);
                File.WriteAllText(
                    contractPath,
                    "public static class CompilerContract { public const int Version = 6; }\n",
                    new UTF8Encoding(false));
            }
            RunGit(path, "add", "--all");
            RunGit(
                path,
                "-c", "user.name=SingPlus Qualification Tests",
                "-c", "user.email=qualification@example.invalid",
                "commit", "--quiet", "--no-gpg-sign", "-m", "fixture");
            return RunGit(path, "rev-parse", "--verify", "HEAD^{commit}");
        }

        private void WriteAdmissionProof()
        {
            var verification = AdmissionVerifier.Verify(
                Inputs.KernelAssemblyPath,
                QualificationRecorder.KernelEntryPoint,
                QualificationRecorder.KernelProfile);
            Assert.True(
                verification.IsAdmitted,
                string.Join(
                    Environment.NewLine,
                    verification.Violations.Select(static violation => violation.CanonicalKey)));
            File.WriteAllBytes(
                Inputs.AdmissionProofPath,
                verification.Proof.SerializeCanonical(verification.Violations));
        }

        private static void EmitManagedAssembly(string path, string assemblyName, string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.CSharp13));
            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                AnalyzerTests.PlatformReferences(),
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    deterministic: true));
            var emit = compilation.Emit(path);
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        }

        private static string RunGit(string repository, params string[] arguments)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("-C");
            process.StartInfo.ArgumentList.Add(repository);
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z";
            process.StartInfo.Environment["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z";
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git fixture command failed ({process.ExitCode}): {standardError.Trim()}");
            }

            return standardOutput.Trim();
        }

        private static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
