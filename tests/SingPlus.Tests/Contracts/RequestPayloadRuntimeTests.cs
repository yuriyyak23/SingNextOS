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
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();

        KernelResult<ChannelEnvelope> valid = kernel.Send(left, right, endpoints.Left, 1, 7);
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        ChannelEndpoint before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        KernelResult<ChannelEnvelope> wrongType = kernel.Send(left, right, endpoints.Left, 1, 7u);

        Assert.False(wrongType.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongType.Error);
        ChannelEndpoint after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void PrimitiveMessageRejectsMissingPayloadWithoutMutation()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        ChannelEndpoint before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        KernelResult<ChannelEnvelope> send = kernel.Send(left, right, endpoints.Left, 1);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        ChannelEndpoint after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void EnumPayloadRequiresExactEnumType()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();

        KernelResult<ChannelEnvelope> valid = kernel.Send(left, right, endpoints.Left, 2, Mode.Diagnostic);
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        ChannelEndpoint before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        KernelResult<ChannelEnvelope> wrongEnum = kernel.Send(left, right, endpoints.Left, 2, OtherMode.Normal);
        KernelResult<ChannelEnvelope> underlyingInteger = kernel.Send(left, right, endpoints.Left, 2, (byte)1);

        Assert.False(wrongEnum.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongEnum.Error);
        Assert.False(underlyingInteger.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, underlyingInteger.Error);
        ChannelEndpoint after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void NonePayloadRejectsUnexpectedValueWithoutMutation()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        ChannelEndpoint before = kernel.Channels.GetEndpoint(endpoints.Left).Value!;

        KernelResult<ChannelEnvelope> send = kernel.Send(left, right, endpoints.Left, 3, 1);

        Assert.False(send.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, send.Error);
        ChannelEndpoint after = kernel.Channels.GetEndpoint(endpoints.Left).Value!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.ProtocolState, after.ProtocolState);
    }

    [Fact]
    public void CorrectPrimitiveEnumAndNonePayloadsRoundTrip()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();

        Assert.True(kernel.Send(left, right, endpoints.Left, 1, 42).IsSuccess);
        Assert.Equal(42, Assert.IsType<int>(kernel.Receive(right, endpoints.Right).Value!.Payload));
        Assert.True(kernel.Send(left, right, endpoints.Left, 2, Mode.Normal).IsSuccess);
        Assert.Equal(Mode.Normal, Assert.IsType<Mode>(kernel.Receive(right, endpoints.Right).Value!.Payload));
        Assert.True(kernel.Send(left, right, endpoints.Left, 3).IsSuccess);
        Assert.Null(kernel.Receive(right, endpoints.Right).Value!.Payload);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Left, ProcessHandle Right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (Sip.SingProcess _, ProcessHandle left) = TestFixtures.Create(kernel, 201, 210);
        (Sip.SingProcess _, ProcessHandle right) = TestFixtures.Create(kernel, 202, 220);
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1(
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
        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = kernel.CreateChannel(left, right, protocol, capacity: 8);
        Assert.True(channel.IsSuccess, channel.Message);
        return (kernel, left, right, channel.Value);
    }
}
