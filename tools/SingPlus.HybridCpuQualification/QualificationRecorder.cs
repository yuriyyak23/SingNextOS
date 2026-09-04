using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SingPlus.Admission;

namespace SingPlus.HybridCpuQualification;

internal sealed record QualificationInputs(
    string SingRepositoryRoot,
    string HybridCpuRepositoryRoot,
    string ExpectedHybridCpuRevision,
    string DotNetSdkVersion,
    string KernelAssemblyPath,
    string BootAssemblyPath,
    string AdmissionProofPath,
    string FirstPassKernelAssemblyPath,
    string FirstPassBootAssemblyPath,
    string FirstPassAdmissionProofPath);

internal sealed class QualificationException(string message) : InvalidOperationException(message);

internal static partial class QualificationRecorder
{
    internal const string Schema = "SingPlusHybridCpuQualificationV1";
    internal const string AdmissionSchema = "SingPlusAdmissionProofV1";
    internal const string KernelEntryPoint = "SingPlus.Kernel.KernelEntryPoint::Run";
    internal const string KernelProfile = "KernelNoHeap";
    internal const string ExternalBlockReason = "PrebuiltHybridCpuManagedAssemblyToolchainNotSupplied";
    internal const string AuditedHybridCpuRevision = "9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9";
    internal const int AuditedHybridCpuCompilerContractVersion = 6;

    private const string HybridCpuCompilerContractPath = "HybridCPU_ISE/CloseToHSL/Core/Contracts/CompilerContract.cs";
    private const string KernelArtifactPath = "src/Kernel/SingPlus.Kernel/bin/Release/net10.0/SingPlus.Kernel.dll";
    private const string BootArtifactPath = "src/Kernel/Boot/SingPlus.Boot/bin/Release/net10.0/SingPlus.Boot.dll";
    private const string AdmissionArtifactPath = "artifacts/hybridcpu-aot-qualification/SingPlusAdmissionProofV1.json";
    internal const string QualificationReportArtifactPath = "artifacts/hybridcpu-aot-qualification/SingPlusHybridCpuQualificationV1.json";
    private const string FirstPassKernelArtifactPath = "artifacts/hybridcpu-aot-qualification/pass1/SingPlus.Kernel.dll";
    private const string FirstPassBootArtifactPath = "artifacts/hybridcpu-aot-qualification/pass1/SingPlus.Boot.dll";
    private const string FirstPassAdmissionArtifactPath = "artifacts/hybridcpu-aot-qualification/pass1/SingPlusAdmissionProofV1.json";

    private static readonly string[] AdmissionPropertyNames =
    [
        "SchemaId",
        "Root",
        "Profile",
        "AssemblyDigest",
        "ReachableMethodCount",
        "ForbiddenOperationCount",
        "DependencyDigest",
        "RulesetDigest",
        "ProofDigest",
        "Violations"
    ];

