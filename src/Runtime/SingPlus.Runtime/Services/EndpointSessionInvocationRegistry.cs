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
        public CancellationScopeHandle? CancellationScope { get; set; }
        public bool Delivered { get; set; }
        public bool ServiceAccepted { get; set; }
        public bool InFlightCancellationAllowed { get; set; }
        public bool CancellationRequested { get; set; }
        public bool CancellationAccepted { get; set; }
        public ResponsePublicationStatus? SettlementInProgress { get; set; }
        public ResponsePublicationStatus? TerminalStatus { get; set; }
    }

    private readonly Dictionary<(EndpointSessionId Session, EndpointSessionGeneration SessionGeneration, EndpointSessionInvocationId Invocation), Record> _records = [];
    private readonly object _gate = new();
    private readonly CancellationScopeAuthority _cancellationScopes;

    internal Action? SettlementReservedHook { get; set; }

    internal EndpointSessionInvocationRegistry(CancellationScopeAuthority cancellationScopes) =>
        _cancellationScopes = cancellationScopes;

    internal EndpointSessionInvocationHandle Register(
        EndpointSessionHandle session,
        ProcessHandle caller,
        ProcessHandle service,
        ulong requestSequence,
        uint messageId,
        CancellationScopeHandle? cancellationScope = null)
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
                    existing.MessageId != messageId ||
                    cancellationScope is { } supplied && existing.CancellationScope is { } current && supplied != current)
                {
                    throw new InvalidOperationException(
                        "Endpoint session invocation identity conflicts with existing correlation.");
                }

                existing.CancellationScope ??= cancellationScope;
                return existing.Handle;
            }

            _records.Add(key, new Record
            {
                Handle = handle,
                Caller = caller,
                Service = service,
                MessageId = messageId,
                CancellationScope = cancellationScope
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

    internal KernelResult BindCancellationScope(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller,
        CancellationScopeHandle scope)
    {
        lock (_gate)
        {
            var resolved = ResolveForCaller(handle, caller);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var temporal = _cancellationScopes.Observe(caller, scope);
            if (!temporal.IsSuccess) return KernelResult.Fail(temporal.Error, temporal.Message!);
            if (temporal.Value!.Disposition == CancellationDisposition.Stale)
                return KernelResult.Fail(KernelError.StaleGeneration, "Cancellation scope generation is stale.");
            var binding = _cancellationScopes.BindConsumer(
                caller,
                scope,
                $"ipc:{handle.Session.SessionId.Value}:{handle.Session.Generation.Value}:{handle.InvocationId.Value}:{handle.Generation.Value}");
            if (!binding.IsSuccess) return binding;
            if (resolved.Value!.CancellationScope is { } existing && existing != scope)
                return KernelResult.Fail(KernelError.StaleGeneration, "Invocation is already bound to another cancellation scope generation.");
            resolved.Value.CancellationScope = scope;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<CancellationObservation> RequestCancellation(
        EndpointSessionInvocationHandle handle,
        ProcessHandle caller,
        CancellationScopeHandle scope)
    {
        lock (_gate)
        {
            var resolved = ResolveForCaller(handle, caller);
            if (!resolved.IsSuccess) return KernelResult<CancellationObservation>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.CancellationScope != scope)
                return KernelResult<CancellationObservation>.Fail(KernelError.StaleGeneration, "Invocation is not bound to the exact cancellation scope generation.");
            var request = _cancellationScopes.Request(caller, scope);
            if (!request.IsSuccess || request.Value!.Disposition == CancellationDisposition.Stale)
                return request;
            if (record.TerminalStatus == ResponsePublicationStatus.Published)
                return _cancellationScopes.RecordDisposition(caller, scope, CancellationDisposition.CompletedBeforeCancellation);
            if (record.TerminalStatus == ResponsePublicationStatus.Cancelled)
                return _cancellationScopes.RecordDisposition(caller, scope, CancellationDisposition.ProviderEffectContained);
            if (record.ServiceAccepted && !record.InFlightCancellationAllowed)
                return _cancellationScopes.RecordDisposition(caller, scope, CancellationDisposition.TooLateEffectMayExist);
            record.CancellationRequested = true;
            return request;
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
        var reservation = ReserveSettlement(
            handle,
            service,
            ResponsePublicationStatus.Published,
            requireCancellationAccepted: false);
        if (!reservation.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(reservation.Error, reservation.Message!);

        return ExecuteSettlement(reservation.Value!, publication);
    }

    internal KernelResult<ResponseEnvelope> Cancel(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        Func<KernelResult<ResponseEnvelope>> cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        var reservation = ReserveSettlement(
            handle,
            service,
            ResponsePublicationStatus.Cancelled,
            requireCancellationAccepted: null);
        if (!reservation.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(reservation.Error, reservation.Message!);

        return ExecuteSettlement(reservation.Value!, cancellation);
    }

    internal KernelResult CompleteInline(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        bool succeeded)
    {
        var status = succeeded ? ResponsePublicationStatus.Published : ResponsePublicationStatus.Cancelled;
        var reservation = ReserveSettlement(
            handle,
            service,
            status,
            requireCancellationAccepted: succeeded ? false : null);
        if (!reservation.IsSuccess) return KernelResult.Fail(reservation.Error, reservation.Message!);

        lock (_gate)
        {
            var record = reservation.Value!;
            if (record.SettlementInProgress != status)
                return KernelResult.Fail(KernelError.InvalidTransition, "Inline invocation settlement reservation was lost.");
            record.SettlementInProgress = null;
            record.TerminalStatus = status;
            if (record.CancellationScope is { } scope)
            {
                var disposition = succeeded
                    ? CancellationDisposition.CompletedBeforeCancellation
                    : record.ServiceAccepted
                        ? CancellationDisposition.ProviderEffectContained
                        : CancellationDisposition.CancelledBeforeEffect;
                if (!succeeded || record.CancellationRequested)
                    _ = _cancellationScopes.RecordDisposition(record.Caller, scope, disposition);
            }
            return KernelResult.Ok();
        }
    }

    private KernelResult<ResponseEnvelope> ExecuteSettlement(
        Record reservation,
        Func<KernelResult<ResponseEnvelope>> action)
    {
        try
        {
            SettlementReservedHook?.Invoke();
            return CompleteSettlement(reservation, action());
        }
        catch
        {
            lock (_gate)
                reservation.SettlementInProgress = null;
            throw;
        }
    }

    private KernelResult<Record> ReserveSettlement(
        EndpointSessionInvocationHandle handle,
        ProcessHandle service,
        ResponsePublicationStatus status,
        bool? requireCancellationAccepted)
    {
        lock (_gate)
        {
            var resolved = ResolveForService(handle, service);
            if (!resolved.IsSuccess)
                return KernelResult<Record>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TerminalStatus is not null || record.SettlementInProgress is not null)
                return KernelResult<Record>.Fail(KernelError.ResponseNotPending, "Endpoint session invocation is already terminal or settlement is in progress.");
            if (!record.Delivered)
                return KernelResult<Record>.Fail(KernelError.ResponseNotDelivered, "Endpoint session invocation has not been delivered to the service.");
            if (requireCancellationAccepted == false && record.CancellationAccepted)
                return KernelResult<Record>.Fail(KernelError.InvalidTransition, "The service accepted cancellation and must settle the invocation as Cancelled.");

            record.SettlementInProgress = status;
            return KernelResult<Record>.Ok(record);
        }
    }

    private KernelResult<ResponseEnvelope> CompleteSettlement(
        Record reservation,
        KernelResult<ResponseEnvelope> result)
    {
        lock (_gate)
        {
            if (reservation.SettlementInProgress is not { } status)
                throw new InvalidOperationException("Endpoint session invocation settlement reservation was lost.");

            reservation.SettlementInProgress = null;
            if (result.IsSuccess)
            {
                reservation.TerminalStatus = status;
                if (reservation.CancellationScope is { } scope)
                {
                    var disposition = status == ResponsePublicationStatus.Published
                        ? CancellationDisposition.CompletedBeforeCancellation
                        : reservation.ServiceAccepted
                            ? CancellationDisposition.ProviderEffectContained
                            : CancellationDisposition.CancelledBeforeEffect;
                    if (status == ResponsePublicationStatus.Cancelled || reservation.CancellationRequested)
                        _ = _cancellationScopes.RecordDisposition(reservation.Caller, scope, disposition);
                }
            }
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
