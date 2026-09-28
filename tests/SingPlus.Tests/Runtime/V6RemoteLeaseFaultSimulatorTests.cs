using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6RemoteLeaseFaultSimulatorTests
{
    [Fact]
    public void PartitionAtEveryLifecyclePrefixPreventsNewSubmitAndPublication()
    {
        var prefixes = new List<V6RemoteLeaseSimulationState> { Initial() };
        var submitted = V6RemoteLeaseFaultSimulator.Submit(prefixes[^1]).Value!;
        prefixes.Add(submitted);
        var fenced = V6RemoteLeaseFaultSimulator.Fence(submitted).Value!;
        prefixes.Add(fenced);
        prefixes.Add(V6RemoteLeaseFaultSimulator.ObserveEffectClosure(fenced).Value!);

        foreach (var prefix in prefixes)
        {
            var partitioned = V6RemoteLeaseFaultSimulator.Partition(prefix).Value!;
            Assert.False(V6RemoteLeaseFaultSimulator.Submit(partitioned).IsSuccess);
            Assert.False(V6RemoteLeaseFaultSimulator.Publish(partitioned).IsSuccess);
            Assert.True(partitioned.HasSingleLogicalOwner);
        }
    }

    [Fact]
    public void RenewRevokeRaceHasNoEligibleOldOrDuplicateGeneration()
    {
        var initial = Initial();
        var renewed = V6RemoteLeaseFaultSimulator.Renew(initial).Value!;
        var revoked = V6RemoteLeaseFaultSimulator.Revoke(renewed).Value!;

        Assert.Equal(initial.Lease.LeaseGeneration + 1, renewed.Lease.LeaseGeneration);
        Assert.Equal(RemoteLeaseAdmissionCodeV1.StaleLeaseGeneration,
            RemoteAuthorityLeaseEvaluatorV1.Evaluate(initial.Lease, renewed.Current));
        Assert.False(V6RemoteLeaseFaultSimulator.Submit(revoked).IsSuccess);
        Assert.False(V6RemoteLeaseFaultSimulator.Renew(revoked).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RebootExpiryAndLeaseAbaNeverResurrectExecution(int fault)
    {
        var submitted = V6RemoteLeaseFaultSimulator.Submit(Initial()).Value!;
        var stale = fault switch
        {
            0 => V6RemoteLeaseFaultSimulator.OwnerReboot(submitted).Value!,
            1 => V6RemoteLeaseFaultSimulator.RemoteReboot(submitted).Value!,
            _ => V6RemoteLeaseFaultSimulator.Expire(submitted).Value!,
        };

        Assert.True(stale.Quarantined);
        Assert.False(V6RemoteLeaseFaultSimulator.Submit(stale).IsSuccess);
        Assert.False(V6RemoteLeaseFaultSimulator.Publish(stale).IsSuccess);
        Assert.False(V6RemoteLeaseFaultSimulator.Reclaim(stale).IsSuccess);
    }

    [Fact]
    public void DuplicateExecutionAndPublicationAreImpossible()
    {
        var state = Initial();
        state = V6RemoteLeaseFaultSimulator.Submit(state).Value!;
        Assert.False(V6RemoteLeaseFaultSimulator.Submit(state).IsSuccess);
        state = V6RemoteLeaseFaultSimulator.Fence(state).Value!;
        state = V6RemoteLeaseFaultSimulator.ObserveEffectClosure(state).Value!;
        state = V6RemoteLeaseFaultSimulator.Publish(state).Value!;

        Assert.False(V6RemoteLeaseFaultSimulator.Publish(state).IsSuccess);
        Assert.Equal(1U, state.SubmitCount);
        Assert.Equal(1U, state.PublishCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderLossAndFabricReconfigurationQuarantineAmbiguousWrite(bool fabricChange)
    {
        var submitted = V6RemoteLeaseFaultSimulator.Submit(Initial()).Value!;
        var failed = fabricChange
            ? V6RemoteLeaseFaultSimulator.FabricReconfigure(submitted).Value!
            : V6RemoteLeaseFaultSimulator.ProviderLoss(submitted).Value!;

        Assert.True(failed.Quarantined);
        Assert.False(V6RemoteLeaseFaultSimulator.Publish(failed).IsSuccess);
        Assert.False(V6RemoteLeaseFaultSimulator.Reclaim(failed).IsSuccess);
    }

    [Fact]
    public void ReclaimRequiresFenceClosureAndConservesEscrowExactlyOnce()
    {
        var state = V6RemoteLeaseFaultSimulator.Submit(Initial()).Value!;
        Assert.False(V6RemoteLeaseFaultSimulator.Reclaim(state).IsSuccess);
        state = V6RemoteLeaseFaultSimulator.Fence(state).Value!;
        Assert.False(V6RemoteLeaseFaultSimulator.Reclaim(state).IsSuccess);
        state = V6RemoteLeaseFaultSimulator.ObserveEffectClosure(state).Value!;
        state = V6RemoteLeaseFaultSimulator.Reclaim(state).Value!;

        Assert.True(state.Reclaimed);
        Assert.Equal(40UL, state.Escrow.ReturnedAllocation);
        Assert.False(V6RemoteLeaseFaultSimulator.Reclaim(state).IsSuccess);
        Assert.Equal(state.Escrow.DelegatedAllocation,
            state.Escrow.ConsumedAllocation + state.Escrow.ReturnedAllocation);
    }

    [Fact]
    public void BoundedFaultInterleavingsPreserveSingleOwnerAndReclaimSafety()
    {
        Func<V6RemoteLeaseSimulationState, KernelResult<V6RemoteLeaseSimulationState>>[] actions =
        [V6RemoteLeaseFaultSimulator.Submit, V6RemoteLeaseFaultSimulator.Partition,
         V6RemoteLeaseFaultSimulator.Revoke, V6RemoteLeaseFaultSimulator.OwnerReboot,
         V6RemoteLeaseFaultSimulator.RemoteReboot, V6RemoteLeaseFaultSimulator.Expire,
         V6RemoteLeaseFaultSimulator.ProviderLoss, V6RemoteLeaseFaultSimulator.FabricReconfigure,
         V6RemoteLeaseFaultSimulator.Fence, V6RemoteLeaseFaultSimulator.ObserveEffectClosure,
         V6RemoteLeaseFaultSimulator.Publish, V6RemoteLeaseFaultSimulator.Reclaim];
        var frontier = new List<V6RemoteLeaseSimulationState> { Initial() };
        for (var depth = 0; depth < 4; depth++)
        {
            var next = new List<V6RemoteLeaseSimulationState>();
            foreach (var state in frontier)
                foreach (var action in actions)
                {
                    var result = action(state);
                    if (!result.IsSuccess) continue;
                    var candidate = result.Value!;
                    Assert.True(candidate.HasSingleLogicalOwner);
                    Assert.InRange(candidate.SubmitCount, 0U, 1U);
                    Assert.InRange(candidate.PublishCount, 0U, 1U);
                    Assert.False(candidate.Reclaimed && (!candidate.Fenced || !candidate.EffectClosed));
                    Assert.False(candidate.PublishCount != 0 && candidate.Quarantined);
                    next.Add(candidate);
                }
            frontier = next;
        }
        Assert.NotEmpty(frontier);
    }

    private static V6RemoteLeaseSimulationState Initial()
    {
        var lease = new RemoteAuthorityLeaseV1(1,
            Guid.Parse("a0cf1f57-a1f4-4181-b3d7-13c2c149e617"), "host:owner", 3,
            "host:remote", 5, new('a', 64), RemoteLeaseRightsV1.Read |
            RemoteLeaseRightsV1.Execute | RemoteLeaseRightsV1.StagedWrite, 7, 11, 13, 17);
        return V6RemoteLeaseFaultSimulator.Initial(lease, 19,
            new(1, 100, 60, 20, 0));
    }
}
