using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Contracts;

public sealed class ResponsePublicationRuntimeTests
{
    private readonly record struct Packet(int PayloadSize, int MaxPayloadSize) : IBoundedPayload;

    [Fact]
    [Trait("Category", "Runtime")]
    public void PrimitiveResponseIsValidatedBeforeClientVisiblePublication()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var sent = kernel.Send(left, right, endpoints.Left, 1);
        Assert.True(sent.IsSuccess, sent.Message);

        var early = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, 7);
        Assert.False(early.IsSuccess);
        Assert.Equal(KernelError.ResponseNotDelivered, early.Error);

        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        var wrongType = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, 7u);
        Assert.False(wrongType.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongType.Error);

        var invisible = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.False(invisible.IsSuccess);
        Assert.Equal(KernelError.ResponseNotAvailable, invisible.Error);

        var publish = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, 7);
        Assert.True(publish.IsSuccess, publish.Message);
        var received = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.True(received.IsSuccess, received.Message);
        Assert.Equal(sent.Value.Sequence, received.Value!.RequestSequence);
        Assert.Equal((uint)1, received.Value.MessageId);
        Assert.Equal(ResponsePublicationStatus.Published, received.Value.Status);
        Assert.Equal(7, Assert.IsType<int>(received.Value.Payload));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CancellationIsExplicitAndClientVisible()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var sent = kernel.Send(left, right, endpoints.Left, 2);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        var cancel = kernel.CancelResponse(right, endpoints.Right, sent.Value!.Sequence);
        Assert.True(cancel.IsSuccess, cancel.Message);

        var received = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.True(received.IsSuccess, received.Message);
        Assert.Equal(ResponsePublicationStatus.Cancelled, received.Value!.Status);
        Assert.Null(received.Value.Payload);

        var latePublish = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence);
        Assert.False(latePublish.IsSuccess);
        Assert.Equal(KernelError.ResponseNotPending, latePublish.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void OwnershipResponseTransfersOnlyAtPublicationBoundary()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var responseBuffer = kernel.AllocateBuffer<int>(right, 4).Value!;
        responseBuffer.Span[0] = 42;
        var originalGeneration = responseBuffer.Handle.Generation;

        var sent = kernel.Send(left, right, endpoints.Left, 3);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        var publish = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, responseBuffer);
        Assert.True(publish.IsSuccess, publish.Message);
        Assert.False(responseBuffer.IsValid);

        var received = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.True(received.IsSuccess, received.Message);
        var moved = Assert.IsType<OwnedBuffer<int>>(received.Value!.Payload);
        Assert.True(moved.IsValid);
        Assert.Equal(originalGeneration.Value + 1, moved.Handle.Generation.Value);
        Assert.Equal(42, moved.Span[0]);
        Assert.True(kernel.ReleaseRegion(left, moved).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void WrongOwnershipResponseKindFailsWithoutOwnershipMutation()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var wrongPayload = kernel.AllocateRegion(right, 99).Value!;
        var originalHandle = wrongPayload.Handle;

        var sent = kernel.Send(left, right, endpoints.Left, 3);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        var publish = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, wrongPayload);
        Assert.False(publish.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, publish.Error);
        Assert.True(wrongPayload.IsValid);
        Assert.Equal(originalHandle, wrongPayload.Handle);

        Assert.True(kernel.CancelResponse(right, endpoints.Right, sent.Value.Sequence).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, kernel.ReceiveResponse(left, endpoints.Left).Value!.Status);
        Assert.True(kernel.ReleaseRegion(right, wrongPayload).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void BoundedResponseMustMatchContractTypeAndLimitBeforePublication()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var sent = kernel.Send(left, right, endpoints.Left, 4);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        var wrongLimit = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, new Packet(8, 32));
        Assert.False(wrongLimit.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongLimit.Error);
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ReceiveResponse(left, endpoints.Left).Error);

        var oversized = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, new Packet(17, 16));
        Assert.False(oversized.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, oversized.Error);

        var publish = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, new Packet(8, 16));
        Assert.True(publish.IsSuccess, publish.Message);
        Assert.Equal(new Packet(8, 16), Assert.IsType<Packet>(kernel.ReceiveResponse(left, endpoints.Left).Value!.Payload));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void OutstandingResponseCapacityAppliesBackpressureAfterRequestDelivery()
    {
        var (kernel, left, right, endpoints) = CreateChannel(capacity: 1);
        var first = kernel.Send(left, right, endpoints.Left, 1);
        Assert.True(first.IsSuccess, first.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        var blocked = kernel.Send(left, right, endpoints.Left, 1);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, blocked.Error);

        Assert.True(kernel.PublishResponse(right, endpoints.Right, first.Value!.Sequence, 1).IsSuccess);
        Assert.True(kernel.ReceiveResponse(left, endpoints.Left).IsSuccess);
        Assert.True(kernel.Send(left, right, endpoints.Left, 1).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ResponseProtocolMustExactlyMatchRequestMessagesAndOwnershipMetadata()
    {
        var kernel = new RuntimeKernel();
        var (_, left) = TestFixtures.Create(kernel, 411, 410);
        var (_, right) = TestFixtures.Create(kernel, 412, 420);
        var protocol = RequestProtocol();
        var incomplete = new ResponseProtocolDefinitionV1(
            protocol.ContractName,
            "incomplete-response-digest",
            new[] { new ResponseMessageDescriptorV1(1, "Read", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Primitive, typeof(int).FullName!)) });

        var create = kernel.CreateChannel(left, right, protocol, incomplete, capacity: 8);

        Assert.False(create.IsSuccess);
        Assert.Equal(KernelError.ResponseProtocolMismatch, create.Error);
        Assert.Empty(kernel.Processes.Resolve(left).Value!.Channels);
        Assert.Empty(kernel.Processes.Resolve(right).Value!.Channels);
    }

    private static (
        RuntimeKernel Kernel,
        ProcessHandle Left,
        ProcessHandle Right,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel(int capacity = 8)
    {
        var kernel = new RuntimeKernel();
        var (_, left) = TestFixtures.Create(kernel, 401, 410);
        var (_, right) = TestFixtures.Create(kernel, 402, 420);
        var protocol = RequestProtocol();
        var responses = new ResponseProtocolDefinitionV1(
            protocol.ContractName,
            "response-publication-digest",
            new[]
            {
                new ResponseMessageDescriptorV1(1, "Read", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Primitive, typeof(int).FullName!)),
                new ResponseMessageDescriptorV1(2, "Ping"),
                new ResponseMessageDescriptorV1(3, "Acquire", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Ownership, "SingPlus.Sip.OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)),
                new ResponseMessageDescriptorV1(4, "Packet", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Bounded, typeof(Packet).FullName!, 16))
            });
        var channel = kernel.CreateChannel(left, right, protocol, responses, capacity);
        Assert.True(channel.IsSuccess, channel.Message);
        return (kernel, left, right, channel.Value);
    }

    private static ProtocolDefinitionV1 RequestProtocol() =>
        new(
            "SingPlus.Tests.IResponsePublicationProtocol",
            "response-publication-contract-digest",
            "Idle",
            terminalStates: null,
            messages: new[]
            {
                new ProtocolMessageDescriptorV1(1, "Read"),
                new ProtocolMessageDescriptorV1(2, "Ping"),
                new ProtocolMessageDescriptorV1(
                    3,
                    "Acquire",
                    returnsOwnership: true,
                    returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer),
                new ProtocolMessageDescriptorV1(4, "Packet")
            },
            transitions: new[]
            {
                new ProtocolTransitionV1(1, "Idle", "Idle"),
                new ProtocolTransitionV1(2, "Idle", "Idle"),
                new ProtocolTransitionV1(3, "Idle", "Idle"),
                new ProtocolTransitionV1(4, "Idle", "Idle")
            });
}
