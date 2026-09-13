using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SingPlus.Analyzers;

namespace SingPlus.Tests.Analyzers;

public sealed class AnalyzerTests
{
    public static IEnumerable<object[]> KernelNoHeapCases()
    {
        yield return new object[] { "class C { object M() => new object(); }", "SING1001" };
        yield return new object[] { "class C { byte[] M() => new byte[4]; }", "SING1001" };
        yield return new object[] { "class C { object M() { object x = 1; return x; } }", "SING1005" };
        yield return new object[] { "class C { void M() => System.Threading.Tasks.Task.Run(() => { }); }", "SING1004" };
        yield return new object[] { "class C { void M() => System.GC.Collect(); }", "SING1004" };
        yield return new object[] { "class C { void M(byte[] x) => System.Reflection.Assembly.Load(x); }", "SING1004" };
        yield return new object[] { "class C { int Root() => A(); int A() => B(); int B() { _ = new object(); return 0; } }", "SING1001" };
    }

    [Theory]
    [MemberData(nameof(KernelNoHeapCases))]
    [Trait("Category", "Analyzers")]
    [Trait("Category", "NegativeCompilation")]
    public async Task KernelNoHeapRejectsForbiddenSource(string source, string expectedId)
    {
        var diagnostics = await Analyze(source, "Kernel", "KernelNoHeap");
        Assert.Contains(diagnostics, d => d.Id == expectedId);
    }

    [Fact]
    [Trait("Category", "Analyzers")]
    public async Task KernelNoHeapAcceptsValueOnlyCode()
    {
        var diagnostics = await Analyze("class C { int Add(int x, int y) => x + y; }", "Kernel", "KernelNoHeap");
        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("SING1", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Ownership")]
    [Trait("Category", "NegativeCompilation")]
    public async Task BorrowEscapeIsRejected()
    {
        const string source = "ref struct BorrowedSpan<T> { } class C { BorrowedSpan<int> Escape(BorrowedSpan<int> borrowed) { return borrowed; } }";
        var diagnostics = await Analyze(source, "Sip", "SipRegion");
        Assert.Contains(diagnostics, d => d.Id == "SING2001");
    }

    [Fact]
    [Trait("Category", "Ownership")]
    [Trait("Category", "NegativeCompilation")]
    public async Task UseAfterMoveIsRejected()
    {
        const string source = "class OwnedBuffer<T> { public OwnedBuffer<T> Move() => this; public int Length => 0; } class C { int M(OwnedBuffer<int> buffer) { _ = buffer.Move(); return buffer.Length; } }";
        var diagnostics = await Analyze(source, "Sip", "SipRegion");
        Assert.Contains(diagnostics, d => d.Id == "SING2002");
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string profile, string memoryProfile)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create("AnalyzerFixture", new[] { tree }, PlatformReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var provider = new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.SingPlusProfile"] = profile,
            ["build_property.SingPlusMemoryProfile"] = memoryProfile
        });
        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, provider);
        var options = new CompilationWithAnalyzersOptions(analyzerOptions, null, true, false, false);
        return await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SingPlusAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
    }

    internal static MetadataReference[] PlatformReferences() => ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("TPA unavailable"))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(static path => MetadataReference.CreateFromFile(path))
        .ToArray();

    private sealed class TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _options = new DictionaryOptions(values);
        public override AnalyzerConfigOptions GlobalOptions => _options;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }

    private sealed class DictionaryOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
