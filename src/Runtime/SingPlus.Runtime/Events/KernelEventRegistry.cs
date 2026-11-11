using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// Small policy-neutral process-bound event mailbox. The registry owns only local
/// event delivery lifetime; platform/provider identities never enter an endpoint or event.
/// Each endpoint intentionally admits at most one staged or committed event. Staged
/// reservations are invisible to consumers until the exact source path commits them.
/// </summary>
internal sealed class KernelEventRegistry
{
    private const int MaximumSourceResourceIdLength = 128;

    private enum EndpointState
    {
        Active = 0,
        OwnerClosing,
        Closed,
    }

    private sealed class WaiterRecord
    {
        public TaskCompletionSource<KernelResult<KernelEvent>> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record WaiterCancellation(
        KernelEventRegistry Registry,
        KernelEventEndpoint Endpoint,
        WaiterRecord Waiter,
        CancellationToken CancellationToken);

    private sealed class EndpointRecord(KernelEventEndpoint endpoint)
    {
        public KernelEventEndpoint Endpoint { get; } = endpoint;
        public KernelEvent? Staged { get; set; }
        public KernelEvent? Pending { get; set; }
        public WaiterRecord? Waiter { get; set; }
        public EndpointState State { get; set; }
        public int PendingIrqAdmissions { get; set; }
        public HashSet<IrqSourceReservation> IrqSources { get; } = [];
    }

    // Opaque local source lifetime, never provider closure or capability permission.
    internal sealed class IrqSourceReservation
    {
        private readonly KernelEventRegistry _registry;
        private readonly EndpointRecord _owner;
        private object? _source;
        private IrqSourceReservation(KernelEventRegistry registry, EndpointRecord owner)
        { _registry = registry; _owner = owner; }
        internal static IrqSourceReservation Create(KernelEventRegistry registry, KernelEventEndpoint endpoint) =>
            new(registry, registry._endpoints[endpoint.EndpointId]);
        internal void BindSource(object source)
        {
            lock (_registry._gate)
            {
                if (_source is not null) throw new InvalidOperationException("Original endpoint source was already bound.");
                _source = source;
            }
        }
        internal void TransferSource(object original, object target)
        {
            lock (_registry._gate)
            {
                if (!ReferenceEquals(_source, original)) throw new InvalidOperationException("Original endpoint source correlation changed.");
                _source = target;
            }
        }
        internal object? SnapshotSource()
        {
            lock (_registry._gate) return _source;
        }

        internal KernelResult CommitRelease(object? source, Func<KernelResult> commit)
        {
            lock (_registry._gate)
            {
                if (!_registry._ownedEndpoints.TryGetValue(_owner.Endpoint.EndpointId, out EndpointRecord? original) ||
                    !ReferenceEquals(original, _owner) ||
                    _registry._endpoints.TryGetValue(_owner.Endpoint.EndpointId, out EndpointRecord? current) && !ReferenceEquals(current, _owner) ||
                    !_owner.IrqSources.Contains(this) || !ReferenceEquals(_source, source))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original endpoint source reservation continuity was lost.");
                // Pure local owner commit only, matching existing IRQ admission lock order.
                KernelResult result = commit();
                if (result.IsSuccess) _owner.IrqSources.Remove(this);
                return result;
            }
        }
    }

    // Snapshot correlations from the existing local lifetime owner. The caller must
    // inspect provider facts after releasing this gate; snapshot does not prove closure.
    internal IReadOnlyList<object> SnapshotIrqSources()
    {
        lock (_gate)
            return _ownedEndpoints.Values.SelectMany(record => record.IrqSources)
                .Select(reservation => reservation.SnapshotSource()).OfType<object>().ToArray();
    }

