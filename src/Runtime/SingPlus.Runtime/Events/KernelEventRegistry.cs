using SingPlus.Contracts;

namespace SingPlus.Runtime;

public readonly record struct KernelEventEndpointId(ulong Value);
public readonly record struct KernelEventEndpointGeneration(ulong Value);
public readonly record struct KernelEventSequence(ulong Value);

public enum KernelEventClass
{
    ExternalSignal = 0,
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
/// Each endpoint intentionally admits at most one pending event in this first slice.
/// </summary>
internal sealed class KernelEventRegistry
{
    private sealed class EndpointRecord(KernelEventEndpoint endpoint)
    {
        public KernelEventEndpoint Endpoint { get; } = endpoint;
        public KernelEvent? Pending { get; set; }
        public bool Closed { get; set; }
    }

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

    public KernelResult Validate(ProcessHandle owner, KernelEventEndpoint endpoint)
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

    public KernelResult<KernelEvent> Publish(
        KernelEventEndpoint endpoint,
        KernelEventClass eventClass,
        string sourceResourceId)
    {
        var validation = Validate(endpoint.Owner, endpoint);
        if (!validation.IsSuccess)
        {
            return KernelResult<KernelEvent>.Fail(
                validation.Error,
                validation.Message!);
        }

        if (!Enum.IsDefined(eventClass) || string.IsNullOrWhiteSpace(sourceResourceId))
        {
            return KernelResult<KernelEvent>.Fail(
                KernelError.InvalidMessage,
                "Kernel events require a defined class and semantic source identity.");
        }

        var record = _endpoints[endpoint.EndpointId];
        if (record.Pending is not null)
        {
            return KernelResult<KernelEvent>.Fail(
                KernelError.CapacityExhausted,
                "The kernel event endpoint already has a pending event.");
        }

        try
        {
            var @event = new KernelEvent(
                endpoint,
                new KernelEventSequence(NextNonZero(ref _nextEventSequence)),
                eventClass,
                sourceResourceId);
            record.Pending = @event;
            return KernelResult<KernelEvent>.Ok(@event);
        }
        catch (Exception)
        {
            return KernelResult<KernelEvent>.Fail(
                KernelError.CapacityExhausted,
                "Kernel event sequence allocation failed.");
        }
    }

    public KernelResult<KernelEvent> Consume(
        ProcessHandle owner,
        KernelEventEndpoint endpoint)
    {
        var validation = Validate(owner, endpoint);
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
                "No kernel event is pending on the exact endpoint.");
        }

        record.Pending = null;
        return KernelResult<KernelEvent>.Ok(pending);
    }

    public KernelResult Close(
        ProcessHandle owner,
        KernelEventEndpoint endpoint)
    {
        var validation = Validate(owner, endpoint);
        if (!validation.IsSuccess) return validation;

        var record = _endpoints[endpoint.EndpointId];
        record.Pending = null;
        record.Closed = true;
        return KernelResult.Ok();
    }

    public void CloseAllForProcess(ProcessHandle owner)
    {
        foreach (var record in _endpoints.Values)
        {
            if (!record.Closed && record.Endpoint.Owner == owner)
            {
                record.Pending = null;
                record.Closed = true;
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
