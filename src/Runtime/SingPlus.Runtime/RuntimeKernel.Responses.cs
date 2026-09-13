using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private ResponseRegistry? _responseRegistry;
    private ResponseRegistry Responses => _responseRegistry ??= new ResponseRegistry(Regions, Channels);

    public KernelResult<ResponseEnvelope> PublishResponse(
        ProcessHandle responder,
        ChannelEndpointHandle endpoint,
        ulong requestSequence,
        object? payload = null)
    {
        var responderProcess = Processes.Resolve(responder);
        if (!responderProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(responderProcess.Error, responderProcess.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!);

            var requesterHandle = Responses.ResolveRequester(endpoint, requestSequence, responder);
            if (!requesterHandle.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(requesterHandle.Error, requesterHandle.Message!);

            var requesterProcess = Processes.Resolve(requesterHandle.Value);
            if (!requesterProcess.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(requesterProcess.Error, requesterProcess.Message!);

            return Responses.Publish(
                responderProcess.Value!,
                requesterProcess.Value!,
                endpoint,
                requestSequence,
                payload);
        }
    }

    public KernelResult<ResponseEnvelope> CancelResponse(
        ProcessHandle responder,
        ChannelEndpointHandle endpoint,
        ulong requestSequence)
    {
        var responderProcess = Processes.Resolve(responder);
        if (!responderProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(responderProcess.Error, responderProcess.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!);

            return Responses.Cancel(responder, endpoint, requestSequence);
        }
    }

    public KernelResult<ResponseEnvelope> ReceiveResponse(
        ProcessHandle requester,
        ChannelEndpointHandle endpoint)
    {
        var requesterProcess = Processes.Resolve(requester);
        if (!requesterProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(requesterProcess.Error, requesterProcess.Message!);

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!);

            return Responses.Receive(requester, endpoint);
        }
    }

    public ValueTask<KernelResult<ResponseEnvelope>> WaitForResponseAsync(
        ProcessHandle requester,
        ChannelEndpointHandle endpoint,
        ulong requestSequence)
    {
        var requesterProcess = Processes.Resolve(requester);
        if (!requesterProcess.IsSuccess)
            return ValueTask.FromResult(
                KernelResult<ResponseEnvelope>.Fail(requesterProcess.Error, requesterProcess.Message!));

        lock (_requestResponseCorrelationGate)
        {
            var endpointValidation = Channels.GetEndpoint(endpoint);
            if (!endpointValidation.IsSuccess)
            {
                return ValueTask.FromResult(
                    KernelResult<ResponseEnvelope>.Fail(endpointValidation.Error, endpointValidation.Message!));
            }

            // ResponseRegistry.WaitAsync records the exact correlated waiter before its
            // first incomplete await. Holding the same gate used by publish/cancel makes
            // waiter registration and terminal completion a single ordered lifecycle.
            return Responses.WaitAsync(requester, endpoint, requestSequence);
        }
    }
}
