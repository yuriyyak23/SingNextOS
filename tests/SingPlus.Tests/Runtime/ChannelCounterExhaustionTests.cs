using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ChannelCounterExhaustionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void ExhaustedSequenceRefusesQueuedAndInlineEffectsBeforeOwnershipMutation(int path, bool lastCommit)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess? senderProcess, ProcessHandle sender) = TestFixtures.Create(kernel, 581, 5801);
        (SingProcess? receiverProcess, ProcessHandle receiver) = TestFixtures.Create(kernel, 582, 5802);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoint = kernel.CreateChannel(sender, receiver, Protocol(path), 4).Value;
        OwnedBuffer<byte> first = kernel.AllocateBuffer<byte>(sender, 1).Value!;
        OwnedBuffer<byte> second = kernel.AllocateBuffer<byte>(sender, 1).Value!;
        SetSequence(kernel, endpoint.Left.ChannelId, lastCommit ? ulong.MaxValue - 1 : ulong.MaxValue);
        IReadOnlyList<RegionDescriptor> before = kernel.Regions.Snapshot();
        KernelError error = path switch
        {
            0 => kernel.Send(sender, receiver, endpoint.Left, 1, 7).Error,
            1 or 2 => kernel.Send(sender, receiver, endpoint.Left, 1, first).Error,
            3 => kernel.Channels.BeginInlineCopiedInvocation(senderProcess, receiverProcess, endpoint.Left, 1, 7).Error,
            4 => kernel.Channels.BeginInlineBorrowInvocation(senderProcess, receiverProcess, endpoint.Left, 1, first).Error,
            5 => kernel.Channels.BeginInlineMoveInvocation(senderProcess, receiverProcess, endpoint.Left, 1, first).Error,
            _ => kernel.SendOwnershipPair(sender, receiver, endpoint.Left, 1, first, second).Error,
        };
        if (lastCommit)
        {
            Assert.Equal(KernelError.None, error);
            Assert.Equal(ulong.MaxValue, kernel.Channels.GetEndpoint(endpoint.Left).Value!.Sequence);
            Assert.Equal(path is 3 or 4 or 5 ? 0 : 1, kernel.Channels.InspectionSummary(sender).QueuedMessages);
            RegionDescriptor firstAfter = kernel.Regions.Snapshot().Single(r => r.Handle.RegionId == first.Handle.RegionId);
            if (path is 1 or 4 or 6) Assert.Equal(RegionState.Loaned, firstAfter.State);
            if (path is 2 or 5)
            {
                Assert.False(first.IsValid);
                Assert.Equal(receiverProcess.DomainId, firstAfter.Owner.DomainId);
            }
            if (path == 6) Assert.False(second.IsValid);
            return;
        }
        Assert.Equal(KernelError.CapacityExhausted, error);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        foreach (RegionDescriptor original in before)
        {
            RegionDescriptor after = kernel.Regions.Snapshot().Single(r => r.Handle == original.Handle);
            Assert.Equal(original.State, after.State);
            Assert.Equal(original.Owner, after.Owner);
            Assert.Equal(original.MutationEpoch, after.MutationEpoch);
        }
        Assert.Equal(0, kernel.Channels.InspectionSummary(sender).QueuedMessages);
        ChannelEndpoint observation = kernel.Channels.GetEndpoint(endpoint.Left).Value!;
        Assert.Equal(ulong.MaxValue, observation.Sequence);
        Assert.Equal("Ready", observation.ProtocolState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastSequenceCanCommitOnceAndThenNeverWrap(bool inline)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess? leftProcess, ProcessHandle left) = TestFixtures.Create(kernel, 583, 5803);
        (SingProcess? rightProcess, ProcessHandle right) = TestFixtures.Create(kernel, 584, 5804);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoint = kernel.CreateChannel(left, right, Protocol(0), 4).Value;
        SetSequence(kernel, endpoint.Left.ChannelId, ulong.MaxValue - 1);
        if (inline)
        {
            Assert.Equal(ulong.MaxValue, kernel.Channels.BeginInlineCopiedInvocation(leftProcess, rightProcess, endpoint.Left, 1, 7).Value);
            Assert.Equal(KernelError.CapacityExhausted,
                kernel.Channels.BeginInlineCopiedInvocation(leftProcess, rightProcess, endpoint.Left, 1, 8).Error);
        }
        else
        {
            Assert.Equal(ulong.MaxValue, kernel.Send(left, right, endpoint.Left, 1, 7).Value!.Sequence);
            Assert.Equal(KernelError.CapacityExhausted, kernel.Send(left, right, endpoint.Left, 1, 8).Error);
            Assert.Equal(ulong.MaxValue, kernel.Receive(right, endpoint.Right).Value!.Sequence);
        }
        Assert.Equal(ulong.MaxValue, kernel.Channels.GetEndpoint(endpoint.Left).Value!.Sequence);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public void ExhaustedIdentityAllocatorCannotPublishChannelOrProcessAttachment(ulong next)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess? leftProcess, ProcessHandle left) = TestFixtures.Create(kernel, 585, 5805);
        (SingProcess? rightProcess, ProcessHandle right) = TestFixtures.Create(kernel, 586, 5806);
        SetNextIdentity(kernel, next);
        Assert.Equal(KernelError.CapacityExhausted, kernel.CreateChannel(left, right, Protocol(0), 4).Error);
        Assert.Equal(KernelError.CapacityExhausted, kernel.Channels.Create(Protocol(0), leftProcess, rightProcess, 4).Error);
        Assert.Empty(leftProcess.Channels);
        Assert.Empty(rightProcess.Channels);
        Assert.Equal(0, kernel.Channels.InspectionSummary(left).Channels);
        Assert.Equal(next, NextIdentity(kernel));
    }

    [Fact]
    public async Task LastIdentityIsUniqueUnderExistingKernelAdmissionGate()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle left = TestFixtures.Create(kernel, 587, 5807).Handle;
        ProcessHandle right = TestFixtures.Create(kernel, 588, 5808).Handle;
        SetNextIdentity(kernel, ulong.MaxValue - 1);
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)>>[] calls = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return kernel.CreateChannel(left, right, Protocol(0), 4);
        })).ToArray();
        start.Set();
        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)>[] results = await Task.WhenAll(calls);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) winner = Assert.Single(results, r => r.IsSuccess).Value;
        Assert.Equal(ulong.MaxValue - 1, winner.Left.ChannelId.Value);
        Assert.Single(results, r => r.Error == KernelError.CapacityExhausted);
        Assert.Equal(ulong.MaxValue, NextIdentity(kernel));
        Assert.True(kernel.Channels.GetEndpoint(winner.Left).IsSuccess);
        Assert.Equal(1, kernel.Channels.InspectionSummary(left).Channels);
    }

    [Fact]
    public void RejectedExhaustedSendRefundsOnlyItsProvisionalIpcBudget()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle sender = TestFixtures.Create(kernel, 589, 5809).Handle;
        ProcessHandle receiver = TestFixtures.Create(kernel, 590, 5810).Handle;
        ProcessHandle admin = TestFixtures.Create(kernel, 591, 5811).Handle;
        CapabilityId cap = kernel.MintCapability(new(5811), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = kernel.AdmitProcessBudget(admin, cap, sender, "exhausted-sequence",
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 64)]).Value!.ProcessBudget;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoint = kernel.CreateChannel(sender, receiver, Protocol(0), 4).Value;
        SetSequence(kernel, endpoint.Left.ChannelId, ulong.MaxValue);
        IReadOnlyList<BudgetUsage> before = kernel.QueryBudget(account).Value!.Usage;
        Assert.Equal(KernelError.CapacityExhausted, kernel.Send(sender, receiver, endpoint.Left, 1, 7).Error);
        Assert.Equal(before, kernel.QueryBudget(account).Value!.Usage);
        Assert.Equal(0, kernel.Channels.InspectionSummary(sender).QueuedMessages);
        Assert.Empty((IDictionary)typeof(RuntimeKernel).GetField("_ipcBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!);
    }

    private static ProtocolDefinitionV1 Protocol(int path)
    {
        RequestPayloadDescriptorV1 payload;
        string[] consumes = [];
        string[] borrows = [];
        if (path is 0 or 3) payload = new(RequestPayloadKind.Primitive, "value", typeof(int).FullName!);
        else if (path == 6)
        {
            payload = new(new OwnershipRequestDescriptorV1("first", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Borrow),
                new OwnershipRequestDescriptorV1("second", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Consume));
            borrows = ["first"];
            consumes = ["second"];
        }
        else
        {
            payload = new(RequestPayloadKind.Ownership, "payload", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
            if (path is 1 or 4) borrows = ["payload"];
            else consumes = ["payload"];
        }
        return new("CounterGuard", "counter-guard", "Ready", null,
            [new(1, "Transfer", consumes: consumes, borrows: borrows, requestPayload: payload)],
            [new(1, "Ready", "Ready")]);
    }

    // Fault injection into existing owners; these observations mint no authority.
    private static void SetSequence(RuntimeKernel kernel, ChannelId id, ulong sequence)
    {
        IDictionary records = (IDictionary)typeof(ChannelRegistry).GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Channels)!;
        object record = records[id]!;
        record.GetType().GetProperty("Sequence")!.SetValue(record, sequence);
    }
    private static void SetNextIdentity(RuntimeKernel kernel, ulong next) => typeof(ChannelRegistry)
        .GetField("_nextChannelId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kernel.Channels, next);
    private static ulong NextIdentity(RuntimeKernel kernel) => (ulong)typeof(ChannelRegistry)
        .GetField("_nextChannelId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Channels)!;
}
