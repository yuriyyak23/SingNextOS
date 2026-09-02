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
        Assert.True(kernel.Send(left, right, endpoints.Left, 1).IsSuccess);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
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
            new ProtocolMessageDescriptorV1(3, "Finish")
        },
        new[]
        {
            new ProtocolTransitionV1(1, "Idle", "Active"),
            new ProtocolTransitionV1(2, "Active", "Active"),
            new ProtocolTransitionV1(3, "Active", "Done")
        });
}
