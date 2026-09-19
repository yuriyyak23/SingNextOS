using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Admission;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Admission;

public sealed class AdmissionVerifierTests
{
    [Theory]
    [InlineData("public static int Root() => 1; public static object Root(int value) => new object();")]
    [InlineData("public static object Root(int value) => new object(); public static int Root() => 1;")]
    public void AmbiguousRootCannotSelectAnOverloadByDeclarationOrder(string methods)
    {
        using var fixture = CompileFixture("public static class Fixture { " + methods + " }");
        var error = Assert.Throws<InvalidOperationException>(() =>
            AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap"));
        Assert.Contains("overloaded", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public static int Probe() => 1; public static object Probe(int value) => new object();")]
    [InlineData("public static object Probe(int value) => new object(); public static int Probe() => 1;")]
    public void CrossAssemblyOverloadedCallAuditsTheSignatureQualifiedMethod(string methods)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "SingPlus.Overloaded.dll");
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        using var fixture = new CompiledFixture(directory, rootPath);
        Emit("public static class External { " + methods + " }", "SingPlus.Overloaded", dependencyPath, null);
        Emit("public static class Fixture { public static object Root() => External.Probe(1); }", "AdmissionFixture",
            rootPath, [MetadataReference.CreateFromFile(dependencyPath)]);
        var result = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");
        Assert.Contains(result.Violations, violation =>
            violation.Method.EndsWith("External::Probe", StringComparison.Ordinal) && violation.Operation == "newobj");
    }

    public static IEnumerable<object[]> ForbiddenCases()
    {
        yield return new object[] { "public static class Fixture { public static object Root() => new object(); }", "newobj" };
        yield return new object[] { "public static class Fixture { public static byte[] Root() => new byte[4]; }", "newarr" };
        yield return new object[] { "public static class Fixture { public static object Root() => 42; }", "box" };
        yield return new object[] { "public static class Fixture { public static void Root() => System.GC.Collect(); }", "forbidden-api" };
        yield return new object[] { "public static unsafe class Fixture { public static int Root(int* p) => *p; }", "unmanaged-memory" };
        yield return new object[] { "public static unsafe class Fixture { public static void Root(int* p) { *p = 1; } }", "unmanaged-memory" };
        yield return new object[] { "public static unsafe class Fixture { public static void Root(int* p) => Fill(out *p); static void Fill(out int x) { x = 1; } }", "unmanaged-memory" };
        yield return new object[] { "public static unsafe class Fixture { public static int Root() { int* p = stackalloc int[2]; return p[0]; } }", "pointer-stackalloc" };
        yield return new object[] { "public static unsafe class Fixture { public static int Root(delegate*<int> p) => p(); }", "function-pointer-invoke" };
        yield return new object[] { "public static class Fixture { [System.Runtime.InteropServices.DllImport(\"native\")] public static extern int Root(); }", "interop-boundary" };
        yield return new object[] { "public static class Fixture { [System.Runtime.InteropServices.UnmanagedCallersOnly] public static int Root() => 1; }", "interop-boundary" };
        yield return new object[] { "public static class Fixture { [System.Runtime.InteropServices.LibraryImport(\"native\")] public static int Root() => 1; }", "interop-boundary" };
        yield return new object[] { "public static class Fixture { public static int Root() => Entry(); [System.Runtime.InteropServices.LibraryImport(\"native\")] static int Entry() => 1; }", "interop-boundary" };
        yield return new object[] { "public static class Fixture { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)] public static extern int Root(); }", "interop-boundary" };
        yield return new object[] { "public static class Fixture { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining, MethodCodeType = System.Runtime.CompilerServices.MethodCodeType.Runtime)] public static extern int Root(); }", "interop-boundary" };
        yield return new object[] { "public static class Fixture { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Unmanaged, MethodCodeType = System.Runtime.CompilerServices.MethodCodeType.Native)] public static extern int Root(); }", "interop-boundary" };
        yield return new object[] { "public static class Fixture { public static int Root(ref byte x) => System.Runtime.CompilerServices.Unsafe.ReadUnaligned<int>(ref x); }", "framework-memory-boundary" };
        yield return new object[] { "public static class Fixture { public static int Root(System.ReadOnlySpan<byte> x) => System.Runtime.InteropServices.MemoryMarshal.Read<int>(x); }", "framework-memory-boundary" };
        yield return new object[] { "public static class Fixture { public static int Root(System.IntPtr p) => System.Runtime.InteropServices.Marshal.ReadInt32(p); }", "framework-memory-boundary" };
        yield return new object[] { "public static class Fixture { public static System.Span<int> Root(System.Collections.Generic.List<int> x) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(x); }", "framework-memory-boundary" };
        yield return new object[] { "public static unsafe class Fixture { public static void* Root() => System.Runtime.InteropServices.NativeMemory.Alloc(16); }", "framework-memory-boundary" };
        yield return new object[] { "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct S { [System.Runtime.InteropServices.FieldOffset(0)] public int X; } public static class Fixture { public static int Root(S s) => s.X; }", "explicit-layout-field" };
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
    public void PureUnsafeSizeOfRemainsAdmissibleWithoutMemoryAccess()
    {
        using var fixture = CompileFixture("public static class Fixture { public static int Root() => System.Runtime.CompilerServices.Unsafe.SizeOf<int>(); }");
        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");
        Assert.True(result.IsAdmitted, string.Join(Environment.NewLine, result.Violations.Select(static v => v.CanonicalKey)));
        Assert.DoesNotContain(result.Violations, v => v.Operation == "framework-memory-boundary");
    }

    [Theory]
    [InlineData("NoInlining")]
    [InlineData("AggressiveInlining")]
    public void OrdinaryCilImplementationHintsDoNotBecomeInterop(string hint)
    {
        using var fixture = CompileFixture("public static class Fixture { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions." + hint + ")] public static int Root() => 1; }");
        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");
        Assert.True(result.IsAdmitted, string.Join(Environment.NewLine, result.Violations.Select(v => v.CanonicalKey)));
        Assert.DoesNotContain(result.Violations, v => v.Operation == "interop-boundary");
    }

    [Fact]
    public void MissingReachableCilBodyCannotProduceSuccessfulAdmission()
    {
        using var fixture = CompileFixture("public static class Fixture { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.ForwardRef)] public static extern int Root(); public static int Sync() => 1; }");
        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");
        Assert.False(result.IsAdmitted);
        Assert.Contains(result.Violations, v => v.Operation == "unavailable-method-body");
        Assert.True(AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Sync", "KernelNoHeap").IsAdmitted);
    }

    [Fact]
    public void AbstractDispatchContractIsPreservedButCannotBeAnExecutableRoot()
    {
        using var fixture = CompileFixture("public interface IPort { int Invoke(); } public static class Fixture { public static int Root(IPort port) => port.Invoke(); }");
        Assert.True(AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap").IsAdmitted);
        var abstractRoot = AdmissionVerifier.Verify(fixture.AssemblyPath, "IPort::Invoke", "KernelNoHeap");
        Assert.False(abstractRoot.IsAdmitted);
        Assert.Contains(abstractRoot.Violations, v => v.Operation == "unavailable-method-body");
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
    public void ExplicitLayoutInLocalDependencyIsRejectedAtIlAdmission()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "SingPlus.Layout.dll");
        Emit("namespace Layout { [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] public struct S { [System.Runtime.InteropServices.FieldOffset(0)] public int X; } }", "SingPlus.Layout", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root(Layout.S s) => s.X; }", "AdmissionFixture", rootPath, [MetadataReference.CreateFromFile(dependencyPath)]);
        using var fixture = new CompiledFixture(directory, rootPath);
        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");
        Assert.Contains(result.Violations, violation => violation.Operation == "explicit-layout-field");
    }

    [Fact]
    public void TransitiveUnsafeCallInLocalDependencyIsRejectedWithoutPreviewMetadata()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "SingPlus.UnsafeDependency.dll");
        Emit("public static unsafe class External { public static int Probe() { int* p = stackalloc int[1]; return p[0]; } }",
            "SingPlus.UnsafeDependency", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root() => External.Probe(); }", "AdmissionFixture",
            rootPath, [MetadataReference.CreateFromFile(dependencyPath)]);
        using var fixture = new CompiledFixture(directory, rootPath);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "KernelNoHeap");

