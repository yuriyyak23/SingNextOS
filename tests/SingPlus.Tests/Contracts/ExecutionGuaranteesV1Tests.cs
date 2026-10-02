using System.Reflection;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Contracts;

public sealed class ExecutionGuaranteesV1Tests
{
    public static IEnumerable<object[]> InvalidProviderTokens()
    {
        for (var field = 0; field < 4; field++)
            for (var fault = 0; fault < 3; fault++)
                yield return [field, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidProviderTokens))]
    public void GuaranteeTupleRejectsInvalidUnicodeAndControlsBeforeDigest(int field, int fault)
    {
        var token = fault switch { 0 => "provider\uD800", 1 => "provider\uDC00", _ => "provider\0tail" };
        var original = HybridCpu114SemanticCompatibility.Map(Request()).Value!;
        var changed = original with { Digest = default };
        changed = field switch
        {
            0 => changed with { ProviderContract = changed.ProviderContract with { PackageId = token } },
            1 => changed with { ProviderContract = changed.ProviderContract with { PackageVersion = token } },
            2 => changed with { ProviderContract = changed.ProviderContract with { SchemaIdentity = token } },
            _ => changed with { ProviderExecutionClass = token },
        };
        Assert.Throws<ArgumentException>(() => changed.Canonicalize());
    }

    [Fact]
    public void ScalarUnicodeGuaranteeTupleRetainsExactV1DigestWithoutNormalization()
    {
        var original = HybridCpu114SemanticCompatibility.Map(Request()).Value!;
        Assert.Equal(original.Digest, original.Canonicalize().Digest);
        var scalar = (original with { Digest = default, ProviderExecutionClass = "provider:é🚀" }).Canonicalize();
        Assert.Equal(scalar.Digest, scalar.Canonicalize().Digest);
        var decomposed = (original with { Digest = default, ProviderExecutionClass = "provider:e\u0301🚀" }).Canonicalize();
        Assert.NotEqual(scalar.Digest, decomposed.Digest);
        Assert.True((original with { Digest = default, ProviderExecutionClass = "provider:\uFFFD" }).Canonicalize().Digest != default);
    }

    [Fact]
    public void HybridCpu114MappingIsDimensionSpecificAndNeverExceedsStaticAdmission()
    {
        var result = HybridCpu114SemanticCompatibility.Map(Request());

        Assert.True(result.IsSuccess, result.Message);
        var guarantees = result.Value!;
        Assert.Equal(new ProviderContractIdentityV1(
            "HybridCPU.ExternalRuntime.Contracts", "1.14.0", "ExternalOperationContract/1.4.0"),
            guarantees.ProviderContract);
        Assert.Equal("external-operation/1.4.0", guarantees.ProviderExecutionClass);
        Assert.All(guarantees.DimensionClaims(), claim =>
        {
            if (claim.Support == SemanticGuaranteeSupportV1.Unsupported)
            {
                Assert.Equal(SemanticClaimLevelV1.ModelOnly, claim.ClaimLevel);
                Assert.Equal(GuaranteeEvidenceSourceV1.None, claim.EvidenceSource);
            }
            else
            {
                Assert.Equal(SemanticClaimLevelV1.StaticAdmission, claim.ClaimLevel);
                Assert.NotEqual(GuaranteeEvidenceSourceV1.None, claim.EvidenceSource);
            }
        });
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Measurement.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.ResourceEnforcement.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Isolation.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Contention.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Replay.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Determinism.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Containment.Support);
        Assert.Equal(SemanticGuaranteeSupportV1.Unsupported, guarantees.Locality.Support);
        Assert.False(guarantees.AuthorizesExecution);
        Assert.False(guarantees.AuthorizesEffect);
        Assert.False(guarantees.AuthorizesPublication);
        Assert.False(guarantees.IsProviderAdmission);
    }

    [Fact]
    public void MappingRejectsUnknownVersionAndSemanticClasses()
    {
        Assert.ThrowsAny<ArgumentException>(() => Request(
            version: new Hc.HybridCpuExternalContractVersion(9, 9, 9)));
        Assert.ThrowsAny<ArgumentException>(() => Request(
            visibility: (Hc.ExternalVisibilityRequirement)99));
        Assert.ThrowsAny<ArgumentException>(() => Request(
            cancellation: (Hc.ExternalCancellationMode)99));
    }

    [Fact]
    public void GuaranteeDigestAndEvidenceSourceValidationFailClosed()
    {
        var guarantees = HybridCpu114SemanticCompatibility.Map(Request()).Value!;
        Assert.Throws<ArgumentException>(() => (guarantees with
        {
            Digest = new("00"),
            ProviderContract = guarantees.ProviderContract with { PackageVersion = "1.14.1" },
        }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (guarantees with
        {
            Digest = default,
            FailureHandling = guarantees.FailureHandling with { EvidenceSource = GuaranteeEvidenceSourceV1.None },
        }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (guarantees with
        {
            Digest = default,
            Measurement = guarantees.Measurement with { ClaimLevel = SemanticClaimLevelV1.RuntimeEnforced },
        }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (guarantees with
        {
            Digest = default,
            ProviderExecutionClass = " external-operation/1.4.0 ",
        }).Canonicalize());
    }

    [Fact]
    public void GuaranteeAndBindingTypesExposeNoAuthorityMutationSurface()
    {
        string[] forbidden =
        [
            "Authorize", "Mint", "Derive", "Revoke", "Reserve", "Charge", "Settle",
            "Publish", "Release", "Reclaim", "Submit", "Execute", "Retire",
        ];
        foreach (var type in new[] { typeof(ExecutionGuaranteesV1), typeof(SemanticExecutionBindingV1) })
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                          BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => forbidden.Any(verb => method.Name.StartsWith(verb, StringComparison.Ordinal)))
                .ToArray();
            Assert.Empty(methods);
        }
    }

    [Fact]
    public void HybridCpuPackageAbiHasNoSingNextOrResourceGuaranteeAuthorityTypes()
    {
        var assembly = typeof(Hc.ExternalOperationRequest).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("SingPlus", StringComparison.OrdinalIgnoreCase) == true);
        var names = assembly.GetExportedTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var absent in new[]
                 {
                     "OperationObligationsV1", "ExecutionGuaranteesV1", "SemanticExecutionBinding",
                     "ExternalResourceEnvelope", "ExternalResourceUsageEvidence", "ExternalBudgetLease",
                 })
            Assert.DoesNotContain(absent, names);
    }

    internal static Hc.ExternalOperationSemanticRequest Request(
        Hc.HybridCpuExternalContractVersion? version = null,
        Hc.ExternalVisibilityRequirement visibility = Hc.ExternalVisibilityRequirement.StagedOutput,
        Hc.ExternalCancellationMode cancellation = Hc.ExternalCancellationMode.ExactAcknowledgement) => new(
        version ?? Hc.ExternalOperationContract.Version,
        new(Guid.Parse("9a862e10-c155-46d3-aafb-ab6078d04824")),
        Hc.ExternalEffectClass.NonIdempotent,
        visibility,
        cancellation,
        Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
}
