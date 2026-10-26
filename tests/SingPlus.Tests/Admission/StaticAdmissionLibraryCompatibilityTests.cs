using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Admission;
using SingPlus.Tests.Analyzers;
using SingPlus.HybridCpuQualification;
using Microsoft.CodeAnalysis.Emit;

namespace SingPlus.Tests.Admission;

public sealed class StaticAdmissionLibraryCompatibilityTests
{
    public static IEnumerable<object[]> LegacyTypes => new Type[]
    {
        typeof(AdmissionVerifier), typeof(AdmissionViolation), typeof(SingPlusAdmissionProofV1),
        typeof(AdmissionVerificationResult), typeof(ManagedCapPolicyDescriptor), typeof(ManagedAsyncPeKind),
        typeof(ManagedAsyncPeQualificationResult), typeof(ManagedAsyncPeQualification), typeof(ManagedAsyncAbi),
        typeof(ManagedAsyncSemanticOperation), typeof(ManagedAsyncFrontendContractV1),
        typeof(ManagedAsyncAdmissionDisposition), typeof(ManagedAsyncFrontendQualification)
    }.Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(LegacyTypes))]
    [Trait("Category", "Admission")]
    public void LegacyAssemblyQualifiedNamesResolveToTheSameLibraryType(Type expected)
    {
        Assembly cli = typeof(AdmissionTelemetry).Assembly;
        // Persisted V1 client names contain the old assembly identity, not the new library name.
        string legacyName = expected.FullName + ", " + cli.FullName;
        Assert.Same(expected, Type.GetType(legacyName, throwOnError: true));
        Assert.Same(expected, cli.GetType(expected.FullName!, throwOnError: true));
        Assert.Contains(expected, cli.GetForwardedTypes());
        Assert.DoesNotContain(cli.DefinedTypes, type => type.FullName == expected.FullName);
    }

    [Fact]
    [Trait("Category", "Admission")]
    public void FreshLoadContextResolvesEveryLegacyIdentityWithoutDefaultContextTypeLeakage()
    {
        AssemblyLoadContext context = new AssemblyLoadContext("admission-compatibility-" + Guid.NewGuid(), isCollectible: true);
        string cliPath = typeof(AdmissionTelemetry).Assembly.Location;
        string libraryPath = typeof(AdmissionVerifier).Assembly.Location;
        context.Resolving += (loader, name) => name.Name == "SingPlus.StaticAdmission"
            ? loader.LoadFromAssemblyPath(libraryPath) : null;
        try
        {
            // Populate this context before dependency resolution can reuse the default
            // context's already loaded library. Resolving runs only after that fallback.
            Assembly isolatedLibrary = context.LoadFromAssemblyPath(libraryPath);
            Assembly cli = context.LoadFromAssemblyPath(cliPath);
            Type[] forwarded = cli.GetForwardedTypes();
            Assert.Equal(13, forwarded.Length);
            foreach (object[] row in LegacyTypes)
            {
                Type original = (Type)row[0];
                Type resolved = Type.GetType(original.FullName + ", " + cli.FullName,
                    name => context.LoadFromAssemblyName(name),
                    (assembly, name, ignoreCase) => assembly!.GetType(name, false, ignoreCase),
                    throwOnError: true)!;
                Assert.Contains(resolved, forwarded);
                Assert.Same(resolved, cli.GetType(original.FullName!, true));
                Assert.NotSame(original, resolved);
                Assert.Same(isolatedLibrary, resolved.Assembly);
                Assert.Equal("SingPlus.StaticAdmission", resolved.Assembly.GetName().Name);
                Assert.Same(context, AssemblyLoadContext.GetLoadContext(resolved.Assembly));
            }
        }
        finally
        {
            context.Unload();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Admission")]
    public async Task LegacyReflectiveVerifierAndCliPreserveCanonicalProofAndExit(bool rejected)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "admission-compatibility", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string assemblyPath = Path.Combine(directory, "CompatibilityFixture.dll");
            string proofPath = Path.Combine(directory, "proof.json");
            string source = rejected
                ? "public static class Fixture { public static object Root() => new object(); }"
                : "public static class Fixture { public static int Root() => 7; }";
            CSharpCompilation compilation = CSharpCompilation.Create("CompatibilityFixture", [CSharpSyntaxTree.ParseText(source)],
                AnalyzerTests.PlatformReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            EmitResult emit = compilation.Emit(assemblyPath);
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

            Assembly cli = typeof(AdmissionTelemetry).Assembly;
            Type legacyVerifier = Type.GetType("SingPlus.Admission.AdmissionVerifier, " + cli.FullName, true)!;
            AdmissionVerificationResult reflective = Assert.IsType<AdmissionVerificationResult>(legacyVerifier.GetMethod("Verify")!
                .Invoke(null, [assemblyPath, "Fixture::Root", "KernelNoHeap"]));
            AdmissionVerificationResult direct = AdmissionVerifier.Verify(assemblyPath, "Fixture::Root", "KernelNoHeap");
            byte[] canonical = direct.Proof.SerializeCanonical(direct.Violations);
            Assert.Equal(canonical, reflective.Proof.SerializeCanonical(reflective.Violations));
            Assert.Equal(!rejected, reflective.IsAdmitted);
            Assert.False(reflective.AuthorizesExecution);
            Assert.False(reflective.MaterializesCapability);
            Assert.False(reflective.IsRuntimeAuthority);
            Assert.False(reflective.Proof.AuthorizesExecution);
            Assert.False(reflective.Proof.MaterializesCapability);
            Assert.False(reflective.Proof.IsRuntimeAuthority);

            ProcessStartInfo start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string? argument in new[] { "exec", "--runtimeconfig",
                Path.Combine(AppContext.BaseDirectory, "SingPlus.Tests.runtimeconfig.json"), cli.Location,
                "verify", "--assembly", assemblyPath, "--root", "Fixture::Root", "--profile", "KernelNoHeap",
                "--proof", proofPath }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            await output;
            string diagnostic = await error;
            Assert.True(process.ExitCode == (rejected ? 2 : 0), diagnostic);
            Assert.Equal(canonical, File.ReadAllBytes(proofPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown-mandatory", "not canonical")]
    [InlineData("malformed", "not valid JSON")]
    [InlineData("assembly-digest", "AssemblyDigest does not match")]
    [Trait("Category", "Admission")]
    public void ExistingQualificationConsumerRejectsMutatedLegacyProof(string fault, string diagnostic)
    {
        // Reuse the existing qualification fixture, without changing its shared owner or
        // duplicating its synthetic Git/toolchain/PE setup. Reflection is confined to
        // test scaffolding; the exercised entry point is the production recorder.
        Type fixtureType = typeof(SingPlus.Tests.Qualification.HybridCpuQualificationTests)
            .GetNestedType("QualificationFixture", BindingFlags.NonPublic)!;
        using IDisposable fixture = (IDisposable)fixtureType.GetMethod("Create")!.Invoke(null, null)!;
        QualificationInputs inputs = (QualificationInputs)fixtureType.GetProperty("Inputs")!.GetValue(fixture)!;
        byte[] baselineBytes = File.ReadAllBytes(inputs.AdmissionProofPath);
        byte[] baselineReport = QualificationRecorder.RecordExternalBlocked(inputs);
        using (JsonDocument report = JsonDocument.Parse(baselineReport))
            Assert.Equal("ExternalBlocked", report.RootElement.GetProperty("Outcome").GetString());

        string proof = Encoding.UTF8.GetString(baselineBytes);
        string mutated = fault switch
        {
            "unknown-mandatory" => proof[..^1] + ",\"MandatoryRuntimePermissionV99\":true}",
            "malformed" => "{not-json",
            "assembly-digest" => ReplaceAssemblyDigest(proof),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        File.WriteAllText(inputs.AdmissionProofPath, mutated, new UTF8Encoding(false));
        byte[] expectedBytes = File.ReadAllBytes(inputs.AdmissionProofPath);
        QualificationException error = Assert.Throws<QualificationException>(() => QualificationRecorder.RecordExternalBlocked(inputs));
        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        Assert.Equal(expectedBytes, File.ReadAllBytes(inputs.AdmissionProofPath));
        Assert.Equal(baselineBytes, File.ReadAllBytes(inputs.FirstPassAdmissionProofPath));
        File.WriteAllBytes(inputs.AdmissionProofPath, baselineBytes);
        Assert.Equal(baselineReport, QualificationRecorder.RecordExternalBlocked(inputs));

        static string ReplaceAssemblyDigest(string canonical)
        {
            using JsonDocument document = JsonDocument.Parse(canonical);
            string digest = document.RootElement.GetProperty("AssemblyDigest").GetString()!;
            string changed = (digest[0] == '0' ? "1" : "0") + digest[1..];
            return canonical.Replace("\"AssemblyDigest\":\"" + digest + "\"",
                "\"AssemblyDigest\":\"" + changed + "\"", StringComparison.Ordinal);
        }
    }
}
