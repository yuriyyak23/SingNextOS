using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal sealed class EndpointSessionInvocationRegistry
{
    private sealed class Record
    {
        public required EndpointSessionInvocationHandle Handle { get; init; }
        public required ProcessHandle Caller { get; init; }
        public required ProcessHandle Service { get; init; }
        public required uint MessageId { get; init; }
        public bool Delivered { get; set; }
        public bool ServiceAccepted { get; set; }
        public bool InFlightCancellationAllowed { get; set; }
        public bool CancellationRequested { get; set; }
        public bool CancellationAccepted { get; set; }
        public ResponsePublicationStatus? TerminalStatus { get; set; }
    }

    private readonly Dictionary<(EndpointSessionId Session, EndpointSessionGeneration SessionGeneration, EndpointSessionInvocationId Invocation), Record> _records = [];
    private readonly object _gate = new();

    internal EndpointSessionInvocationHandle Register(
        EndpointSessionHandle session,
        ProcessHandle caller,
        ProcessHandle service,
        ulong requestSequence,
        uint messageId)
    {
        if (requestSequence == 0) throw new ArgumentOutOfRangeException(nameof(requestSequence));
        var handle = new EndpointSessionInvocationHandle(
            session,
            new EndpointSessionInvocationId(requestSequence),
            new EndpointSessionInvocationGeneration(1));
        var key = Key(handle);
        lock (_gate)
        {
            if (_records.TryGetValue(key, out var existing))
            {
                if (existing.Caller != caller ||
                    existing.Service != service ||
                    existing.MessageId != messageId)
                {
                    throw new InvalidOperationException(
                        "Endpoint session invocation identity conflicts with existing correlation.");
                }

                return existing.Handle;
            }

            _records.Add(key, new Record
            {
                Handle = handle,
                Caller = caller,
                Service = service,
                MessageId = messageId
            });
        }
        return handle;
    }

    internal KernelResult<EndpointSessionInvocationHandle> MarkDelivered(
        EndpointSessionHandle session,
        ProcessHandle service,
        ulong requestSequence)
    {
        var handle = new EndpointSessionInvocationHandle(
            session,
            new EndpointSessionInvocationId(requestSequence),
            new EndpointSessionInvocationGeneration(1));
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.TerminalStatus is not null)
                return KernelResult<EndpointSessionInvocationHandle>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            resolved.Value.Delivered = true;
            return KernelResult<EndpointSessionInvocationHandle>.Ok(handle);
        }
    }

    internal KernelResult RequestCancellation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller)
    {
        lock (_gate)
        {
            var resolved = ResolveForCaller(handle, caller);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (record.ServiceAccepted && !record.InFlightCancellationAllowed)
                return KernelResult.Fail(KernelError.InvalidTransition, "The service already accepted this invocation without an in-flight cancellation contour.");
            record.CancellationRequested = true;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<bool> IsCancellationRequested(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            return resolved.IsSuccess
                ? KernelResult<bool>.Ok(resolved.Value!.CancellationRequested)
                : KernelResult<bool>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult AcceptInvocation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        bool allowInFlightCancellation)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (!record.Delivered)
                return KernelResult.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (record.ServiceAccepted)
                return KernelResult.Fail(KernelError.InvalidTransition, "Endpoint session invocation was already accepted by the service.");
            if (record.CancellationRequested)
                return KernelResult.Fail(KernelError.InvalidTransition, "Cancellation was requested before service acceptance; execution must not begin.");
            record.ServiceAccepted = true;
            record.InFlightCancellationAllowed = allowInFlightCancellation;
            return KernelResult.Ok();
        }
    }

    internal KernelResult AcceptCancellation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (!record.Delivered)
                return KernelResult.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (!record.CancellationRequested)
                return KernelResult.Fail(KernelError.InvalidTransition, "The caller has not requested cancellation for this endpoint session invocation.");
            if (record.ServiceAccepted && !record.InFlightCancellationAllowed)
                return KernelResult.Fail(KernelError.InvalidTransition, "The accepted endpoint session invocation does not allow in-flight cancellation.");
            record.CancellationAccepted = true;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<ResponseEnvelope> Publish(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        Func<KernelResult<ResponseEnvelope>> publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (!record.Delivered)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (record.CancellationAccepted)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.InvalidTransition, "The service accepted cancellation and must settle the invocation as Cancelled.");

            var result = publication();
            if (result.IsSuccess) record.TerminalStatus = ResponsePublicationStatus.Published;
            return result;
        }
    }

    internal KernelResult<ResponseEnvelope> Cancel(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        Func<KernelResult<ResponseEnvelope>> cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess)
                return KernelResult<ResponseEnvelope>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal.");
            if (!record.Delivered)
                return KernelResult<ResponseEnvelope>.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");

            var result = cancellation();
            if (result.IsSuccess) record.TerminalStatus = ResponsePublicationStatus.Cancelled;
            return result;
        }
    }

    internal int PendingCount(EndpointSessionHandle session)
    {
        lock (_gate)
        {
            return _records.Values.Count(record =>
                record.Handle.Session == session &&
                record.TerminalStatus is null);
        }
    }

    internal void CloseSession(EndpointSessionHandle session)
    {
        lock (_gate)
        {
            foreach (var key in _records.Keys
                         .Where(key => key.Session == session.SessionId && key.SessionGeneration == session.Generation)
                         .ToArray())
                _records.Remove(key);
        }
    }

    private KernelResult<Record> ResolveForCaller(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller)
    {
        var resolved = Resolve(handle);
        if (!resolved.IsSuccess) return resolved;
        return resolved.Value!.Caller == caller
            ? resolved
            : KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Caller does not own the endpoint session invocation.");
    }

    private KernelResult<Record> ResolveForService(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service)
    {
        var resolved = Resolve(handle);
        if (!resolved.IsSuccess) return resolved;
        return resolved.Value!.Service == service
            ? resolved
            : KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Service does not own the endpoint session invocation peer.");
    }

    private KernelResult<Record> Resolve(EndpointSessionInvocationHandle handle)
    {
        if (handle.Generation.Value != 1)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Endpoint session invocation generation is stale.");
        return _records.TryGetValue(Key(handle), out var record)
            ? KernelResult<Record>.Ok(record)
            : KernelResult<Record>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation was not found or is no longer tracked.");
    }

    private static (EndpointSessionId, EndpointSessionGeneration, EndpointSessionInvocationId) Key(EndpointSessionInvocationHandle handle) =>
        (handle.Session.SessionId, handle.Session.Generation, handle.InvocationId);
}
