using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SingPlus.Admission;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Admission;

public sealed class ManagedAsyncPeQualificationTests
{
    private const string Source = "public static class Fixture { public static async System.Threading.Tasks.Task<int> Root() { await System.Threading.Tasks.Task.Yield(); return 7; } public static async System.Threading.Tasks.Task Plain() { await System.Threading.Tasks.Task.Yield(); } public static async System.Threading.Tasks.ValueTask<int> Value() { await System.Threading.Tasks.Task.Yield(); return 3; } public static int Sync() => 1; }";

    [Fact]
    public void RealCompilerOutputSeparatesLegacyAndRuntimeAsyncV2()
    {
        using EmittedFixture legacy = EmitFixture(runtimeAsync: false);
        using EmittedFixture runtimeV2 = EmitFixture(runtimeAsync: true);
        ManagedAsyncPeQualificationResult legacyResult = ManagedAsyncPeQualification.Recognize(legacy.Path, "Fixture::Root");
        ManagedAsyncPeQualificationResult runtimeResult = ManagedAsyncPeQualification.Recognize(runtimeV2.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.LegacyCilStateMachine, legacyResult.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.LegacyCompatible, legacyResult.AdmissionDisposition);
        Assert.Equal(ManagedAsyncPeKind.RuntimeAsyncV2, runtimeResult.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.RecognizedButLoweringUnavailable, runtimeResult.AdmissionDisposition);
        Assert.Equal(ManagedAsyncPeKind.NotAsync, ManagedAsyncPeQualification.Recognize(runtimeV2.Path, "Fixture::Sync").Kind);
        Assert.Equal(ManagedAsyncPeKind.RuntimeAsyncV2, ManagedAsyncPeQualification.Recognize(runtimeV2.Path, "Fixture::Plain").Kind);
        Assert.Equal(ManagedAsyncPeKind.RuntimeAsyncV2, ManagedAsyncPeQualification.Recognize(runtimeV2.Path, "Fixture::Value").Kind);
    }

