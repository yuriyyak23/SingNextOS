using SingPlus.Boot.Samples;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot;

// Cooperatively pumps this bounded copied-value service after the real runtime
// transport enqueues its request. Every envelope and response remains runtime-owned.
internal sealed class CopiedCounterPump(RuntimeKernel runtime, ProcessHandle caller, ProcessHandle server,
    EndpointSessionHandle session, CancellationToken cancellation) : ISipClientRuntimeTransport
{
    private readonly RuntimeSipClientTransport _transport = new(runtime, caller, session, cancellation);
    public int DispatchCount { get; private set; }
    public bool CancelNextInvocation { get; set; }
    public bool RequestLateCancellation { get; set; }
    public ResponseEnvelope Invoke(uint messageId, object? payload = null) => InvokeAsync(messageId, payload).AsTask().GetAwaiter().GetResult();
    public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? payload = null) => InvokeAsync(messageId, payload, cancellation);
    public async ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? payload, CancellationToken token)
    {
        ValueTask<ResponseEnvelope> response = _transport.InvokeAsync(messageId, payload, token);
        if (!response.IsCompleted)
        {
            KernelResult<EndpointSessionRequestEnvelope> received = runtime.ReceiveSessionRequest(server, session);
            if (!received.IsSuccess) throw new InvalidOperationException($"Receive failed: {received.Error}: {received.Message}");
            if (CancelNextInvocation)
            {
                CancelNextInvocation = false;
                KernelResult<CancellationObservation> scope = runtime.CreateCancellationScope(caller);
                if (!scope.IsSuccess) throw new InvalidOperationException($"Scope failed: {scope.Error}: {scope.Message}");
                KernelResult bound = runtime.BindSessionCancellationScope(caller, received.Value!.Invocation, scope.Value!.Scope);
                if (!bound.IsSuccess) throw new InvalidOperationException($"Scope binding failed: {bound.Error}: {bound.Message}");
                KernelResult<CancellationObservation> requested = runtime.RequestSessionCancellation(caller, received.Value.Invocation, scope.Value.Scope);
                if (!requested.IsSuccess) throw new InvalidOperationException($"Cancellation failed: {requested.Error}: {requested.Message}");
                KernelResult refused = runtime.AcceptSessionInvocation(server, received.Value.Invocation, allowInFlightCancellation: false);
                if (refused.IsSuccess || refused.Error != KernelError.InvalidTransition)
                    throw new InvalidOperationException("Cancelled invocation was not refused before acceptance.");
                KernelResult settled = runtime.AcceptSessionCancellation(server, received.Value.Invocation);
                if (!settled.IsSuccess) throw new InvalidOperationException($"Cancellation acceptance failed: {settled.Error}: {settled.Message}");
                KernelResult<ResponseEnvelope> cancelled = runtime.CancelSessionResponse(server, received.Value.Invocation);
                if (!cancelled.IsSuccess) throw new InvalidOperationException($"Cancellation settlement failed: {cancelled.Error}: {cancelled.Message}");
                return await response.ConfigureAwait(false);
            }
            KernelResult accepted = runtime.AcceptSessionInvocation(server, received.Value!.Invocation, allowInFlightCancellation: false);
            if (!accepted.IsSuccess) throw new InvalidOperationException($"Accept failed: {accepted.Error}: {accepted.Message}");
            if (RequestLateCancellation)
            {
                RequestLateCancellation = false;
                KernelResult<CancellationObservation> scope = runtime.CreateCancellationScope(caller);
                if (!scope.IsSuccess) throw new InvalidOperationException($"Late scope failed: {scope.Error}: {scope.Message}");
                KernelResult bound = runtime.BindSessionCancellationScope(caller, received.Value.Invocation, scope.Value!.Scope);
                if (!bound.IsSuccess) throw new InvalidOperationException($"Late scope binding failed: {bound.Error}: {bound.Message}");
                KernelResult<CancellationObservation> requested = runtime.RequestSessionCancellation(caller, received.Value.Invocation, scope.Value.Scope);
                if (!requested.IsSuccess) throw new InvalidOperationException($"Late cancellation failed: {requested.Error}: {requested.Message}");
                if (requested.Value!.Disposition != CancellationDisposition.TooLateEffectMayExist)
                    throw new InvalidOperationException($"Unexpected late cancellation disposition: {requested.Value.Disposition}");
                KernelResult<bool> observed = runtime.QuerySessionCancellation(server, received.Value.Invocation);
                if (!observed.IsSuccess || observed.Value)
                    throw new InvalidOperationException("Non-cancellable acceptance did not win the late cancellation race.");
            }
            if (received.Value.Request.MessageId != CounterBindings.IncrementMessageId || received.Value.Request.Payload is not CounterValue request)
                throw new InvalidOperationException("Generated counter request shape mismatch.");
            CounterValue answer = CounterBindings.Dispatch(request);
            DispatchCount++;
            KernelResult<ResponseEnvelope> published = runtime.PublishSessionResponse(server, received.Value.Invocation, answer);
            if (!published.IsSuccess) throw new InvalidOperationException($"Publication failed: {published.Error}: {published.Message}");
        }
        return await response.ConfigureAwait(false);
    }
    public ResponseEnvelope InvokeOwnershipPair(uint id, object first, object second) => throw new NotSupportedException("Counter contract has no ownership-pair messages.");
    public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint id, object first, object second) => throw new NotSupportedException("Counter contract has no ownership-pair messages.");
    public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint id, object first, object second, CancellationToken token) => throw new NotSupportedException("Counter contract has no ownership-pair messages.");
}
