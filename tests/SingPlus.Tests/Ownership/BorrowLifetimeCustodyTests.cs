using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Ownership;

public sealed partial class BorrowLifetimeCustodyTests
{
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, ProcessHandle Borrower,
        BudgetAccountHandle Budget, OwnedBuffer<byte> Buffer, BorrowLease<byte> Lease, RegionUseHandle Use);

    private static Scenario Build(byte value)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle owner = TestFixtures.Create(kernel, 3141, 31410).Handle;
        ProcessHandle borrower = TestFixtures.Create(kernel, 3142, 31420).Handle;
        ProcessHandle admin = TestFixtures.Create(kernel, 3143, 31430).Handle;
        CapabilityId cap = kernel.MintCapability(new(31430), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, cap, owner, "borrow-custody",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8), new(ServiceBudgetDimension.IpcMessages, 4),
             new(ServiceBudgetDimension.IpcBytes, 4096)]).Value!.ProcessBudget;
        OwnedBuffer<byte> bytes = kernel.AllocateBuffer<byte>(owner, 8).Value!; bytes.Span.Fill(value);
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("BorrowCustody", "borrow-custody", "Ready", ["Done"],
            [new ProtocolMessageDescriptorV1(1, "Read", borrows: ["data"], requestPayload:
                new RequestPayloadDescriptorV1(RequestPayloadKind.Ownership, "data", "SingPlus.Sip.OwnedBuffer",
                    ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))],
            [new ProtocolTransitionV1(1, "Ready", "Ready")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(owner, borrower, protocol, 2).Value;
        KernelResult<ChannelEnvelope> sent = kernel.Send(owner, borrower, channel.Left, 1, bytes); Assert.True(sent.IsSuccess, sent.Message);
        KernelResult<ChannelEnvelope> received = kernel.Receive(borrower, channel.Right); Assert.True(received.IsSuccess, received.Message);
        BorrowLease<byte> lease = Assert.IsType<BorrowLease<byte>>(received.Value!.Payload);
        KernelResult<RegionUseDescriptor> use = kernel.AcquireBorrowRegionUse(owner, borrower, lease.Handle, RegionUseMode.ReadOnly, new(0, 8));
        Assert.True(use.IsSuccess, use.Message);
        return new(kernel, owner, borrower, budget, bytes, lease, use.Value!.Handle);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void BorrowRoutingLossCannotAdmitNewUseOrCloseForeignLifetime(int fault, bool exiting)
    {
        Scenario x = Build(0x6B); Scenario foreign = Build(0x7D);
        Assert.Equal(x.Lease.Handle, foreign.Lease.Handle);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Record(Scenario s) => ((IDictionary)typeof(RegionAuthority).GetField("_regions", flags)!.GetValue(s.Kernel.Regions)!)[s.Buffer.Handle.RegionId]!;
        object record = Record(x); PropertyInfo route = record.GetType().GetProperty("BorrowLifetime")!;
        object original = route.GetValue(record)!;
        route.SetValue(record, fault switch { 0 => null, 1 => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null), _ => route.GetValue(Record(foreign)) });
        Assert.False(x.Kernel.AcquireBorrowRegionUse(x.Owner, x.Borrower, x.Lease.Handle, RegionUseMode.ReadOnly, new(0, 8)).IsSuccess);
        Assert.False(exiting ? x.Kernel.TerminateProcess(x.Borrower).IsSuccess : x.Kernel.ReturnBorrow(x.Borrower, x.Lease.Handle).IsSuccess);
        Assert.Equal(!exiting, x.Lease.IsValid);
        Assert.True(foreign.Lease.IsValid); Assert.All(foreign.Lease.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.Equal(RegionState.Loaned, Assert.Single(x.Kernel.Regions.Snapshot()).State);
        ulong Used(Scenario s) => s.Kernel.QueryBudget(s.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(8UL, Used(x)); Assert.Equal(8UL, Used(foreign));
        Assert.NotEqual(RegionUseState.Released, Assert.Single(x.Kernel.Regions.SnapshotUses()).State);
        route.SetValue(record, original);
        Assert.True(exiting ? x.Kernel.TerminateProcess(x.Borrower).IsSuccess : x.Kernel.ReturnBorrow(x.Borrower, x.Lease.Handle).IsSuccess);
        Assert.False(x.Lease.IsValid); Assert.Equal(RegionState.Owned, Assert.Single(x.Kernel.Regions.Snapshot()).State);
        if (!exiting)
        {
            Assert.False(x.Kernel.ReleaseRegion(x.Owner, x.Buffer).IsSuccess);
            Assert.True(x.Kernel.ReleaseRegionUse(x.Borrower, x.Use).IsSuccess);
        }
        Assert.All(x.Buffer.Span.ToArray(), b => Assert.Equal((byte)0x6B, b));
        Assert.True(x.Kernel.ReleaseRegion(x.Owner, x.Buffer).IsSuccess); Assert.Equal(0UL, Used(x));
        Assert.Equal(8UL, Used(foreign)); Assert.True(foreign.Lease.IsValid);
        Assert.True(foreign.Kernel.ReturnBorrow(foreign.Borrower, foreign.Lease.Handle).IsSuccess);
        Assert.True(foreign.Kernel.ReleaseRegionUse(foreign.Borrower, foreign.Use).IsSuccess);
        Assert.True(foreign.Kernel.ReleaseRegion(foreign.Owner, foreign.Buffer).IsSuccess); Assert.Equal(0UL, Used(foreign));
    }
}
