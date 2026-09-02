using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> CreateChannel(
        ProcessHandle left,
        ProcessHandle right,
        ProtocolDefinitionV1 protocol,
        int capacity) =>
        CreateChannelCore(left, right, protocol, responseProtocol: null, capacity);

    public KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> CreateChannel(
        ProcessHandle left,
        ProcessHandle right,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1 responseProtocol,
        int capacity) =>
        CreateChannelCore(left, right, protocol, responseProtocol, capacity);

    private KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> CreateChannelCore(
        ProcessHandle left,
        ProcessHandle right,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1? responseProtocol,
        int capacity)
    {
        var leftProcess = Processes.Resolve(left);
        if (!leftProcess.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(leftProcess.Error, leftProcess.Message!);
        var rightProcess = Processes.Resolve(right);
        if (!rightProcess.IsSuccess)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(rightProcess.Error, rightProcess.Message!);
        if (leftProcess.Value!.Channels.Count >= leftProcess.Value.Manifest.ResourceLimits.MaxChannels ||
            rightProcess.Value!.Channels.Count >= rightProcess.Value.Manifest.ResourceLimits.MaxChannels)
        {
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(
                KernelError.CapacityExhausted,
                "Channel resource limit exceeded.");
        }

        if (responseProtocol is not null)
        {
            var responseValidation = ResponseRegistry.ValidateProtocol(protocol, responseProtocol);
            if (!responseValidation.IsSuccess)
            {
                return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(
                    responseValidation.Error,
                    responseValidation.Message!);
            }
        }

        var endpoints = Channels.Create(protocol, leftProcess.Value, rightProcess.Value, capacity);
        if (responseProtocol is not null)
        {
            Responses.RegisterChannel(
                endpoints.Left,
                endpoints.Right,
                responseProtocol,
                left,
                right,
                capacity);
        }

        leftProcess.Value.AddChannel(endpoints.Left);
        rightProcess.Value.AddChannel(endpoints.Right);
        return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Ok(endpoints);
    }

    public KernelResult<ChannelEnvelope> Send(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        object? payload = null,
        IReadOnlyCollection<CapabilityId>? capabilities = null)
    {
        var senderProcess = Processes.Resolve(sender);
        if (!senderProcess.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(senderProcess.Error, senderProcess.Message!);
        var receiverProcess = Processes.Resolve(receiver);
        if (!receiverProcess.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(receiverProcess.Error, receiverProcess.Message!);

        var responsePreflight = Responses.CanRegisterRequest(endpoint, messageId, sender, receiver);
        if (!responsePreflight.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(responsePreflight.Error, responsePreflight.Message!);

        var send = Channels.Send(
            senderProcess.Value!,
            receiverProcess.Value!,
            endpoint,
            messageId,
            payload,
            capabilities);
        if (!send.IsSuccess) return send;

        Responses.RegisterRequest(endpoint, send.Value!, sender, receiver);
        return send;
    }

    public KernelResult<ChannelEnvelope> Receive(ProcessHandle receiver, ChannelEndpointHandle endpoint)
    {
        var process = Processes.Resolve(receiver);
        if (!process.IsSuccess)
            return KernelResult<ChannelEnvelope>.Fail(process.Error, process.Message!);

        var receive = Channels.Receive(process.Value!, endpoint);
        if (!receive.IsSuccess) return receive;

        Responses.MarkDelivered(endpoint, receive.Value!, receiver);
        return receive;
    }
}
