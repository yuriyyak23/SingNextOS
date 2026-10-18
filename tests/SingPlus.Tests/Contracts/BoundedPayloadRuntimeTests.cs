using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Contracts;

public sealed class BoundedPayloadRuntimeTests
{
    private readonly record struct Packet(int PayloadSize, int MaxPayloadSize) : IBoundedPayload;
    private readonly record struct OtherPacket(int PayloadSize, int MaxPayloadSize) : IBoundedPayload;

    [Fact]
    public void DescriptorRejectsMalformedBoundedPayloadMetadata()
    {
        Assert.Throws<ArgumentException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "", typeof(Packet).FullName!, 64));
        Assert.Throws<ArgumentException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "packet", "", 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "packet", typeof(Packet).FullName!, 0));

        var bounded = new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "packet", typeof(Packet).FullName!, 64);
        Assert.Throws<ArgumentException>(() => new ProtocolMessageDescriptorV1(
            10,
            "Ambiguous",
            consumes: new[] { "data" },
            requestPayload: bounded));
    }

    [Fact]
    public void ValidBoundedPayloadUsesContractDeclaredShapeAndLimit()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var payload = new Packet(64, 64);

        var send = kernel.Send(left, right, endpoints.Left, 1, payload);
        var receive = kernel.Receive(right, endpoints.Right);

        Assert.True(send.IsSuccess, send.Message);
        Assert.True(receive.IsSuccess, receive.Message);
        Assert.Equal(payload, Assert.IsType<Packet>(receive.Value!.Payload));
        Assert.Equal<ulong>(1, kernel.Channels.GetEndpoint(endpoints.Left).Value!.Sequence);
    }

    [Fact]
    public void ContractMaxBytesRejectsOversizeEvenWhenPayloadClaimsLargerLimit()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        var payload = new Packet(65, 1024);

        var send = kernel.Send(left, right, endpoints.Left, 1, payload);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.ProtocolState, after.ProtocolState);
        Assert.Equal(before.Sequence, after.Sequence);
    }

    [Fact]
    public void SelfReportedLimitMustMatchContractMetadata()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        var payload = new Packet(32, 1024);

        var send = kernel.Send(left, right, endpoints.Left, 1, payload);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.ProtocolState, after.ProtocolState);
        Assert.Equal(before.Sequence, after.Sequence);
    }

    [Fact]
    public void BoundedPayloadRuntimeTypeMustMatchContractShape()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        var payload = new OtherPacket(8, 64);

        var send = kernel.Send(left, right, endpoints.Left, 1, payload);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.ProtocolState, after.ProtocolState);
        Assert.Equal(before.Sequence, after.Sequence);
    }

    [Fact]
    public void UndeclaredBoundedPayloadIsRejected()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var send = kernel.Send(left, right, endpoints.Left, 2, new Packet(8, 64));

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.ProtocolState, after.ProtocolState);
        Assert.Equal(before.Sequence, after.Sequence);
    }

    [Fact]
    public void DeclaredBoundedMessageRejectsNonBoundedPayload()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var send = kernel.Send(left, right, endpoints.Left, 1, 8);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.ProtocolState, after.ProtocolState);
        Assert.Equal(before.Sequence, after.Sequence);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Left, ProcessHandle Right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel()
    {
        var kernel = new RuntimeKernel();
        var (_, left) = TestFixtures.Create(kernel, 101, 110);
        var (_, right) = TestFixtures.Create(kernel, 102, 120);
        var protocol = new ProtocolDefinitionV1(
            "SingPlus.Tests.IBoundedProtocol",
            "bounded-contract-digest",
            "Idle",
            terminalStates: null,
            messages: new[]
            {
                new ProtocolMessageDescriptorV1(
                    1,
                    "Packet",
                    requestPayload: new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "packet", typeof(Packet).FullName!, 64)),
                new ProtocolMessageDescriptorV1(2, "Plain")
            },
            transitions: new[]
            {
                new ProtocolTransitionV1(1, "Idle", "Idle"),
                new ProtocolTransitionV1(2, "Idle", "Idle")
            });
        var channel = kernel.CreateChannel(left, right, protocol, capacity: 4);
        Assert.True(channel.IsSuccess, channel.Message);
        return (kernel, left, right, channel.Value);
    }
}
