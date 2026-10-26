using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapDelegateCommitTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public async Task CallbackPreparationAllowsRealOwnerProgressAndRefusesStaleCommit(bool nonce, int fault)
    {
        CapabilityAuthority authority = new CapabilityAuthority(new(Guid.NewGuid()), new(128, 128));
        CapabilityDescriptorV1 parent = authority.Mint(new(10), new(20), ResourceKind.Device, "source",
            CapabilityRights.Read | CapabilityRights.Delegate, 7, 1, null, null, 1, 2).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        void Block() { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); }
        if (nonce) SetNonce(authority, () => { Block(); return Guid.NewGuid(); });
        Task<KernelResult<CapabilityDescriptorV1>> delegateTask = Task.Run(() => authority.Delegate(parent.CapabilityId, new(20), new(30), CapabilityRights.Read, 9,
            c => { if (!nonce) Block(); return c; }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.Run(() =>
            {
                Assert.Single(authority.InspectionSnapshot());
                if (fault == 0) Assert.True(authority.Revoke(parent.CapabilityId).IsSuccess);
                else if (fault == 1) authority.RevokeAllForDomain(new(30));
                else Assert.True(authority.ConsumeQuota(parent.CapabilityId, new(20), 7, 1).IsSuccess);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        KernelResult<CapabilityDescriptorV1> result = await delegateTask;
        SetNonce(authority, Guid.NewGuid);
        if (fault < 2)
        {
            Assert.Equal(KernelError.CapabilityRevoked, result.Error);
            Assert.Single(authority.InspectionSnapshot());
            KernelResult<CapabilityDescriptorV1> fresh = authority.Mint(new(10), new(40), ResourceKind.Device, "fresh", CapabilityRights.Read, 1);
            Assert.Equal(2UL, fresh.Value!.CapabilityId.Value);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            CapabilityDescriptorV1 child = result.Value!;
            Assert.Equal(authority.InspectConstraints(parent.CapabilityId)!.Value.Constraints.Quota.Account,
                authority.InspectConstraints(child.CapabilityId)!.Value.Constraints.Quota.Account);
            Assert.Equal(0UL, authority.InspectConstraints(child.CapabilityId)!.Value.SharedRemaining);
            Assert.Equal(KernelError.BudgetExceeded, authority.ConsumeQuota(child.CapabilityId, new(30), 9, 1).Error);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualV1V2SourceDelegateDoesNotHoldOuterGateAroundNonce(bool v2)
    {
        CapabilityAuthority authority = new CapabilityAuthority(new(Guid.NewGuid()), new(128, 128));
        CapabilityDescriptorV1 parent = authority.Mint(new(10), new(20), ResourceKind.Device, "source", CapabilityRights.Read | CapabilityRights.Delegate, 7).Value!;
        CapabilityHandleV2 opaque = authority.GetHandleV2(parent.CapabilityId, new(20), 7).Value;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        SetNonce(authority, () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); return Guid.NewGuid(); });
        Task<KernelError> work = Task.Run(() => v2 ? authority.Delegate(opaque, new(20), new(30), CapabilityRights.Read, 9).Error :
            authority.Delegate(parent.CapabilityId, new(20), new(30), CapabilityRights.Read, 9).Error);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.Run(() => Assert.True(authority.Revoke(parent.CapabilityId).IsSuccess)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.CapabilityRevoked, await work);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task KernelFinalCommitRejectsStoppedOrReplacedExactParty(bool source, bool replace)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess _, ProcessHandle delegator) = TestFixtures.Create(kernel, 1, 20);
        (Sip.SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 30);
        CapabilityDescriptorV1 parent = kernel.MintCapability(new(20), delegator, ResourceKind.Device, "source", CapabilityRights.Read | CapabilityRights.Delegate).Value!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        SetNonce(kernel.CapabilityAuthority, () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); return Guid.NewGuid(); });
        Task<KernelResult<CapabilityDescriptorV1>> work = Task.Run(() => kernel.DelegateCapability(delegator, target, parent.CapabilityId, CapabilityRights.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            ProcessHandle stopped = source ? delegator : target;
            await Task.Run(() =>
            {
                Assert.True(kernel.TerminateProcess(stopped).IsSuccess);
                if (replace) TestFixtures.Create(kernel, stopped.ProcessId.Value, source ? 20UL : 30UL, 2);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.StaleHandle, (await work).Error);
        Assert.Single(kernel.CapabilityAuthority.InspectionSnapshot());
    }

    [Fact]
    public void KernelDelegationRejectsSourceFromAnotherSubjectGeneration()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess _, ProcessHandle delegator) = TestFixtures.Create(kernel, 1, 20);
        (Sip.SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 30);
        CapabilityDescriptorV1 source = kernel.CapabilityAuthority.Mint(new(20), new(20), ResourceKind.Device, "wrong-generation",
            CapabilityRights.Read | CapabilityRights.Delegate, 7).Value!;
        Assert.Equal(KernelError.StaleGeneration, kernel.DelegateCapability(delegator, target,
            source.CapabilityId, CapabilityRights.Read).Error);
        Assert.Single(kernel.CapabilityAuthority.InspectionSnapshot());
    }

    [Fact]
    public void NonceFaultClearsDelegateGuardWithoutConsumingIdentityOrQuota()
    {
        CapabilityAuthority authority = new CapabilityAuthority(new(Guid.NewGuid()), new(128, 128));
        CapabilityDescriptorV1 parent = authority.Mint(new(10), new(20), ResourceKind.Device, "source",
            CapabilityRights.Read | CapabilityRights.Delegate, 7, 1, null, null, 1, 2).Value!;
        SetNonce(authority, () =>
        {
            Assert.Equal(KernelError.CapacityExhausted, authority.Delegate(parent.CapabilityId, new(20), new(30), CapabilityRights.Read, 9).Error);
            throw new InvalidOperationException("controlled nonce fault");
        });
        Assert.Throws<InvalidOperationException>(() => authority.Delegate(parent.CapabilityId, new(20), new(30), CapabilityRights.Read, 9));
        Assert.Single(authority.InspectionSnapshot());
        Assert.Equal(1UL, authority.InspectConstraints(parent.CapabilityId)!.Value.SharedRemaining);
        SetNonce(authority, Guid.NewGuid);
        KernelResult<CapabilityDescriptorV1> fresh = authority.Delegate(parent.CapabilityId, new(20), new(30), CapabilityRights.Read, 9);
        Assert.True(fresh.IsSuccess);
        Assert.Equal(2UL, fresh.Value!.CapabilityId.Value);
        Assert.Equal(1UL, authority.InspectConstraints(fresh.Value.CapabilityId)!.Value.SharedRemaining);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OverlappingDelegationNoncesCommitDistinctSlotsAndOneSharedQuota(bool collision)
    {
        CapabilityAuthority authority = new CapabilityAuthority(new(Guid.NewGuid()), new(128, 128));
        CapabilityDescriptorV1 parent = authority.Mint(new(10), new(20), ResourceKind.Device, "source",
            CapabilityRights.Read | CapabilityRights.Delegate, 7, 1, null, null, 1, 2).Value!;
        Guid repeated = Guid.NewGuid();
        using Barrier barrier = new Barrier(2);
        int calls = 0;
        SetNonce(authority, () =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
                return collision ? repeated : Guid.NewGuid();
            }
            return Guid.NewGuid();
        });
        Task<KernelResult<CapabilityDescriptorV1>> first = Task.Run(() => authority.Delegate(parent.CapabilityId, new(20), new(30), CapabilityRights.Read, 9));
        Task<KernelResult<CapabilityDescriptorV1>> second = Task.Run(() => authority.Delegate(parent.CapabilityId, new(20), new(30), CapabilityRights.Read, 9));
        KernelResult<CapabilityDescriptorV1>[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(collision ? 3 : 2, calls);
        Assert.Equal(2, results.Select(r => r.Value!.CapabilityId).Distinct().Count());
        Assert.Equal(2, results.Select(r => authority.GetReference(r.Value!.CapabilityId).Value.Nonce).Distinct().Count());
        Assert.All(results, r => Assert.Equal(authority.InspectConstraints(parent.CapabilityId)!.Value.Constraints.Quota.Account,
            authority.InspectConstraints(r.Value!.CapabilityId)!.Value.Constraints.Quota.Account));
        Assert.True(authority.ConsumeQuota(results[0].Value!.CapabilityId, new(30), 9, 1).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded, authority.ConsumeQuota(results[1].Value!.CapabilityId, new(30), 9, 1).Error);
    }
    private static void SetNonce(CapabilityAuthority authority, Func<Guid> factory) =>
        typeof(CapabilityAuthority).GetField("_nonceFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(authority, factory);
}


