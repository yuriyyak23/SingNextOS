using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed class RuntimeSipClientTransport : ISipClientRuntimeTransport
{
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _requester;
    private readonly ProcessHandle? _responder;
    private readonly ChannelEndpointHandle _requesterEndpoint;
    private readonly EndpointSessionHandle? _session;
    private readonly IReadOnlyCollection<CapabilityId>? _capabilities;
    private readonly CancellationToken _defaultCancellationToken;

    public RuntimeSipClientTransport(
        RuntimeKernel kernel,
        ProcessHandle requester,
        ProcessHandle responder,
        ChannelEndpointHandle requesterEndpoint,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _requester = requester;
        _responder = responder;
        _requesterEndpoint = requesterEndpoint;
        _capabilities = capabilities;
        _defaultCancellationToken = cancellationToken;
    }

    public RuntimeSipClientTransport(
        RuntimeKernel kernel,
        ProcessHandle caller,
        EndpointSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _requester = caller;
        _session = session;
        _defaultCancellationToken = cancellationToken;
    }

    public ResponseEnvelope Invoke(uint messageId, object? requestPayload = null) =>
        InvokeAsync(messageId, requestPayload, _defaultCancellationToken).AsTask().GetAwaiter().GetResult();

    public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload = null) =>
        InvokeAsync(messageId, requestPayload, _defaultCancellationToken);

    public async ValueTask<ResponseEnvelope> InvokeAsync(
        uint messageId,
        object? requestPayload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_session is { } session)
        {
            var result = await _kernel.InvokeSessionAsync(
                _requester,
                session,
                messageId,
                requestPayload,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) throw Failure("session invoke", result.Error, result.Message);
            return result.Value!;
        }

        RejectCancellableLegacyInvocation(cancellationToken);
        var send = _kernel.Send(
            _requester,
            _responder!.Value,
            _requesterEndpoint,
            messageId,
            requestPayload,
            _capabilities);
        return await WaitForResponseAsync(send).ConfigureAwait(false);
    }

    public ResponseEnvelope InvokeOwnershipPair(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload) =>
        InvokeOwnershipPairAsync(
                messageId,
                firstOwnershipPayload,
                secondOwnershipPayload,
                _defaultCancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload) =>
        InvokeOwnershipPairAsync(
            messageId,
            firstOwnershipPayload,
            secondOwnershipPayload,
            _defaultCancellationToken);

    public async ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firstOwnershipPayload);
        ArgumentNullException.ThrowIfNull(secondOwnershipPayload);
        cancellationToken.ThrowIfCancellationRequested();

        if (_session is { } session)
        {
            var result = await _kernel.InvokeSessionOwnershipPairAsync(
                _requester,
                session,
                messageId,
                firstOwnershipPayload,
                secondOwnershipPayload,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) throw Failure("session invoke", result.Error, result.Message);
            return result.Value!;
        }

        RejectCancellableLegacyInvocation(cancellationToken);
        var send = _kernel.SendOwnershipPair(
            _requester,
            _responder!.Value,
            _requesterEndpoint,
            messageId,
            firstOwnershipPayload,
            secondOwnershipPayload,
            _capabilities);
        return await WaitForResponseAsync(send).ConfigureAwait(false);
    }

    private async ValueTask<ResponseEnvelope> WaitForResponseAsync(
        KernelResult<ChannelEnvelope> send)
    {
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

    private void RejectCancellableLegacyInvocation(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
            throw new InvalidOperationException("Cancellable SIP invocation requires an EndpointSessionHandle; legacy raw-channel transport cannot safely settle ownership or service work from caller cancellation.");
    }

    private static InvalidOperationException Failure(
        string stage,
        KernelError error,
        string? message) =>
        new($"SIP client runtime {stage} failed with {error}: {message ?? "no diagnostic"}");
}
