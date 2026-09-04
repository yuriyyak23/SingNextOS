using SingPlus.Contracts;

namespace SingPlus.Runtime;

public readonly record struct KernelEventEndpointId(ulong Value);
public readonly record struct KernelEventEndpointGeneration(ulong Value);
public readonly record struct KernelEventSequence(ulong Value);

public enum KernelEventClass
{
    ExternalSignal = 0,
    Completion = 1,
}

public readonly record struct KernelEventEndpoint(
    KernelEventEndpointId EndpointId,
    KernelEventEndpointGeneration Generation,
    ProcessHandle Owner);

public readonly record struct KernelEvent(
    KernelEventEndpoint Endpoint,
    KernelEventSequence Sequence,
    KernelEventClass EventClass,
    string SourceResourceId);

/// <summary>
/// Small policy-neutral process-bound event mailbox. The registry owns only local
/// event delivery lifetime; platform/provider identities never enter an endpoint or event.
/// Each endpoint intentionally admits at most one staged or committed event. Staged
/// reservations are invisible to consumers until the exact source path commits them.
/// </summary>
internal sealed class KernelEventRegistry
{
    private const int MaximumSourceResourceIdLength = 128;

    private sealed class EndpointRecord(KernelEventEndpoint endpoint)
    {
        public KernelEventEndpoint Endpoint { get; } = endpoint;
        public KernelEvent? Staged { get; set; }
        public KernelEvent? Pending { get; set; }
        public bool Closed { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<KernelEventEndpointId, EndpointRecord> _endpoints = [];
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
                var endpoint = new KernelEventEndpoint(
                    new KernelEventEndpointId(NextNonZero(ref _nextEndpointId)),
                    new KernelEventEndpointGeneration(1),
                    owner);
                _endpoints.Add(endpoint.EndpointId, new EndpointRecord(endpoint));
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
    {
        lock (_gate)
        {
            var validation = ValidateLocked(owner, endpoint);
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

            var record = _endpoints[endpoint.EndpointId];
            if (record.Staged is not null || record.Pending is not null)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.CapacityExhausted,
                    "The kernel event endpoint already has a reserved or pending event.");
            }

            try
            {
                var @event = new KernelEvent(
                    endpoint,
                    new KernelEventSequence(NextNonZero(ref _nextEventSequence)),
                    eventClass,
                    sourceResourceId);
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

    public KernelResult<KernelEvent> CommitExact(
        ProcessHandle owner,
        KernelEvent staged)
    {
        lock (_gate)
        {
            var validation = ValidateLocked(owner, staged.Endpoint);
            if (!validation.IsSuccess)
            {
                return KernelResult<KernelEvent>.Fail(
                    validation.Error,
                    validation.Message!);
            }

            var record = _endpoints[staged.Endpoint.EndpointId];
            if (record.Staged is not { } exact || exact != staged || record.Pending is not null)
            {
                return KernelResult<KernelEvent>.Fail(
                    KernelError.PlatformFaulted,
                    "The exact staged kernel event is no longer the endpoint's publication reservation.");
            }

            record.Staged = null;
            record.Pending = staged;
            return KernelResult<KernelEvent>.Ok(staged);
        }
    }

    public KernelResult RollbackExact(
        ProcessHandle owner,
        KernelEvent staged)
    {
        lock (_gate)
        {
            var validation = ValidateLocked(owner, staged.Endpoint);
            if (!validation.IsSuccess) return validation;

            var record = _endpoints[staged.Endpoint.EndpointId];
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
        if (!_endpoints.TryGetValue(endpoint.EndpointId, out var record))
        {
            return KernelResult.Fail(
                KernelError.EndpointNotFound,
                "The kernel event endpoint does not exist.");
        }

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

        if (record.Closed)
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
            var validation = ValidateLocked(owner, endpoint);
            if (!validation.IsSuccess)
            {
                return KernelResult<KernelEvent>.Fail(
                    validation.Error,
                    validation.Message!);
            }

            var record = _endpoints[endpoint.EndpointId];
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

    public KernelResult Close(
        ProcessHandle owner,
        KernelEventEndpoint endpoint)
    {
        lock (_gate)
        {
            var validation = ValidateLocked(owner, endpoint);
            if (!validation.IsSuccess) return validation;

            var record = _endpoints[endpoint.EndpointId];
            if (record.Staged is not null)
            {
                return KernelResult.Fail(
                    KernelError.PlatformBindingDraining,
                    "The kernel event endpoint has an in-flight publication reservation.");
            }

            record.Pending = null;
            record.Closed = true;
            return KernelResult.Ok();
        }
    }

    public void CloseAllForProcess(ProcessHandle owner)
    {
        lock (_gate)
        {
            foreach (var record in _endpoints.Values)
            {
                if (!record.Closed && record.Endpoint.Owner == owner)
                {
                    record.Staged = null;
                    record.Pending = null;
                    record.Closed = true;
                }
            }
        }
    }

    private static ulong NextNonZero(ref ulong next)
    {
        var value = next;
        unchecked { next++; }
        if (value == 0)
            throw new InvalidOperationException("Kernel event identity space is exhausted.");
        return value;
    }
}
