using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class Phase12ReplayProviderRefinementTests
{
    [Fact]
    public void ReplayReportAndTraceEvidenceExposeNoSubmitOrAuthoritySurface()
    {
        Assert.False(new TraceReplayReport(TraceReplayMode.Diagnostic, true, []).AuthorizesEffect);
        Assert.False(new TraceReplayReport(TraceReplayMode.DeterministicModel, true, []).AuthorizesProviderSubmission);

        string[] forbidden = ["Submit", "Execute", "Authorize", "Mint", "Reserve", "Publish"];
        foreach (var type in new[] { typeof(TraceReplayReport), typeof(SemanticTraceEvent) })
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                  BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                method => forbidden.Any(verb => method.Name.StartsWith(verb, StringComparison.Ordinal)));
    }

    [Fact]
    public void HybridCpu114CannotClaimReplayDeterminismOrTypedIsolation()
    {
        var guarantees = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;

        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Replay.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Determinism.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Isolation.Support);
        Assert.All(new[] { guarantees.Replay.ClaimLevel, guarantees.Determinism.ClaimLevel,
            guarantees.Isolation.ClaimLevel }, level => Assert.Equal(SemanticClaimLevelV1.ModelOnly, level));
    }

    [Fact]
    public void RuntimeLegalityContractHasNoCompilerMetadataOrTypedSlotAuthorityInput()
    {
        var method = Assert.Single(typeof(IRuntimeLegalityService).GetMethods());
        var parameter = Assert.Single(method.GetParameters());

        Assert.Equal(typeof(SemanticExecutionBindingV1), parameter.ParameterType);
        Assert.DoesNotContain("Compiler", parameter.ParameterType.FullName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TypedSlot", parameter.ParameterType.FullName, StringComparison.OrdinalIgnoreCase);
    }
}
