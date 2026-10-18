using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class RasFailureContractsV1Tests
{
    [Fact]
    public void DamagedSubrangeEvidenceProjectsWithoutBecomingAuthority()
    {
        var evidence = Evidence(ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission,
            new RegionUseRange(8, 16));
        var consequence = FailureConsequencePolicyV1.Project(evidence);

        Assert.Equal(FailureConsequenceKindV1.RegionSubrangeQuarantine, consequence.Kind);
        Assert.Equal(evidence.Scope, consequence.Scope);
        Assert.False(evidence.AuthorizesRegionMutation);
        Assert.False(evidence.ProvesExternalOperationClosure);
        Assert.False(evidence.AuthorizesReclaim);
        Assert.False(consequence.AuthorizesRegionMutation);
        Assert.False(consequence.ProvesClosure);
        Assert.False(consequence.AuthorizesReclaim);
    }

    [Fact]
    public void WholeDomainFailureProjectsOnlyInFlightAmbiguity()
    {
        var consequence = FailureConsequencePolicyV1.Project(
            Evidence(ProviderHealthStateV1.Failed, ProviderFaultClassV1.CrashStop, null));

        Assert.Equal(FailureConsequenceKindV1.InFlightOperationAmbiguity, consequence.Kind);
        Assert.Null(consequence.AffectedRange);
        Assert.False(consequence.ProvesClosure);
    }

    [Fact]
    public void HealthyEvidenceNeverClearsPriorQuarantine()
    {
        var consequence = FailureConsequencePolicyV1.Project(
            Evidence(ProviderHealthStateV1.Healthy, ProviderFaultClassV1.Recovery, null));

        Assert.Equal(FailureConsequenceKindV1.None, consequence.Kind);
        Assert.False(consequence.AuthorizesRegionMutation);
    }

    [Fact]
    public void UnknownOrMalformedEvidenceFailsClosed()
    {
        Assert.Throws<NotSupportedException>(() => Evidence((ProviderHealthStateV1)255,
            ProviderFaultClassV1.Omission, null).Validate());
        Assert.Throws<ArgumentException>(() => Evidence(ProviderHealthStateV1.Healthy,
            ProviderFaultClassV1.Recovery, new RegionUseRange(0, 1)).Validate());
        Assert.Throws<ArgumentException>(() => new ProviderHealthEvidenceV1(1,
            new("provider", "domain", 0, 1), 1, ProviderHealthStateV1.Failed,
            ProviderFaultClassV1.CrashStop, null).Validate());
    }

    [Fact]
    public void BackingReplacementCorrelationIsBoundedAndNonAuthoritative()
    {
        var replacement = Replacement();

        Assert.Equal(replacement, replacement.Validate());
        Assert.False(replacement.AuthorizesRegionMutation);
        Assert.False(replacement.ProvesEffectClosure);
        Assert.False(replacement.AuthorizesReclaim);
        Assert.DoesNotContain(typeof(RegionHandle), typeof(RegionBackingReplacementEvidenceV1)
            .GetProperties().Select(property => property.PropertyType));
    }

    [Fact]
    public void BackingReplacementRejectsMalformedGenerationRangeAndDigest()
    {
        var replacement = Replacement();

        Assert.Throws<ArgumentException>(() => (replacement with
            { ReplacementBackingGeneration = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (replacement with
            { Range = new(0, 0) }).Validate());
        Assert.Throws<ArgumentException>(() => (replacement with
            { PriorEffectClosureDigest = new string('A', 64) }).Validate());
    }

    private static ProviderHealthEvidenceV1 Evidence(ProviderHealthStateV1 health,
        ProviderFaultClassV1 fault, RegionUseRange? range) => new(1,
        new("provider-a", "memory-bank-2", 3, 7), 11, health, fault, range);

    private static RegionBackingReplacementEvidenceV1 Replacement() => new(1,
        new(4, 2), new("provider-a", "memory-bank-2", 4, 8), 12, 9,
        new(8, 16), new('a', 64), new('b', 64));
}
