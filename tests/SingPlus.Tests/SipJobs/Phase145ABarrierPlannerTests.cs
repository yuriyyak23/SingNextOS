using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase145ABarrierPlannerTests
{
    [Fact]
    public void EveryKnownBoundaryIsClosedAndConservative()
    {
        foreach (var barrier in Enum.GetValues<SipJobBarrierClass>())
        {
            var decision = SipJobBarrierPlanner.Classify(SipJobBarrierPlanner.Version, barrier);
            Assert.True(decision.RequiresLiveRevalidation);
            if (barrier == SipJobBarrierClass.None)
                Assert.Equal(SipJobBarrierDisposition.Fuse, decision.Disposition);
            else if (barrier is SipJobBarrierClass.NativeIsolated or SipJobBarrierClass.ConfidentialDomain)
                Assert.Equal(SipJobBarrierDisposition.RejectFutureGated, decision.Disposition);
            else
                Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip, decision.Disposition);
        }

        Assert.False(SipJobFeatureGates.IsEnabled("FG-BARRIER-MODEL"));
    }

    [Fact]
    public void UnknownVersionAndBoundaryFailClosed()
    {
        Assert.Equal(SipJobBarrierDisposition.RejectFutureGated,
            SipJobBarrierPlanner.Classify(2, SipJobBarrierClass.None).Disposition);
        Assert.Equal(SipJobBarrierDisposition.RejectFutureGated,
            SipJobBarrierPlanner.Classify(1, (SipJobBarrierClass)int.MaxValue).Disposition);
    }

    [Fact]
    public void CrossRuntimeAlwaysUsesOrdinaryTransport()
    {
        var decision = SipJobBarrierPlanner.Classify(1, SipJobBarrierClass.CrossRuntime);
        Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip, decision.Disposition);
        Assert.Equal("OrdinarySipTransport", decision.LifecycleOwner);
    }

    [Theory]
    [InlineData((int)SipJobBarrierClass.ExternalEffect, "ExternalOperationAuthority")]
    [InlineData((int)SipJobBarrierClass.Publication, "ResponseRegistry")]
    [InlineData((int)SipJobBarrierClass.OwnershipSettlement, "RegionAuthority")]
    [InlineData((int)SipJobBarrierClass.UnsupportedAuthorityCommit, "CapabilityAuthority")]
    public void MaterializationRetainsExistingLifecycleOwner(
        int barrierValue, string owner)
    {
        var barrier = (SipJobBarrierClass)barrierValue;
        var decision = SipJobBarrierPlanner.Classify(1, barrier);
        Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip, decision.Disposition);
        Assert.Equal(owner, decision.LifecycleOwner);
    }
}
