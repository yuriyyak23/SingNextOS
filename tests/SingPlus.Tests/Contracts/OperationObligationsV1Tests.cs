using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Contracts;

public sealed class OperationObligationsV1Tests
{
    [Fact]
    public void CanonicalizationClonesCollectionsAndProducesStableExactDigest()
    {
        var regions = new[] { RegionObligation() };
        var resources = new[] { Envelope(10) };
        var first = Contract(regions, resources).Canonicalize();

        regions[0] = RegionObligation() with { MutationEpoch = new(2) };
        resources[0] = Envelope(11);
        var second = Contract([RegionObligation()], [Envelope(10)]).Canonicalize();

        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(new MutationEpoch(1), first.RegionUses[0].MutationEpoch);
        Assert.Equal(10UL, first.ResourceRequirements[0].Amount);
        Assert.False(first.AuthorizesExecution);
        Assert.False(first.AuthorizesEffect);
        Assert.False(first.ReservesResources);
        Assert.False(first.AuthorizesPublication);
    }

    [Fact]
    public void TamperingOrUnknownContractValuesFailClosed()
    {
        var canonical = Contract([RegionObligation()], [Envelope(10)]).Canonicalize();
        Assert.Throws<ArgumentException>(() => (canonical with
        {
            Digest = new("00"),
            ResourceRequirements = [Envelope(11)],
        }).Canonicalize());
        Assert.Throws<NotSupportedException>(() => (canonical with { Version = 2, Digest = default }).Canonicalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => (canonical with
        {
            PublicationPolicy = (ExternalPublicationPolicy)99,
            Digest = default,
        }).Canonicalize());
        Assert.Throws<NotSupportedException>(() => (canonical with
        {
            SemanticRequirements = Requirements() with
            {
                Replay = new(1, SemanticRequirementStrengthV1.Mandatory, (ReplayClassV1)99),
            },
            Digest = default,
        }).Canonicalize());
        Assert.Throws<ArgumentNullException>(() => (canonical with
        {
            RegionUses = null!,
            Digest = default,
        }).Canonicalize());
    }

    [Fact]
    public void PublicSurfaceHasNoAuthorityMutationMethods()
    {
        string[] forbidden =
        [
            "Authorize", "Mint", "Derive", "Revoke", "Reserve", "Charge", "Settle",
            "Publish", "Release", "Reclaim", "Submit", "Execute", "Retire",
        ];
        var methods = typeof(OperationObligationsV1)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => forbidden.Any(verb => method.Name.StartsWith(verb, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(methods);
    }

    private static OperationObligationsV1 Contract(
        IReadOnlyList<OperationRegionObligationV1> regions,
        IReadOnlyList<ResourceEnvelopeV1> resources) => new(
        1,
        new(new(1), 1),
        new(new(1), new(1)),
        null,
        regions,
        resources,
        new(0, long.MaxValue),
        ExternalVisibilityRequirement.PublicationFence,
        ExternalPublicationPolicy.Staged,
        new(ExternalEffectClass.StagedReversibleUntilPublish, ExternalReplayProtection.None, false),
        Requirements());

    private static OperationRegionObligationV1 RegionObligation() => new(
        new(new(1), new(1)), RegionUseMode.StagedOutput, new(0, 16), new(1));

    internal static OperationSemanticRequirementsV1 Requirements() => new(
        Required(IsolationClassV1.DomainSeparated),
        Required(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
        Required(ReplayClassV1.None),
        Required(DeterminismClassV1.StableOrdering),
        Required(CancellationClassV1.BeforeDispatch),
        Required(ContainmentClassV1.None),
        Advisory(LocalityClassV1.Any),
        Required(ResourceAssuranceV1.RuntimeEnforced));

    private static SemanticRequirementV1<T> Required<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Mandatory, value);

    private static SemanticRequirementV1<T> Advisory<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Advisory, value);

    internal static ResourceEnvelopeV1 Envelope(ulong amount) => new(
        1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");
}