    internal KernelResult<IrqSourceReservation> ReserveIrqSource(ProcessHandle owner, KernelEventEndpoint endpoint)
    {
        lock (_gate)
        {
            KernelResult valid = ValidateLocked(owner, endpoint);
            if (!valid.IsSuccess) return KernelResult<IrqSourceReservation>.Fail(valid.Error, valid.Message!);
            EndpointRecord record = _endpoints[endpoint.EndpointId];
            if (record.State != EndpointState.Active || record.PendingIrqAdmissions == 0)
                return KernelResult<IrqSourceReservation>.Fail(KernelError.PlatformBindingDraining, "Exact endpoint IRQ admission was lost.");
            if (record.IrqSources.Count == int.MaxValue)
                return KernelResult<IrqSourceReservation>.Fail(KernelError.CapacityExhausted, "Endpoint source lifetime capacity is exhausted.");
            IrqSourceReservation reservation = IrqSourceReservation.Create(this, endpoint);
            record.IrqSources.Add(reservation);
            return KernelResult<IrqSourceReservation>.Ok(reservation);
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<KernelEventEndpointId, EndpointRecord> _endpoints = [];
    private readonly Dictionary<KernelEventEndpointId, EndpointRecord> _ownedEndpoints = [];
    private ulong _nextEndpointId = 1;
    private ulong _nextEventSequence = 1;

    public KernelResult<KernelEventEndpoint> Create(ProcessHandle owner)
    {
        if (owner.ProcessId.Value == 0 || owner.Generation == 0)
        {
            return KernelResult<KernelEventEndpoint>.Fail(
                KernelError.StaleHandle,
                "Kernel event endpoints require an exact materialized process generation.");
        }

        lock (_gate)
        {
            try
            {
                KernelEventEndpoint endpoint = new KernelEventEndpoint(
                    new KernelEventEndpointId(NextNonZero(ref _nextEndpointId)),
                    new KernelEventEndpointGeneration(1),
                    owner);
                EndpointRecord record = new EndpointRecord(endpoint);
                _ownedEndpoints.EnsureCapacity(_ownedEndpoints.Count + 1);
                _endpoints.EnsureCapacity(_endpoints.Count + 1);
                _ownedEndpoints.Add(endpoint.EndpointId, record);
                _endpoints.Add(endpoint.EndpointId, record);
                return KernelResult<KernelEventEndpoint>.Ok(endpoint);
            }
            catch (Exception)
            {
                return KernelResult<KernelEventEndpoint>.Fail(
                    KernelError.CapacityExhausted,
                    "Kernel event endpoint identity allocation failed.");
            }
        }
    }

    internal KernelResult BeginIrqBindingAdmission(ProcessHandle owner, KernelEventEndpoint endpoint)
    {
        lock (_gate)
        {
            KernelResult valid = ValidateLocked(owner, endpoint);
            if (!valid.IsSuccess) return valid;
            EndpointRecord record = _endpoints[endpoint.EndpointId];
            if (record.State != EndpointState.Active)
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Endpoint closing state denies new IRQ admission.");
            if (record.PendingIrqAdmissions == int.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted,
                    "Endpoint IRQ admission accounting is exhausted.");
            record.PendingIrqAdmissions++;
            return KernelResult.Ok();
        }
    }

    internal KernelResult CommitIrqBindingAdmission(ProcessHandle owner,
        KernelEventEndpoint endpoint, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult valid = ValidateLocked(owner, endpoint);
            if (!valid.IsSuccess) return valid;
            EndpointRecord record = _endpoints[endpoint.EndpointId];
            if (record.State != EndpointState.Active || record.PendingIrqAdmissions == 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Exact endpoint admission no longer permits IRQ publication.");
            // This callback only commits local owner state, without provider calls.
            return commit();
        }
    }
    internal void EndIrqBindingAdmission(KernelEventEndpoint endpoint)
    {
        lock (_gate)
        {
            if (!_ownedEndpoints.TryGetValue(endpoint.EndpointId, out EndpointRecord? record) ||
                record.Endpoint != endpoint || record.PendingIrqAdmissions <= 0)
                throw new InvalidOperationException("Exact endpoint IRQ admission accounting was lost.");
            // OwnerClosing is allowed here: it denies new effects, but does not erase pending admission.
            record.PendingIrqAdmissions--;
        }
    }
    public KernelResult Validate(ProcessHandle owner, KernelEventEndpoint endpoint)
    {
        lock (_gate)
            return ValidateLocked(owner, endpoint);
    }

    /// <summary>
    /// Reserves the endpoint's single mailbox slot without making an event visible
    /// to the owner. A source must commit this exact staged event only after its
    /// own completion/publication condition succeeds, or roll it back on failure.
    /// </summary>
    public KernelResult<KernelEvent> Stage(
        ProcessHandle owner,
        KernelEventEndpoint endpoint,
        KernelEventClass eventClass,
        string sourceResourceId)
        => Stage(owner, endpoint, eventClass, sourceResourceId, out _);

    internal KernelResult<KernelEvent> Stage(ProcessHandle owner, KernelEventEndpoint endpoint,
        KernelEventClass eventClass, string sourceResourceId, out Func<KernelResult>? rollback)
    {
        rollback = null;
        lock (_gate)
        {
            KernelResult validation = ValidateLocked(owner, endpoint);
            if (!validation.IsSuccess)
            {
                return KernelResult<KernelEvent>.Fail(
                    validation.Error,
                    validation.Message!);
            }

            if (!Enum.IsDefined(eventClass) ||
                string.IsNullOrWhiteSpace(sourceResourceId) ||
                sourceResourceId.Length > MaximumSourceResourceIdLength)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.InvalidMessage,
                    "Kernel events require a defined class and bounded semantic source identity.");
            }

            EndpointRecord record = _endpoints[endpoint.EndpointId];
            if (record.State != EndpointState.Active)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.InvalidTransition,
                    "A process that is closing cannot admit a new kernel event publication.");
            }

