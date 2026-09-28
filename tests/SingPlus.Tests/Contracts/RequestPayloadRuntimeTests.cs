using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Contracts;

public sealed class RequestPayloadRuntimeTests
{
    private enum Mode : byte { Normal = 0, Diagnostic = 1 }
    private enum OtherMode : byte { Normal = 0 }

    [Fact]
    public void DescriptorRejectsMalformedUnifiedRequestShapes()
    {
        Assert.Throws<ArgumentException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.None, "value", typeof(int).FullName!));
        Assert.Throws<ArgumentException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.Primitive, "value", typeof(string).FullName!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "value", "Tests.Packet", 0));
        Assert.Throws<ArgumentException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.Ownership, "data", "SingPlus.Sip.OwnedBuffer"));
        Assert.Throws<ArgumentException>(() => new RequestPayloadDescriptorV1(RequestPayloadKind.Ownership, "data", "SingPlus.Sip.OwnedRegion", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer));
    }

    [Fact]
    public void PrimitivePayloadRequiresExactContractType()
    {
        var (kernel, left, right, endpoints) = CreateChannel();

        var valid = kernel.Send(left, right, endpoints.Left, 1, 7);
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var wrongType = kernel.Send(left, right, endpoints.Left, 1, 7u);

        Assert.False(wrongType.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongType.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void PrimitiveMessageRejectsMissingPayloadWithoutMutation()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var send = kernel.Send(left, right, endpoints.Left, 1);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void EnumPayloadRequiresExactEnumType()
    {
        var (kernel, left, right, endpoints) = CreateChannel();

        var valid = kernel.Send(left, right, endpoints.Left, 2, Mode.Diagnostic);
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var wrongEnum = kernel.Send(left, right, endpoints.Left, 2, OtherMode.Normal);
        var underlyingInteger = kernel.Send(left, right, endpoints.Left, 2, (byte)1);

        Assert.False(wrongEnum.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongEnum.Error);
        Assert.False(underlyingInteger.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, underlyingInteger.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void NonePayloadRejectsUnexpectedValueWithoutMutation()
    {
        var (kernel, left, right, endpoints) = CreateChannel();
        var before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        var send = kernel.Send(left, right, endpoints.Left, 3, 1);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        var after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void CorrectPrimitiveEnumAndNonePayloadsRoundTrip()
    {
        var (kernel, left, right, endpoints) = CreateChannel();

        Assert.True(kernel.Send(left, right, endpoints.Left, 1, 42).IsSuccess);
        Assert.Equal(42, Assert.IsType<int>(kernel.Receive(right, endpoints.Right).Value!.Payload));
        Assert.True(kernel.Send(left, right, endpoints.Left, 2, Mode.Normal).IsSuccess);
        Assert.Equal(Mode.Normal, Assert.IsType<Mode>(kernel.Receive(right, endpoints.Right).Value!.Payload));
        Assert.True(kernel.Send(left, right, endpoints.Left, 3).IsSuccess);
        Assert.Null(kernel.Receive(right, endpoints.Right).Value!.Payload);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Left, ProcessHandle Right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel()
    {
        var kernel = new RuntimeKernel();
        var (_, left) = TestFixtures.Create(kernel, 201, 210);
        var (_, right) = TestFixtures.Create(kernel, 202, 220);
        var protocol = new ProtocolDefinitionV1(
            "SingPlus.Tests.IRequestShapeProtocol",
            "request-shape-contract-digest",
            "Idle",
            terminalStates: null,
            messages: new[]
            {
                new ProtocolMessageDescriptorV1(
                    1,
                    "Number",
                    requestPayload: new RequestPayloadDescriptorV1(RequestPayloadKind.Primitive, "value", typeof(int).FullName!)),
                new ProtocolMessageDescriptorV1(
                    2,
                    "Mode",
                    requestPayload: new RequestPayloadDescriptorV1(RequestPayloadKind.Enum, "mode", typeof(Mode).FullName!)),
                new ProtocolMessageDescriptorV1(3, "Ping")
            },
            transitions: new[]
            {
                new ProtocolTransitionV1(1, "Idle", "Idle"),
                new ProtocolTransitionV1(2, "Idle", "Idle"),
                new ProtocolTransitionV1(3, "Idle", "Idle")
            });
        var channel = kernel.CreateChannel(left, right, protocol, capacity: 8);
        Assert.True(channel.IsSuccess, channel.Message);
        return (kernel, left, right, channel.Value);
    }
}
