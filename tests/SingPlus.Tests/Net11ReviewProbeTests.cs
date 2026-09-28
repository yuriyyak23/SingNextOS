using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Admission;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests;

public sealed class Net11ReviewProbeTests
{
    [Theory]
    [InlineData("unsafe class C { void M() { int* p = stackalloc[] { 1, 2 }; } }", "SING1008")]
    [InlineData("unsafe struct S { public int X; } unsafe class C { void M(S* p) { (*p).X = 1; } }", "SING1010")]
    [InlineData("unsafe class C { void M(int* p) { ref int r = ref *p; r = 1; } }", "SING1010")]
    [InlineData("unsafe class Provider { public static int Value => 1; } class C { int M() => Provider.Value; }", "SING1011")]
    public async Task SourceContour(string source, string expected)
    {
        using var output = new MemoryStream();
        var compilation = CSharpCompilation.Create("Probe", [CSharpSyntaxTree.ParseText(source)], AnalyzerTests.PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        foreach (var (profile, memory) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memory, language);
            Assert.Contains(diagnostics, d => d.Id == expected);
        }
    }

    [Fact]
    public void GenericReachability()
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "Probe.dll");
            Emit("public static class G<T> { public static int Read() { _ = new object(); return 1; } } public static class Fixture { public static int Root() => G<int>.Read(); }", "Probe", path);
            var result = AdmissionVerifier.Verify(path, "Fixture::Root", "KernelNoHeap");
            Assert.False(result.IsAdmitted);
        });
    }

    [Fact]
    public void DuplicateDependencyIdentity()
    {
        WithDirectory(directory =>
        {
            var safe = Path.Combine(directory, "A.dll");
            var unsafePath = Path.Combine(directory, "SingPlus.Dep.dll");
            Emit("public static class Bridge { public static int Read() => 1; }", "SingPlus.Dep", safe);
            Emit("public static class Bridge { public static int Read() { _ = new object(); return 1; } }", "SingPlus.Dep", unsafePath);
            var root = Path.Combine(directory, "Probe.dll");
            Emit("public static class Fixture { public static int Root() => Bridge.Read(); }", "Probe", root, MetadataReference.CreateFromFile(unsafePath));
            var rejection = Assert.Throws<InvalidOperationException>(() => AdmissionVerifier.Verify(root, "Fixture::Root", "KernelNoHeap"));
            Assert.Contains("Ambiguous local assembly identity", rejection.Message);
        });
    }

    [Fact]
    public void NestedDependencyReachability()
    {
        WithDirectory(directory =>
        {
            var dependency = Path.Combine(directory, "SingPlus.Dep.dll");
            Emit("public static class Outer { public static class Inner { public static int Read() { _ = new object(); return 1; } } }", "SingPlus.Dep", dependency);
            var root = Path.Combine(directory, "Probe.dll");
            Emit("public static class Fixture { public static int Root() => Outer.Inner.Read(); }", "Probe", root, MetadataReference.CreateFromFile(dependency));
            Assert.False(AdmissionVerifier.Verify(root, "Fixture::Root", "KernelNoHeap").IsAdmitted);
        });
    }

    [Theory]
    [InlineData("public static class G<T> { public static int Read() => 1; } public static class Fixture { public static int Root() => G<int>.Read(); }")]
    [InlineData("public static class Outer { public static class Inner { public static int Read() => 1; } } public static class Fixture { public static int Root() => Outer.Inner.Read(); }")]
    public void SupportedValueOnlyCalleesRemainAdmitted(string source)
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "Probe.dll");
            Emit(source, "Probe", path);
            var result = AdmissionVerifier.Verify(path, "Fixture::Root", "KernelNoHeap");
            Assert.True(result.IsAdmitted);
            Assert.True(result.Proof.ReachableMethodCount >= 2);
        });
    }

    [Theory]
    [InlineData("class C { int M() { System.Span<int> p = stackalloc[] { 1, 2 }; return p[0]; } }", false)]
    [InlineData("unsafe class C { int M(int* p) { ref readonly int r = ref *p; return r; } }", true)]
    [InlineData("unsafe struct S { public int X; } unsafe class C { int* M(S* p) => &p->X; }", false)]
    public async Task FormationAndReadonlyControlsDoNotClaimWrites(string source, bool expectedRead)
    {
        using var image = new MemoryStream();
        Assert.True(CSharpCompilation.Create("Control", [CSharpSyntaxTree.ParseText(source)], AnalyzerTests.PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)).Emit(image).Success);
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        {
            var diagnostics = await Analyze(source, "Sip", "SipRegion", language);
            Assert.DoesNotContain(diagnostics, d => d.Id == "SING1010");
            Assert.Equal(expectedRead, diagnostics.Any(d => d.Id == "SING1009"));
            if (source.Contains("Span<int>")) Assert.DoesNotContain(diagnostics, d => d.Id == "SING1008");
        }
    }

    [Theory]
    [InlineData(true, "int M(Provider p) => p.Value;")]
    [InlineData(true, "void M(Provider p) { p.Value = 1; }")]
    [InlineData(true, "void M(Provider p) { p[0]++; }")]
    [InlineData(false, "int M(Provider p) => p.Value;")]
    [InlineData(false, "void M(Provider p) { p.Value = 1; }")]
    [InlineData(false, "void M(Provider p) { p[0]++; }")]
    public async Task SelectedAccessorsPreserveSafeControls(bool unsafeType, string method)
    {
        var source = (unsafeType ? "unsafe " : "") + "class Provider { public int Value { get; set; } public int this[int i] { get => 1; set { } } } class C { " + method + " }";
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        {
            var diagnostics = await Analyze(source, "Sip", "SipRegion", language);
            Assert.Equal(unsafeType, diagnostics.Any(d => d.Id == "SING1011"));
        }
    }

    private static Task<ImmutableArray<Diagnostic>> Analyze(string source, string profile, string memory, LanguageVersion language) =>
        (Task<ImmutableArray<Diagnostic>>)typeof(AnalyzerTests).GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source, profile, memory, language, null])!;

    private static void Emit(string source, string name, string path, MetadataReference? additional = null)
    {
        var references = AnalyzerTests.PlatformReferences().AsEnumerable();
        if (additional is not null) references = references.Append(additional);
        var result = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }

    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "review-probes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