    [Fact]
    public void UnknownOrMalformedAsyncIdentityFailsClosed()
    {
        using EmittedFixture fixture = EmitFixture(runtimeAsync: false);
        Assert.Equal(ManagedAsyncPeKind.Rejected, ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Missing").Kind);
        Assert.Equal(ManagedAsyncPeKind.Rejected, ManagedAsyncPeQualification.Recognize(fixture.Path, "Root").Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KernelAdmissionRejectsRuntimeAsyncFrontendWithoutLowering(bool runtimeAsync)
    {
        using EmittedFixture fixture = EmitSource("public static class Fixture { public static async System.Threading.Tasks.ValueTask<int> Root() { return 7; } public static int Sync() => 1; }", runtimeAsync);
        ManagedAsyncPeQualificationResult recognition = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(runtimeAsync ? ManagedAsyncPeKind.RuntimeAsyncV2 : ManagedAsyncPeKind.LegacyCilStateMachine, recognition.Kind);
        AdmissionVerificationResult result = AdmissionVerifier.Verify(fixture.Path, "Fixture::Root", "KernelNoHeap");
        Assert.Equal(runtimeAsync, result.Violations.Any(v => v.Operation == "unsupported-async-abi"));
        if (runtimeAsync) Assert.False(result.IsAdmitted);
        Assert.True(AdmissionVerifier.Verify(fixture.Path, "Fixture::Sync", "KernelNoHeap").IsAdmitted);
    }

    [Fact]
    public void CorruptPeReturnsExplicitRejection()
    {
        using EmittedFixture fixture = EmitFixture(runtimeAsync: false);
        File.WriteAllBytes(fixture.Path, [0x4d, 0x5a, 0x00]);
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.Rejected, result.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected, result.AdmissionDisposition);
        Assert.Equal("Assembly contains invalid PE or managed metadata.", result.Reason);
    }

    [Fact]
    public void OverloadedAsyncIdentityIsRejectedRatherThanSelectingFirstMethod()
    {
        using EmittedFixture fixture = EmitSource("public static class Fixture { public static async System.Threading.Tasks.Task Root() { await System.Threading.Tasks.Task.Yield(); } public static int Root(int value) => value; }");
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.Rejected, result.Kind);
        Assert.Contains("overloaded", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedTypeIdentityRecognizesActualLegacyAsync()
    {
        using EmittedFixture fixture = EmitSource("public static class Outer { public static class Fixture { public static async System.Threading.Tasks.Task Root() { await System.Threading.Tasks.Task.Yield(); } } }");
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Outer+Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.LegacyCilStateMachine, result.Kind);
    }

    [Fact]
    public void SpoofedLegacyAttributeDoesNotAdmitMissingStateMachine()
    {
        using EmittedFixture fixture = EmitSource("public static class Fixture { [System.Runtime.CompilerServices.AsyncStateMachine(typeof(object))] public static void Root() { } }");
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.Rejected, result.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected, result.AdmissionDisposition);
        Assert.Contains("state machine", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealLegacyAsyncVoidRemainsCompatible()
    {
        using EmittedFixture fixture = EmitSource("public static class Fixture { public static async void Root() { await System.Threading.Tasks.Task.Yield(); } }");
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.LegacyCilStateMachine, result.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.LegacyCompatible, result.AdmissionDisposition);
    }

    [Fact]
    public void RealGenericLegacyAsyncRemainsCompatible()
    {
        using EmittedFixture fixture = EmitSource("public static class Fixture { public static async System.Threading.Tasks.Task<T> Root<T>(T value) { await System.Threading.Tasks.Task.Yield(); return value; } }");
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.LegacyCilStateMachine, result.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.LegacyCompatible, result.AdmissionDisposition);
    }

    [Fact]
    public void SameNamedAsyncMarkerFromForeignAssemblyFailsClosed()
    {
        using EmittedFixture fakeMarker = EmitSource("namespace System.Runtime.CompilerServices { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class AsyncStateMachineAttribute : System.Attribute { public AsyncStateMachineAttribute(System.Type stateMachine) { } } }",
            assemblyName: "SingPlus.FakeAsyncMarker");
        PortableExecutableReference reference = MetadataReference.CreateFromFile(fakeMarker.Path,
            new MetadataReferenceProperties(aliases: global::System.Collections.Immutable.ImmutableArray.Create("fake")));
        const string source = "extern alias fake; public static class Fixture { [fake::System.Runtime.CompilerServices.AsyncStateMachine(typeof(FakeStateMachine))] public static void Root() { } } public sealed class FakeStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { public void MoveNext() { } public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) { } }";
        using EmittedFixture fixture = EmitSource(source, additionalReferences: [reference]);
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.Rejected, result.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected, result.AdmissionDisposition);
        Assert.Contains("outside", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForeignStateMachineCannotMatchSameNamedLocalType()
    {
        const string stateMachine = "public sealed class FakeStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { public void MoveNext() { } public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) { } }";
        using EmittedFixture foreign = EmitSource(stateMachine, assemblyName: "SingPlus.ForeignStateMachine");
        PortableExecutableReference reference = MetadataReference.CreateFromFile(foreign.Path,
            new MetadataReferenceProperties(aliases: global::System.Collections.Immutable.ImmutableArray.Create("foreign")));
        using EmittedFixture fixture = EmitSource("extern alias foreign; public static class Fixture { [System.Runtime.CompilerServices.AsyncStateMachine(typeof(foreign::FakeStateMachine))] public static void Root() { } } " + stateMachine,
            additionalReferences: [reference]);
        ManagedAsyncPeQualificationResult result = ManagedAsyncPeQualification.Recognize(fixture.Path, "Fixture::Root");
        Assert.Equal(ManagedAsyncPeKind.Rejected, result.Kind);
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected, result.AdmissionDisposition);
    }

    private static EmittedFixture EmitFixture(bool runtimeAsync) => EmitSource(Source, runtimeAsync);

    private static EmittedFixture EmitSource(string source, bool runtimeAsync = false,
        IEnumerable<MetadataReference>? additionalReferences = null, string assemblyName = "AsyncFixture")
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, assemblyName + ".dll");
        CSharpParseOptions parse = new CSharpParseOptions(LanguageVersion.Preview);
        if (runtimeAsync) parse = parse.WithFeatures([new KeyValuePair<string, string>("runtime-async", "on")]);
        IEnumerable<MetadataReference> references = AnalyzerTests.PlatformReferences().AsEnumerable();
        if (additionalReferences is not null) references = references.Concat(additionalReferences);
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source, parse)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithDeterministic(true));
        EmitResult result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return new EmittedFixture(directory, path);
    }

    private sealed class EmittedFixture(string directory, string path) : IDisposable
    {
        public string Path { get; } = path;
        public void Dispose()
        {
            string basePath = global::System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(global::System.IO.Path.DirectorySeparatorChar) + global::System.IO.Path.DirectorySeparatorChar;
            string target = global::System.IO.Path.GetFullPath(directory);
            if (!target.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Async fixture cleanup escaped test output.");
            Directory.Delete(target, recursive: true);
        }
    }
}
