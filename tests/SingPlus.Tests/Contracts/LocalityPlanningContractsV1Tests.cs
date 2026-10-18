using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class LocalityPlanningContractsV1Tests
{
    [Fact]
    public void PlannerSelectsFreshLowestAdjustedCostWithoutGrantingAuthority()
    {
        var source = Estimate("source", 3, latency: 100, contention: 0);
        var plan = LocalityPlanningPolicyV1.PlanHostMove(source,
            [Estimate("busy-fast", 4, latency: 60, contention: 1000),
             Estimate("steady", 5, latency: 80, contention: 100)],
            1500, 7, 9, 64);

        Assert.Equal("steady", plan.DestinationProviderClass);
        Assert.Equal(5UL, plan.DestinationEstimateGeneration);
        Assert.False(source.AuthorizesPlacement);
        Assert.False(source.AuthorizesDataMotion);
        Assert.False(plan.AuthorizesRegionAccess);
        Assert.False(plan.AuthorizesMovement);
        Assert.False(plan.ReservesBudget);
    }

    [Fact]
    public void StaleOrIncompatibleEvidenceCannotProduceAPlan()
    {
        var source = Estimate("source", 1, validUntil: 2000);
        Assert.Throws<InvalidOperationException>(() => LocalityPlanningPolicyV1.PlanHostMove(source,
            [Estimate("destination", 1)], 2000, 1, 1, 64));
        Assert.Throws<InvalidOperationException>(() => LocalityPlanningPolicyV1.PlanHostMove(source,
            [Estimate("destination", 1) with { DataMotionBytes = 32 }], 1500, 1, 1, 64));
    }

    [Fact]
    public void UnknownVersionAndUnboundedValuesFailClosed()
    {
        Assert.Throws<NotSupportedException>(() => (Estimate("provider", 1) with { Version = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (Estimate("provider", 1) with { ConfidencePermille = 1001 }).Validate());
        Assert.Throws<ArgumentException>(() => new DataMotionPlanV1(1, "source", "destination", 1, 1,
            1, 1, 64, 63, DataMotionModeV1.HostCopyThenReleaseSource).Validate());
    }

    [Fact]
    public void PublicPlanningSurfaceContainsNoPrivateTopologyOrAuthorityIdentity()
    {
        var names = new[] { typeof(LocalityCostEstimateV1), typeof(DataMotionPlanV1),
                typeof(DataMotionReceiptV1), typeof(LocalityPlanningExperimentV1) }
            .SelectMany(type => type.GetProperties().Select(property => property.Name));
        string[] forbidden = ["Numa", "Core", "Lane", "Bdf", "PhysicalAddress", "Pasid", "Capability", "RegionHandle", "TopologyId"];
        Assert.All(names, name => Assert.All(forbidden,
            fragment => Assert.DoesNotContain(fragment, name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ControlledModeledAbReportsImprovementWithoutPromotingPolicy()
    {
        var experiment = LocalityPlanningExperimentPolicyV1.CompareAgainstReference(
            Estimate("source", 1),
            [Estimate("reference", 2, latency: 200), Estimate("candidate", 3, latency: 80)],
            "reference", 1500, 1, 1, 64);

        Assert.Equal("candidate", experiment.SelectedProviderClass);
        Assert.Equal((ushort)600, experiment.ModeledImprovementPermille);
        Assert.True(experiment.ObservedModeledAdvantage);
        Assert.False(experiment.AuthorizesPlacement);
        Assert.False(experiment.AuthorizesMovement);
        Assert.False(experiment.QualifiesElapsedTimePerformance);
        Assert.False(experiment.SupportsPolicyPromotion);
    }

    [Fact]
    public void ControlledModeledAbReportsNoAdvantageWhenReferenceWins()
    {
        var experiment = LocalityPlanningExperimentPolicyV1.CompareAgainstReference(
            Estimate("source", 1),
            [Estimate("reference", 2, latency: 50), Estimate("candidate", 3, latency: 80)],
            "reference", 1500, 1, 1, 64);

        Assert.Equal("reference", experiment.SelectedProviderClass);
        Assert.Equal((ushort)0, experiment.ModeledImprovementPermille);
        Assert.False(experiment.ObservedModeledAdvantage);
    }

    [Fact]
    public void ControlledModeledAbRejectsMissingOrStaleReferenceArm()
    {
        var source = Estimate("source", 1);
        Assert.Throws<InvalidOperationException>(() =>
            LocalityPlanningExperimentPolicyV1.CompareAgainstReference(source,
                [Estimate("candidate", 3)], "missing", 1500, 1, 1, 64));
        Assert.Throws<InvalidOperationException>(() =>
            LocalityPlanningExperimentPolicyV1.CompareAgainstReference(source,
                [Estimate("reference", 2, validUntil: 1400), Estimate("candidate", 3)],
                "reference", 1500, 1, 1, 64));
    }

    private static LocalityCostEstimateV1 Estimate(string provider, ulong generation,
        ulong latency = 100, ushort contention = 0, long validUntil = 2000) =>
        new(1, provider, generation, 1000, validUntil, 64, latency, contention, 900);
}
