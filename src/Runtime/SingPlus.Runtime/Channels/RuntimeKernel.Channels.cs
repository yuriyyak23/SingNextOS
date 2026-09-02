using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> CreateChannel(ProcessHandle left, ProcessHandle right, ProtocolDefinitionV1 protocol, int capacity)
    {
        var leftProcess = Processes.Resolve(left);
        if (!leftProcess.IsSuccess) return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(leftProcess.Error, leftProcess.Message!);
        var rightProcess = Processes.Resolve(right);
        if (!rightProcess.IsSuccess) return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(rightProcess.Error, rightProcess.Message!);
        if (leftProcess.Value!.Channels.Count >= leftProcess.Value.Manifest.ResourceLimits.MaxChannels || rightProcess.Value!.Channels.Count >= rightProcess.Value.Manifest.ResourceLimits.MaxChannels)
            return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Fail(KernelError.CapacityExhausted, "Channel resource limit exceeded.");
        var endpoints = Channels.Create(protocol, leftProcess.Value, rightProcess.Value, capacity);
        leftProcess.Value.AddChannel(endpoints.Left);
        rightProcess.Value.AddChannel(endpoints.Right);
        return KernelResult<(ChannelEndpointHandle, ChannelEndpointHandle)>.Ok(endpoints);
    }

    public KernelResult<ChannelEnvelope> Send(ProcessHandle sender, ProcessHandle receiver, ChannelEndpointHandle endpoint, uint messageId, object? payload = null, IReadOnlyCollection<CapabilityId>? capabilities = null)
    {
        var senderProcess = Processes.Resolve(sender);
        if (!senderProcess.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(senderProcess.Error, senderProcess.Message!);
        var receiverProcess = Processes.Resolve(receiver);
        if (!receiverProcess.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(receiverProcess.Error, receiverProcess.Message!);
        return Channels.Send(senderProcess.Value!, receiverProcess.Value!, endpoint, messageId, payload, capabilities);
    }

    public KernelResult<ChannelEnvelope> Receive(ProcessHandle receiver, ChannelEndpointHandle endpoint)
    {
        var process = Processes.Resolve(receiver);
        if (!process.IsSuccess) return KernelResult<ChannelEnvelope>.Fail(process.Error, process.Message!);
        return Channels.Receive(process.Value!, endpoint);
    }
}
