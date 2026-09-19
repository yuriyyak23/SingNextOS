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
        public ChannelEndpointHandle ServiceEndpoint => Channel with { EndpointId = new EndpointId(2) };
        public EndpointSessionState State { get; set; } = EndpointSessionState.Opening;
    }

    private readonly Dictionary<EndpointSessionId, Record> _records = [];
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private ulong _nextId = 1;

    internal EndpointSessionRegistry(TimeProvider timeProvider) => _timeProvider = timeProvider;

    internal KernelResult<Record> Add(ProcessHandle caller, ProcessHandle service, IReadOnlyList<CapabilityId> capabilities, IReadOnlyList<CapabilityRequirementV1> requirements, ChannelEndpointHandle channel, TimeSpan? lifetime)
    {
        lock (_gate)
        {
            var id = new EndpointSessionId(_nextId++);
            var record = new Record { Handle = new EndpointSessionHandle(id, new EndpointSessionGeneration(1)), Caller = caller, Service = service, Capabilities = capabilities.ToArray(), Requirements = requirements.ToArray(), Channel = channel, ExpiresAt = lifetime is null ? null : _timeProvider.GetUtcNow() + lifetime.Value };
            _records.Add(id, record);
            record.State = EndpointSessionState.Active;
            return KernelResult<Record>.Ok(record);
        }
    }

    internal Record? ExpireIfDue(EndpointSessionHandle handle, ProcessHandle caller)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(handle.SessionId, out var record) || record.Handle.Generation != handle.Generation || record.Caller != caller || record.State != EndpointSessionState.Active || record.ExpiresAt is not { } expiresAt || _timeProvider.GetUtcNow() < expiresAt)
                return null;
            record.State = EndpointSessionState.Closed;
            return record;
        }
    }

    internal KernelResult<Record> Resolve(EndpointSessionHandle handle, ProcessHandle caller)
    {
        lock (_gate)
        {
        if (!_records.TryGetValue(handle.SessionId, out var record))
            return KernelResult<Record>.Fail(KernelError.SessionNotFound, "Endpoint session was not found.");
        if (record.Handle.Generation != handle.Generation)
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
        if (record.Handle.Generation != handle.Generation)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Endpoint session generation is stale.");
        if (record.Service != service) return KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Service does not own the endpoint session peer.");
        if (record.State != EndpointSessionState.Active) return KernelResult<Record>.Fail(KernelError.SessionClosed, "Endpoint session is not active.");
        return KernelResult<Record>.Ok(record);
        }
    }

    internal KernelResult<Record> Close(EndpointSessionHandle handle, ProcessHandle caller)
    {
        lock (_gate)
        {
            var resolved = Resolve(handle, caller);
            if (!resolved.IsSuccess) return resolved;
            if (resolved.Value!.ActivePins != 0)
            {
                resolved.Value.CloseRequested = true;
                resolved.Value.State = EndpointSessionState.Draining;
                return KernelResult<Record>.Fail(KernelError.SessionDraining, "Endpoint session has an admitted effect pin and is draining.");
            }
            resolved.Value.State = EndpointSessionState.Closed;
            return resolved;
        }
    }

    internal Record[] CloseForProcess(ProcessHandle process)
    {
        lock (_gate)
        {
            var records = _records.Values.Where(x => x.State != EndpointSessionState.Closed && (x.Caller == process || x.Service == process)).ToArray();
            foreach (var record in records)
            {
                if (record.ActivePins == 0) record.State = EndpointSessionState.Closed;
                else { record.CloseRequested = true; record.State = EndpointSessionState.Draining; }
            }
            return records;
        }
    }

    internal EndpointSessionDiagnosticSnapshot[] SnapshotForProcess(ProcessHandle process)
    {
        lock (_gate)
        {
            return _records.Values
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

    internal KernelResult<EndpointSessionPin> AcquirePin(
        EndpointSessionHandle handle, ProcessHandle caller, ProcessHandle service)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(handle.SessionId, out var record))
                return KernelResult<EndpointSessionPin>.Fail(KernelError.SessionNotFound, "Endpoint session was not found.");
            if (record.Handle != handle)
                return KernelResult<EndpointSessionPin>.Fail(KernelError.StaleGeneration, "Endpoint session generation is stale.");
            if (record.Caller != caller || record.Service != service)
                return KernelResult<EndpointSessionPin>.Fail(KernelError.WrongSessionOwner, "Endpoint session parties do not match the admission attempt.");
            if (record.State != EndpointSessionState.Active)
                return KernelResult<EndpointSessionPin>.Fail(KernelError.SessionClosed, "Endpoint session is not active.");
            checked { record.ActivePins++; }
            return KernelResult<EndpointSessionPin>.Ok(new EndpointSessionPin(this, handle, caller, service));
        }
    }

    internal KernelResult RevalidatePin(EndpointSessionPin pin)
    {
        lock (_gate)
            return _records.TryGetValue(pin.Handle.SessionId, out var record) &&
                   record.Handle == pin.Handle && record.Caller == pin.Caller && record.Service == pin.Service &&
                   record.ActivePins > 0 && record.State == EndpointSessionState.Active
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.SessionClosed, "Endpoint session pin is no longer current.");
    }

    internal void ReleasePin(EndpointSessionPin pin)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(pin.Handle.SessionId, out var record) || record.Handle != pin.Handle || record.ActivePins == 0)
                return;
            record.ActivePins--;
            if (record.ActivePins == 0 && record.CloseRequested) record.State = EndpointSessionState.Closed;
        }
    }

    internal int ActivePinCount(EndpointSessionHandle handle)
    {
        lock (_gate) return _records.TryGetValue(handle.SessionId, out var record) && record.Handle == handle
            ? record.ActivePins : 0;
    }
}

internal sealed class EndpointSessionPin : IDisposable
{
    private EndpointSessionRegistry? _owner;
    internal EndpointSessionPin(EndpointSessionRegistry owner, EndpointSessionHandle handle,
        ProcessHandle caller, ProcessHandle service) => (_owner, Handle, Caller, Service) = (owner, handle, caller, service);
    internal EndpointSessionHandle Handle { get; }
    internal ProcessHandle Caller { get; }
    internal ProcessHandle Service { get; }
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleasePin(this);
}