        Assert.False(result.IsAdmitted);
        Assert.Contains(result.Violations, violation => violation.Method.EndsWith("External::Probe", StringComparison.Ordinal) &&
            violation.Operation is "pointer-stackalloc" or "unmanaged-memory");
    }

    [Fact]
    public void MissingReferencedSingPlusDependencyFailsClosed()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "SingPlus.Local.dll");
        Emit("public static class LocalApi { public static int Value() => 3; }", "SingPlus.Local", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root() => LocalApi.Value(); }", "AdmissionFixture",
            rootPath, [MetadataReference.CreateFromFile(dependencyPath)]);
        using var fixture = new CompiledFixture(directory, rootPath);

        File.Delete(dependencyPath);
        var result = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");

        Assert.False(result.IsAdmitted);
        Assert.Contains(result.Violations, v => v.Operation == "missing-local-dependency" && v.Detail == "SingPlus.Local");
    }

    [Fact]
    public void LocalDependencyByteChangeChangesCanonicalProofIdentity()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "SingPlus.Local.dll");
        Emit("public static class LocalApi { public static int Value() => 3; }", "SingPlus.Local", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root() => LocalApi.Value(); }", "AdmissionFixture",
            rootPath, [MetadataReference.CreateFromFile(dependencyPath)]);
        using var fixture = new CompiledFixture(directory, rootPath);
        var first = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");

        var replacementDirectory = Path.Combine(directory, "replacement");
        Directory.CreateDirectory(replacementDirectory);
        var replacementPath = Path.Combine(replacementDirectory, "SingPlus.Local.dll");
        Emit("public static class LocalApi { public static int Value() => 4; }", "SingPlus.Local", replacementPath, null);
        File.Copy(replacementPath, dependencyPath, overwrite: true);
        var second = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");

        Assert.True(first.IsAdmitted);
        Assert.True(second.IsAdmitted);
        Assert.Equal(first.Proof.AssemblyDigest, second.Proof.AssemblyDigest);
        Assert.NotEqual(first.Proof.DependencyDigest, second.Proof.DependencyDigest);
        Assert.NotEqual(first.Proof.ProofDigest, second.Proof.ProofDigest);
    }

    [Fact]
    public void TransitiveLocalDependencyIsHashedAndRequired()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var leafPath = Path.Combine(directory, "SingPlus.Leaf.dll");
        Emit("public static class LeafApi { public static int Value() => 3; }", "SingPlus.Leaf", leafPath, null);
        var middlePath = Path.Combine(directory, "SingPlus.Middle.dll");
        Emit("public static class MiddleApi { public static int Value() => LeafApi.Value(); }", "SingPlus.Middle",
            middlePath, [MetadataReference.CreateFromFile(leafPath)]);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root() => MiddleApi.Value(); }", "AdmissionFixture",
            rootPath, [MetadataReference.CreateFromFile(middlePath)]);
        using var fixture = new CompiledFixture(directory, rootPath);
        var first = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");

        var replacementDirectory = Path.Combine(directory, "replacement");
        Directory.CreateDirectory(replacementDirectory);
        var replacementPath = Path.Combine(replacementDirectory, "SingPlus.Leaf.dll");
        Emit("public static class LeafApi { public static int Value() => 4; }", "SingPlus.Leaf", replacementPath, null);
        File.Copy(replacementPath, leafPath, overwrite: true);
        var second = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");
        File.Delete(leafPath);
        var missing = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");

        Assert.True(first.IsAdmitted);
        Assert.True(second.IsAdmitted);
        Assert.Equal(first.Proof.AssemblyDigest, second.Proof.AssemblyDigest);
        Assert.NotEqual(first.Proof.DependencyDigest, second.Proof.DependencyDigest);
        Assert.Contains(missing.Violations, v => v.Operation == "missing-local-dependency" && v.Detail == "SingPlus.Leaf");
    }

    [Fact]
    public void SameNamedLocalDependencyWithDifferentVersionFailsClosed()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "SingPlus.Local.dll");
        Emit("[assembly:System.Reflection.AssemblyVersion(\"1.0.0.0\")] public static class LocalApi { public static int Value() => 3; }",
            "SingPlus.Local", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root() => LocalApi.Value(); }", "AdmissionFixture",
            rootPath, [MetadataReference.CreateFromFile(dependencyPath)]);
        using var fixture = new CompiledFixture(directory, rootPath);
        var first = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");

        var replacementDirectory = Path.Combine(directory, "replacement");
        Directory.CreateDirectory(replacementDirectory);
        var replacementPath = Path.Combine(replacementDirectory, "SingPlus.Local.dll");
        Emit("[assembly:System.Reflection.AssemblyVersion(\"2.0.0.0\")] public static class LocalApi { public static int Value() => 3; }",
            "SingPlus.Local", replacementPath, null);
        File.Copy(replacementPath, dependencyPath, overwrite: true);
        var second = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "KernelNoHeap");

        Assert.True(first.IsAdmitted);
        Assert.False(second.IsAdmitted);
        Assert.Contains(second.Violations, v => v.Operation == "local-dependency-identity-mismatch" && v.Detail.Contains("SingPlus.Local", StringComparison.Ordinal));
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

    [Fact]
    [Trait("Category", "Admission")]
    public void ManagedCapAdmitsVersionedExactFrameworkMember()
    {
        using var fixture = CompileFixture("public static class Fixture { public static int Root(int value) => System.Math.Abs(value); }", allowUnsafe: false);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.True(result.IsAdmitted, string.Join(Environment.NewLine, result.Violations.Select(static v => v.CanonicalKey)));
    }

    [Fact]
    public void ManagedCapRejectsHarmlessLookingUnclassifiedFrameworkMember()
    {
        using var fixture = CompileFixture("public static class Fixture { public static int Root(int value) => System.Math.Clamp(value, 0, 10); }");

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, v => v.Operation == "unclassified-framework-member" && v.Detail.Contains("System.Math::Clamp", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagedCapScansForbiddenBodyOutsideRootReachability()
    {
        using var fixture = CompileFixture("public static unsafe class Fixture { public static int Root() => 1; private static int Hidden() { int* p = stackalloc int[1]; return p[0]; } }");

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, v => v.Method.EndsWith("Fixture::Hidden", StringComparison.Ordinal) &&
            v.Operation is "pointer-stackalloc" or "unmanaged-memory");
    }

    [Theory]
    [InlineData("private static System.Reflection.Assembly Hidden() => System.Reflection.Assembly.Load(new byte[1]);", "forbidden-api")]
    [InlineData("private static unsafe int Hidden(delegate*<int> target) => target();", "function-pointer-invoke")]
    public void ManagedCapRejectsHiddenRuntimeExpansionAndFunctionPointers(string hiddenMethod, string operation)
    {
        using var fixture = CompileFixture("public static unsafe class Fixture { public static int Root() => 1; " + hiddenMethod + " }");

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, v => v.Method.EndsWith("Fixture::Hidden", StringComparison.Ordinal) && v.Operation == operation);
    }

    [Fact]
    public void ManagedCapScansModuleInitializerAndStaticConstructor()
    {
        const string source = "using System.Runtime.CompilerServices; public static class Fixture { [ModuleInitializer] public static void Init() => System.GC.Collect(); static Fixture() => System.Environment.FailFast(\"x\"); public static int Root() => 1; }";
        using var fixture = CompileFixture(source);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, v => v.Method.EndsWith("Fixture::Init", StringComparison.Ordinal) && v.Operation == "forbidden-api");
        Assert.Contains(result.Violations, v => v.Method.EndsWith("Fixture::.cctor", StringComparison.Ordinal) && v.Operation == "forbidden-api");
    }

    [Fact]
    public void ManagedCapRejectsAmbientMutableReferenceStaticButNotValueStatic()
    {
        using var referenceFixture = CompileFixture("public static class Fixture { private static object State = new object(); public static int Root() => 1; }", allowUnsafe: false);
        using var valueFixture = CompileFixture("public static class Fixture { private static int Count; public static int Root() => Count; }", allowUnsafe: false);

        var rejected = AdmissionVerifier.Verify(referenceFixture.AssemblyPath, "Fixture::Root", "ManagedCap");
        var admitted = AdmissionVerifier.Verify(valueFixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(rejected.Violations, v => v.Operation == "ambient-mutable-static-reference");
        Assert.True(admitted.IsAdmitted, string.Join(Environment.NewLine, admitted.Violations.Select(static v => v.CanonicalKey)));
    }

    [Fact]
    public void ManagedCapRejectsAmbientReferenceHiddenInsideStaticValueType()
    {
        const string source = "public struct HiddenState { public object Value; } public static class Fixture { private static HiddenState State; public static int Root() => State.Value is null ? 0 : 1; }";
        using var fixture = CompileFixture(source, allowUnsafe: false);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, violation =>
            violation.Operation == "ambient-mutable-static-reference" &&
            violation.Method.EndsWith("Fixture::State", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagedCapScansEveryMethodInLocalDependencyClosure()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "SingPlus.ManagedLeaf.dll");
        Emit("public static unsafe class Leaf { public static int Safe() => 1; private static int Hidden() { int* p = stackalloc int[1]; return p[0]; } }",
            "SingPlus.ManagedLeaf", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root() => Leaf.Safe(); }", "AdmissionFixture", rootPath,
            [MetadataReference.CreateFromFile(dependencyPath)]);
        using var fixture = new CompiledFixture(directory, rootPath);

        var result = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, v => v.Method.EndsWith("Leaf::Hidden", StringComparison.Ordinal) &&
            v.Operation is "pointer-stackalloc" or "unmanaged-memory");
    }

    [Fact]
    public void ManagedCapRejectsUndeclaredNativeAssetAndBindsItIntoDependencyEvidence()
    {
        using var fixture = CompileFixture("public static class Fixture { public static int Root() => 1; }");
        var nativePath = Path.Combine(Path.GetDirectoryName(fixture.AssemblyPath)!, "hidden-native.dll");
        File.WriteAllBytes(nativePath, [0x4d, 0x5a, 0x00, 0x01]);

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, v => v.Operation == "undeclared-native-asset" && v.Detail == "hidden-native.dll");
    }

    [Fact]
    public void UnknownAdmissionProfileFailsClosed()
    {
        using var fixture = CompileFixture("public static class Fixture { public static int Root() => 1; }");

        var result = AdmissionVerifier.Verify(fixture.AssemblyPath, "Fixture::Root", "ManagedCapV2");

        Assert.Contains(result.Violations, v => v.Operation == "unknown-profile-policy");
    }

    [Fact]
    public void ManagedCapRejectsDriftedFrameworkReferenceVersion()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "System.Future.dll");
        Emit("[assembly:System.Reflection.AssemblyVersion(\"12.0.0.0\")] public static class FutureApi { public static int Value() => 1; }",
            "System.Future", dependencyPath, null, allowUnsafe: false);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        Emit("public static class Fixture { public static int Root() => FutureApi.Value(); }", "AdmissionFixture", rootPath,
            [MetadataReference.CreateFromFile(dependencyPath)], allowUnsafe: false);
        using var fixture = new CompiledFixture(directory, rootPath);
        File.Delete(dependencyPath);

        var result = AdmissionVerifier.Verify(rootPath, "Fixture::Root", "ManagedCap");

        Assert.Contains(result.Violations, v => v.Operation == "unclassified-framework-version" && v.Detail == "System.Future|12.0.0.0");
    }

    private static CompiledFixture CompileFixture(string source, string assemblyName = "AdmissionFixture", IEnumerable<MetadataReference>? additionalReferences = null, bool allowUnsafe = true)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, assemblyName + ".dll");
        Emit(source, assemblyName, outputPath, additionalReferences, allowUnsafe);
        return new CompiledFixture(directory, outputPath);
    }

    private static CompiledFixture CompileWithUnknownDependency()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "qualification-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dependencyPath = Path.Combine(directory, "ThirdParty.Unknown.dll");
        Emit("namespace ThirdParty { public static class Api { public static int Value() => 9; } }", "ThirdParty.Unknown", dependencyPath, null);
        var rootPath = Path.Combine(directory, "AdmissionFixture.dll");
        var dependencyReference = MetadataReference.CreateFromFile(dependencyPath);
        Emit("public static class Fixture { public static int Root() => ThirdParty.Api.Value(); }", "AdmissionFixture", rootPath, new[] { dependencyReference });
        return new CompiledFixture(directory, rootPath);
    }

    private static void Emit(string source, string assemblyName, string outputPath, IEnumerable<MetadataReference>? additionalReferences, bool allowUnsafe = true)
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        if (additionalReferences is not null) references.AddRange(additionalReferences);
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: allowUnsafe).WithDeterministic(true);
        var compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, references, options);
        var emit = compilation.Emit(outputPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
    }

    private sealed class CompiledFixture(string directory, string assemblyPath) : IDisposable
    {
        public string AssemblyPath { get; } = assemblyPath;

        public void Dispose()
        {
            try
            {
                var basePath = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var target = Path.GetFullPath(directory);
                if (!target.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Fixture cleanup target escaped the test output directory.");
                Directory.Delete(target, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
