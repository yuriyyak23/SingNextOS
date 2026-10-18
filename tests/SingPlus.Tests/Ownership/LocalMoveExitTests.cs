using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Ownership;

public sealed class LocalMoveExitTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CopiedSourceCannotMintOriginalLocalMoveOrAccessBytes(bool scalar)
    {
        var kernel = new RuntimeKernel(); var owner = TestFixtures.Create(kernel, 3127, 31270).Handle;
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var region = scalar ? kernel.AllocateRegion(owner, 7L).Value! : null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        if (scalar)
        {
            var copy = (SingPlus.Sip.OwnedRegion<long>)Clone(region!);
            Assert.Throws<InvalidOperationException>(() => copy.Move());
            Assert.False(copy.IsValid);
            Assert.Throws<InvalidOperationException>(() => copy.Value = 99);
            Assert.Equal(7L, region!.Value);
            Assert.True(kernel.ReleaseRegion(owner, region.Move()).IsSuccess);
        }
        else
        {
            var copy = (SingPlus.Sip.OwnedBuffer<byte>)Clone(buffer!);
            Assert.Throws<InvalidOperationException>(() => copy.Move());
            Assert.False(copy.IsValid);
            Assert.Throws<InvalidOperationException>(() => copy.Span[0] = 99);
            Assert.Throws<InvalidOperationException>(() => { _ = copy.Borrow(); });
            Assert.True(buffer!.IsValid);
            Assert.True(kernel.ReleaseRegion(owner, buffer.Move()).IsSuccess);
        }
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CopiedOrForeignMoveOriginCannotGrantAccessOrResurrectAfterExit(bool foreignOrigin)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 3125, 31250).Handle;
        var peerOwner = TestFixtures.Create(kernel, 3126, 31260).Handle;
        var original = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var moved = original.Move();
        var peer = kernel.AllocateBuffer<byte>(peerOwner, 8).Value!.Move(); peer.Span.Fill(0x7D);
        var use = kernel.AcquireRegionUse(owner, moved.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.InvalidateRegionUse(owner, use.Handle).IsSuccess);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var origin = moved.GetType().GetField("_localMoveOrigin", flags)!;
        var exact = origin.GetValue(moved)!;
        origin.SetValue(moved, foreignOrigin ? origin.GetValue(peer) :
            typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(exact, null));
        Assert.False(moved.IsValid);
        Assert.Throws<InvalidOperationException>(() => moved.Span[0] = 0x17);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        origin.SetValue(moved, exact);
        Assert.False(moved.IsValid);
        Assert.Throws<InvalidOperationException>(() => moved.Move());
        Assert.All(peer.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.True(kernel.ReleaseRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.ReleaseRegion(peerOwner, peer).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ProcessExitRevokesMovedLocalAccessWithoutClosingIndependentUseOrForeignStorage(bool scalar)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 3121, 31210).Handle;
        var foreign = TestFixtures.Create(kernel, 3122, 31220).Handle;
        var admin = TestFixtures.Create(kernel, 3123, 31230).Handle;
        var cap = kernel.MintCapability(new(31230), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle Admit(ProcessHandle subject) => kernel.AdmitProcessBudget(admin, cap, subject, "move-exit",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!.ProcessBudget;
        var budget = Admit(owner); var otherBudget = Admit(foreign);
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(owner, 8).Value!.Move().Move();
        var region = scalar ? kernel.AllocateRegion(owner, 0x6BL).Value!.Move().Move() : null;
        var peer = kernel.AllocateBuffer<byte>(foreign, 8).Value!.Move().Move(); peer.Span.Fill(0x7D);
        buffer?.Span.Fill(0x6B);
        var handle = scalar ? region!.Handle : buffer!.Handle;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object bytes = scalar ? region!.GetType().GetField("_buffer", flags)!.GetValue(region)! : buffer!;
        var storage = bytes.GetType().GetField("_storage", flags)!.GetValue(bytes)!;
        var data = (Array)storage.GetType().GetProperty("Data")!.GetValue(storage)!;
        var use = kernel.AcquireRegionUse(owner, handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.InvalidateRegionUse(owner, use.Handle).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(ProcessState.Exiting, kernel.Processes.Resolve(owner).Value!.State);
        Assert.False(scalar ? region!.IsValid : buffer!.IsValid);
        if (scalar)
        {
            Assert.Throws<InvalidOperationException>(() => region!.Value = 0x17);
            Assert.Throws<InvalidOperationException>(() => region!.Move());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => buffer!.Span[0] = 0x17);
            Assert.Throws<InvalidOperationException>(() => buffer!.Move());
            Assert.Throws<InvalidOperationException>(() => buffer!.Borrow());
        }
        Assert.Equal(0x6BL, Convert.ToInt64(data.GetValue(0)));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
        Assert.Equal(RegionUseState.Invalidated, kernel.Regions.SnapshotUses().Single(u => u.Handle == use.Handle).State);
        ulong Used(BudgetAccountHandle account) => kernel.QueryBudget(account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(8UL, Used(budget)); Assert.Equal(8UL, Used(otherBudget));
        Assert.All(peer.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.True(kernel.ReleaseRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
        Assert.Equal(0UL, Used(budget)); Assert.Equal(8UL, Used(otherBudget));
        Assert.All(peer.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.True(kernel.ReleaseRegion(foreign, peer).IsSuccess);
        Assert.Equal(0UL, Used(otherBudget));
    }
}
