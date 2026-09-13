using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Contracts;
using SingPlus.Generators;
using SingPlus.Sip.Sdk;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Generators;

public sealed class ClientRuntimeAdapterGeneratorTests
{
    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void EmitsCompilingRuntimeAdapterForSyncAndAsyncClientShapes()
    {
        const string source = """
using System.Threading.Tasks;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;

[SipContract]
public interface IClientFixture
{
    [Message(1)] int Count(int value);
    [Message(2)] ValueTask<int> CountAsync();
    [Message(3)] Task FlushAsync();
    [Message(4)] void Ping();
}
""";

        var first = Run(source);
        var second = Run(source);

        Assert.Equal(first, second);
        Assert.Contains("IClientFixtureRuntimeClientTransport", first, StringComparison.Ordinal);
        Assert.Contains("IIClientFixtureClientTransport", first, StringComparison.Ordinal);
        Assert.Contains("IClientFixtureRuntimeClient", first, StringComparison.Ordinal);
        Assert.Contains("ISipClientRuntimeTransport", first, StringComparison.Ordinal);
        Assert.Contains("Decode<", first, StringComparison.Ordinal);
        Assert.Contains("AwaitValueTask<", first, StringComparison.Ordinal);
        Assert.Contains("AwaitTask(_runtime.InvokeAsync", first, StringComparison.Ordinal);
        Assert.Contains("EnsureVoid(_runtime.Invoke", first, StringComparison.Ordinal);
    }

    private static string Run(string source)
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(SingProcessManifestV1).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SipContractAttribute).Assembly.Location));

        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create(
            "ClientRuntimeAdapterFixture",
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