            if (record.Staged is not null || record.Pending is not null)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.CapacityExhausted,
                    "The kernel event endpoint already has a reserved or pending event.");
            }

            try
            {
                KernelEvent @event = new KernelEvent(
                    endpoint,
                    new KernelEventSequence(NextNonZero(ref _nextEventSequence)),
                    eventClass,
                    sourceResourceId);
                // Retain the existing original owner before callbacks can lose its route.
                rollback = () => RollbackOriginalStaged(record, owner, @event);
                record.Staged = @event;
                return KernelResult<KernelEvent>.Ok(@event);
            }
            catch (Exception)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.CapacityExhausted,
                    "Kernel event sequence allocation failed.");
            }
        }
    }

    // A staged mailbox pins its capacity, not permission. Revalidate the original
    // endpoint and exact reservation inside the source's existing pure commit.
    internal KernelResult CommitStagedAdmission(ProcessHandle owner, KernelEvent staged, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult validation = ValidateLocked(owner, staged.Endpoint);
            if (!validation.IsSuccess) return validation;
            EndpointRecord record = _endpoints[staged.Endpoint.EndpointId];
            if (record.State != EndpointState.Active || record.Staged != staged || record.Pending is not null)
                return KernelResult.Fail(KernelError.InvalidTransition, "Original staged event no longer admits delivery.");
            return commit();
        }
    }

    private KernelResult RollbackOriginalStaged(EndpointRecord record, ProcessHandle owner, KernelEvent staged)
    {
        lock (_gate)
        {
            if (!_ownedEndpoints.TryGetValue(staged.Endpoint.EndpointId, out EndpointRecord? original) ||
                !ReferenceEquals(original, record) || record.Endpoint != staged.Endpoint || record.Endpoint.Owner != owner ||
                record.Staged != staged)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original staged event rollback fact changed.");
            // Consumption of a completed local refusal; routing/active permission is not required.
            record.Staged = null;
            return KernelResult.Ok();
        }
    }

    public KernelResult<KernelEvent> CommitExact(
        ProcessHandle owner,
        KernelEvent staged)
    {
        KernelResult<KernelEvent> result = CommitExactDeferred(owner, staged, out Action? notify);
        notify?.Invoke();
        return result;
    }

    // The source calls the returned notification only after all admission locks exit.
    internal KernelResult<KernelEvent> CommitExactDeferred(
        ProcessHandle owner, KernelEvent staged, out Action? notify)
    {
        notify = null;
        WaiterRecord? waiter;
        KernelResult<KernelEvent> result;
        lock (_gate)
        {
            KernelResult validation = ValidateLocked(owner, staged.Endpoint);
            if (!validation.IsSuccess)
            {
                return KernelResult<KernelEvent>.Fail(
                    validation.Error,
                    validation.Message!);
            }

            EndpointRecord record = _endpoints[staged.Endpoint.EndpointId];
            if (record.Staged is not { } exact || exact != staged || record.Pending is not null)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.PlatformFaulted,
                    "The exact staged kernel event is no longer the endpoint's publication reservation.");
            }

            record.Staged = null;
            waiter = record.Waiter;
            if (waiter is null)
            {
                record.Pending = staged;
            }
            else
            {
                // The exact waiter owns this committed event at the commit
                // linearization point. A later cancellation or close cannot steal it.
                record.Waiter = null;
            }

            result = KernelResult<KernelEvent>.Ok(staged);
        }

        if (waiter is not null) notify = () => { _ = waiter.Source.TrySetResult(result); };
        return result;
    }

    public KernelResult RollbackExact(
        ProcessHandle owner,
        KernelEvent staged)
    {
        lock (_gate)
        {
            KernelResult validation = ValidateLocked(owner, staged.Endpoint);
            if (!validation.IsSuccess) return validation;

            EndpointRecord record = _endpoints[staged.Endpoint.EndpointId];
            if (record.Staged is not { } exact || exact != staged)
            {
                return KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "The exact staged kernel event selected for rollback is no longer reserved.");
            }

            record.Staged = null;
            return KernelResult.Ok();
        }
    }

    private KernelResult ValidateLocked(ProcessHandle owner, KernelEventEndpoint endpoint)
    {
        if (!_endpoints.TryGetValue(endpoint.EndpointId, out EndpointRecord? record))
        {
            return KernelResult.Fail(
                KernelError.EndpointNotFound,
                "The kernel event endpoint does not exist.");
        }

        if (!_ownedEndpoints.TryGetValue(endpoint.EndpointId, out EndpointRecord? original) || !ReferenceEquals(record, original))
            return KernelResult.Fail(KernelError.EndpointNotFound, "Kernel event route is not the original owner.");

        if (record.Endpoint.Generation != endpoint.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The kernel event endpoint generation is stale.");
        }

        if (record.Endpoint != endpoint)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The kernel event endpoint identity is malformed.");
        }

        if (record.Endpoint.Owner != owner)
        {
            return KernelResult.Fail(
                KernelError.WrongEndpointOwner,
                "The kernel event endpoint belongs to a different process generation.");
        }

        if (record.State == EndpointState.Closed)
        {
            return KernelResult.Fail(
                KernelError.EndpointNotFound,
                "The kernel event endpoint has been closed.");
        }

        return KernelResult.Ok();
    }

    public KernelResult<KernelEvent> Consume(
        ProcessHandle owner,
        KernelEventEndpoint endpoint)
    {
        lock (_gate)
        {
            KernelResult validation = ValidateLocked(owner, endpoint);
            if (!validation.IsSuccess)
            {
                return KernelResult<KernelEvent>.Fail(
                    validation.Error,
                    validation.Message!);
            }

            EndpointRecord record = _endpoints[endpoint.EndpointId];
            if (record.State != EndpointState.Active)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.InvalidTransition,
                    "A process that is closing cannot consume kernel event delivery.");
            }

            if (record.Pending is not { } pending)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.ResponseNotAvailable,
                    "No committed kernel event is pending on the exact endpoint.");
            }

            record.Pending = null;
            return KernelResult<KernelEvent>.Ok(pending);
        }
    }

    public ValueTask<KernelResult<KernelEvent>> WaitAsync(
        ProcessHandle owner,
        KernelEventEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        WaiterRecord waiter;
        lock (_gate)
        {
            KernelResult validation = ValidateLocked(owner, endpoint);
            if (!validation.IsSuccess)
            {
                return ValueTask.FromResult(KernelResult<KernelEvent>.Fail(
                    validation.Error,
                    validation.Message!));
            }

            EndpointRecord record = _endpoints[endpoint.EndpointId];
            if (record.State != EndpointState.Active)
            {
                return ValueTask.FromResult(KernelResult<KernelEvent>.Fail(
                    KernelError.InvalidTransition,
                    "A process that is closing cannot register a kernel event waiter."));
            }

            if (record.Pending is { } pending)
            {
                // A commit that already won is observable even when the caller's
                // token is concurrently cancelled.
                record.Pending = null;
                return ValueTask.FromResult(KernelResult<KernelEvent>.Ok(pending));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<KernelResult<KernelEvent>>(cancellationToken);
            }

            if (record.Waiter is not null)
            {
                return ValueTask.FromResult(KernelResult<KernelEvent>.Fail(
                    KernelError.CapacityExhausted,
                    "The kernel event endpoint already has an asynchronous waiter."));
            }

            waiter = new WaiterRecord();
            record.Waiter = waiter;
        }

        return AwaitWaiterAsync(endpoint, waiter, cancellationToken);
    }

    private async ValueTask<KernelResult<KernelEvent>> AwaitWaiterAsync(
        KernelEventEndpoint endpoint,
        WaiterRecord waiter,
        CancellationToken cancellationToken)
    {
        WaiterCancellation state = new WaiterCancellation(
            this,
            endpoint,
            waiter,
            cancellationToken);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static value =>
            {
                WaiterCancellation cancellation = (WaiterCancellation)value!;
                cancellation.Registry.CancelExactWaiter(cancellation);
            },
            state);
        return await waiter.Source.Task.ConfigureAwait(false);
    }

    private void CancelExactWaiter(WaiterCancellation cancellation)
    {
        bool removed = false;
        lock (_gate)
        {
            if (_ownedEndpoints.TryGetValue(cancellation.Endpoint.EndpointId, out EndpointRecord? record) &&
                record.Endpoint == cancellation.Endpoint &&
                ReferenceEquals(record.Waiter, cancellation.Waiter))
            {
                record.Waiter = null;
                removed = true;
            }
        }

        if (removed)
            _ = cancellation.Waiter.Source.TrySetCanceled(cancellation.CancellationToken);
    }

    public KernelResult Close(
        ProcessHandle owner,
        KernelEventEndpoint endpoint)
    {
        WaiterRecord? waiter;
        lock (_gate)
        {
            KernelResult validation = ValidateLocked(owner, endpoint);
            if (!validation.IsSuccess) return validation;

            EndpointRecord record = _endpoints[endpoint.EndpointId];
            if (record.Staged is not null || record.PendingIrqAdmissions != 0 || record.IrqSources.Count != 0)
            {
                return KernelResult.Fail(
                    KernelError.PlatformBindingDraining,
                    "The kernel event endpoint still owns IRQ admission, source lifetime or staged publication.");
            }

            record.Pending = null;
            waiter = record.Waiter;
            record.Waiter = null;
            record.State = EndpointState.Closed;
        }

        _ = waiter?.Source.TrySetCanceled();
        return KernelResult.Ok();
    }

    /// <summary>
    /// Stops new waiter/publication admission and cancels only current waits.
    /// Staged and committed events remain intact until final process reclaim.
    /// </summary>
    public void BeginCloseForProcess(ProcessHandle owner)
    {
        List<WaiterRecord> waiters = [];
        lock (_gate)
        {
            foreach (EndpointRecord record in _ownedEndpoints.Values)
            {
                if (record.State != EndpointState.Closed && record.Endpoint.Owner == owner)
                {
                    record.State = EndpointState.OwnerClosing;
                    if (record.Waiter is { } waiter)
                    {
                        record.Waiter = null;
                        waiters.Add(waiter);
                    }
                }
            }
        }

        foreach (WaiterRecord waiter in waiters)
            _ = waiter.Source.TrySetCanceled();
    }

    /// <summary>
    /// Reclaims local event state after source lifetime and staged publication end.
    /// The precheck makes the close all-or-none for one owner.
    /// </summary>
    public KernelResult CloseAllForProcess(ProcessHandle owner)
    {
        List<WaiterRecord> waiters = [];
        lock (_gate)
        {
            EndpointRecord[] records = _ownedEndpoints.Values
                .Where(record => record.State != EndpointState.Closed && record.Endpoint.Owner == owner)
                .ToArray();
            if (records.Any(static record => record.Staged is not null || record.PendingIrqAdmissions != 0 || record.IrqSources.Count != 0))
            {
                return KernelResult.Fail(
                    KernelError.PlatformBindingDraining,
                    "A kernel event source still owns IRQ admission, lifetime or staged publication during process teardown.");
            }

            foreach (EndpointRecord? record in records)
            {
                record.Pending = null;
                if (record.Waiter is { } waiter)
                {
                    record.Waiter = null;
                    waiters.Add(waiter);
                }
                record.State = EndpointState.Closed;
            }
        }

        foreach (WaiterRecord waiter in waiters)
            _ = waiter.Source.TrySetCanceled();
        return KernelResult.Ok();
    }

    private static ulong NextNonZero(ref ulong next)
    {
        if (next == 0)
            throw new InvalidOperationException("Kernel event identity space is exhausted.");
        ulong value = next;
        next = value == ulong.MaxValue ? 0 : value + 1;
        return value;
    }
}
