using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Contracts;
using SingPlus.Generators;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Generators;

public sealed class OwnershipPairClientRuntimeAdapterTests
{
    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void TypedClientRoutesPairThroughExactOwnershipPairTransport()
    {
        const string source = """
using System.Threading.Tasks;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;

[SipContract]
public interface IComputeService
{
    [Message(1), ReturnsOwnership]
    ValueTask<OwnedBuffer<byte>> CopyAsync(
        [Borrows] OwnedBuffer<byte> source,
        [Consumes] OwnedBuffer<byte> destination);
}
""";

        var first = Run(source);
        var second = Run(source);

        Assert.Equal(first, second);
        Assert.Contains(
            "_runtime.InvokeOwnershipPairAsync(messageId, @source, @destination)",
            first,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_runtime.InvokeAsync(messageId, @source)",
            first,
            StringComparison.Ordinal);
    }

    private static string Run(string source)
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(SingProcessManifestV1).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(OwnedBuffer<>).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SipContractAttribute).Assembly.Location));

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create(
            "OwnershipPairClientRuntimeAdapterFixture",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new SingPlusGenerator().AsSourceGenerator(),
            new ClientRuntimeAdapterGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);

        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        return driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .Single(sourceResult => sourceResult.HintName.Contains("ClientRuntimeAdapter", StringComparison.Ordinal))
            .SourceText
            .ToString();
    }
}
