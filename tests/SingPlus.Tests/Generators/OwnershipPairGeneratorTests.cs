using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Contracts;
using SingPlus.Generators;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Generators;

public sealed class OwnershipPairGeneratorTests
{
    private const string Source = """
using System.Threading.Tasks;
using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;
namespace GeneratedTest;

[SipContract, InitialState("Ready")]
public interface IComputeService
{
    [Message(1), Transition("Ready", "Ready"), RequiresCapability(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute), ReturnsOwnership]
    ValueTask<OwnedBuffer<byte>> CopyAsync(
        [Borrows] OwnedBuffer<byte> source,
        [Consumes] OwnedBuffer<byte> destination);
}
""";

    [Fact]
    [Trait("Category", "Generators")]
    [Trait("Category", "Determinism")]
    public void BorrowConsumePairProducesDeterministicTypedProtocol()
    {
        var first = Run();
        var second = Run();

        Assert.Equal(first.Keys.OrderBy(static key => key), second.Keys.OrderBy(static key => key));
        foreach (var key in first.Keys) Assert.Equal(first[key], second[key]);

        Assert.Contains(first.Values, text =>
            text.Contains("OwnershipRequestDescriptorV1", StringComparison.Ordinal) &&
            text.Contains("OwnershipRequestDisposition)1", StringComparison.Ordinal) &&
            text.Contains("OwnershipRequestDisposition)2", StringComparison.Ordinal));
        Assert.Contains(first.Values, text =>
            text.Contains("request=1|5|pair|source:1:1;destination:1:2", StringComparison.Ordinal));
        Assert.Contains(first.Values, text =>
            text.Contains("Consumes=destination;Borrows=source", StringComparison.Ordinal) &&
            text.Contains("ReturnKind=1", StringComparison.Ordinal));
        Assert.Contains(first.Values, text =>
            text.Contains("Dispatch_CopyAsync", StringComparison.Ordinal) &&
            text.Contains("@source, @destination", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Run()
    {
        var references = AnalyzerTests.PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(SingProcessManifestV1).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(OwnedBuffer<>).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SipContractAttribute).Assembly.Location));
        var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.CSharp13));
        var compilation = CSharpCompilation.Create(
            "OwnershipPairGeneratorFixture",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new SingPlusGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);

        Assert.DoesNotContain(generatorDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(outputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return driver.GetRunResult().Results.Single().GeneratedSources
            .ToDictionary(static source => source.HintName, static source => source.SourceText.ToString(), StringComparer.Ordinal);
    }
}
