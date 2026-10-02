using System.Security.Cryptography;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class Phase05ResourceBudgetTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public void IpcQueueLifetimeCannotEraseRefusedExactBudgetAssociation(int path, int fault)
    {
        var kernel = new RuntimeKernel();
        var sender = Admit(kernel, "ipc-retention", 571, 5701,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        var receiver = TestFixtures.Create(kernel, 572, 5702).Handle;
        var channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        var sent = kernel.Send(sender.Process, receiver, channel.Left, 1);
        Assert.True(sent.IsSuccess, sent.Message);
        var key = (channel.Left.ChannelId, sent.Value!.Sequence);
        var charge = IpcCharges(kernel)[key];
        if (fault == 1 || fault == 3)
        {
            Assert.True(kernel.Budgets.QuarantineLease(sender.Process, charge.Reservation).IsSuccess);
            if (fault == 3) Assert.True(kernel.Budgets.ReconcileLease(sender.Process, charge.Reservation).IsSuccess);
        }
        else if (fault == 2)
        {
            Assert.True(kernel.Budgets.BindLease(sender.Process, charge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.BeginConsumption(sender.Process, charge.Reservation).IsSuccess);
        }
        var before = kernel.QueryBudget(charge.Reservation).Value!;
        if (path == 0)
        {
            Assert.True(kernel.Receive(receiver, channel.Right).IsSuccess);
            Assert.False(kernel.Receive(receiver, channel.Right).IsSuccess);
        }
        else if (path == 1) Assert.True(kernel.TerminateProcess(sender.Process).IsSuccess);
        else if (path == 2) Assert.True(kernel.TerminateProcess(receiver).IsSuccess);
        else Assert.True(kernel.Channels.Close(channel.Left).IsSuccess);
        if (fault == 0)
        {
            Assert.Empty(IpcCharges(kernel));
            Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(charge.Reservation).Value!.State);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcBytes).Used);
        }
        else
        {
            Assert.Equal(charge, Assert.Single(IpcCharges(kernel)).Value);
            Assert.Equal(before.State, kernel.QueryBudget(charge.Reservation).Value!.State);
            Assert.Equal(1UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
            Assert.Equal(before.Amounts.Single(a => a.Dimension == ServiceBudgetDimension.IpcBytes).Amount,
                Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcBytes).Used);
            Assert.False(kernel.ReleaseBudget(sender.Process, charge.Reservation).IsSuccess);
        }
    }

    [Fact]
    public void IpcReceiveAndClosureRetainOnlyRefusedExactSequenceAcrossChannels()
    {
        var kernel = new RuntimeKernel();
        var sender = Admit(kernel, "ipc-exact-sequences", 573, 5703,
            [new(ServiceBudgetDimension.IpcMessages, 2), new(ServiceBudgetDimension.IpcBytes, 64)]).Value!;
        var receiver = TestFixtures.Create(kernel, 574, 5704).Handle;
        var first = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        var second = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        var sent1 = kernel.Send(sender.Process, receiver, first.Left, 1).Value!;
        var sent2 = kernel.Send(sender.Process, receiver, second.Left, 1).Value!;
        Assert.Equal(sent1.Sequence, sent2.Sequence);
        var key1 = (first.Left.ChannelId, sent1.Sequence);
        var key2 = (second.Left.ChannelId, sent2.Sequence);
        var retained = IpcCharges(kernel)[key1];
        Assert.True(kernel.Budgets.QuarantineLease(sender.Process, retained.Reservation).IsSuccess);
        Assert.True(kernel.Receive(receiver, first.Right).IsSuccess);
        Assert.Equal(retained, IpcCharges(kernel)[key1]);
        Assert.Equal(KernelError.BudgetExceeded, kernel.Send(sender.Process, receiver, second.Left, 1).Error);
        Assert.True(kernel.Receive(receiver, second.Right).IsSuccess);
        Assert.False(IpcCharges(kernel).ContainsKey(key2));
        var sent3 = kernel.Send(sender.Process, receiver, second.Left, 1).Value!;
        Assert.NotEqual(sent2.Sequence, sent3.Sequence);
        Assert.True(kernel.Channels.Close(second.Left).IsSuccess);
        Assert.Equal(retained, Assert.Single(IpcCharges(kernel)).Value);
        Assert.True(kernel.Channels.Close(first.Left).IsSuccess);
        Assert.False(kernel.Receive(receiver, first.Right).IsSuccess);
        Assert.Equal(retained, Assert.Single(IpcCharges(kernel)).Value);
        Assert.True(kernel.TerminateProcess(receiver).IsSuccess);
        var replacement = TestFixtures.Create(kernel, 574, 5705, generation: 2).Handle;
        Assert.False(kernel.ReleaseBudget(replacement, retained.Reservation).IsSuccess);
        Assert.Equal(retained, Assert.Single(IpcCharges(kernel)).Value);
        Assert.Equal(1UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingIpcReceiveAndReceiverTeardownKeepQuantitativeDecisionWithBudgetOwner(bool quarantine)
    {
        var kernel = new RuntimeKernel();
        var sender = Admit(kernel, "ipc-overlap", 575, 5705,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        var receiver = TestFixtures.Create(kernel, 576, 5706).Handle;
        var channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        var sent = kernel.Send(sender.Process, receiver, channel.Left, 1).Value!;
        var charge = IpcCharges(kernel)[(channel.Left.ChannelId, sent.Sequence)];
        if (quarantine) Assert.True(kernel.Budgets.QuarantineLease(sender.Process, charge.Reservation).IsSuccess);
        using var start = new ManualResetEventSlim(false);
        var receive = Task.Run(() => { start.Wait(); return kernel.Receive(receiver, channel.Right); });
        var teardown = Task.Run(() => { start.Wait(); return kernel.TerminateProcess(receiver); });
        start.Set();
        await receive;
        Assert.True((await teardown).IsSuccess);
        Assert.Equal(quarantine ? BudgetReservationState.Quarantined : BudgetReservationState.Released,
            kernel.QueryBudget(charge.Reservation).Value!.State);
        if (quarantine) Assert.Equal(charge, Assert.Single(IpcCharges(kernel)).Value);
        else Assert.Empty(IpcCharges(kernel));
        Assert.Equal(quarantine ? 1UL : 0UL,
            Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
    }

    private static ProtocolDefinitionV1 IpcBudgetProtocol() => new("BudgetIpc", "budget-ipc", "Idle", null,
        [new(1, "Ping")], [new(1, "Idle", "Idle")]);

    private static Dictionary<(ChannelId Channel, ulong Sequence), (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)> IpcCharges(RuntimeKernel kernel) =>
        (Dictionary<(ChannelId Channel, ulong Sequence), (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)>)typeof(RuntimeKernel)
            .GetField("_ipcBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    [InlineData(true, true, 3)]
    public void FinalDomainReclaimReleasesExactCohortChargesAndPreservesRefusedAccounting(bool scalar, bool reverseExit, int fault)
    {
        var kernel = new RuntimeKernel();
        var left = Admit(kernel, "cohort-left", 561, 5601,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var right = Admit(kernel, "cohort-right", 562, 5601,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var outside = Admit(kernel, "cohort-outside", 563, 5602,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var leftHandle = scalar ? kernel.AllocateRegion(left.Process, 7).Value!.Handle
            : kernel.AllocateBuffer<byte>(left.Process, 4).Value!.Handle;
        var rightHandle = scalar ? kernel.AllocateRegion(right.Process, 7).Value!.Handle
            : kernel.AllocateBuffer<byte>(right.Process, 4).Value!.Handle;
        var outsideHandle = kernel.AllocateBuffer<byte>(outside.Process, 4).Value!.Handle;
        var charges = RegionCharges(kernel);
        var leftCharge = charges[leftHandle];
        var rightCharge = charges[rightHandle];
        var outsideCharge = charges[outsideHandle];
        if (fault == 1 || fault == 3)
        {
            Assert.True(kernel.Budgets.QuarantineLease(left.Process, leftCharge.Reservation).IsSuccess);
            if (fault == 3) Assert.True(kernel.Budgets.ReconcileLease(left.Process, leftCharge.Reservation).IsSuccess);
        }
        else if (fault == 2)
        {
            Assert.True(kernel.Budgets.BindLease(left.Process, leftCharge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.BeginConsumption(left.Process, leftCharge.Reservation).IsSuccess);
        }
        var leftState = kernel.QueryBudget(leftCharge.Reservation).Value!.State;
        var first = reverseExit ? right : left;
        var last = reverseExit ? left : right;
        Assert.True(kernel.TerminateProcess(first.Process).IsSuccess);
        Assert.True(kernel.Domains.Contains(new(5601)));
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(first.Process).Error);
        Assert.Equal(leftCharge, charges[leftHandle]);
        Assert.Equal(rightCharge, charges[rightHandle]);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == leftHandle).State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(left.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(right.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.TerminateProcess(last.Process).IsSuccess);
        Assert.False(kernel.Domains.Contains(new(5601)));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == leftHandle).State);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == rightHandle).State);
        Assert.False(charges.ContainsKey(rightHandle));
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(rightCharge.Reservation).Value!.State);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(right.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (fault == 0)
        {
            Assert.False(charges.ContainsKey(leftHandle));
            Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(leftCharge.Reservation).Value!.State);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(left.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            Assert.Equal(leftCharge, charges[leftHandle]);
            Assert.Equal(leftState, kernel.QueryBudget(leftCharge.Reservation).Value!.State);
            Assert.Equal(4UL, Usage(kernel.QueryBudget(left.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        Assert.Equal(outsideCharge, charges[outsideHandle]);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == outsideHandle).State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(outside.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CohortReclaimCannotSubstituteTransferredRegionGenerationOrReusedProcessId(bool scalar)
    {
        var kernel = new RuntimeKernel();
        var source = Admit(kernel, "cohort-transfer-source", 564, 5604,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var sibling = Admit(kernel, "cohort-transfer-sibling", 565, 5604,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        var old = scalar ? region!.Handle : buffer!.Handle;
        var charge = RegionCharges(kernel)[old];
        Assert.True(kernel.Budgets.QuarantineLease(source.Process, charge.Reservation).IsSuccess);
        var movedHandle = scalar
            ? kernel.TransferRegion(source.Process, sibling.Process, region!).Value!.Handle
            : kernel.TransferRegion(source.Process, sibling.Process, buffer!).Value!.Handle;
        Assert.Equal(old.RegionId, movedHandle.RegionId);
        Assert.NotEqual(old.Generation, movedHandle.Generation);
        var movedCharge = RegionCharges(kernel)[movedHandle];
        Assert.Equal(charge, RegionCharges(kernel)[old]);
        Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        // The domain survives but the process generation does not. A reused PID
        // must not resolve the retired generation's accounting or Region authority.
        var replacement = TestFixtures.Create(kernel, 564, 5605, generation: 2).Handle;
        var admin = TestFixtures.Create(kernel, 566, 5606).Handle;
        var cap = kernel.MintCapability(new(5606), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, cap, replacement, "cohort-replacement",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!.ProcessBudget;
        var fresh = kernel.AllocateBuffer<byte>(replacement, 4).Value!;
        var freshCharge = RegionCharges(kernel)[fresh.Handle];
        Assert.False(kernel.ReleaseBudget(replacement, charge.Reservation).IsSuccess);
        Assert.True(kernel.TerminateProcess(sibling.Process).IsSuccess);
        Assert.False(RegionCharges(kernel).ContainsKey(movedHandle));
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(movedCharge.Reservation).Value!.State);
        Assert.Equal(charge, RegionCharges(kernel)[old]);
        Assert.Equal(freshCharge, RegionCharges(kernel)[fresh.Handle]);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(budget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == fresh.Handle).State);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 0, 1)]
    [InlineData(false, 0, 2)]
    [InlineData(false, 1, 0)]
    [InlineData(false, 1, 1)]
    [InlineData(false, 1, 2)]
    [InlineData(false, 2, 0)]
    [InlineData(false, 2, 1)]
    [InlineData(false, 2, 2)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 0, 2)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 1, 2)]
    [InlineData(true, 2, 0)]
    [InlineData(true, 2, 1)]
    [InlineData(true, 2, 2)]
    public void RefusedRegionAccountingReleaseRetainsEveryExactGeneration(bool scalar, int path, int fault)
    {
        var kernel = new RuntimeKernel();
        var source = Admit(kernel, "retained-source", 551, 5501,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var target = Admit(kernel, "retained-target", 552, 5502,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        var old = scalar ? region!.Handle : buffer!.Handle;
        var associations = RegionCharges(kernel);
        var charge = associations[old];
        if (fault == 0)
        {
            Assert.True(kernel.Budgets.BindLease(source.Process, charge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.BeginConsumption(source.Process, charge.Reservation).IsSuccess);
        }
        else
        {
            Assert.True(kernel.Budgets.QuarantineLease(source.Process, charge.Reservation).IsSuccess);
            if (fault == 2) Assert.True(kernel.Budgets.ReconcileLease(source.Process, charge.Reservation).IsSuccess);
        }
        var before = kernel.QueryBudget(charge.Reservation).Value!;
        var stale = source.Process with { Generation = source.Process.Generation + 1 };
        Assert.False((scalar ? kernel.ReleaseRegion(stale, region!) : kernel.ReleaseRegion(stale, buffer!)).IsSuccess);
        Assert.Equal(charge, associations[old]);
        if (path == 0)
            Assert.True((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
        else if (path == 1)
            Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        else
        {
            RegionHandle movedHandle;
            if (scalar)
            {
                var moved = kernel.TransferRegion(source.Process, target.Process, region!);
                Assert.True(moved.IsSuccess, moved.Message);
                movedHandle = moved.Value!.Handle;
                Assert.Equal(charge, associations[old]);
                Assert.True(kernel.ReleaseRegion(target.Process, moved.Value).IsSuccess);
            }
            else
            {
                var moved = kernel.TransferRegion(source.Process, target.Process, buffer!);
                Assert.True(moved.IsSuccess, moved.Message);
                movedHandle = moved.Value!.Handle;
                Assert.Equal(charge, associations[old]);
                Assert.True(kernel.ReleaseRegion(target.Process, moved.Value).IsSuccess);
            }
            Assert.Equal(old.RegionId, movedHandle.RegionId);
            Assert.NotEqual(old.Generation, movedHandle.Generation);
            Assert.False(associations.ContainsKey(movedHandle));
            Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.False((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
            Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        }
        Assert.Equal(charge, Assert.Single(associations).Value);
        Assert.Equal(before.State, kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.ReleaseBudget(source.Process, charge.Reservation).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulRegionTransferRoutesOnlyNewGenerationCharge(bool scalar)
    {
        var kernel = new RuntimeKernel();
        var source = Admit(kernel, "transfer-source", 553, 5503,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var target = Admit(kernel, "transfer-target", 554, 5504,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        RegionHandle old;
        if (scalar)
        {
            var region = kernel.AllocateRegion(source.Process, 7).Value!;
            old = region.Handle;
            var moved = kernel.TransferRegion(source.Process, target.Process, region).Value!;
            Assert.False(RegionCharges(kernel).ContainsKey(old));
            Assert.Equal(target.Process, RegionCharges(kernel)[moved.Handle].Owner);
            Assert.True(kernel.ReleaseRegion(target.Process, moved).IsSuccess);
            Assert.False(kernel.ReleaseRegion(source.Process, region).IsSuccess);
        }
        else
        {
            var buffer = kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
            old = buffer.Handle;
            var moved = kernel.TransferRegion(source.Process, target.Process, buffer).Value!;
            Assert.False(RegionCharges(kernel).ContainsKey(old));
            Assert.Equal(target.Process, RegionCharges(kernel)[moved.Handle].Owner);
            Assert.True(kernel.ReleaseRegion(target.Process, moved).IsSuccess);
            Assert.False(kernel.ReleaseRegion(source.Process, buffer).IsSuccess);
        }
        Assert.Empty(RegionCharges(kernel));
        Assert.Equal(0UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRegionTransferLeavesSourceAssociationAndRefundsOnlyProvisionalTarget(bool scalar)
    {
        var kernel = new RuntimeKernel();
        var source = Admit(kernel, "denied-source", 555, 5505,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var target = Admit(kernel, "denied-target", 556, 5506,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        var handle = scalar ? region!.Handle : buffer!.Handle;
        var charge = RegionCharges(kernel)[handle];
        var use = kernel.AcquireRegionUse(source.Process, handle, RegionUseMode.ExclusiveWrite, new(0, 4)).Value!;
        var denied = scalar ? kernel.TransferRegion(source.Process, target.Process, region!).Error
            : kernel.TransferRegion(source.Process, target.Process, buffer!).Error;
        Assert.Equal(KernelError.RegionUseConflict, denied);
        Assert.Equal(charge, Assert.Single(RegionCharges(kernel)).Value);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.ReleaseRegionUse(source.Process, use.Handle).IsSuccess);
        Assert.True((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingRegionReleasesCannotRemoveOrRefundAnotherGeneration(bool scalar)
    {
        var kernel = new RuntimeKernel();
        var owner = Admit(kernel, "overlap-region", 557, 5507,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(owner.Process, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(owner.Process, 7).Value! : null;
        using var start = new ManualResetEventSlim(false);
        var calls = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return scalar ? kernel.ReleaseRegion(owner.Process, region!) : kernel.ReleaseRegion(owner.Process, buffer!);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(calls);
        Assert.Single(results, r => r.IsSuccess);
        Assert.Empty(RegionCharges(kernel));
        Assert.Equal(0UL, Usage(kernel.QueryBudget(owner.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        var fresh = kernel.AllocateBuffer<byte>(owner.Process, 4).Value!;
        var charge = RegionCharges(kernel)[fresh.Handle];
        Assert.False((scalar ? kernel.ReleaseRegion(owner.Process, region!) : kernel.ReleaseRegion(owner.Process, buffer!)).IsSuccess);
        Assert.Equal(charge, Assert.Single(RegionCharges(kernel)).Value);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(owner.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    private static Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> RegionCharges(RuntimeKernel kernel) =>
        (Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>)typeof(RuntimeKernel)
            .GetField("_regionBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;

    [Fact]
    public void ChildServiceBudgetCannotExceedConfiguredSystemParent()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 501, 5001).Handle;
        var capability = kernel.MintCapability(new(5001), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.ConfigureSystemBudget(admin, capability,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).IsSuccess);

        var denied = Admit(kernel, "over-parent", 502, 5002,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]);

        Assert.Equal(KernelError.BudgetExceeded, denied.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new(502), 1)).Error);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotOvercommitAndStaleReleaseCannotFreeCurrentCharge()
    {
        var kernel = new RuntimeKernel();
        var component = Admit(kernel, "race", 503, 5003,
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            start.Wait();
            return kernel.ReserveBudget(component.Process,
                [new(ServiceBudgetDimension.ExternalOperations, 1)],
                BudgetReservationLifetime.ExternalEffect,
                index == 0 ? AdmissionQosHint.LatencySensitive : AdmissionQosHint.Background);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);

        var winner = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Single(results, result => result.Error == KernelError.BudgetExceeded);
        Assert.True(kernel.ReleaseBudget(component.Process, winner.Reservation).IsSuccess);
        var current = kernel.ReserveBudget(component.Process,
            [new(ServiceBudgetDimension.ExternalOperations, 1)],
            BudgetReservationLifetime.ExternalEffect).Value!;
        var stale = current.Reservation with { Generation = new(current.Reservation.Generation.Value + 1) };

        Assert.Equal(BudgetReservationState.Stale,
            kernel.ReleaseBudget(component.Process, stale).Value!.State);
        Assert.Equal(KernelError.BudgetExceeded,
            kernel.ReserveBudget(component.Process,
                [new(ServiceBudgetDimension.ExternalOperations, 1)],
                BudgetReservationLifetime.ExternalEffect,
                AdmissionQosHint.ThroughputOriented).Error);
        Assert.True(kernel.ReleaseBudget(component.Process, current.Reservation).IsSuccess);
    }

    [Fact]
    public void OwnedMemoryBudgetGatesAllocationAndRecoversOnExactRelease()
    {
        var kernel = new RuntimeKernel();
        var component = Admit(kernel, "memory", 504, 5004,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        var first = kernel.AllocateBuffer<byte>(component.Process, 8);
        Assert.True(first.IsSuccess, first.Message);

        Assert.Equal(KernelError.BudgetExceeded,
            kernel.AllocateBuffer<byte>(component.Process, 1).Error);
        Assert.True(kernel.ReleaseRegion(component.Process, first.Value!).IsSuccess);
        Assert.True(kernel.AllocateBuffer<byte>(component.Process, 8).IsSuccess);
    }

    [Fact]
    public void IpcMessageAndByteBudgetsReleaseWhenQueueLifetimeEnds()
    {
        var kernel = new RuntimeKernel();
        var sender = Admit(kernel, "ipc", 505, 5005,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        var receiver = TestFixtures.Create(kernel, 506, 5006).Handle;
        var protocol = new ProtocolDefinitionV1("BudgetIpc", "budget-ipc", "Idle", null,
            [new(1, "Ping")], [new(1, "Idle", "Idle")]);
        var channel = kernel.CreateChannel(sender.Process, receiver, protocol, 4).Value;

        Assert.True(kernel.Send(sender.Process, receiver, channel.Left, 1).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded,
            kernel.Send(sender.Process, receiver, channel.Left, 1).Error);
        Assert.True(kernel.Receive(receiver, channel.Right).IsSuccess);
        Assert.True(kernel.Send(sender.Process, receiver, channel.Left, 1).IsSuccess);
    }

    [Fact]
    public void MappedMemoryBudgetIsIndependentAndReleasesOnlyAfterMappingClosure()
    {
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        var admin = TestFixtures.Create(kernel, 507, 5007).Handle;
        var owner = TestFixtures.Create(kernel, 509, 5009).Handle;
        var budgetCapability = kernel.MintCapability(new(5007), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, budgetCapability, owner, "mapping",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 256), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 64)]).IsSuccess);
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var first = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        var second = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        var firstCapability = kernel.MintCapability(new(5009), owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(first.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var secondCapability = kernel.MintCapability(new(5009), owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(second.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, firstCapability,
            first.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);

        Assert.Equal(KernelError.BudgetExceeded,
            kernel.MapPlatformOwnedRegion(owner, binding, secondCapability,
                second.Handle, PlatformMemoryAccess.Read).Error);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value!).IsSuccess);
        Assert.True(kernel.MapPlatformOwnedRegion(owner, binding, secondCapability,
            second.Handle, PlatformMemoryAccess.Read).IsSuccess);
    }

    [Fact]
    public void CrashWithSubmittedExternalEffectKeepsChargeUntilExactClosure()
    {
        var kernel = new RuntimeKernel();
        var component = Admit(kernel, "external", 508, 5008,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 16), new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        var prepared = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)), new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(component.Process, prepared.Operation, dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(component.Process, prepared.Operation, dependencies).Value!;

        var faulted = kernel.FaultComponent(component.Identity);
        Assert.True(faulted.IsSuccess, faulted.Message);
        var charged = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(BudgetPressureState.PinnedByExternalEffect, charged.Pressure);
        Assert.Equal(1UL, Usage(charged, ServiceBudgetDimension.ExternalOperations).Used);

        Assert.True(kernel.RecordExternalOperationCompletion(component.Process,
            new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(kernel.ObserveComponentTeardown(component.Identity).IsSuccess);
        var closurePending = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(BudgetPressureState.PinnedByExternalEffect, closurePending.Pressure);
        Assert.Equal(1UL, Usage(closurePending, ServiceBudgetDimension.ExternalOperations).Used);

        Assert.True(kernel.ReleaseExternalOperation(component.Process, prepared.Operation, new(true, false)).IsSuccess);
        Assert.True(kernel.ObserveComponentTeardown(component.Identity).IsSuccess);
        var closed = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(0UL, Usage(closed, ServiceBudgetDimension.ExternalOperations).Used);
    }

    [Fact]
    public void BudgetDtosAndQosHintsAreNotAuthority()
    {
        var observation = new BudgetReservationSnapshot(default, default, default, [],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.LatencySensitive,
            BudgetReservationState.Active);
        Assert.False(observation.AuthorizesEffect);
        Assert.False(observation.AuthorizesReclaim);
        Assert.False(new BudgetAccountSnapshot(default, BudgetAccountLevel.System, null, "system", [],
            BudgetPressureState.Normal).MaterializesAuthority);
        var vocabulary = typeof(AdmissionQosHint).GetEnumNames();
        Assert.DoesNotContain(vocabulary, name => name.Contains("Cxl", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("Lane", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("Hybrid", StringComparison.OrdinalIgnoreCase));
    }

    private static BudgetUsage Usage(BudgetAccountSnapshot account, ServiceBudgetDimension dimension) =>
        Assert.Single(account.Usage, usage => usage.Dimension == dimension);

    private static KernelResult<ComponentLifecycleSnapshot> Admit(
        RuntimeKernel kernel, string name, ulong processId, ulong domainId,
        IReadOnlyList<ServiceBudgetRequestV1> budgets)
    {
        byte[] image = [(byte)(processId & 0xff), 0x05];
        var manifest = new ServiceManifestV1(
            new(name), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(processId, domainId, identity: name), budgetRequests: budgets);
        return kernel.AdmitComponent(new(manifest, image));
    }
}
