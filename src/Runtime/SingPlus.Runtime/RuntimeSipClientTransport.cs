using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed class RuntimeSipClientTransport : ISipClientRuntimeTransport
{
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _requester;
    private readonly ProcessHandle _responder;
    private readonly ChannelEndpointHandle _requesterEndpoint;
    private readonly IReadOnlyCollection<CapabilityId>? _capabilities;

    public RuntimeSipClientTransport(
        RuntimeKernel kernel,
        ProcessHandle requester,
        ProcessHandle responder,
        ChannelEndpointHandle requesterEndpoint,
        IReadOnlyCollection<CapabilityId>? capabilities = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _requester = requester;
        _responder = responder;
        _requesterEndpoint = requesterEndpoint;
        _capabilities = capabilities;
    }

    public ResponseEnvelope Invoke(uint messageId, object? requestPayload = null) =>
        InvokeAsync(messageId, requestPayload).AsTask().GetAwaiter().GetResult();

    public async ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload = null)
    {
        var send = _kernel.Send(
            _requester,
            _responder,
            _requesterEndpoint,
            messageId,
            requestPayload,
            _capabilities);
        if (!send.IsSuccess)
            throw Failure("send", send.Error, send.Message);

        var response = await _kernel.WaitForResponseAsync(
            _requester,
            _requesterEndpoint,
            send.Value!.Sequence).ConfigureAwait(false);
        if (!response.IsSuccess)
            throw Failure("response wait", response.Error, response.Message);

        return response.Value!;
    }

    private static InvalidOperationException Failure(
        string stage,
        KernelError error,
        string? message) =>
        new($"SIP client runtime {stage} failed with {error}: {message ?? "no diagnostic"}");
}
