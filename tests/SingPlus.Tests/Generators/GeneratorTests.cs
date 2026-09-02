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

    [Message(6), Transition("Ready", "Ready")]
    void Ping();
}
""";

    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void GeneratorProducesFourDeterministicArtifactsWithCompleteRequestShapes()
    {
        var first = RunValid(ContractSource);
        var second = RunValid(ContractSource);
        Assert.Equal(first.Keys.OrderBy(x => x), second.Keys.OrderBy(x => x));
        foreach (var key in first.Keys) Assert.Equal(first[key], second[key]);
        Assert.Contains(first.Keys, x => x.EndsWith(".Protocol.g.cs", StringComparison.Ordinal));
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
        Assert.Contains(first.Values, text => text.Contains("ContractDigest", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("void Bad([Consumes] int value);", "SINGGEN001")]
    [InlineData("void Bad(OwnedBuffer<byte> value);", "SINGGEN002")]
    [InlineData("void Bad([Consumes] OwnedBuffer<byte> first, [Borrows] OwnedRegion<int> second);", "SINGGEN003")]
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
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        return (driver, outputCompilation, generatorDiagnostics);
    }
}
