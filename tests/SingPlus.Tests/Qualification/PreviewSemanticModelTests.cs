using SingPlus.HybridCpuQualification;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Qualification;

public sealed class PreviewSemanticModelTests
{
    [Theory]
    [InlineData("internal closed record class Outcome; internal sealed record class Success : Outcome; internal sealed record class Failure : Outcome;", "Outcome")]
    [InlineData("internal sealed record class Success; internal sealed record class Failure; internal union Outcome(Success, Failure);", "Outcome")]
    public void PreviewCompilerDetectsMissingSemanticVariant(string declarations, string parameterType)
    {
        var complete = Diagnostics(declarations + " internal static class Match { internal static int Describe(" + parameterType + " value) => value switch { Success => 1, Failure => 2 }; }");
        Assert.DoesNotContain(complete, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(complete, d => d.Id == "CS8509");

        var incomplete = Diagnostics(declarations + " internal static class Match { internal static int Describe(" + parameterType + " value) => value switch { Success => 1 }; }");
        Assert.DoesNotContain(incomplete, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(incomplete, d => d.Id == "CS8509");
    }

    private static IEnumerable<Diagnostic> Diagnostics(string source) =>
        CSharpCompilation.Create("PreviewExhaustivenessFixture",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            AnalyzerTests.PlatformReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics();

    [Fact]
    public void StageOutcomeRejectsInvalidOptionalFields()
    {
        Assert.Throws<ArgumentException>(() => StageOutcomeModel.Parse("Validated", "unexpected"));
        Assert.Throws<ArgumentException>(() => StageOutcomeModel.Parse("ExternalBlocked", null));
        Assert.Equal(("Validated", (string?)null), StageOutcomeModel.Canonical(new StageValidated()));
        Assert.Equal(("ExternalBlocked", "compiler unavailable"), StageOutcomeModel.Canonical(new StageExternalBlocked("compiler unavailable")));
        Assert.Equal(("NotProduced", "compiler unavailable"), StageOutcomeModel.Canonical(new StageNotProduced("compiler unavailable")));
        Assert.Equal(("NotAttempted", "previous stage blocked"), StageOutcomeModel.Canonical(new StageNotAttempted("previous stage blocked")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void DirectSemanticConstructionCannotBypassRequiredPayload(string? payload)
    {
        Assert.ThrowsAny<ArgumentException>(() => new StageExternalBlocked(payload!));
        Assert.ThrowsAny<ArgumentException>(() => new StageNotProduced(payload!));
        Assert.ThrowsAny<ArgumentException>(() => new StageNotAttempted(payload!));
    }

#if SINGPLUS_PREVIEW_LANGUAGE
    [Fact]
    public void ColdUnionMatchesEachCaseExhaustively()
    {
        QualificationRoute local = new LocalCorpus("sha256:abc");
        QualificationRoute external = new ExternalCompilerNeeded("v2");
        Assert.Equal("local:sha256:abc", PreviewUnionQualification.Describe(local));
        Assert.Equal("external:v2", PreviewUnionQualification.Describe(external));
    }
#endif
}
