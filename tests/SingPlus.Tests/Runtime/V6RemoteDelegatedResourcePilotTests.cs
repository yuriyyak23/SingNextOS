using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6RemoteDelegatedResourcePilotTests
{
    private static readonly RemoteAuthorityLeaseV1 Lease = new(1,
        Guid.Parse("690c6048-a963-4ae0-a687-f2ee6adbd44d"),
        "host:owner", 3, "host:remote", 5, new('c', 64),
        RemoteLeaseRightsV1.Read | RemoteLeaseRightsV1.Execute | RemoteLeaseRightsV1.StagedWrite,
        7, 11, 13, 17);
    private static readonly RemoteLeaseCurrentStateV1 Current = new(3, 5, 7, 11, 13, false, false);
    private static readonly RemoteResourceEscrowV1 Escrow = new(1, 100, 60, 0, 0);

    [Fact]
    public void ProductionFactoryCannotBypassDisabledFeatureGate()
    {
        var result = V6RemoteDelegatedResourcePilot.Create(Lease, Current, Escrow, 19);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
    }

    [Fact]
    public void QualificationPilotAdmitsExactNarrowRightsAndConservesEscrow()
    {
        var pilot = Create();

        var submitted = pilot.Submit(Lease,
            RemoteLeaseRightsV1.Execute | RemoteLeaseRightsV1.StagedWrite, 20);

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.Submitted, submitted.Value!.State);
        Assert.Equal(20UL, submitted.Value.Escrow.ConsumedAllocation);
        Assert.Equal(1U, submitted.Value.SubmitCount);
        Assert.True(submitted.Value.HasSingleLogicalOwner);
        Assert.False(submitted.Value.TransfersParentAuthority);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ResourceIdentityRightsAndEscrowBoundsFailClosed(int mutation)
    {
        var pilot = Create();
        var presented = mutation == 0 ? Lease with { ResourceCorrelationDigest = new string('d', 64) } : Lease;
        var rights = mutation == 1 ? (RemoteLeaseRightsV1)8 : RemoteLeaseRightsV1.Execute;
        var allocation = mutation == 2 ? 61UL : 1UL;

        var result = pilot.Submit(presented, rights, allocation);

        Assert.False(result.IsSuccess);
        Assert.Equal(0U, pilot.Query().SubmitCount);
        Assert.Equal(0UL, pilot.Query().Escrow.ConsumedAllocation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartitionRevocationAndRebootsDenyFreshSubmit(int fault)
    {
        var pilot = Create();
        _ = fault switch
        {
            0 => pilot.Partition(),
            1 => pilot.Revoke(),
            2 => pilot.RebootOwner(4, 8),
            _ => pilot.RebootRemote(6),
        };

        Assert.False(pilot.Submit(Lease, RemoteLeaseRightsV1.Execute, 1).IsSuccess);
        Assert.Equal(0U, pilot.Query().SubmitCount);
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmitHasExactlyOneLinearizationWinner()
    {
        var pilot = Create();
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            pilot.Submit(Lease, RemoteLeaseRightsV1.Execute, 1))).ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(1U, pilot.Query().SubmitCount);
        Assert.Equal(1UL, pilot.Query().Escrow.ConsumedAllocation);
    }

    [Fact]
    public async Task ExactFenceClosurePublicationAndReclaimAreSingleShotUnderConcurrency()
    {
        var pilot = Create();
        Assert.True(pilot.Submit(Lease, RemoteLeaseRightsV1.StagedWrite, 20).IsSuccess);
        Assert.True(pilot.Fence(Lease).IsSuccess);
        var closure = Closure();
        Assert.True(pilot.ObserveEffectClosure(closure).IsSuccess);
        var publications = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => pilot.Publish(Lease))));
        Assert.Single(publications, result => result.IsSuccess);

        var reclaimed = pilot.Reclaim(closure);

        Assert.True(reclaimed.IsSuccess, reclaimed.Message);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.Reclaimed, reclaimed.Value!.State);
        Assert.Equal(1U, reclaimed.Value.PublishCount);
        Assert.Equal(reclaimed.Value.Escrow.DelegatedAllocation,
            reclaimed.Value.Escrow.ConsumedAllocation + reclaimed.Value.Escrow.ReturnedAllocation);
        Assert.False(pilot.Reclaim(closure).IsSuccess);
    }

    [Fact]
    public void WrongProviderOrLeaseClosureCannotUnlockPublicationOrReclaim()
    {
        var pilot = Create();
        Assert.True(pilot.Submit(Lease, RemoteLeaseRightsV1.StagedWrite, 20).IsSuccess);
        Assert.True(pilot.Fence(Lease).IsSuccess);

        Assert.Equal(KernelError.ExternalEffectUncontained,
            pilot.ObserveEffectClosure(Closure() with { ProviderGeneration = 20 }).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            pilot.ObserveEffectClosure(Closure() with { LeaseGeneration = 12 }).Error);
        Assert.False(pilot.Publish(Lease).IsSuccess);
        Assert.False(pilot.Reclaim(Closure()).IsSuccess);
    }

    [Fact]
    public void PartitionBetweenClosureAndPublicationDeniesPublishButNotExactSafeReclaim()
    {
        var pilot = Create();
        Assert.True(pilot.Submit(Lease, RemoteLeaseRightsV1.StagedWrite, 15).IsSuccess);
        Assert.True(pilot.Fence(Lease).IsSuccess);
        var closure = Closure();
        Assert.True(pilot.ObserveEffectClosure(closure).IsSuccess);
        pilot.Partition();

        Assert.Equal(KernelError.DependencyUnavailable, pilot.Publish(Lease).Error);
        Assert.True(pilot.Reclaim(closure).IsSuccess);
    }

    [Fact]
    public void OwnerSequenceExpiryDeniesSubmitWithoutWallClockAuthority()
    {
        var pilot = Create();

        var snapshot = pilot.AdvanceOwnerSequence(18);

        Assert.False(snapshot.Lease.UsesWallClockExpiry);
        Assert.Equal(KernelError.StaleGeneration,
            pilot.Submit(Lease, RemoteLeaseRightsV1.Execute, 1).Error);
    }

    private static V6RemoteDelegatedResourcePilot Create() =>
        V6RemoteDelegatedResourcePilot.CreateForQualification(Lease, Current, Escrow, 19).Value!;

    private static RemoteEffectClosureV1 Closure() =>
        new(1, Lease.LeaseId, Lease.OwnerEpoch, Lease.LeaseGeneration, 19, 23, true, true);
}
