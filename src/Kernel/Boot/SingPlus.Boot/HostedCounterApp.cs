using SingPlus.Boot.Samples;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot;

// Host-owned cooperative application. All authority, requests and responses go
// through the runtime; the generated implementation runs on the host CLR.
internal sealed class HostedCounterApp(
    ProcessHandle caller, ProcessHandle server, EndpointSessionHandle handle, CapabilityId capability)
{
    public ProcessHandle Caller { get; } = caller;
    public ProcessHandle Server { get; } = server;
    public EndpointSessionHandle Session { get; } = handle;
    public CapabilityId AdmissionCapability { get; } = capability;
    public int Calls { get; private set; }
    public int? LastResult { get; private set; }
    public bool Closing { get; private set; }
    private bool _sessionClosed;
    private bool _callerReclaimed;
    private bool _serverReclaimed;

    public static KernelResult<HostedCounterApp> Start(EmulatorSession session, string name)
    {
        if (session.Runtime is not { } runtime)
            return KernelResult<HostedCounterApp>.Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || session.HostedCounters.ContainsKey(name))
            return KernelResult<HostedCounterApp>.Fail(KernelError.InvalidTransition, "Application name must be unique and contain 1..128 characters.");
        var caller = session.SpawnProcess($"singplus.hosted.counter.client:{name}", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!caller.IsSuccess) return KernelResult<HostedCounterApp>.Fail(caller.Error, caller.Message!);
        ProcessHandle? serverHandle = null;
        var server = session.SpawnProcess($"singplus.hosted.counter.server:{name}", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!server.IsSuccess) return Failed(server.Error, server.Message!);
        serverHandle = server.Value;
        if (session.Options.TraceEnabled)
        {
            foreach (var owner in new[] { caller.Value, server.Value })
            {
                var trace = session.StartTrace(owner, session.Options.TraceCapacity);
                if (!trace.IsSuccess) return Failed(trace.Error, trace.Message!);
            }
        }
        var protocol = CounterBindings.Protocol;
        var resource = $"singplus.hosted.counter:{server.Value.ProcessId.Value}:{name}";
        var registered = runtime.RegisterService(server.Value, resource,
            new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest),
            protocol, CounterBindings.Responses,
            [new CapabilityRequirementV1(ResourceKind.KernelService, resource, CapabilityRights.Read)]);
        if (!registered.IsSuccess) return Failed(registered.Error, registered.Message!);
        var resolved = runtime.Processes.Resolve(caller.Value);
        if (!resolved.IsSuccess) return Failed(resolved.Error, resolved.Message!);
        var granted = runtime.MintCapability(resolved.Value!.DomainId, caller.Value,
            ResourceKind.KernelService, resource, CapabilityRights.Read);
        if (!granted.IsSuccess) return Failed(granted.Error, granted.Message!);
        var opened = runtime.OpenSession(caller.Value, registered.Value, [granted.Value!.CapabilityId]);
        if (!opened.IsSuccess) return Failed(opened.Error, opened.Message!);
        var app = new HostedCounterApp(caller.Value, server.Value, opened.Value, granted.Value.CapabilityId);
        session.HostedCounters.Add(name, app);
        return KernelResult<HostedCounterApp>.Ok(app);

        KernelResult<HostedCounterApp> Failed(KernelError error, string message)
        {
            foreach (var owner in serverHandle is { } knownServer ? new[] { caller.Value, knownServer } : new[] { caller.Value })
            {
                var reclaimed = session.TerminateAndReclaim(owner);
                if (!reclaimed.IsSuccess)
                    message += $" Cleanup for pid={owner.ProcessId.Value} incomplete: {reclaimed.Error}: {reclaimed.Message}; retained for shutdown.";
            }
            return KernelResult<HostedCounterApp>.Fail(error, message);
        }
    }

    public KernelResult<int> Invoke(EmulatorSession session, int value)
    {
        if (Closing) return KernelResult<int>.Fail(KernelError.InvalidTransition, "Application is closing; retry counter-stop.");
        if (value == int.MaxValue) return KernelResult<int>.Fail(KernelError.InvalidMessage, "Increment would overflow Int32.");
        if (session.Runtime is not { } runtime)
            return KernelResult<int>.Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        var authority = runtime.ValidateCapability(Caller, AdmissionCapability, CapabilityRights.Read);
        if (!authority.IsSuccess) return KernelResult<int>.Fail(authority.Error, authority.Message!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new CopiedCounterPump(runtime, Caller, Server, Session, timeout.Token);
        try
        {
            var response = CounterBindings.CreateClient(transport).Increment(new CounterValue(value));
            if (response.Value != value + 1 || transport.DispatchCount != 1)
                return KernelResult<int>.Fail(KernelError.InvalidMessage, "Generated response or dispatch count mismatch.");
            Calls++;
            LastResult = response.Value;
            return KernelResult<int>.Ok(response.Value);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            return KernelResult<int>.Fail(KernelError.InvalidTransition, $"Hosted invocation failed: {exception.Message}");
        }
    }

    public KernelResult Stop(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime)
            return KernelResult.Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        Closing = true;
        // Pids leave this set only after verified terminal reclaim, which also
        // closes sessions owned by either participant.
        if (!session.EmulatorSpawnedPids.Contains(Caller.ProcessId.Value))
        {
            _callerReclaimed = true;
            _sessionClosed = true;
        }
        if (!session.EmulatorSpawnedPids.Contains(Server.ProcessId.Value))
        {
            _serverReclaimed = true;
            _sessionClosed = true;
        }
        if (!_sessionClosed)
        {
            var closed = runtime.CloseSession(Caller, Session);
            if (!closed.IsSuccess) return closed;
            _sessionClosed = true;
        }
        if (!_callerReclaimed)
        {
            var reclaimed = session.TerminateAndReclaim(Caller);
            if (!reclaimed.IsSuccess) return reclaimed;
            _callerReclaimed = true;
        }
        if (!_serverReclaimed)
        {
            var reclaimed = session.TerminateAndReclaim(Server);
            if (!reclaimed.IsSuccess) return reclaimed;
            _serverReclaimed = true;
        }
        return KernelResult.Ok();
    }
}