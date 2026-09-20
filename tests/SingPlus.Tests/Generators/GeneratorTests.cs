using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Contracts;
using SingPlus.Generators;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Generators;

public sealed class GeneratorTests
{
    private const string ContractSource = """
using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;

public enum ConsoleMode : byte { Normal = 0, Diagnostic = 1 }

[BoundedPayload(64)]
public readonly struct ConsolePacket : IBoundedPayload
{
    public int PayloadSize => 8;
    public int MaxPayloadSize => 64;
}

[SipContract, InitialState("Ready"), TerminalState("Closed")]
public interface IConsoleService
{
    [Message(1), Transition("Ready", "Ready"), RequiresCapability(ResourceKind.Device, "console", CapabilityRights.Write)]
    void Write([Consumes] OwnedBuffer<byte> data);

    [Message(2), ReturnsOwnership]
    OwnedRegion<int> Acquire();

    [Message(3), Transition("Ready", "Ready")]
    void Configure(ConsolePacket packet);

    [Message(4), Transition("Ready", "Ready")]
    void SetLevel(int level);

    [Message(5), Transition("Ready", "Ready")]
    void SetMode(ConsoleMode mode);

    [Message(6), Transition("Ready", "Busy"), CancellationTransition("Busy", "Ready")]
    [RequiresResource(1, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 50, "host:compute-v1")]
    void Ping();

    [Message(7), Transition("Ready", "Ready")]
    void Read([Borrows] OwnedBuffer<byte> data);
}
""";

    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void GeneratorProducesFiveDeterministicArtifactsWithCompleteRequestShapes()
    {
        var first = RunValid(ContractSource);
        var second = RunValid(ContractSource);
        Assert.Equal(first.Keys.OrderBy(x => x), second.Keys.OrderBy(x => x));
        foreach (var key in first.Keys) Assert.Equal(first[key], second[key]);
        Assert.Contains(first.Keys, x => x.EndsWith(".Protocol.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Sentries.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Dispatcher.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Manifest.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Capabilities.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("RequestPayloadKind)4", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("ReturnKind=2", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("RequestPayloadKind)3", StringComparison.Ordinal) && text.Contains("GeneratedTest.ConsolePacket", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("Kind=1;Parameter=level;Type=System.Int32", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("Kind=2;Parameter=mode;Type=GeneratedTest.ConsoleMode", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("Kind=0;Parameter=;Type=", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("request=3|3|packet|GeneratedTest.ConsolePacket|64|0", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("request=4|1|level|System.Int32|0|0", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("request=5|2|mode|GeneratedTest.ConsoleMode|0|0", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("Write_ValueSchema", StringComparison.Ordinal) && text.Contains("request=ownership:", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("cancel=6|Busy|Ready", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("ProtocolCancellationTransitionV1", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("ContractDigest", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("Ping_Resource", StringComparison.Ordinal) && text.Contains("host:compute-v1", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("GeneratedSipResourceSentry.Enter", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Security")]
    public void DispatcherCanEnterImplementationOnlyThroughTypedGeneratedOperationSentry()
    {
        var generated = RunValid(ContractSource);
        var dispatcher = Assert.Single(generated, item => item.Key.EndsWith(".Dispatcher.g.cs", StringComparison.Ordinal)).Value;
        var sentries = Assert.Single(generated, item => item.Key.EndsWith(".Sentries.g.cs", StringComparison.Ordinal)).Value;

        Assert.Contains("GeneratedOperationSentries.Invoke_Write(_implementation, @data)", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("_implementation.@", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", dispatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Delegate", dispatcher, StringComparison.Ordinal);
        Assert.Contains("internal interface IIConsoleServiceGeneratedSentryTarget_Write", sentries, StringComparison.Ordinal);
        Assert.Contains("GeneratedSipSentryResult<global::SingPlus.Sip.Sdk.GeneratedSipUnit> Sentry_Write(in global::SingPlus.Sip.Sdk.TrustedSipInvocationContext context, global::SingPlus.Sip.OwnedBuffer<byte> @data)", sentries, StringComparison.Ordinal);
        Assert.Contains("InvokeRuntime_Write(IIConsoleServiceGeneratedSentryTarget_Write target, in global::SingPlus.Sip.Sdk.TrustedSipInvocationContext context, global::SingPlus.Sip.OwnedBuffer<byte> @data)", sentries, StringComparison.Ordinal);
        Assert.Contains("internal static void Invoke_Write(global::GeneratedTest.IConsoleService implementation", sentries, StringComparison.Ordinal);
        Assert.Contains("=> implementation.@Write(@data);", sentries, StringComparison.Ordinal);
        Assert.Contains("GeneratedSipSentryResult<global::SingPlus.Sip.Sdk.GeneratedSipUnit> Sentry_Read(in global::SingPlus.Sip.Sdk.TrustedSipInvocationContext context, global::SingPlus.Sip.BorrowLease<byte> @data)", sentries, StringComparison.Ordinal);
        Assert.Contains("InvokeRuntime_Read(IIConsoleServiceGeneratedSentryTarget_Read target, in global::SingPlus.Sip.Sdk.TrustedSipInvocationContext context, global::SingPlus.Sip.BorrowLease<byte> @data)", sentries, StringComparison.Ordinal);
        Assert.Contains("internal static void Invoke_Read(global::GeneratedTest.IConsoleService implementation, global::SingPlus.Sip.OwnedBuffer<byte> @data)", sentries, StringComparison.Ordinal);
        Assert.DoesNotContain("GeneratedSentryTarget_Read\n{\n    global::SingPlus.Sip.Sdk.GeneratedSipSentryResult<global::SingPlus.Sip.Sdk.GeneratedSipUnit> Sentry_Read(in global::SingPlus.Sip.Sdk.TrustedSipInvocationContext context, global::SingPlus.Sip.OwnedBuffer", sentries, StringComparison.Ordinal);
        Assert.Contains("public const string Thunk_Write", sentries, StringComparison.Ordinal);
        Assert.Contains("public const string Thunk_Write_Digest", sentries, StringComparison.Ordinal);
        Assert.DoesNotContain("object implementation", sentries, StringComparison.Ordinal);
        Assert.DoesNotContain("IServiceProvider", sentries, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", sentries, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("void Bad([Consumes] int value);", "SINGGEN001")]
    [InlineData("void Bad(OwnedBuffer<byte> value);", "SINGGEN002")]
    [InlineData("void Bad([Consumes] OwnedBuffer<byte> first, [Consumes] OwnedRegion<int> second);", "SINGGEN003")]
    [InlineData("[ReturnsOwnership] int Bad();", "SINGGEN004")]
    [InlineData("OwnedRegion<int> Bad();", "SINGGEN004")]
    [Trait("Category", "Generators")]
    public void MalformedOwnershipContractsFailClosed(string methodDeclaration, string expectedDiagnostic)
    {
        var source = $$"""
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[SipContract]
public interface IBadContract
{
    [Message(1)]
    {{methodDeclaration}}
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == expectedDiagnostic && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("RequiresResource(2, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 1, \"host:compute-v1\")")]
    [InlineData("RequiresResource(1, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 0, \"host:compute-v1\")")]
    [InlineData("RequiresResource(1, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 1, \"\")")]
    [InlineData("RequiresResource(1, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 1, \"host:compute-v1\", ResourceAssuranceV1.GuaranteedReservation)")]
    [Trait("Category", "Generators")]
    public void MalformedResourceRequirementsFailClosed(string attribute)
    {
        var source = $$"""
using SingPlus.Contracts;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[SipContract]
public interface IBadContract
{
    [Message(1), {{attribute}}]
    void Bad();
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN012" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "Generators")]
    public void ParameterCannotBeBothConsumedAndBorrowed()
    {
        const string source = """
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[SipContract]
public interface IBadContract
{
    [Message(1)]
    void Bad([Consumes, Borrows] OwnedBuffer<byte> value);
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN003" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(32, false)]
    [Trait("Category", "Generators")]
    public void InvalidBoundedPayloadShapeFailsClosed(int maxBytes, bool implementInterface)
    {
        var interfaceClause = implementInterface ? " : IBoundedPayload" : string.Empty;
        var members = implementInterface ? "public int PayloadSize => 1; public int MaxPayloadSize => " + maxBytes + ";" : string.Empty;
        var source = $$"""
using SingPlus.Contracts;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[BoundedPayload({{maxBytes}})]
public readonly struct Packet{{interfaceClause}}
{
    {{members}}
}
[SipContract]
public interface IBadContract
{
    [Message(1)]
    void Bad(Packet packet);
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN005" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "Generators")]
    public void BoundedInterfaceWithoutAttributeFailsClosed()
    {
        const string source = """
using SingPlus.Contracts;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
public readonly struct Packet : IBoundedPayload
{
    public int PayloadSize => 1;
    public int MaxPayloadSize => 32;
}
[SipContract]
public interface IBadContract
{
    [Message(1)]
    void Bad(Packet packet);
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN005" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "Generators")]
    public void BoundedPayloadMustOccupyTheSingleMessagePayloadSlot()
    {
        const string source = """
using SingPlus.Contracts;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[BoundedPayload(32)]
public readonly struct Packet : IBoundedPayload
{
    public int PayloadSize => 1;
    public int MaxPayloadSize => 32;
}
[SipContract]
public interface IBadContract
{
    [Message(1)]
    void Bad(int code, Packet packet);
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN006" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN007" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("int[]")]
    [InlineData("System.Collections.Generic.List<int>")]
    [InlineData("object")]
    [Trait("Category", "Generators")]
    public void DeepMutableGraphInsideReadonlyRecordFailsClosed(string memberType)
    {
        var source = $$"""
using SingPlus.Contracts;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[BoundedPayload(64)]
public readonly record struct Packet({{memberType}} Value) : IBoundedPayload
{
    public int PayloadSize => 1;
    public int MaxPayloadSize => 64;
}
[SipContract]
public interface IBadContract
{
    [Message(1)] void Bad(Packet packet);
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN011" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("System.Collections.Generic.List<int>")]
    [Trait("Category", "Generators")]
    public void HiddenMutableFieldInsideReadonlyPayloadFailsClosed(string fieldType)
    {
        var source = $$"""
using SingPlus.Contracts;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[BoundedPayload(64)]
public readonly struct Packet : IBoundedPayload
{
    private readonly {{fieldType}} _hidden;
    public Packet({{fieldType}} hidden) => _hidden = hidden;
    public int PayloadSize => 1;
    public int MaxPayloadSize => 64;
}
[SipContract]
public interface IBadContract
{
    [Message(1)] void Bad(Packet packet);
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN011" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "Generators")]
    public void RecursiveReadonlyValueGraphIsAccepted()
    {
        const string source = """
using SingPlus.Contracts;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
public readonly record struct Inner(int Value);
[BoundedPayload(64)]
public readonly record struct Packet(Inner Value, string Label) : IBoundedPayload
{
    public int PayloadSize => 16;
    public int MaxPayloadSize => 64;
}
[SipContract]
public interface IGoodContract
{
    [Message(1)] void Send(Packet packet);
}
""";

        _ = RunValid(source);
    }

    [Theory]
    [InlineData("void Bad(int first, int second);", "SINGGEN007")]
    [InlineData("void Bad([Consumes] OwnedBuffer<byte> data, int flags);", "SINGGEN007")]
    [InlineData("void Bad(string text);", "SINGGEN008")]
    [InlineData("void Bad(ref int value);", "SINGGEN008")]
    [Trait("Category", "Generators")]
    public void UnsupportedRequestShapeFailsClosed(string methodDeclaration, string expectedDiagnostic)
    {
        var source = $$"""
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[SipContract]
public interface IBadContract
{
    [Message(1)]
    {{methodDeclaration}}
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == expectedDiagnostic && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static Dictionary<string, string> RunValid(string source)
    {
        var (driver, outputCompilation, generatorDiagnostics) = Run(source);
        Assert.DoesNotContain(generatorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var result = driver.GetRunResult();
        return result.Results.Single().GeneratedSources.ToDictionary(static s => s.HintName, static s => s.SourceText.ToString(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<Diagnostic> RunDiagnostics(string source)
    {
        var (driver, _, generatorDiagnostics) = Run(source);
        return generatorDiagnostics.Concat(driver.GetRunResult().Results.SelectMany(static result => result.Diagnostics)).ToArray();
    }

    private static (GeneratorDriver Driver, Compilation OutputCompilation, IReadOnlyList<Diagnostic> GeneratorDiagnostics) Run(string source)
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(SingProcessManifestV1).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(OwnedBuffer<>).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SipContractAttribute).Assembly.Location));
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create("GeneratorFixture", new[] { tree }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new SingPlusGenerator().AsSourceGenerator());
        driver = driver.WithUpdatedParseOptions((CSharpParseOptions)tree.Options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        return (driver, outputCompilation, generatorDiagnostics);
    }
}
