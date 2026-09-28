using System.Reflection;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase12SipJobResourceBoundaryTests
{
    [Fact]
    public void ResourceConsumptionAlwaysMaterializesOrdinaryOwnerBoundary()
    {
        var decision = SipJobBarrierPlanner.Classify(
            SipJobBarrierPlanner.Version, SipJobBarrierClass.ResourceConsumption);

        Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip, decision.Disposition);
        Assert.Equal("ResourceBudgetAuthority", decision.LifecycleOwner);
        Assert.True(decision.RequiresLiveRevalidation);
    }

    [Fact]
    public void ResourceSipJobGateRemainsOffAndCannotBeEnabledByPlanMetadata()
    {
        Assert.False(VNextFeatureGates.IsEnabled("FG-VNX-SIPJOB-RESOURCE"));
        Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip,
            SipJobBarrierPlanner.Classify(SipJobBarrierPlanner.Version,
                SipJobBarrierClass.ResourceConsumption).Disposition);
    }

    [Fact]
    public void SipJobMetadataSurfaceHasNoResourceAuthorityMutationApi()
    {
        string[] forbidden = ["Mint", "Reserve", "BindLease", "ConsumeLease", "SettleLease",
            "ReleaseLease", "AuthorizeResource", "ResourceBudgetAuthority"];
        Type[] types = [typeof(SipJobPlanDescriptor), typeof(SipJobVerifiedSegmentMetadata),
            typeof(SipJobBarrierDecision), typeof(VerifiedPlanMetadata)];
        var surface = types.SelectMany(type => type.GetMembers(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(member => member.Name);
        Assert.DoesNotContain(surface, name => forbidden.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ExternalEffectPublicationAndResourceRemainDistinctBarriers()
    {
        var resource = SipJobBarrierPlanner.Classify(1, SipJobBarrierClass.ResourceConsumption);
        var effect = SipJobBarrierPlanner.Classify(1, SipJobBarrierClass.ExternalEffect);
        var publication = SipJobBarrierPlanner.Classify(1, SipJobBarrierClass.Publication);

        Assert.Equal("ResourceBudgetAuthority", resource.LifecycleOwner);
        Assert.Equal("ExternalOperationAuthority", effect.LifecycleOwner);
        Assert.Equal("ResponseRegistry", publication.LifecycleOwner);
        Assert.All([resource, effect, publication], decision =>
            Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip, decision.Disposition));
    }
}
