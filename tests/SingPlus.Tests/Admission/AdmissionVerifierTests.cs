using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Admission;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Admission;

public sealed class AdmissionVerifierTests
{
    public static IEnumerable<object[]> ForbiddenCases()
    {
        yield return new object[] { "public static class Fixture { public static object Root() => new object(); }", "newobj" };
        yield return new object[] { "public static class Fixture { public static byte[] Root() => new byte[4]; }", "newarr" };
        yield return new object[] { "public static class Fixture { public static object Root() => 42; }", "box" };
        yield return new object[] { "public static class Fixture { public static void Root() => System.GC.Collect(); }", "forbidden-api" };
    }

    [Theory]
    [MemberData(nameof(ForbiddenCases))]
    [Trait("Category", "Admission")]
    public void KernelNoHeapRejectsForbiddenCil(string source, string operation)
    {
        using var fixture = CompileFixture(source);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");

        Assert.False(result.IsAdmitted);
        Assert.Contains(result.Violations, violation => violation.Operation == operation);
        Assert.Equal(result.Violations.Count, result.Proof.ForbiddenOperationCount);
    }

    [Fact]
    [Trait("Category", "Admission")]
    public void TransitiveForbiddenOperationIsFoundThroughReachabilityGraph()
    {
        const string source = "public static class Fixture { public static int Root() => HelperA(); static int HelperA() => HelperB(); static int HelperB() { _ = new object(); return 7; } }";
        using var fixture = CompileFixture(source);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");

        Assert.False(result.IsAdmitted);
        Assert.True(result.Proof.ReachableMethodCount >= 3);
        Assert.Contains(result.Violations, violation => violation.Operation == "newobj" && violation.Method.EndsWith("Fixture::HelperB", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Admission")]
    public void ValueOnlyReachableGraphIsAdmitted()
    {
        const string source = "public static class Fixture { public static int Root() => Add(2, 3); static int Add(int x, int y) => x + y; }";
        using var fixture = CompileFixture(source);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");

        Assert.True(result.IsAdmitted, string.Join(Environment.NewLine, result.Violations.Select(static v => v.CanonicalKey)));
        Assert.Equal(0, result.Proof.ForbiddenOperationCount);
        Assert.True(result.Proof.ReachableMethodCount >= 2);
    }

    [Fact]
    [Trait("Category", "Admission")]
    public void UnknownDependencyCategoryIsRejected()
    {
        using var fixture = CompileWithUnknownDependency();

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");

        Assert.False(result.IsAdmitted);
        Assert.Contains(result.Violations, violation => violation.Operation == "unknown-dependency-category" && violation.Detail == "ThirdParty.Unknown");
    }

    [Fact]
    [Trait("Category", "Admission")]
    [Trait("Category", "Determinism")]
    public void RepeatedVerificationProducesIdenticalProofAndRulesetDigest()
    {
        const string source = "public static class Fixture { public static int Root() => Helper(); static int Helper() => 17; }";
        using var fixture = CompileFixture(source);

        var first = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");
        var second = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");

        Assert.Equal(first.Proof.AssemblyDigest, second.Proof.AssemblyDigest);
        Assert.Equal(first.Proof.DependencyDigest, second.Proof.DependencyDigest);
        Assert.Equal(first.Proof.RulesetDigest, second.Proof.RulesetDigest);
        Assert.Equal(first.Proof.ProofDigest, second.Proof.ProofDigest);
        Assert.Equal(first.Proof.SerializeCanonical(first.Violations), second.Proof.SerializeCanonical(second.Violations));
    }

    private static CompiledFixture CompileFixture(string source, string assemblyName = "AdmissionFixture", IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "singplus-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, assemblyName + ".dll");
        Emit(source, assemblyName, outputPath, additionalReferences);
        return new CompiledFixture(directory, outputPath);
    }

    private static CompiledFixture CompileWithUnknownDependency()
    {
        var directory = Path.Combine(Path.GetTempPath(), "singplus-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "ThirdParty.Unknown.dll");
        Emit("namespace ThirdParty { public static class Api { public static int Value() => 9; } }", "ThirdParty.Unknown", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        var dependencyReference = MetadataReference.CreateFromFile(dependencyPath);
        Emit("public static class Fixture { public static int Root() => ThirdParty.Api.Value(); }", "AdmissionFixture", rootPath, new[] { dependencyReference });
        return new CompiledFixture(directory, rootPath);
    }

    private static void Emit(string source, string assemblyName, string outputPath, IEnumerable<MetadataReference>? additionalReferences)
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        if (additionalReferences is not null) references.AddRange(additionalReferences);
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release).WithDeterministic(true);
        var compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, references, options);
        var emit = compilation.Emit(outputPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
    }

    private sealed class CompiledFixture(string directory, string assemblyPath) : IDisposable
    {
        public string AssemblyPath { get; } = assemblyPath;

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
