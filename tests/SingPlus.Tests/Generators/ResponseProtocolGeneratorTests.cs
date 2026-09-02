using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Contracts;
using SingPlus.Generators;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Generators;

public sealed class ResponseProtocolGeneratorTests
{
    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void EmitsDeterministicRuntimeResponseProtocolDefinition()
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
}
""";

        var first = Run(source);
        var second = Run(source);

        Assert.Equal(first, second);
        Assert.Contains("ResponseProtocolDefinitionV1", first, StringComparison.Ordinal);
        Assert.Contains("ResponseMessageDescriptorV1(1u, \"Ping\"", first, StringComparison.Ordinal);
        Assert.Contains("(global::SingPlus.Contracts.ResponsePayloadKind)1", first, StringComparison.Ordinal);
        Assert.Contains("GeneratedTest.Mode", first, StringComparison.Ordinal);
        Assert.Contains("GeneratedTest.Packet", first, StringComparison.Ordinal);
        Assert.Contains("SingPlus.Sip.OwnedRegion", first, StringComparison.Ordinal);
        Assert.Contains("(global::SingPlus.Contracts.OwnershipPayloadKind)2", first, StringComparison.Ordinal);
    }

    private static string Run(string source)
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(SingProcessManifestV1).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(OwnedBuffer<>).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SipContractAttribute).Assembly.Location));
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create(
            "ResponseProtocolGeneratorFixture",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ResponseProtocolGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return driver.GetRunResult().Results.Single().GeneratedSources.Single().SourceText.ToString();
    }
}
