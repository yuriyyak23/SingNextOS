using System.Collections.Concurrent;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapMintCommitTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreparationCallbackDoesNotHoldSourceGateAndRevocationDeniesCommit(bool canonical)
    {
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Guid Nonce() { if (!canonical) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); } return Guid.NewGuid(); }
        IEnumerable<CapabilityOperation> Operations()
        {
            if (canonical) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); }
            yield return CapabilityOperation.Read;
        }
        CapabilityAuthority authority = new CapabilityAuthority(new(Guid.NewGuid()), new(128, 128), nonceFactory: Nonce);
        Task<KernelResult<CapabilityDescriptorV1>> mint = Task.Run(() => authority.Mint(new(10), new(20), ResourceKind.Device, "pending",
            CapabilityRights.Read, 7, 1, null, null, 3, 1, operations: Operations()));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.Run(() => { Assert.Empty(authority.InspectionSnapshot()); authority.RevokeAllForDomain(new(20)); })
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.CapabilityRevoked, (await mint).Error);
        Assert.Empty(authority.InspectionSnapshot());
        KernelResult<CapabilityDescriptorV1> fresh = authority.Mint(new(10), new(20), ResourceKind.Device, "fresh", CapabilityRights.Read, 7);
        Assert.True(fresh.IsSuccess);
        Assert.Equal(1UL, fresh.Value!.CapabilityId.Value);
        Assert.Equal(1UL, authority.InspectConstraints(fresh.Value.CapabilityId)!.Value.Constraints.Quota.Account.Value);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task KernelMintRevalidatesActualSubjectAndIssuerAfterNonce(int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess _, ProcessHandle issuer) = TestFixtures.Create(kernel, 1, 10);
        (Sip.SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 2, 20);
        if (fault == 0) kernel.RegisterComposedWorkTeardownParticipant(new RefusingParticipant());
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        SetNonce(kernel.CapabilityAuthority, () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); return Guid.NewGuid(); });
        Task<KernelResult<CapabilityDescriptorV1>> mint = Task.Run(() => kernel.MintCapability(new(10), subject, ResourceKind.Device, "pending", CapabilityRights.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            await Task.Run(() =>
            {
                Assert.Empty(kernel.CapabilityAuthority.InspectionSnapshot());
                KernelResult stop = kernel.TerminateProcess(fault == 3 ? issuer : subject);
                if (fault == 0) Assert.False(stop.IsSuccess); else Assert.True(stop.IsSuccess, stop.Message);
                if (fault == 2) TestFixtures.Create(kernel, 2, 20, 2);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        KernelResult<CapabilityDescriptorV1> result = await mint;
        Assert.False(result.IsSuccess);
        Assert.Equal(fault == 0 ? KernelError.InvalidTransition : fault == 2 ? KernelError.StaleHandle :
            fault == 3 ? KernelError.DomainNotFound : KernelError.StaleHandle, result.Error);
        Assert.Empty(kernel.CapabilityAuthority.InspectionSnapshot());
    }

    [Fact]
    public void OrdinaryConcurrentMintCommitsFreshDistinctIdentityAndQuota()
    {
        CapabilityAuthority authority = new CapabilityAuthority(new(Guid.NewGuid()), new(128, 128));
        ConcurrentBag<CapabilityDescriptorV1> results = new ConcurrentBag<CapabilityDescriptorV1>();
        Parallel.For(0, 64, i =>
        {
            KernelResult<CapabilityDescriptorV1> minted = authority.Mint(new(10), new(20), ResourceKind.Device, $"device:{i}", CapabilityRights.Read, 7);
            Assert.True(minted.IsSuccess, minted.Message);
            results.Add(minted.Value!);
        });
        Assert.Equal(64, results.Select(x => x.CapabilityId).Distinct().Count());
        Assert.Equal(64, results.Select(x => authority.InspectConstraints(x.CapabilityId)!.Value.Constraints.Quota.Account).Distinct().Count());
        Assert.Equal(64, authority.InspectionSnapshot().Length);
    }

    [Fact]
    public void CallbackExceptionClearsReentryGuardWithoutIdentityConsumption()
    {
        CapabilityAuthority authority = null!;
        bool fail = true;
        authority = new(new(Guid.NewGuid()), new(128, 128), nonceFactory: () =>
        {
            if (fail)
            {
                Assert.Equal(KernelError.CapacityExhausted, authority.Mint(new(10), new(20), ResourceKind.Device, "nested", CapabilityRights.Read, 7).Error);
                throw new InvalidOperationException("nonce preparation fault");
            }
            return Guid.NewGuid();
        });
        Assert.Throws<InvalidOperationException>(() => authority.Mint(new(10), new(20), ResourceKind.Device, "fault", CapabilityRights.Read, 7));
        Assert.Empty(authority.InspectionSnapshot());
        fail = false;
        KernelResult<CapabilityDescriptorV1> fresh = authority.Mint(new(10), new(20), ResourceKind.Device, "fresh", CapabilityRights.Read, 7);
        Assert.True(fresh.IsSuccess);
        Assert.Equal(1UL, fresh.Value!.CapabilityId.Value);
    }

    [Fact]
    public async Task OverlappingPreparedNonceCollisionRetriesAgainstActualOwner()
    {
        using Barrier barrier = new Barrier(2);
        Guid repeated = Guid.NewGuid();
        int calls = 0;
        CapabilityAuthority authority = new CapabilityAuthority(new(Guid.NewGuid()), new(128, 128), nonceFactory: () =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
                return repeated;
            }
            return Guid.NewGuid();
        });
        Task<KernelResult<CapabilityDescriptorV1>> first = Task.Run(() => authority.Mint(new(10), new(20), ResourceKind.Device, "first", CapabilityRights.Read, 7));
        Task<KernelResult<CapabilityDescriptorV1>> second = Task.Run(() => authority.Mint(new(10), new(20), ResourceKind.Device, "second", CapabilityRights.Read, 7));
        KernelResult<CapabilityDescriptorV1>[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(results, x => Assert.True(x.IsSuccess, x.Message));
        Assert.Equal(3, calls);
        Assert.Equal(2, results.Select(x => authority.GetReference(x.Value!.CapabilityId).Value.Nonce).Distinct().Count());
        Assert.Equal(2, results.Select(x => authority.InspectConstraints(x.Value!.CapabilityId)!.Value.Constraints.Quota.Account).Distinct().Count());
    }
    [Fact]
    public void DelegateCallbackCanMintWithoutOuterSourceGate()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        (Sip.SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 2, 20);
        CapabilityDescriptorV1 source = kernel.MintCapability(new(10), subject, ResourceKind.Device, "source", CapabilityRights.Read | CapabilityRights.Delegate).Value!;
        bool invoked = false;
        SetNonce(kernel.CapabilityAuthority, () => { return Guid.NewGuid(); });
        KernelResult<CapabilityDescriptorV1> delegated = kernel.CapabilityAuthority.Delegate(source.CapabilityId, new(20), new(30), CapabilityRights.Read, 1, child =>
        {
            Assert.True(kernel.MintCapability(new(10), subject, ResourceKind.Device, "nested", CapabilityRights.Read).IsSuccess);
            invoked = true;
            return child;
        });
        Assert.True(delegated.IsSuccess, delegated.Message);
        Assert.True(invoked);
        Assert.Equal(3, kernel.CapabilityAuthority.InspectionSnapshot().Length);
    }
    private static void SetNonce(CapabilityAuthority authority, Func<Guid> factory) =>
        typeof(CapabilityAuthority).GetField("_nonceFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(authority, factory);

    private sealed class RefusingParticipant : IComposedWorkTeardownParticipant
    {
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) =>
            KernelResult.Fail(KernelError.ExternalEffectUncontained, "Controlled missing producer closure.");
    }
}