    internal static byte[] RecordExternalBlocked(QualificationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var expectedHybridRevision = ValidateRevision(
            inputs.ExpectedHybridCpuRevision,
            nameof(inputs.ExpectedHybridCpuRevision));
        var expectedSdkVersion = ValidateSdkVersion(inputs.DotNetSdkVersion);
        var singRevision = ReadExactHead(inputs.SingRepositoryRoot, "SingNextOS");
        var hybridRevision = ReadExactHead(inputs.HybridCpuRepositoryRoot, "HybridCPU");
        if (!string.Equals(hybridRevision, expectedHybridRevision, StringComparison.Ordinal))
        {
            throw new QualificationException(
                $"HybridCPU HEAD '{hybridRevision}' does not match expected revision '{expectedHybridRevision}'.");
        }

        var singRequestedSdkVersion = ReadRequestedSdkVersion(inputs.SingRepositoryRoot, "SingNextOS");
        var hybridRequestedSdkVersion = ReadRequestedSdkVersion(inputs.HybridCpuRepositoryRoot, "HybridCPU");
        var actualSdkVersion = ReadDotNetSdkVersion(inputs.SingRepositoryRoot);
        if (!string.Equals(expectedSdkVersion, actualSdkVersion, StringComparison.Ordinal))
        {
            throw new QualificationException(
                $"Expected .NET SDK version '{expectedSdkVersion}' does not match observed version '{actualSdkVersion}'.");
        }
        if (!string.Equals(actualSdkVersion, singRequestedSdkVersion, StringComparison.Ordinal))
        {
            throw new QualificationException(
                $"Observed .NET SDK version '{actualSdkVersion}' does not match SingNextOS global.json version '{singRequestedSdkVersion}'.");
        }

        var hybridTree = ValidateRevision(
            RunGit(Path.GetFullPath(inputs.HybridCpuRepositoryRoot), "rev-parse", "--verify", "HEAD^{tree}"),
            "HybridCPU HEAD tree");
        var hybridCompilerContractVersion = ReadHybridCpuCompilerContractVersion(inputs.HybridCpuRepositoryRoot);
        ValidateCanonicalArtifactPath(inputs.SingRepositoryRoot, inputs.KernelAssemblyPath, KernelArtifactPath, "kernel assembly");
        ValidateCanonicalArtifactPath(inputs.SingRepositoryRoot, inputs.BootAssemblyPath, BootArtifactPath, "boot assembly");
        ValidateCanonicalArtifactPath(inputs.SingRepositoryRoot, inputs.AdmissionProofPath, AdmissionArtifactPath, "admission proof");
        ValidateCanonicalArtifactPath(inputs.SingRepositoryRoot, inputs.FirstPassKernelAssemblyPath, FirstPassKernelArtifactPath, "first-pass kernel assembly");
        ValidateCanonicalArtifactPath(inputs.SingRepositoryRoot, inputs.FirstPassBootAssemblyPath, FirstPassBootArtifactPath, "first-pass boot assembly");
        ValidateCanonicalArtifactPath(inputs.SingRepositoryRoot, inputs.FirstPassAdmissionProofPath, FirstPassAdmissionArtifactPath, "first-pass admission proof");

        var kernelBytes = ReadRequiredArtifact(inputs.KernelAssemblyPath, "kernel assembly");
        var bootBytes = ReadRequiredArtifact(inputs.BootAssemblyPath, "boot assembly");
        var admissionBytes = ReadRequiredArtifact(inputs.AdmissionProofPath, "admission proof");
        var firstPassKernelBytes = ReadRequiredArtifact(inputs.FirstPassKernelAssemblyPath, "first-pass kernel assembly");
        var firstPassBootBytes = ReadRequiredArtifact(inputs.FirstPassBootAssemblyPath, "first-pass boot assembly");
        var firstPassAdmissionBytes = ReadRequiredArtifact(inputs.FirstPassAdmissionProofPath, "first-pass admission proof");
        var kernelDigest = Sha256(kernelBytes);
        var bootDigest = Sha256(bootBytes);
        var admissionDigest = Sha256(admissionBytes);
        ValidateManagedAssembly(kernelBytes, "SingPlus.Kernel", "kernel assembly");
        ValidateManagedAssembly(bootBytes, "SingPlus.Boot", "host boot assembly");
        ValidateManagedAssembly(firstPassKernelBytes, "SingPlus.Kernel", "first-pass kernel assembly");
        ValidateManagedAssembly(firstPassBootBytes, "SingPlus.Boot", "first-pass host boot assembly");
        var proof = ValidateCanonicalAdmissionProof(admissionBytes, kernelDigest);
        ValidateWithCurrentAdmissionVerifier(inputs.KernelAssemblyPath, admissionBytes);
        ValidateCanonicalAdmissionProof(firstPassAdmissionBytes, Sha256(firstPassKernelBytes));
        RequireByteIdentical(firstPassKernelBytes, kernelBytes, "kernel assembly");
        RequireByteIdentical(firstPassBootBytes, bootBytes, "host boot assembly");
        RequireByteIdentical(firstPassAdmissionBytes, admissionBytes, "admission proof");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("SchemaId", Schema);
            writer.WriteString("Outcome", "ExternalBlocked");

            writer.WritePropertyName("Inputs");
            writer.WriteStartObject();
            writer.WriteString("SingNextOsRevision", singRevision);
            writer.WriteString("HybridCpuRevision", hybridRevision);
            writer.WriteString("HybridCpuTree", hybridTree);
            writer.WriteString("ExpectedHybridCpuRevision", expectedHybridRevision);
            writer.WriteString("SingNextOsRequestedSdkVersion", singRequestedSdkVersion);
            writer.WriteString("ActualDotNetSdkVersion", actualSdkVersion);
            writer.WriteString("HybridCpuRequestedSdkVersion", hybridRequestedSdkVersion);
            writer.WriteNumber("HybridCpuCompilerContractVersion", hybridCompilerContractVersion);
            writer.WriteString("KernelEntryPoint", KernelEntryPoint);
            writer.WriteEndObject();

            WriteReproductionCommands(writer);

            writer.WritePropertyName("Artifacts");
            writer.WriteStartArray();
            WriteArtifact(writer, "KernelAssembly", KernelArtifactPath, kernelDigest);
            WriteArtifact(writer, "HostBootAssembly", BootArtifactPath, bootDigest);
            WriteArtifact(writer, "AdmissionProof", AdmissionArtifactPath, admissionDigest);
            writer.WriteEndArray();

            writer.WritePropertyName("Admission");
            writer.WriteStartObject();
            writer.WriteString("SchemaId", AdmissionSchema);
            writer.WriteString("Root", proof.Root);
            writer.WriteString("Profile", proof.Profile);
            writer.WriteString("AssemblyDigest", proof.AssemblyDigest);
            writer.WriteString("ProofDigest", proof.ProofDigest);
            writer.WriteNumber("ReachableMethodCount", proof.ReachableMethodCount);
            writer.WriteNumber("ForbiddenOperationCount", proof.ForbiddenOperationCount);
            writer.WriteEndObject();

            writer.WritePropertyName("Reproducibility");
            writer.WriteStartObject();
            writer.WriteNumber("ComparedArtifactSets", 2);
            writer.WriteString("Comparison", "ByteIdentical");
            writer.WriteString("KernelAssemblySha256", kernelDigest);
            writer.WriteString("HostBootAssemblySha256", bootDigest);
            writer.WriteString("AdmissionProofSha256", admissionDigest);
            writer.WriteEndObject();

            writer.WritePropertyName("Stages");
            writer.WriteStartArray();
            WriteStage(writer, "LocalArtifacts", "Validated", null);
            WriteStage(writer, "LocalAdmissionProof", "Validated", null);
            WriteStage(writer, "LocalArtifactComparison", "Validated", null);
            WriteStage(writer, "ManagedAssemblyToHybridCpuAot", "ExternalBlocked", ExternalBlockReason);
            WriteStage(writer, "ImageGeneration", "NotProduced", "ManagedAssemblyToHybridCpuAot");
            WriteStage(writer, "IseLoaderAcceptance", "NotAttempted", "ImageGeneration");
            writer.WriteEndArray();

            writer.WriteNull("ToolchainIdentity");
            writer.WriteNull("AotCommand");
            writer.WriteNull("ImagePath");
            writer.WriteNull("ImageDigest");
            writer.WriteNull("IseCommand");
            writer.WriteNull("IseResult");
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static AdmissionProof ValidateCanonicalAdmissionProof(byte[] bytes, string kernelDigest)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException ex)
        {
            throw new QualificationException($"Admission proof is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new QualificationException("Admission proof root must be a JSON object.");

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != AdmissionPropertyNames.Length ||
                !properties.Select(static property => property.Name).SequenceEqual(AdmissionPropertyNames, StringComparer.Ordinal))
            {
                throw new QualificationException("Admission proof is not canonical SingPlusAdmissionProofV1 JSON.");
            }

            var schema = RequireString(properties[0], "SchemaId");
            var proofRoot = RequireString(properties[1], "Root");
            var profile = RequireString(properties[2], "Profile");
            var assemblyDigest = RequireDigest(properties[3], "AssemblyDigest");
            var reachableMethodCount = RequireNonNegativeInt32(properties[4], "ReachableMethodCount");
            var forbiddenOperationCount = RequireNonNegativeInt32(properties[5], "ForbiddenOperationCount");
            var dependencyDigest = RequireDigest(properties[6], "DependencyDigest");
            var rulesetDigest = RequireDigest(properties[7], "RulesetDigest");
            var proofDigest = RequireDigest(properties[8], "ProofDigest");

            if (!string.Equals(schema, AdmissionSchema, StringComparison.Ordinal))
                throw new QualificationException($"Admission proof schema must be '{AdmissionSchema}'.");
            if (!string.Equals(proofRoot, KernelEntryPoint, StringComparison.Ordinal))
                throw new QualificationException($"Admission proof root must be '{KernelEntryPoint}'.");
            if (!string.Equals(profile, KernelProfile, StringComparison.Ordinal))
                throw new QualificationException($"Admission proof profile must be '{KernelProfile}'.");
            if (reachableMethodCount == 0)
                throw new QualificationException("Admission proof must contain at least one reachable method.");
            if (forbiddenOperationCount != 0)
                throw new QualificationException("Admission proof contains forbidden operations.");
            if (properties[9].Value.ValueKind != JsonValueKind.Array || properties[9].Value.GetArrayLength() != 0)
                throw new QualificationException("Admitted proof Violations must be an empty array.");
            if (!string.Equals(assemblyDigest, kernelDigest, StringComparison.Ordinal))
                throw new QualificationException("Admission proof AssemblyDigest does not match kernel assembly SHA-256.");

            var proof = new AdmissionProof(
                proofRoot,
                profile,
                assemblyDigest,
                reachableMethodCount,
                forbiddenOperationCount,
                dependencyDigest,
                rulesetDigest,
                proofDigest);
            var expectedProofDigest = ComputeProofDigest(proof);
            if (!string.Equals(proofDigest, expectedProofDigest, StringComparison.Ordinal))
                throw new QualificationException("Admission proof ProofDigest does not match its semantic proof seed.");
            if (!SerializeCanonicalProof(proof).AsSpan().SequenceEqual(bytes))
                throw new QualificationException("Admission proof is not canonical SingPlusAdmissionProofV1 JSON.");

            return proof;
        }
    }

    private static void ValidateWithCurrentAdmissionVerifier(string kernelAssemblyPath, byte[] admissionBytes)
    {
        AdmissionVerificationResult verification;
        try
        {
            verification = AdmissionVerifier.Verify(kernelAssemblyPath, KernelEntryPoint, KernelProfile);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            throw new QualificationException($"Current admission verifier could not validate the kernel assembly: {ex.Message}");
        }

        if (!verification.IsAdmitted)
        {
            var firstViolation = verification.Violations.Count == 0
                ? "no violation detail"
                : verification.Violations[0].CanonicalKey;
            throw new QualificationException(
                $"Current admission verifier rejected the kernel assembly: {firstViolation}.");
        }

        var currentProof = verification.Proof.SerializeCanonical(verification.Violations);
        if (!currentProof.AsSpan().SequenceEqual(admissionBytes))
        {
            throw new QualificationException(
                "Admission proof does not match the canonical proof produced by the current AdmissionVerifier.");
        }
    }

    private static void ValidateManagedAssembly(byte[] bytes, string expectedName, string description)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                throw new QualificationException($"The {description} is not a managed PE assembly.");

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
                throw new QualificationException($"The {description} does not contain an assembly definition.");

            var actualName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (!string.Equals(actualName, expectedName, StringComparison.Ordinal))
            {
                throw new QualificationException(
                    $"The {description} assembly name '{actualName}' does not match expected '{expectedName}'.");
            }
        }
        catch (BadImageFormatException ex)
        {
            throw new QualificationException($"The {description} is not a valid managed PE assembly: {ex.Message}");
        }
    }

    private static byte[] SerializeCanonicalProof(AdmissionProof proof)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("SchemaId", AdmissionSchema);
            writer.WriteString("Root", proof.Root);
            writer.WriteString("Profile", proof.Profile);
            writer.WriteString("AssemblyDigest", proof.AssemblyDigest);
            writer.WriteNumber("ReachableMethodCount", proof.ReachableMethodCount);
            writer.WriteNumber("ForbiddenOperationCount", proof.ForbiddenOperationCount);
            writer.WriteString("DependencyDigest", proof.DependencyDigest);
            writer.WriteString("RulesetDigest", proof.RulesetDigest);
            writer.WriteString("ProofDigest", proof.ProofDigest);
            writer.WritePropertyName("Violations");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static string ComputeProofDigest(AdmissionProof proof)
    {
        var proofSeed = string.Join("\n",
        new[]
        {
            AdmissionSchema,
            proof.Root,
            proof.Profile,
            proof.AssemblyDigest,
            proof.ReachableMethodCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            proof.ForbiddenOperationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            proof.DependencyDigest,
            proof.RulesetDigest,
            string.Empty
        });
        return Sha256(Encoding.UTF8.GetBytes(proofSeed));
    }

    private static string ReadRequestedSdkVersion(string repositoryRoot, string repositoryName)
    {
        var committedJson = RunGit(Path.GetFullPath(repositoryRoot), "show", "HEAD:global.json");
        try
        {
            using var document = JsonDocument.Parse(committedJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("sdk", out var sdk) ||
                sdk.ValueKind != JsonValueKind.Object ||
                !sdk.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String)
            {
                throw new QualificationException($"{repositoryName} global.json must contain sdk.version.");
            }

            return ValidateSdkVersion(version.GetString()!);
        }
        catch (JsonException ex)
        {
            throw new QualificationException($"{repositoryName} global.json is not valid JSON: {ex.Message}");
        }
    }

    private static int ReadHybridCpuCompilerContractVersion(string repositoryRoot)
    {
        var source = RunGit(
            Path.GetFullPath(repositoryRoot),
            "show",
            $"HEAD:{HybridCpuCompilerContractPath}");
        var matches = CompilerContractVersionPattern().Matches(source);
        if (matches.Count != 1 ||
            !int.TryParse(
                matches[0].Groups[1].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var version))
        {
            throw new QualificationException(
                $"HybridCPU committed '{HybridCpuCompilerContractPath}' must declare exactly one integer CompilerContract.Version.");
        }

        if (version != AuditedHybridCpuCompilerContractVersion)
        {
            throw new QualificationException(
                $"HybridCPU CompilerContract.Version '{version}' does not match audited version '{AuditedHybridCpuCompilerContractVersion}'.");
        }

        return version;
    }

    private static string ReadDotNetSdkVersion(string repositoryRoot)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetFullPath(repositoryRoot),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("--version");

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new QualificationException($"Unable to query the active .NET SDK: {ex.Message}");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new QualificationException(
                $"dotnet --version failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return ValidateSdkVersion(output.Trim());
    }

    private static void ValidateCanonicalArtifactPath(
        string repositoryRoot,
        string providedPath,
        string canonicalRelativePath,
        string description)
    {
        if (string.IsNullOrWhiteSpace(providedPath))
            throw new ArgumentException($"Path to {description} is required.", nameof(providedPath));

        var expectedPath = Path.GetFullPath(Path.Combine(repositoryRoot, canonicalRelativePath));
        var actualPath = Path.GetFullPath(providedPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expectedPath, actualPath, comparison))
        {
            throw new QualificationException(
                $"The {description} path must be the canonical SingNextOS path '{expectedPath}'.");
        }
    }

    internal static string ResolveCanonicalReportPath(string repositoryRoot, string providedPath)
    {
        ValidateCanonicalArtifactPath(
            repositoryRoot,
            providedPath,
            QualificationReportArtifactPath,
            "qualification report");
        return Path.GetFullPath(providedPath);
    }

    private static string ReadExactHead(string repositoryRoot, string repositoryName)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new ArgumentException($"{repositoryName} repository root is required.", nameof(repositoryRoot));

        var fullRoot = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(fullRoot))
            throw new QualificationException($"{repositoryName} repository root '{fullRoot}' does not exist.");

        var actualRoot = RunGit(fullRoot, "rev-parse", "--show-toplevel");
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(TrimDirectorySeparator(Path.GetFullPath(actualRoot)), TrimDirectorySeparator(fullRoot), pathComparison))
            throw new QualificationException($"{repositoryName} repository root must be the exact Git worktree root.");

        var worktreeStatus = RunGit(fullRoot, "status", "--porcelain=v1", "--untracked-files=all");
        if (!string.IsNullOrEmpty(worktreeStatus))
            throw new QualificationException($"{repositoryName} worktree must be clean for qualification.");

        return ValidateRevision(RunGit(fullRoot, "rev-parse", "--verify", "HEAD^{commit}"), repositoryName + " HEAD");
    }

    private static string RunGit(string repositoryRoot, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new QualificationException($"Unable to start Git: {ex.Message}");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new QualificationException($"Git failed with exit code {process.ExitCode}: {error.Trim()}");
        return output.Trim();
    }

    private static string ValidateRevision(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !RevisionPattern().IsMatch(value))
            throw new QualificationException($"{fieldName} must be an exact 40-character Git revision.");
        return value.ToLowerInvariant();
    }

    private static string ValidateSdkVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !SdkVersionPattern().IsMatch(value))
        {
            throw new QualificationException("DotNetSdkVersion must be an exact .NET SDK version string.");
        }

        return value;
    }

    private static void RequireByteIdentical(byte[] firstPass, byte[] secondPass, string description)
    {
        if (!firstPass.AsSpan().SequenceEqual(secondPass))
        {
            throw new QualificationException(
                $"The two supplied {description} artifact sets are not byte-identical.");
        }
    }

    private static byte[] ReadRequiredArtifact(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"Path to {description} is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new QualificationException($"Required {description} '{fullPath}' does not exist.");
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length == 0)
            throw new QualificationException($"Required {description} '{fullPath}' is empty.");
        return bytes;
    }

    private static string RequireString(JsonProperty property, string name)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
            throw new QualificationException($"Admission proof {name} must be a string.");
        return property.Value.GetString()!;
    }

    private static string RequireDigest(JsonProperty property, string name)
    {
        var value = RequireString(property, name);
        if (!DigestPattern().IsMatch(value))
            throw new QualificationException($"Admission proof {name} must be a lowercase SHA-256 digest.");
        return value;
    }

    private static int RequireNonNegativeInt32(JsonProperty property, string name)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value) || value < 0)
            throw new QualificationException($"Admission proof {name} must be a non-negative integer.");
        return value;
    }

    private static void WriteReproductionCommands(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("ReproductionCommands");
        writer.WriteStartArray();
        WriteCommand(writer, "DotNetSdkIdentity",
        [
            "dotnet", "--version"
        ]);
        WriteCommand(writer, "BootRestore",
        [
            "dotnet", "restore", "src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj"
        ]);
        WriteCommand(writer, "RecorderRestore",
        [
            "dotnet", "restore", "tools/SingPlus.HybridCpuQualification/SingPlus.HybridCpuQualification.csproj"
        ]);
        WriteCommand(writer, "RecorderBuild",
        [
            "dotnet", "build", "tools/SingPlus.HybridCpuQualification/SingPlus.HybridCpuQualification.csproj",
            "--configuration", "Release", "--no-restore", "-p:ContinuousIntegrationBuild=true"
        ]);
        WriteCommand(writer, "FirstPassClean",
        [
            "dotnet", "clean", "src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj",
            "--configuration", "Release"
        ]);
        WriteCommand(writer, "FirstPassBuild",
        [
            "dotnet", "build", "src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj",
            "--configuration", "Release", "--no-restore", "-p:ContinuousIntegrationBuild=true"
        ]);
        WriteCommand(writer, "FirstPassAdmission",
        [
            "dotnet", "tools/SingPlus.Admission/bin/Release/net10.0/SingPlus.Admission.dll", "verify",
            "--assembly", KernelArtifactPath,
            "--root", KernelEntryPoint,
            "--profile", KernelProfile,
            "--proof", AdmissionArtifactPath
        ]);
        WriteCommand(writer, "PreserveFirstPassKernel",
        [
            "cp", "--", KernelArtifactPath, FirstPassKernelArtifactPath
        ]);
        WriteCommand(writer, "PreserveFirstPassBoot",
        [
            "cp", "--", BootArtifactPath, FirstPassBootArtifactPath
        ]);
        WriteCommand(writer, "PreserveFirstPassAdmission",
        [
            "cp", "--", AdmissionArtifactPath, FirstPassAdmissionArtifactPath
        ]);
        WriteCommand(writer, "SecondPassClean",
        [
            "dotnet", "clean", "src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj",
            "--configuration", "Release"
        ]);
        WriteCommand(writer, "SecondPassBuild",
        [
            "dotnet", "build", "src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj",
            "--configuration", "Release", "--no-restore", "-p:ContinuousIntegrationBuild=true"
        ]);
        WriteCommand(writer, "SecondPassAdmission",
        [
            "dotnet", "tools/SingPlus.Admission/bin/Release/net10.0/SingPlus.Admission.dll", "verify",
            "--assembly", KernelArtifactPath,
            "--root", KernelEntryPoint,
            "--profile", KernelProfile,
            "--proof", AdmissionArtifactPath
        ]);
        writer.WriteEndArray();
    }

    private static void WriteCommand(Utf8JsonWriter writer, string stage, IReadOnlyList<string> arguments)
    {
        writer.WriteStartObject();
        writer.WriteString("Stage", stage);
        writer.WritePropertyName("Argv");
        writer.WriteStartArray();
        foreach (var argument in arguments) writer.WriteStringValue(argument);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteArtifact(Utf8JsonWriter writer, string kind, string path, string digest)
    {
        writer.WriteStartObject();
        writer.WriteString("Kind", kind);
        writer.WriteString("Path", path);
        writer.WriteString("Sha256", digest);
        writer.WriteEndObject();
    }

    private static void WriteStage(Utf8JsonWriter writer, string name, string outcome, string? reason)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", name);
        writer.WriteString("Outcome", outcome);
        if (reason is null) writer.WriteNull("Reason");
        else writer.WriteString("Reason", reason);
        writer.WriteEndObject();
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string TrimDirectorySeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkVersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"public\s+const\s+int\s+Version\s*=\s*([0-9]+)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerContractVersionPattern();

    private sealed record AdmissionProof(
        string Root,
        string Profile,
        string AssemblyDigest,
        int ReachableMethodCount,
        int ForbiddenOperationCount,
        string DependencyDigest,
        string RulesetDigest,
        string ProofDigest);
}
