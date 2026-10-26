using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Ownership;

public sealed class RegionPayloadExitCustodyTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void ExitAndReclaimRequireCanonicalPayloadAndNeverInvalidateForeignStorage(bool scalar, int fault)
    {
        (RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) Build()
        {
            RuntimeKernel kernel = new RuntimeKernel(); ProcessHandle owner = TestFixtures.Create(kernel, 3131, 31310).Handle;
            ProcessHandle admin = TestFixtures.Create(kernel, 3132, 31320).Handle;
            CapabilityId cap = kernel.MintCapability(new(31320), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, cap, owner, "payload-exit",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!.ProcessBudget;
            return (kernel, owner, budget);
        }
        (RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) x = Build(); (RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) foreign = Build();
        OwnedBuffer<byte>? buffer = scalar ? null : x.Kernel.AllocateBuffer<byte>(x.Owner, 8).Value!;
        OwnedRegion<long>? region = scalar ? x.Kernel.AllocateRegion(x.Owner, 0x6BL).Value! : null;
        OwnedBuffer<byte>? peerBuffer = scalar ? null : foreign.Kernel.AllocateBuffer<byte>(foreign.Owner, 8).Value!;
        OwnedRegion<long>? peerRegion = scalar ? foreign.Kernel.AllocateRegion(foreign.Owner, 0x7DL).Value! : null;
        buffer?.Span.Fill(0x6B); peerBuffer?.Span.Fill(0x7D);
        OwnedBuffer<byte>? movedBuffer = buffer?.Move().Move(); OwnedRegion<long>? movedRegion = region?.Move().Move();
        OwnedBuffer<byte>? peerMovedBuffer = peerBuffer?.Move().Move(); OwnedRegion<long>? peerMovedRegion = peerRegion?.Move().Move();
        RegionHandle handle = scalar ? region!.Handle : buffer!.Handle;
        Assert.Equal(handle, scalar ? peerRegion!.Handle : peerBuffer!.Handle);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary records = (IDictionary)typeof(RegionAuthority).GetField("_regions", flags)!.GetValue(x.Kernel.Regions)!;
        object record = records[handle.RegionId]!; PropertyInfo route = record.GetType().GetProperty("Payload")!;
        object original = route.GetValue(record)!;
        PropertyInfo canonical = record.GetType().GetProperty("OriginalPayload")!;
        Assert.Same(original, canonical.GetValue(record));
        RegionUseDescriptor use = x.Kernel.AcquireRegionUse(x.Owner, handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(x.Kernel.InvalidateRegionUse(x.Owner, use.Handle).IsSuccess);
        route.SetValue(record, fault switch
        {
            0 => null,
            1 => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null),
            _ => scalar ? peerRegion : peerBuffer
        });
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.True(scalar ? peerMovedRegion!.IsValid : peerMovedBuffer!.IsValid);
        Assert.False(scalar ? movedRegion!.IsValid : movedBuffer!.IsValid);
        if (scalar) Assert.Equal(0x7DL, peerMovedRegion!.Value);
        else Assert.All(peerMovedBuffer!.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.True(x.Kernel.ReleaseRegionUse(x.Owner, use.Handle).IsSuccess);
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess); // Closing a use does not repair payload custody.
        ulong Used((RuntimeKernel Kernel, ProcessHandle Owner, BudgetAccountHandle Budget) item) =>
            item.Kernel.QueryBudget(item.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(8UL, Used(x)); Assert.Equal(8UL, Used(foreign));
        Assert.Same(original, canonical.GetValue(record));
        Assert.Equal(RegionState.Owned, Assert.Single(x.Kernel.Regions.Snapshot()).State);
        route.SetValue(record, original);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL, Used(x)); Assert.Equal(8UL, Used(foreign));
        Assert.Equal(RegionState.Released, Assert.Single(x.Kernel.Regions.Snapshot()).State);
        Assert.Null(canonical.GetValue(record)); Assert.Null(route.GetValue(record));
        Assert.True(scalar ? peerMovedRegion!.IsValid : peerMovedBuffer!.IsValid);
        Assert.True(scalar ? foreign.Kernel.ReleaseRegion(foreign.Owner, peerMovedRegion!).IsSuccess
            : foreign.Kernel.ReleaseRegion(foreign.Owner, peerMovedBuffer!).IsSuccess);
        Assert.Equal(0UL, Used(foreign));
    }
}
