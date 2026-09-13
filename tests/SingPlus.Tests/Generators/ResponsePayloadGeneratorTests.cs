using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Contracts;
using SingPlus.Generators;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Generators;

public sealed class ResponsePayloadGeneratorTests
{
    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void EmitsDeterministicMetadataForSupportedResponseKinds()
    {
        const string source = """
using System.Threading.Tasks;
using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;

public enum Mode : byte { Off, On }

[BoundedPayload(48)]
public readonly struct Packet : IBoundedPayload
{
    public int PayloadSize => 8;
    public int MaxPayloadSize => 48;
}

[SipContract]
public interface IResponses
{
    [Message(1)] void Ping();
    [Message(2)] int Count();
    [Message(3)] ValueTask<Mode> ReadMode();
    [Message(4)] Task<Packet> ReadPacket();
    [Message(5), ReturnsOwnership] OwnedRegion<int> Acquire();
    [Message(6)] ValueTask Flush();
}
""";

        var first = RunValid(source);
        var second = RunValid(source);

        Assert.Equal(first, second);
        Assert.Contains("CanonicalResponseMetadata", first, StringComparison.Ordinal);
        Assert.Contains("ResponseMetadataDigest", first, StringComparison.Ordinal);
        Assert.Contains("Kind=0;Type=;MaxBytes=0;OwnershipKind=0", first, StringComparison.Ordinal);
        Assert.Contains("Kind=1;", first, StringComparison.Ordinal);
        Assert.Contains("Kind=2;Type=GeneratedTest.Mode", first, StringComparison.Ordinal);
        Assert.Contains("Kind=3;Type=GeneratedTest.Packet;MaxBytes=48", first, StringComparison.Ordinal);
        Assert.Contains("Kind=4;Type=SingPlus.Sip.OwnedRegion;MaxBytes=0;OwnershipKind=2", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("string Read();", "SINGGEN009")]
    [InlineData("object Read();", "SINGGEN009")]
    [InlineData("System.Threading.Tasks.Task<string> Read();", "SINGGEN009")]
    [Trait("Category", "Generators")]
    public void UnsupportedResponseTypesFailClosed(string declaration, string diagnosticId)
    {
        var source = $$"""
using SingPlus.Sip.Sdk;
namespace GeneratedTest;
[SipContract]
public interface IBadResponse
{
    [Message(1)] {{declaration}}
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == diagnosticId && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(32, false)]
    [Trait("Category", "Generators")]
    public void InvalidBoundedResponseFailsClosed(int maxBytes, bool implementInterface)
    {
        var interfaceClause = implementInterface ? " : IBoundedPayload" : string.Empty;
        var members = implementInterface ? $"public int PayloadSize => 1; public int MaxPayloadSize => {maxBytes};" : string.Empty;
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
public interface IBadResponse
{
    [Message(1)] Packet Read();
}
""";

        var diagnostics = RunDiagnostics(source);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SINGGEN010" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static string RunValid(string source)
    {
        var (driver, outputCompilation, generatorDiagnostics) = Run(source);
        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return driver.GetRunResult().Results.Single().GeneratedSources.Single().SourceText.ToString();
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
        var compilation = CSharpCompilation.Create("ResponseGeneratorFixture", new[] { tree }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ResponsePayloadGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        return (driver, outputCompilation, generatorDiagnostics);
    }
}
