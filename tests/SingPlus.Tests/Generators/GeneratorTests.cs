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

    [Message(2), ReturnsOwnership]
    OwnedRegion<int> Acquire();
}
""";

    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void GeneratorProducesFourDeterministicArtifactsWithOwnershipShape()
    {
        var first = RunValid(ContractSource);
        var second = RunValid(ContractSource);
        Assert.Equal(first.Keys.OrderBy(x => x), second.Keys.OrderBy(x => x));
        foreach (var key in first.Keys) Assert.Equal(first[key], second[key]);
        Assert.Contains(first.Keys, x => x.EndsWith(".Protocol.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Dispatcher.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Manifest.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Keys, x => x.EndsWith(".Capabilities.g.cs", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("OwnershipPayloadKind)1", StringComparison.Ordinal));
        Assert.Contains(first.Values, text => text.Contains("ReturnKind=2", StringComparison.Ordinal));
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
