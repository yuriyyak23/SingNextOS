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
        yield return new object[] { "unsafe class C { int M(int* pointer) => *pointer; }", "SING1006" };
        yield return new object[] { "unsafe class C { void M(delegate*<void> callback) => callback(); }", "SING1007" };
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

    public static IEnumerable<object[]> MemoryCases()
    {
        yield return ["unsafe class C { int* M(ref int x) { fixed (int* p = &x) return p; } }", "SING1008"];
        yield return ["unsafe class C { void M() { int* p = stackalloc int[2]; } }", "SING1008"];
        yield return ["unsafe class C { int M(int* p) => *p; }", "SING1006"];
        yield return ["unsafe class C { struct S { public int X; } int M(S* p) => p->X; }", "SING1006"];
        yield return ["unsafe class C { int M(int* p) => p[0]; }", "SING1014"];
        yield return ["unsafe class C { int M(int* p) => *p; }", "SING1009"];
        yield return ["unsafe class C { void M(int* p) { *p = 1; } }", "SING1010"];
        yield return ["unsafe class C { void M(delegate*<void> f) => f(); }", "SING1007"];
        yield return ["unsafe class C { unsafe void Inner() { } void M() => Inner(); }", "SING1011"];
        yield return ["[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] struct S { [System.Runtime.InteropServices.FieldOffset(0)] public int X; } class C { int M(S s) => s.X; }", "SING1012"];
        yield return ["class C { [System.Runtime.InteropServices.DllImport(\"native\")] static extern void Native(); void M() => Native(); }", "SING1013"];
        yield return ["class C { [System.Runtime.InteropServices.UnmanagedCallersOnly] public static int Entry() => 1; }", "SING1013"];
        yield return ["class C { [System.Runtime.InteropServices.LibraryImport(\"native\")] public static int Entry() => 1; }", "SING1013"];
    }

    [Theory]
    [MemberData(nameof(MemoryCases))]
    public async Task KernelRejectsEachMemoryContour(string source, string id)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        Assert.Contains(await Analyze(source, "Kernel", "KernelNoHeap", LanguageVersion.Preview), d => d.Id == id);
    }

    [Theory]
    [MemberData(nameof(MemoryCases))]
    public async Task SipAndDriverAuditEachMemoryContour(string source, string kernelId)
    {
        var expectedId = kernelId switch { "SING1006" => "SING1009", "SING1007" => "SING1015", _ => kernelId };
        foreach (var profile in new[] { "Sip", "Driver" })
            Assert.Contains(await Analyze(source, profile, "SipRegion", LanguageVersion.Preview), d => d.Id == expectedId);
    }

    [Theory]
    [InlineData("Sip")]
    [InlineData("Driver")]
    public async Task MemoryAuditDoesNotMintAuthority(string profile)
    {
        var diagnostics = await Analyze("unsafe class C { int M(int* p) => p[0]; }", profile, "SipRegion", LanguageVersion.Preview);
        Assert.Contains(diagnostics, d => d.Id == "SING1014");
        Assert.Contains(diagnostics, d => d.Id == "SING1009");
        Assert.DoesNotContain(diagnostics, d => d.Id == "SING4001");
    }

    [Theory]
    [InlineData("unsafe class C { void M(int* p) { *p += 1; } }", false)]
    [InlineData("unsafe class C { void M(int* p) { ++*p; } }", false)]
    [InlineData("unsafe class C { void M(int* p) { p[0] += 1; } }", true)]
    [InlineData("unsafe class C { void M(int* p) { ((*p))++; } }", false)]
    [InlineData("unsafe class C { void M(int* p) { ((p[0])) += 1; } }", true)]
    public async Task ReadModifyWriteReportsBothMemoryEffects(string source, bool indexed)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, LanguageVersion.Preview);
            Assert.Contains(diagnostics, d => d.Id == "SING1009");
            Assert.Contains(diagnostics, d => d.Id == "SING1010");
            Assert.Equal(indexed, diagnostics.Any(d => d.Id == "SING1014"));
        }
    }

    [Fact]
    public async Task SimplePointerWriteDoesNotClaimARead()
    {
        foreach (var source in new[] { "unsafe class C { void M(int* p) { *p = 1; } }", "unsafe class C { void M(int* p) { ((*p)) = 1; } }" })
        {
            AssertCompilerAcceptsMemoryFixture(source);
            var diagnostics = await Analyze(source, "Kernel", "KernelNoHeap", LanguageVersion.Preview);
            Assert.Contains(diagnostics, d => d.Id == "SING1010");
            Assert.DoesNotContain(diagnostics, d => d.Id == "SING1009");
        }
    }

    [Theory]
    [InlineData("unsafe class C { static void Fill(out int x) { x = 1; } void M(int* p) => Fill(out *p); }", false, true)]
    [InlineData("unsafe class C { static void Touch(ref int x) { x++; } void M(int* p) => Touch(ref ((*p))); }", true, true)]
    [InlineData("unsafe class C { static int Read(in int x) => x; int M(int* p) => Read(in p[0]); }", true, false)]
    public async Task ByReferencePointerArgumentReportsPossibleMemoryEffects(string source, bool read, bool write)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, LanguageVersion.Preview);
            Assert.Equal(read, diagnostics.Any(d => d.Id == "SING1009"));
            Assert.Equal(write, diagnostics.Any(d => d.Id == "SING1010"));
        }
    }

    [Theory]
    [InlineData("class C { int M(ref byte x) => System.Runtime.CompilerServices.Unsafe.ReadUnaligned<int>(ref x); }")]
    [InlineData("class C { int M(System.ReadOnlySpan<byte> x) => System.Runtime.InteropServices.MemoryMarshal.Read<int>(x); }")]
    [InlineData("class C { int M(System.IntPtr p) => System.Runtime.InteropServices.Marshal.ReadInt32(p); }")]
    [InlineData("class C { System.Span<int> M(System.Collections.Generic.List<int> x) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(x); }")]
    [InlineData("unsafe class C { void* M() => System.Runtime.InteropServices.NativeMemory.Alloc(16); }")]
    public async Task FrameworkMemoryIntrinsicRequiresIndependentAudit(string source)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, LanguageVersion.Preview);
            var boundary = Assert.Single(diagnostics.Where(d => d.Id == "SING1017"));
            Assert.Equal("Memory access audit", boundary.Descriptor.Category);
            Assert.DoesNotContain(diagnostics, d => d.Id == "SING4001");
        }
    }

    [Fact]
    public async Task PureUnsafeSizeOfDoesNotReportFrameworkMemoryBoundary()
    {
        var diagnostics = await Analyze("class C { int M() => System.Runtime.CompilerServices.Unsafe.SizeOf<int>(); }",
            "Kernel", "KernelNoHeap", LanguageVersion.Preview);
        Assert.DoesNotContain(diagnostics, d => d.Id == "SING1017");
    }

    [Theory]
    [InlineData("class C { int M(int x) => x + 1; }")]
    [InlineData("class C { int M() { System.Span<int> values = stackalloc int[2]; return values.Length; } }")]
    public async Task SafeOperationsDoNotTriggerMemoryAudit(string source)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        var diagnostics = await Analyze(source, "Kernel", "KernelNoHeap", LanguageVersion.Preview);
        Assert.DoesNotContain(diagnostics, d => d.Id is "SING1008" or "SING1009" or "SING1010" or "SING1014");
    }

    [Theory]
    [InlineData("unsafe class C { bool M(int* p) => p == null; }")]
    [InlineData("unsafe class C { bool M(delegate*<void> f) => f == null; }")]
    [InlineData("class C { int M(int[] values) => values[0]; }")]
    [InlineData("class C { void M(int[] values) { values[0] = 1; } }")]
    [InlineData("class C { void M(System.Action callback) => callback(); }")]
    [InlineData("class C { static int Forward(int value) => value; int M(int value) => Forward(value); }")]
    [InlineData("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct S { public int X; } class C { int M(S value) => value.X; }")]
    [InlineData("class C { int M() => System.Runtime.CompilerServices.Unsafe.SizeOf<int>(); }")]
    public async Task ManagedControlsAndPointerValuesAreNotMemoryEffects(string source)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, language);
            Assert.DoesNotContain(diagnostics, d => d.Descriptor.Category == "Memory access audit" ||
                d.Id is "SING1006" or "SING1007");
        }
    }

    private static void AssertCompilerAcceptsMemoryFixture(string source)
    {
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        {
            var compilation = CSharpCompilation.Create("MemoryFixture",
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(language))], PlatformReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            using var image = new MemoryStream();
            var result = compilation.Emit(image);
            Assert.True(result.Success, language + ": " + string.Join(Environment.NewLine, result.Diagnostics));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsafeCallAuditIncludesEnclosingTypesButNotSafeNestedTypes(bool unsafeOuter)
    {
        var source = (unsafeOuter ? "public unsafe class Outer" : "public class Outer") +
            " { public class Inner { public static int Forward(int value) => value; } } class Caller { int M(int value) => Outer.Inner.Forward(value); }";
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, language);
            Assert.Equal(unsafeOuter, diagnostics.Any(d => d.Id == "SING1011"));
            Assert.DoesNotContain(diagnostics, d => d.Id is "SING1009" or "SING1010" or "SING4001");
        }
    }

    public static IEnumerable<object[]> ImplicitCallableCases()
    {
        var cases = new[]
        {
            ("public $member Provider(int value) { }", "Provider M() => new Provider(1);"),
            ("public $member Provider(int value) { }", "Provider M() => new(1);"),
            ("public static $member Provider operator +(Provider a, Provider b) => default;", "Provider M(Provider a, Provider b) => a + b;"),
            ("public static $member Provider operator -(Provider a) => default;", "Provider M(Provider a) => -a;"),
            ("public static $member Provider operator ++(Provider a) => default;", "Provider M(Provider a) => ++a;"),
            ("public static $member Provider operator ++(Provider a) => default;", "Provider M(Provider a) => a++;"),
            ("public static $member Provider operator +(Provider a, Provider b) => default;", "Provider M(Provider a, Provider b) { a += b; return a; }"),
            ("public static $member explicit operator int(Provider a) => 1;", "int M(Provider a) => (int)a;"),
            ("public static $member implicit operator int(Provider a) => 1;", "int M(Provider a) => a;")
        };
        foreach (var (declaration, caller) in cases)
        foreach (var mode in new[] { "safe", "member", "type", "caller" })
        {
            var source = (mode == "type" ? "unsafe " : "") + "struct Provider { " +
                declaration.Replace("$member", mode == "member" ? "unsafe" : "") + " } " +
                (mode == "caller" ? "unsafe " : "") + "class Caller { " + caller + " }";
            yield return [source, mode is "member" or "type"];
        }
    }

    [Theory]
    [MemberData(nameof(ImplicitCallableCases))]
    public async Task ImplicitCallablesAuditDeclarationContext(string source, bool expectedAudit)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, language);
            Assert.Equal(expectedAudit ? 1 : 0, diagnostics.Count(d => d.Id == "SING1011"));
            Assert.DoesNotContain(diagnostics, d => d.Id is "SING1009" or "SING1010" or "SING4001");
        }
    }

    [Theory]
    [InlineData(true, "p.Changed += callback;")]
    [InlineData(true, "p.Changed -= callback;")]
    [InlineData(false, "p.Changed += callback;")]
    [InlineData(false, "p.Changed -= callback;")]
    public async Task SelectedEventAccessorsAuditDeclarationContext(bool unsafeOwner, string access)
    {
        var source = (unsafeOwner ? "unsafe " : "") +
            "class Provider { public event System.Action? Changed; } class Caller { void M(Provider p, System.Action callback) { " + access + " } }";
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, language);
            Assert.Equal(unsafeOwner, diagnostics.Any(d => d.Id == "SING1011"));
            Assert.DoesNotContain(diagnostics, d => d.Id is "SING1009" or "SING1010" or "SING4001");
        }
    }

    [Theory]
    [InlineData("class C { int M(int value) { unsafe int Forward(int x) => x; return Forward(value); } }", true)]
    [InlineData("class C { unsafe int M(int value) { int Forward(int x) => x; return Forward(value); } }", true)]
    [InlineData("class C { int M(int value) { unsafe { int Forward(int x) => x; return Forward(value); } } }", true)]
    [InlineData("class C { int M(int value) { int Forward(int x) => x; return Forward(value); } }", false)]
    [InlineData("class C { int M(int value) { int Forward(int x) => x; unsafe { return Forward(value); } } }", false)]
    public async Task LocalFunctionCallsAuditTheirDeclarationUnsafeContext(string source, bool expectedAudit)
    {
        AssertCompilerAcceptsMemoryFixture(source);
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        foreach (var (profile, memoryProfile) in new[] { ("Kernel", "KernelNoHeap"), ("Sip", "SipRegion"), ("Driver", "DriverRegion") })
        {
            var diagnostics = await Analyze(source, profile, memoryProfile, language);
            Assert.Equal(expectedAudit, diagnostics.Any(d => d.Id == "SING1011"));
            Assert.DoesNotContain(diagnostics, d => d.Id is "SING1009" or "SING1010" or "SING4001");
        }
    }

    [Fact]
    public async Task PreviewParserStillEnforcesMemoryRules()
    {
        const string source = "unsafe class C { int M(int* p) => p[0]; }";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        Assert.DoesNotContain(tree.GetDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(await Analyze(source, "Kernel", "KernelNoHeap", LanguageVersion.Preview), d => d.Id == "SING1014");
    }

    [Theory]
    [InlineData("Kernel", "KernelNoHeap")]
    [InlineData("Sip", "SipRegion")]
    [InlineData("Driver", "DriverRegion")]
    public async Task PointerCallFromMetadataRequiresSeparateBoundaryAudit(string profile, string memoryProfile)
    {
        var external = CSharpCompilation.Create("ExternalPointerFixture",
            [CSharpSyntaxTree.ParseText("public static unsafe class External { public static int* Forward(int* p) => p; public static int ManagedForward(int value) => value; }")],
            PlatformReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var image = new MemoryStream();
        var emitted = external.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var reference = MetadataReference.CreateFromImage(image.ToArray());
        var diagnostics = await Analyze("unsafe class C { int* M(int* p) => External.Forward(p); }", profile, memoryProfile,
            LanguageVersion.Preview, [reference]);
        var boundary = Assert.Single(diagnostics.Where(static d => d.Id == "SING1016"));
        Assert.Equal("Memory access audit", boundary.Descriptor.Category);
        Assert.DoesNotContain(diagnostics, static d => d.Id == "SING4001");
        foreach (var language in new[] { LanguageVersion.CSharp13, LanguageVersion.Preview })
        {
            var managed = await Analyze("class C { int M(int value) => External.ManagedForward(value); }", profile, memoryProfile,
                language, [reference]);
            Assert.DoesNotContain(managed, d => d.Descriptor.Category == "Memory access audit");
        }
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

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string profile, string memoryProfile, LanguageVersion languageVersion = LanguageVersion.CSharp13,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(languageVersion));
        var references = PlatformReferences().AsEnumerable();
        if (additionalReferences is not null) references = references.Concat(additionalReferences);
        var compilation = CSharpCompilation.Create("AnalyzerFixture", new[] { tree }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
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
