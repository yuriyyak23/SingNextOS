using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Contracts;

public sealed class ProtocolRuntimeTests
{
    [Fact]
    public void LegalMessageSequenceAdvancesProtocolState()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);

        var start = kernel.Send(left, right, endpoints.Left, 1);
        var startReceive = kernel.Receive(right, endpoints.Right);
        var finish = kernel.Send(left, right, endpoints.Left, 3);

        Assert.True(start.IsSuccess, start.Message);
        Assert.True(startReceive.IsSuccess, startReceive.Message);
        Assert.True(finish.IsSuccess, finish.Message);
        var endpoint = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal("Done", endpoint.ProtocolState);
        Assert.Equal<ulong>(2, endpoint.Sequence);
    }

    [Fact]
    public void IllegalTransitionDoesNotMutateChannelState()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var result = kernel.Send(left, right, endpoints.Left, 2, 7);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.InvalidProtocolTransition, result.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.ProtocolState, after.ProtocolState);
        Assert.Equal(before.Sequence, after.Sequence);
    }

    [Fact]
    public void UnknownMessageIdIsRejectedWithoutStateChange()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);

        var result = kernel.Send(left, right, endpoints.Left, 999);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        var endpoint = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal("Idle", endpoint.ProtocolState);
        Assert.Equal<ulong>(0, endpoint.Sequence);
    }

    [Fact]
    public void RequiredCapabilityIsEnforcedBeforeMutation()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        Assert.True(kernel.Send(left, right, endpoints.Left, 1).IsSuccess);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var result = kernel.Send(left, right, endpoints.Left, 2, 7);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.MissingCapability, result.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.ProtocolState, after.ProtocolState);
        Assert.Equal(before.Sequence, after.Sequence);
    }

    [Fact]
    public void ConsumingMessageTransfersOwnershipToPeer()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        Activate(kernel, left, right, endpoints);
        var capability = kernel.MintCapability(new DomainId(10), left, ResourceKind.Device, "console0", CapabilityRights.Write);
        Assert.True(capability.IsSuccess, capability.Message);
        var buffer = kernel.AllocateBuffer<byte>(left, 4).Value!;
        buffer.Span[0] = 42;
        var oldHandle = buffer.Handle;

        var send = kernel.Send(left, right, endpoints.Left, 2, buffer, new[] { capability.Value!.CapabilityId });

        Assert.True(send.IsSuccess, send.Message);
        Assert.False(buffer.IsValid);
        var transferred = Assert.IsType<OwnedBuffer<byte>>(send.Value!.Payload);
        Assert.True(transferred.IsValid);
        Assert.Equal((byte)42, transferred.Span[0]);
        Assert.Equal(oldHandle.RegionId, transferred.Handle.RegionId);
        Assert.Equal(oldHandle.Generation.Value + 1, transferred.Handle.Generation.Value);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
    }

    [Fact]
    public void BorrowingMessageDeliversReadOnlyLeaseAndReturnRevokesAccess()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        Activate(kernel, left, right, endpoints);
        var buffer = kernel.AllocateBuffer<byte>(left, 4).Value!;
        buffer.Span[0] = 42;

        var send = kernel.Send(left, right, endpoints.Left, 4, buffer);
        var receive = kernel.Receive(right, endpoints.Right);

        Assert.True(send.IsSuccess, send.Message);
        Assert.True(receive.IsSuccess, receive.Message);
        Assert.True(buffer.IsValid);
        Assert.Equal(RegionState.Loaned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Throws<InvalidOperationException>(() => buffer.Span[0] = 7);

        var lease = Assert.IsType<BorrowLease<byte>>(receive.Value!.Payload);
        Assert.True(lease.IsValid);
        Assert.Equal(4, lease.Length);
        Assert.Equal((byte)42, lease.Span[0]);
        Assert.Equal(buffer.Handle, lease.Handle.Region);

        var returned = kernel.ReturnBorrow(right, lease.Handle);

        Assert.True(returned.IsSuccess, returned.Message);
        Assert.False(lease.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = lease.Length);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        buffer.Span[0] = 43;
        Assert.Equal((byte)43, buffer.Span[0]);
    }

    [Fact]
    public void OwnerRevokeInvalidatesChannelBorrowLeaseAndRejectsLateReturn()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        Activate(kernel, left, right, endpoints);
        var buffer = kernel.AllocateBuffer<int>(left, 2).Value!;
        buffer.Span[0] = 11;
        Assert.True(kernel.Send(left, right, endpoints.Left, 4, buffer).IsSuccess);
        var receive = kernel.Receive(right, endpoints.Right);
        Assert.True(receive.IsSuccess, receive.Message);
        var lease = Assert.IsType<BorrowLease<int>>(receive.Value!.Payload);

        var revoked = kernel.RevokeBorrow(left, lease.Handle);
        var lateReturn = kernel.ReturnBorrow(right, lease.Handle);

        Assert.True(revoked.IsSuccess, revoked.Message);
        Assert.False(lease.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = lease.Span[0]);
        Assert.False(lateReturn.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, lateReturn.Error);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(11, buffer.Span[0]);
    }

    [Fact]
    public void ReborrowIncrementsLeaseGenerationAndRejectsStaleToken()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 6);
        Activate(kernel, left, right, endpoints);
        var buffer = kernel.AllocateBuffer<byte>(left, 1).Value!;
        buffer.Span[0] = 9;

        Assert.True(kernel.Send(left, right, endpoints.Left, 4, buffer).IsSuccess);
        var first = Assert.IsType<BorrowLease<byte>>(kernel.Receive(right, endpoints.Right).Value!.Payload);
        Assert.True(kernel.ReturnBorrow(right, first.Handle).IsSuccess);

        Assert.True(kernel.Send(left, right, endpoints.Left, 4, buffer).IsSuccess);
        var second = Assert.IsType<BorrowLease<byte>>(kernel.Receive(right, endpoints.Right).Value!.Payload);

        Assert.Equal(first.Handle.Region, second.Handle.Region);
        Assert.Equal(first.Handle.Generation.Value + 1, second.Handle.Generation.Value);
        var staleReturn = kernel.ReturnBorrow(right, first.Handle);
        Assert.False(staleReturn.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleReturn.Error);
        Assert.False(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal((byte)9, second.Span[0]);
        Assert.True(kernel.RevokeBorrow(left, second.Handle).IsSuccess);
    }

    [Fact]
    public void BorrowerTerminationInvalidatesChannelLeaseAndRestoresOwnerAccess()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        Activate(kernel, left, right, endpoints);
        var buffer = kernel.AllocateBuffer<int>(left, 1).Value!;
        buffer.Span[0] = 55;
        Assert.True(kernel.Send(left, right, endpoints.Left, 4, buffer).IsSuccess);
        var lease = Assert.IsType<BorrowLease<int>>(kernel.Receive(right, endpoints.Right).Value!.Payload);

        var terminated = kernel.TerminateProcess(right);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.False(lease.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = lease.Length);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(55, buffer.Span[0]);
    }

    [Fact]
    public void OwnerTerminationInvalidatesChannelLeaseAndBackingStorage()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        Activate(kernel, left, right, endpoints);
        var buffer = kernel.AllocateBuffer<int>(left, 1).Value!;
        buffer.Span[0] = 77;
        Assert.True(kernel.Send(left, right, endpoints.Left, 4, buffer).IsSuccess);
        var lease = Assert.IsType<BorrowLease<int>>(kernel.Receive(right, endpoints.Right).Value!.Payload);

        var terminated = kernel.TerminateProcess(left);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.False(lease.IsValid);
        Assert.False(buffer.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = lease.Length);
        Assert.Throws<InvalidOperationException>(() => _ = buffer.Length);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void BorrowLeaseCannotBeRedelegatedAsChannelPayload()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);
        Activate(kernel, left, right, endpoints);
        var buffer = kernel.AllocateBuffer<byte>(left, 1).Value!;
        Assert.True(kernel.Send(left, right, endpoints.Left, 4, buffer).IsSuccess);
        var lease = Assert.IsType<BorrowLease<byte>>(kernel.Receive(right, endpoints.Right).Value!.Payload);

        var redelegate = kernel.Send(right, left, endpoints.Right, 4, lease);

        Assert.False(redelegate.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, redelegate.Error);
        Assert.True(lease.IsValid);
        Assert.True(kernel.ReturnBorrow(right, lease.Handle).IsSuccess);
    }

    [Fact]
    public void CapacityExhaustionDoesNotConsumeAdditionalSequence()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 1);

        var first = kernel.Send(left, right, endpoints.Left, 1);
        var second = kernel.Send(left, right, endpoints.Left, 3);

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, second.Error);
        Assert.Equal<ulong>(1, kernel.Channels.GetEndpoint(endpoints.Left).Value!.Sequence);

        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        Assert.True(kernel.Send(left, right, endpoints.Left, 3).IsSuccess);
    }

    [Fact]
    public void StaleEndpointGenerationIsRejectedAfterDomainTermination()
    {
        var (kernel, left, _, endpoints) = CreateChannel(capacity: 4);

        Assert.True(kernel.TerminateProcess(left).IsSuccess);
        var endpoint = kernel.Channels.GetEndpoint(endpoints.Left);

        Assert.False(endpoint.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, endpoint.Error);
    }

    [Fact]
    public void WrongEndpointOwnerIsRejected()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 4);

        var result = kernel.Send(right, left, endpoints.Left, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.WrongEndpointOwner, result.Error);
    }

    private static void Activate(RuntimeKernel kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints)
    {
        var start = kernel.Send(left, right, endpoints.Left, 1);
        Assert.True(start.IsSuccess, start.Message);
        var receive = kernel.Receive(right, endpoints.Right);
        Assert.True(receive.IsSuccess, receive.Message);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Left, ProcessHandle Right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel(int capacity)
    {
        var kernel = new RuntimeKernel();
        var (_, left) = TestFixtures.Create(kernel, 1, 10);
        var (_, right) = TestFixtures.Create(kernel, 2, 20);
        var channel = kernel.CreateChannel(left, right, Protocol(), capacity);
        Assert.True(channel.IsSuccess, channel.Message);
        return (kernel, left, right, channel.Value);
    }

    private static ProtocolDefinitionV1 Protocol() => new(
        "SingPlus.Tests.IConsoleProtocol",
        "test-contract-digest",
        "Idle",
        new[] { "Done" },
        new[]
        {
            new ProtocolMessageDescriptorV1(1, "Start"),
            new ProtocolMessageDescriptorV1(
                2,
                "Write",
                new[] { new CapabilityRequirementV1(ResourceKind.Device, "console0", CapabilityRights.Write) },
                consumes: new[] { "data" }),
            new ProtocolMessageDescriptorV1(3, "Finish"),
            new ProtocolMessageDescriptorV1(4, "Peek", borrows: new[] { "data" })
        },
        new[]
        {
            new ProtocolTransitionV1(1, "Idle", "Active"),
            new ProtocolTransitionV1(2, "Active", "Active"),
            new ProtocolTransitionV1(3, "Active", "Done"),
            new ProtocolTransitionV1(4, "Active", "Active")
        });
}
