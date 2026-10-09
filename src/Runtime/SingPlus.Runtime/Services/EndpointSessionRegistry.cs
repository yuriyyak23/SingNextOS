using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal sealed class EndpointSessionRegistry
{
    internal sealed class Record
    {
        public required EndpointSessionHandle Handle { get; init; }
        public required ProcessHandle Caller { get; init; }
        public required ProcessHandle Service { get; init; }
        public required IReadOnlyList<CapabilityId> Capabilities { get; init; }
        public required IReadOnlyList<CapabilityRequirementV1> Requirements { get; init; }
        public required ChannelEndpointHandle Channel { get; init; }
        public required DateTimeOffset? ExpiresAt { get; init; }
        public int ActivePins { get; set; }
        public bool CloseRequested { get; set; }
        public bool CloseCleanupClaimed { get; set; }
        // Three transitions are possible: Active, optional Draining, Closed.
        // Reserve observation storage before publishing the session record.
        public List<SessionLifecycleTraceEventV1> LifecycleTrace { get; } = new(3);
        public bool LifecycleTraceComplete { get; set; } = true;
        public ChannelEndpointHandle ServiceEndpoint => Channel with { EndpointId = new EndpointId(2) };
        public EndpointSessionState State { get; set; } = EndpointSessionState.Opening;
    }

    private readonly Dictionary<EndpointSessionId, Record> _records = [];
    // Same authoritative records; routing loss cannot erase lifetime or cleanup.
    private readonly Dictionary<EndpointSessionId, Record> _ownedRecords = [];
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private ulong _nextId = 1;

    internal EndpointSessionRegistry(TimeProvider timeProvider) => _timeProvider = timeProvider;

    internal KernelResult<Record> Add(ProcessHandle caller, ProcessHandle service, IReadOnlyList<CapabilityId> capabilities, IReadOnlyList<CapabilityRequirementV1> requirements, ChannelEndpointHandle channel, TimeSpan? lifetime)
    {
        var capabilitySnapshot = capabilities.ToArray();
        var requirementSnapshot = requirements.ToArray();
        var deadline = PrepareDeadline(lifetime);
        return deadline.IsSuccess
            ? AddPrepared(caller, service, capabilitySnapshot, requirementSnapshot, channel, deadline.Value)
            : KernelResult<Record>.Fail(deadline.Error, deadline.Message!);
    }

    internal KernelResult<DateTimeOffset?> PrepareDeadline(TimeSpan? lifetime)
    {
        if (lifetime is null) return KernelResult<DateTimeOffset?>.Ok(null);
        if (lifetime <= TimeSpan.Zero)
            return KernelResult<DateTimeOffset?>.Fail(KernelError.SessionTimedOut, "Session lifetime must be positive.");
        try { return KernelResult<DateTimeOffset?>.Ok(_timeProvider.GetUtcNow() + lifetime.Value); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult<DateTimeOffset?>.Fail(KernelError.PlatformFaulted, $"Session deadline preparation failed: {exception.Message}"); }
    }

    // Trusted pure local channel/session allocation only. Capacity admission
    // precedes channel effects, and concurrent openings cannot share the final ID.
    internal KernelResult<T> CommitOpeningCapacity<T>(Func<KernelResult<T>> allocate)
    {
        lock (_gate)
            return _nextId == 0
                ? KernelResult<T>.Fail(KernelError.CapacityExhausted, "Session identity space is exhausted.")
                : allocate();
    }

    // Private arrays and prepared deadline only; no caller/clock callbacks.
    internal KernelResult<Record> AddPrepared(ProcessHandle caller, ProcessHandle service, CapabilityId[] capabilities,
        CapabilityRequirementV1[] requirements, ChannelEndpointHandle channel, DateTimeOffset? expiresAt)
    {
        lock (_gate)
        {
            if (_nextId == 0)
                return KernelResult<Record>.Fail(KernelError.CapacityExhausted, "Session identity space is exhausted.");
            _ownedRecords.EnsureCapacity(_ownedRecords.Count + 1);
            _records.EnsureCapacity(_records.Count + 1);
            var id = new EndpointSessionId(_nextId);
            _nextId = _nextId == ulong.MaxValue ? 0 : _nextId + 1;
            var record = new Record { Handle = new EndpointSessionHandle(id, new EndpointSessionGeneration(1)), Caller = caller, Service = service, Capabilities = Array.AsReadOnly(capabilities), Requirements = Array.AsReadOnly(requirements), Channel = channel, ExpiresAt = expiresAt };
            _ownedRecords.Add(id, record);
            _records.Add(id, record);
            Transition(record, EndpointSessionState.Active, SessionLifecycleTraceCauseV1.Opened);
            return KernelResult<Record>.Ok(record);
        }
    }

    internal sealed record ExpiryObservation(Record Session, DateTimeOffset Time);

    internal KernelResult<ExpiryObservation?> ReadExpiryObservation(EndpointSessionHandle handle, ProcessHandle caller)
    {
        Record candidate;
        lock (_gate)
        {
            if (!_records.TryGetValue(handle.SessionId, out var record) || record.Handle != handle ||
                record.Caller != caller || !OwnsOriginalRecord(record) || record.State != EndpointSessionState.Active || record.ExpiresAt is null)
                return KernelResult<ExpiryObservation?>.Ok(null);
            candidate = record;
        }
        try { return KernelResult<ExpiryObservation?>.Ok(new(candidate, _timeProvider.GetUtcNow())); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult<ExpiryObservation?>.Fail(KernelError.PlatformFaulted, $"Session expiry clock failed: {exception.Message}"); }
    }

    internal Record? ExpireIfDue(ExpiryObservation? observation)
    {
        if (observation is null) return null;
        lock (_gate)
        {
            var expected = observation.Session;
            if (!_records.TryGetValue(expected.Handle.SessionId, out var record) || !ReferenceEquals(record, expected) || !OwnsOriginalRecord(record) ||
                record.State != EndpointSessionState.Active || record.ExpiresAt is not { } expiresAt || observation.Time < expiresAt)
                return null;
            if (record.ActivePins == 0)
                Transition(record, EndpointSessionState.Closed, SessionLifecycleTraceCauseV1.Expired);
            else
            {
                record.CloseRequested = true;
                Transition(record, EndpointSessionState.Draining, SessionLifecycleTraceCauseV1.Expired);
            }
            return record;
        }
    }

    internal DateTimeOffset ReadDonationPublicationTime() => _timeProvider.GetUtcNow();

    // Trusted local publication only; clock and qualification callbacks precede owner locks.
    internal KernelResult CommitDonationPublication(Record expected, DateTimeOffset observedTime, Func<KernelResult> publish)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(expected.Handle.SessionId, out var current) ||
                !ReferenceEquals(current, expected) || !OwnsOriginalRecord(current) || current.State != EndpointSessionState.Active ||
                current.CloseRequested || current.ExpiresAt is { } expiresAt && observedTime >= expiresAt)
                return KernelResult.Fail(KernelError.SessionClosed, "Donation session is no longer active.");
            return publish();
        }
    }

    internal KernelResult<Record> Resolve(EndpointSessionHandle handle, ProcessHandle caller)
    {
        lock (_gate)
        {
        if (!_records.TryGetValue(handle.SessionId, out var record))
            return KernelResult<Record>.Fail(KernelError.SessionNotFound, "Endpoint session was not found.");
        if (!OwnsOriginalRecord(record)) return KernelResult<Record>.Fail(KernelError.PlatformFaulted, "Original session owner continuity was lost.");
        if (record.Handle != handle)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Endpoint session generation is stale.");
        if (record.Caller != caller) return KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Caller does not own the endpoint session.");
        if (record.State == EndpointSessionState.Closed) return KernelResult<Record>.Fail(KernelError.SessionClosed, "Endpoint session is closed.");
        if (record.State == EndpointSessionState.Draining) return KernelResult<Record>.Fail(KernelError.SessionDraining, "Endpoint session is draining.");
        if (record.State == EndpointSessionState.Faulted) return KernelResult<Record>.Fail(KernelError.SessionClosed, "Endpoint session is faulted.");
        return KernelResult<Record>.Ok(record);
        }
    }

    internal KernelResult<Record> ResolveForService(EndpointSessionHandle handle, ProcessHandle service)
    {
        lock (_gate)
        {
        if (!_records.TryGetValue(handle.SessionId, out var record))
            return KernelResult<Record>.Fail(KernelError.SessionNotFound, "Endpoint session was not found.");
        if (!OwnsOriginalRecord(record)) return KernelResult<Record>.Fail(KernelError.PlatformFaulted, "Original session owner continuity was lost.");
        if (record.Handle != handle)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Endpoint session generation is stale.");
        if (record.Service != service) return KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Service does not own the endpoint session peer.");
        if (record.State != EndpointSessionState.Active) return KernelResult<Record>.Fail(KernelError.SessionClosed, "Endpoint session is not active.");
        return KernelResult<Record>.Ok(record);
        }
    }

    // Read-only close admission before native callbacks. Completed cleanup does
    // not require a new active capability admission; original identity still does.
    internal KernelResult<Record> PrepareClose(EndpointSessionHandle handle, ProcessHandle caller, bool allowAlreadyClosed)
    {
        lock (_gate)
        {
            if (allowAlreadyClosed && _records.TryGetValue(handle.SessionId, out var closed) &&
                OwnsOriginalRecord(closed) && closed.Handle == handle && closed.Caller == caller && closed.State == EndpointSessionState.Closed &&
                closed.ActivePins == 0)
                return KernelResult<Record>.Ok(closed);
            return Resolve(handle, caller);
        }
    }

    internal KernelResult<Record> Close(EndpointSessionHandle handle, ProcessHandle caller, bool allowAlreadyClosed = false)
    {
        lock (_gate)
        {
            var resolved = PrepareClose(handle, caller, allowAlreadyClosed);
            if (!resolved.IsSuccess) return resolved;
            if (resolved.Value!.ActivePins != 0)
            {
                resolved.Value.CloseRequested = true;
                Transition(resolved.Value, EndpointSessionState.Draining, SessionLifecycleTraceCauseV1.ExplicitClose);
                return KernelResult<Record>.Fail(KernelError.SessionDraining, "Endpoint session has an admitted effect pin and is draining.");
            }
            Transition(resolved.Value, EndpointSessionState.Closed, SessionLifecycleTraceCauseV1.ExplicitClose);
            return resolved;
        }
    }

    // Exact owner relationship capture for native cleanup routing. Includes
    // closed transport records: transport closure cannot retire native work.
    internal EndpointSessionHandle[] CaptureHandlesForProcess(ProcessHandle process)
    {
        lock (_gate)
            return _ownedRecords.Values.Where(record => record.Caller == process || record.Service == process)
                .Select(record => record.Handle).ToArray();
    }

    internal Record[] CloseForProcess(ProcessHandle process)
    {
        lock (_gate)
        {
            var records = _ownedRecords.Values.Where(x => x.State != EndpointSessionState.Closed && (x.Caller == process || x.Service == process)).ToArray();
            foreach (var record in records)
            {
                if (record.ActivePins == 0)
                    Transition(record, EndpointSessionState.Closed, SessionLifecycleTraceCauseV1.ProcessTeardown);
                else
                {
                    record.CloseRequested = true;
                    Transition(record, EndpointSessionState.Draining, SessionLifecycleTraceCauseV1.ProcessTeardown);
                }
            }
            return records;
        }
    }

    internal EndpointSessionDiagnosticSnapshot[] SnapshotForProcess(ProcessHandle process)
    {
        lock (_gate)
        {
            return _ownedRecords.Values
                .Where(record => record.Caller == process || record.Service == process)
                .OrderBy(record => record.Handle.SessionId.Value)
                .Select(record => new EndpointSessionDiagnosticSnapshot(
                    record.Handle,
                    record.Caller,
                    record.Service,
                    record.State,
                    record.ExpiresAt,
                    0,
                    false))
                .ToArray();
        }
    }

    internal SessionLifecycleTraceEventV1[] SnapshotLifecycleTrace(EndpointSessionHandle handle)
    {
        lock (_gate)
            return _ownedRecords.TryGetValue(handle.SessionId, out var record) && record.Handle == handle &&
                   record.LifecycleTraceComplete
                ? record.LifecycleTrace.ToArray()
                : [];
    }

    internal KernelResult<EndpointSessionPin> AcquirePin(
        EndpointSessionHandle handle, ProcessHandle caller, ProcessHandle service)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(handle.SessionId, out var record))
                return KernelResult<EndpointSessionPin>.Fail(KernelError.SessionNotFound, "Endpoint session was not found.");
            if (!OwnsOriginalRecord(record))
                return KernelResult<EndpointSessionPin>.Fail(KernelError.PlatformFaulted, "Original session owner continuity was lost.");
            if (record.Handle != handle)
                return KernelResult<EndpointSessionPin>.Fail(KernelError.StaleGeneration, "Endpoint session generation is stale.");
            if (record.Caller != caller || record.Service != service)
                return KernelResult<EndpointSessionPin>.Fail(KernelError.WrongSessionOwner, "Endpoint session parties do not match the admission attempt.");
            if (record.State != EndpointSessionState.Active)
                return KernelResult<EndpointSessionPin>.Fail(KernelError.SessionClosed, "Endpoint session is not active.");
            if (record.ActivePins == int.MaxValue)
                return KernelResult<EndpointSessionPin>.Fail(KernelError.CapacityExhausted, "Session effect pin capacity is exhausted.");
            record.ActivePins++;
            return KernelResult<EndpointSessionPin>.Ok(new EndpointSessionPin(this, record));
        }
    }

    internal KernelResult RevalidatePin(EndpointSessionPin pin)
    {
        lock (_gate)
            return pin.IsOwnedBy(this) && _records.TryGetValue(pin.Handle.SessionId, out var record) &&
                   ReferenceEquals(record, pin.OriginalRecord) && OwnsOriginalRecord(record) &&
                   record.Handle == pin.Handle && record.Caller == pin.Caller && record.Service == pin.Service &&
                   record.ActivePins > 0 && record.State == EndpointSessionState.Active
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.SessionClosed, "Endpoint session pin is no longer current.");
    }

    // Pure local consumptive admission only. Caller freezes process lifecycle;
    // callback performs source commit, never clock/provider/lifecycle callbacks.
    internal KernelResult<T> CommitPinnedEffectAdmission<T>(EndpointSessionPin pin, Func<KernelResult<T>> commit)
    {
        lock (_gate)
        {
            if (!pin.IsOwnedBy(this))
                return KernelResult<T>.Fail(KernelError.SessionClosed, "Endpoint session pin is disposed or foreign.");
            var current = RevalidatePin(pin);
            return current.IsSuccess ? commit() : KernelResult<T>.Fail(current.Error, current.Message!);
        }
    }

    internal void ReleasePin(EndpointSessionPin pin)
    {
        lock (_gate)
        {
            var record = pin.OriginalRecord;
            if (!OwnsOriginalRecord(record) || record.ActivePins == 0)
                return;
            record.ActivePins--;
            if (record.ActivePins == 0 && record.CloseRequested)
                Transition(record, EndpointSessionState.Closed, SessionLifecycleTraceCauseV1.PinReleased);
        }
    }

    internal int ActivePinCount(EndpointSessionHandle handle)
    {
        lock (_gate) return _ownedRecords.TryGetValue(handle.SessionId, out var record) && record.Handle == handle
            ? record.ActivePins : 0;
    }

    internal Record? ClaimClosedCleanup(EndpointSessionHandle handle)
    {
        lock (_gate)
        {
            if (!_ownedRecords.TryGetValue(handle.SessionId, out var record) ||
                record.Handle != handle ||
                record.State != EndpointSessionState.Closed ||
                !record.CloseRequested ||
                record.CloseCleanupClaimed)
                return null;
            record.CloseCleanupClaimed = true;
            return record;
        }
    }

    private bool OwnsOriginalRecord(Record record) =>
        _ownedRecords.TryGetValue(record.Handle.SessionId, out var original) && ReferenceEquals(original, record);

    private static void Transition(Record record, EndpointSessionState state,
        SessionLifecycleTraceCauseV1 cause)
    {
        if (record.State == state) return;
        record.State = state;
        if (record.LifecycleTrace.Count == 3)
        {
            record.LifecycleTraceComplete = false;
            return;
        }
        record.LifecycleTrace.Add(new SessionLifecycleTraceEventV1(
            SessionLifecycleTraceEventV1.CurrentVersion, record.Handle,
            checked((ulong)record.LifecycleTrace.Count + 1), state, cause));
    }
}

internal sealed class EndpointSessionPin : IDisposable
{
    private EndpointSessionRegistry? _owner;
    internal EndpointSessionPin(EndpointSessionRegistry owner, EndpointSessionRegistry.Record original)
        => (_owner, OriginalRecord, Handle, Caller, Service) = (owner, original, original.Handle, original.Caller, original.Service);
    internal EndpointSessionRegistry.Record OriginalRecord { get; }
    internal EndpointSessionHandle Handle { get; }
    internal ProcessHandle Caller { get; }
    internal ProcessHandle Service { get; }
    internal bool IsOwnedBy(EndpointSessionRegistry owner) => ReferenceEquals(Volatile.Read(ref _owner), owner);
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleasePin(this);
}
