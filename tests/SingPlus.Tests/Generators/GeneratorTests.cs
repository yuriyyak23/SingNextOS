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
[SipContract, InitialState("Ready"), TerminalState("Closed")]
public interface IConsoleService
{
    [Message(1), Transition("Ready", "Ready"), RequiresCapability(ResourceKind.Device, "console", CapabilityRights.Write)]
    void Write([Consumes] OwnedBuffer<byte> data);
}
""";

    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void GeneratorProducesFourDeterministicArtifacts()
    {
        var first = Run();
        var second = Run();
        Assert.Equal(first.Keys.OrderBy(x => x), second.Keys.OrderBy(x => x));
        foreach (var key in first.Keys) Assert.Equal(first[key], second[key]);
        Assert.Contains(first.Keys, x => x.EndsWith(".Protocol.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Dispatcher.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Manifest.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Capabilities.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("ContractDigest", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Run()
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(SingProcessManifestV1).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(OwnedBuffer<>).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SipContractAttribute).Assembly.Location));
        var tree = CSharpSyntaxTree.ParseText(ContractSource, new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create("GeneratorFixture", new[] { tree }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new SingPlusGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        Assert.DoesNotContain(generatorDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var result = driver.GetRunResult();
        return result.Results.Single().GeneratedSources.ToDictionary(static s => s.HintName, static s => s.SourceText.ToString(), StringComparer.Ordinal);
    }
}
