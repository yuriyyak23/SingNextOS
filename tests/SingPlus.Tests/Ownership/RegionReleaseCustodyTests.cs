using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Ownership;

public sealed class RegionReleaseCustodyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualLocalMoveRetainsReleaseContinuityButCopiedMovedWrapperDoesNot(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel(); ProcessHandle owner = TestFixtures.Create(kernel, 3113, 31130).Handle;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(owner, 8).Value!.Move().Move();
        OwnedRegion<long>? region = scalar ? kernel.AllocateRegion(owner, 7L).Value!.Move().Move() : null;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;
        Assert.False(scalar ? kernel.ReleaseRegion(owner, (SingPlus.Sip.OwnedRegion<long>)Clone(region!)).IsSuccess
            : kernel.ReleaseRegion(owner, (SingPlus.Sip.OwnedBuffer<byte>)Clone(buffer!)).IsSuccess);
        Assert.True(scalar ? region!.IsValid : buffer!.IsValid);
        Assert.True(scalar ? kernel.ReleaseRegion(owner, region!).IsSuccess : kernel.ReleaseRegion(owner, buffer!).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ReleaseRequiresOriginalRegisteredPayloadAcrossEqualPublicTuples(bool scalar, bool copy)
    {
        (RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) Build()
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ProcessHandle owner = TestFixtures.Create(kernel, 3111, 31110).Handle;
            ProcessHandle admin = TestFixtures.Create(kernel, 3112, 31120).Handle;
            CapabilityId cap = kernel.MintCapability(new(31120), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, cap, owner, "release-custody",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!.ProcessBudget;
            return (kernel, owner, budget);
        }
        (RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) x = Build(); (RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) foreign = Build();
        OwnedBuffer<byte>? localBuffer = scalar ? null : x.Kernel.AllocateBuffer<byte>(x.Owner, 8).Value!;
        OwnedBuffer<byte>? otherBuffer = scalar ? null : foreign.Kernel.AllocateBuffer<byte>(foreign.Owner, 8).Value!;
        OwnedRegion<long>? localRegion = scalar ? x.Kernel.AllocateRegion(x.Owner, 0x6BL).Value! : null;
        OwnedRegion<long>? otherRegion = scalar ? foreign.Kernel.AllocateRegion(foreign.Owner, 0x7DL).Value! : null;
        localBuffer?.Span.Fill(0x6B); otherBuffer?.Span.Fill(0x7D);
        RegionHandle handle = scalar ? localRegion!.Handle : localBuffer!.Handle;
        Assert.Equal(handle, scalar ? otherRegion!.Handle : otherBuffer!.Handle);
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;
        KernelResult released = scalar
            ? x.Kernel.ReleaseRegion(x.Owner, copy ? (SingPlus.Sip.OwnedRegion<long>)Clone(localRegion!) : otherRegion!)
            : x.Kernel.ReleaseRegion(x.Owner, copy ? (SingPlus.Sip.OwnedBuffer<byte>)Clone(localBuffer!) : otherBuffer!);
        Assert.False(released.IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(x.Kernel.Regions.Snapshot()).State);
        Assert.Equal(RegionState.Owned, Assert.Single(foreign.Kernel.Regions.Snapshot()).State);
        ulong Used((RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) item) =>
            item.Kernel.QueryBudget(item.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(8UL, Used(x)); Assert.Equal(8UL, Used(foreign));
        if (scalar)
        {
            Assert.Equal(0x6BL, localRegion!.Value); Assert.Equal(0x7DL, otherRegion!.Value);
            Assert.True(x.Kernel.ReleaseRegion(x.Owner, localRegion).IsSuccess);
        }
        else
        {
            Assert.All(localBuffer!.Span.ToArray(), b => Assert.Equal((byte)0x6B, b));
            Assert.All(otherBuffer!.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
            Assert.True(x.Kernel.ReleaseRegion(x.Owner, localBuffer).IsSuccess);
        }
        Assert.Equal(0UL, Used(x)); Assert.Equal(8UL, Used(foreign));
        Assert.True(scalar ? foreign.Kernel.ReleaseRegion(foreign.Owner, otherRegion!).IsSuccess
            : foreign.Kernel.ReleaseRegion(foreign.Owner, otherBuffer!).IsSuccess);
        Assert.Equal(0UL, Used(foreign));
    }
}
