namespace SingPlus.Contracts;

public readonly record struct ServiceId(ulong Value);
public readonly record struct ServiceGeneration(ulong Value);
public readonly record struct EndpointSessionId(ulong Value);
public readonly record struct EndpointSessionGeneration(ulong Value);
public readonly record struct EndpointSessionInvocationId(ulong Value);
public readonly record struct EndpointSessionInvocationGeneration(ulong Value);

public readonly record struct ServiceIdentity(ServiceId Id, string Name);
public readonly record struct ServiceContractIdentity(string Name, string Version, string Digest);

public enum ServiceAvailability
{
    Accepting = 0,
    Draining = 1,
    Unavailable = 2
}

public enum EndpointSessionState
{
    Opening = 0,
    Active = 1,
    Draining = 2,
    Closed = 3,
    Faulted = 4
}

public readonly record struct ServiceEndpointDescriptor(
    ServiceIdentity Service,
    ServiceGeneration Generation,
    ServiceContractIdentity Contract,
    string DiscoveryHint,
    ServiceAvailability Availability);

public readonly record struct EndpointSessionHandle(
    EndpointSessionId SessionId,
    EndpointSessionGeneration Generation);

public readonly record struct EndpointSessionInvocationHandle(
    EndpointSessionHandle Session,
    EndpointSessionInvocationId InvocationId,
    EndpointSessionInvocationGeneration Generation);

public sealed record EndpointSessionRequestEnvelope(
    EndpointSessionInvocationHandle Invocation,
    ChannelEnvelope Request,
    bool CancellationRequested);
