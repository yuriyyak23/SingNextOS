using System.Reflection;
using SingPlus.Contracts;

namespace SingPlus.Tests.Ownership;

public sealed partial class BorrowLifetimeCustodyTests
{
    private const BindingFlags ByteFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Close(Scenario s)
    {
        Assert.True(s.Kernel.ReturnBorrow(s.Borrower, s.Lease.Handle).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegionUse(s.Borrower, s.Use).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Owner, s.Buffer).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReplacedLeaseLifetimeCannotReadOwnerReuseAfterOriginalReturn(bool foreignLifetime)
    {
        var x = Build(0x6B); var peer = Build(0x7D);
        Assert.Equal(x.Lease.Handle, peer.Lease.Handle);
        var field = x.Lease.GetType().GetField("_lifetime", ByteFlags)!;
        var original = field.GetValue(x.Lease)!;
        field.SetValue(x.Lease, foreignLifetime ? field.GetValue(peer.Lease) :
            typeof(object).GetMethod("MemberwiseClone", ByteFlags)!.Invoke(original, null));
        Assert.True(x.Kernel.ReturnBorrow(x.Borrower, x.Lease.Handle).IsSuccess);
        x.Buffer.Span.Fill(0x17);
        byte? leaked = null;
        var refused = Record.Exception(() => leaked = x.Lease.Span[0]);
        Assert.Null(leaked); Assert.IsType<InvalidOperationException>(refused); Assert.False(x.Lease.IsValid);
        Assert.True(peer.Lease.IsValid); Assert.All(peer.Lease.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        field.SetValue(x.Lease, original); Assert.False(x.Lease.IsValid);
        Assert.True(x.Kernel.ReleaseRegionUse(x.Borrower, x.Use).IsSuccess);
        Assert.True(x.Kernel.ReleaseRegion(x.Owner, x.Buffer).IsSuccess);
        Assert.Equal(0UL, x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Close(peer);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void BorrowStorageRouteLossDeniesOwnerWritesAndFreshBorrowViews(int fault)
    {
        var x = Build(0x6B); var peer = Build(0x7D);
        var storage = x.Buffer.GetType().GetField("_storage", ByteFlags)!.GetValue(x.Buffer)!;
        var route = storage.GetType().GetField("_borrowLifetime", ByteFlags)!;
        var original = route.GetValue(storage)!;
        var peerLife = peer.Lease.GetType().GetField("_lifetime", ByteFlags)!.GetValue(peer.Lease)!;
        route.SetValue(storage, fault switch { 0 => null, 1 => typeof(object).GetMethod("MemberwiseClone", ByteFlags)!.Invoke(original, null), _ => peerLife });
        Assert.Throws<InvalidOperationException>(() => x.Buffer.Span[0] = 0x17);
        Assert.False(x.Lease.IsValid); Assert.Throws<InvalidOperationException>(() => { _ = x.Lease.Span[0]; });
        Assert.Throws<InvalidOperationException>(() => x.Buffer.Move());
        Assert.Equal(8UL, x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        route.SetValue(storage, original);
        Assert.True(x.Lease.IsValid); Assert.All(x.Lease.Span.ToArray(), b => Assert.Equal((byte)0x6B, b));
        Assert.True(peer.Lease.IsValid); Assert.All(peer.Lease.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Close(x); Close(peer);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReclaimInvalidatesOriginalStorageBorrowOnlyAndPreservesForeignActiveBorrow(bool canonicalField)
    {
        var x = Build(0x6B); var peer = Build(0x7D);
        Assert.True(x.Kernel.ReturnBorrow(x.Borrower, x.Lease.Handle).IsSuccess);
        Assert.True(x.Kernel.ReleaseRegionUse(x.Borrower, x.Use).IsSuccess);
        var storage = x.Buffer.GetType().GetField("_storage", ByteFlags)!.GetValue(x.Buffer)!;
        storage.GetType().GetField(canonicalField ? "_originalBorrowLifetime" : "_borrowLifetime", ByteFlags)!.SetValue(storage,
            peer.Lease.GetType().GetField("_lifetime", ByteFlags)!.GetValue(peer.Lease));
        Assert.True(x.Kernel.ReleaseRegion(x.Owner, x.Buffer).IsSuccess);
        Assert.Equal(0UL, x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(peer.Lease.IsValid); Assert.All(peer.Lease.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.Equal(8UL, peer.Kernel.QueryBudget(peer.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Close(peer);
    }
}
