using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SingPlus.Admission;

namespace SingPlus.Tests.Admission;

public sealed class BootCapsuleAdmissionTests
{
    [Fact]
    public void P15_08_BootCapsuleProfileAdmitsBoundedScalarFixture()
    {
        using Compiled fixture = Compile("public static class Fixture { public static int Root() => 0; }");
        AdmissionVerificationResult result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "BootCapsule");
        Assert.True(result.IsAdmitted, string.Join(Environment.NewLine, result.Violations.Select(static x => x.CanonicalKey)));
    }

    [Fact]
    public void P15_08_ActualCapsuleAssemblyClosureRemainsFailClosed()
    {
        string assembly = typeof(SingNext.Boot.Capsule.BootCapsuleEntry).Assembly.Location;
        AdmissionVerificationResult result = AdmissionVerifier.Verify(assembly, "SingNext.Boot.Capsule.BootCapsuleEntry::Run", "BootCapsule");

        Assert.False(result.IsAdmitted);
        Assert.Contains(result.Violations, static x => x.Operation == "unknown-dependency-category");
    }

    [Fact]
    public void P15_08_FinalBoundedPrepareRootRemainsFailClosedUntilVerifierCanProveIt()
    {
        string assembly = typeof(SingNext.Boot.Capsule.BootCapsuleEntry).Assembly.Location;
        AdmissionVerificationResult result = AdmissionVerifier.Verify(assembly, "SingNext.Boot.Capsule.BootCapsuleEntry::Prepare", "BootCapsule");

        Assert.False(result.IsAdmitted);
        Assert.Contains(result.Violations, static x =>
            x.Operation is "unmanaged-memory" or "newobj" or "unknown-dependency-category");
    }

    [Fact]
    public void P15_08_BootCapsuleRejectsAllocationAndRuntimeDependency()
    {
        using Compiled allocating = Compile("public static class Fixture { public static int Root() { _ = new object(); return 0; } }");
        AdmissionVerificationResult allocation = AdmissionVerifier.Verify(allocating.AssemblyPath, "Fixture::Root", "BootCapsule");
        Assert.Contains(allocation.Violations, static x => x.Operation == "newobj");

        using Compiled runtime = Compile(
            "public static class Fixture { public static int Root(SingPlus.Runtime.HybridBootInfoImporter value) => value is null ? 0 : 1; }",
            MetadataReference.CreateFromFile(typeof(SingPlus.Runtime.HybridBootInfoImporter).Assembly.Location));
        AdmissionVerificationResult dependency = AdmissionVerifier.Verify(runtime.AssemblyPath, "Fixture::Root", "BootCapsule");
        Assert.Contains(dependency.Violations, static x => x.Operation == "unknown-dependency-category" && x.Detail == "SingPlus.Runtime");
    }

    private static Compiled Compile(string source, params MetadataReference[] additional)
    {
        string directory = Path.Combine(Path.GetTempPath(), "singnext-capsule-admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "Fixture.dll");
        IEnumerable<MetadataReference> references = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!.ToString()!.Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)).Concat(additional);
        CSharpCompilation compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        EmitResult emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return new Compiled(directory, output);
    }

    private sealed class Compiled(string directory, string assemblyPath) : IDisposable
    {
        public string AssemblyPath { get; } = assemblyPath;
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
