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
            resolved.Value!.State = EndpointSessionState.Closed;
            return resolved;
        }
    }

    internal Record[] CloseForProcess(ProcessHandle process)
    {
        lock (_gate)
        {
            var records = _records.Values.Where(x => x.State != EndpointSessionState.Closed && (x.Caller == process || x.Service == process)).ToArray();
            foreach (var record in records) record.State = EndpointSessionState.Closed;
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
}
