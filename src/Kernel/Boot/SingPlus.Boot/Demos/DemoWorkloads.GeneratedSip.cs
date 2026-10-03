using SingPlus.Boot.Diagnostics;
using SingPlus.Boot.Samples;
using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunGeneratedSip(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime) return KernelBootStageResult.Failure("Runtime is unavailable.");
        var caller = session.SpawnProcess("singplus.emulator.generated.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!caller.IsSuccess) return Failure("spawn caller", caller.Error, caller.Message);
        var server = session.SpawnProcess("singplus.emulator.generated.server", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!server.IsSuccess) return Failure("spawn server", server.Error, server.Message);
        if (session.Options.TraceEnabled)
        {
            var callerTrace = session.StartTrace(caller.Value, session.Options.TraceCapacity);
            if (!callerTrace.IsSuccess) return Failure("start generated caller trace", callerTrace.Error, callerTrace.Message);
            var serverTrace = session.StartTrace(server.Value, session.Options.TraceCapacity);
            if (!serverTrace.IsSuccess) return Failure("start generated server trace", serverTrace.Error, serverTrace.Message);
        }
        var protocol = CounterBindings.Protocol;
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var resource = $"singplus.emulator.generated:{server.Value.ProcessId.Value}";
        var registered = runtime.RegisterService(server.Value, resource, contract, protocol, CounterBindings.Responses,
            [new CapabilityRequirementV1(ResourceKind.KernelService, resource, CapabilityRights.Read)]);
        if (!registered.IsSuccess) return Failure("register generated service", registered.Error, registered.Message);
        var process = runtime.Processes.Resolve(caller.Value);
        if (!process.IsSuccess) return Failure("resolve caller", process.Error, process.Message);
        var granted = runtime.MintCapability(process.Value!.DomainId, caller.Value, ResourceKind.KernelService, resource, CapabilityRights.Read);
        if (!granted.IsSuccess) return Failure("mint", granted.Error, granted.Message);
        var opened = runtime.OpenSession(caller.Value, registered.Value, [granted.Value!.CapabilityId]);
        if (!opened.IsSuccess) return Failure("open session", opened.Error, opened.Message);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new CopiedCounterPump(runtime, caller.Value, server.Value, opened.Value, timeout.Token);
        var client = CounterBindings.CreateClient(transport);
        try
        {
            var answer = client.Increment(new CounterValue(41));
            if (answer.Value != 42 || transport.DispatchCount != 1)
                return KernelBootStageResult.Failure("Generated client/dispatcher response mismatch.");
            transport.CancelNextInvocation = true;
            var cancelled = transport.Invoke(CounterBindings.IncrementMessageId, new CounterValue(99));
            if (cancelled.Status != ResponsePublicationStatus.Cancelled || cancelled.Payload is not null || transport.DispatchCount != 1)
                return KernelBootStageResult.Failure("Cancelled invocation entered implementation or published output.");
            transport.RequestLateCancellation = true;
            var resumed = client.Increment(new CounterValue(10));
            if (resumed.Value != 11 || transport.DispatchCount != 2)
                return KernelBootStageResult.Failure("Session did not remain usable after settled cancellation.");
            EmulatorConsole.StageDetail("runtime scope cancellation before acceptance: Cancelled, no payload, no dispatcher entry; next generated call: 10 -> 11");
            EmulatorConsole.StageDetail("late scope cancellation after non-cancellable acceptance: TooLateEffectMayExist; real response publication remains permitted");
            var revoked = runtime.RevokeCapability(granted.Value.CapabilityId);
            if (!revoked.IsSuccess) return Failure("revoke admission capability", revoked.Error, revoked.Message);
            try
            {
                _ = client.Increment(new CounterValue(99));
                return KernelBootStageResult.Failure("Generated invocation accepted a revoked admission capability.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("CapabilityRevoked", StringComparison.Ordinal))
            { EmulatorConsole.StageDetail("generated invocation rejected revoked authority before dispatcher entry (CapabilityRevoked)"); }
            if (transport.DispatchCount != 2) return KernelBootStageResult.Failure("Rejected call entered generated implementation.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        { return KernelBootStageResult.Failure($"Generated SIP workload failed: {exception.Message}"); }
        var closed = runtime.CloseSession(caller.Value, opened.Value);
        if (!closed.IsSuccess) return Failure("close", closed.Error, closed.Message);
        EmulatorConsole.StageDetail("actual generated client -> RuntimeSipClientTransport -> EndpointSession -> accepted invocation -> generated copied-value dispatcher -> runtime response: 41 -> 42");
        EmulatorConsole.StageDetail("trusted host workload; no admitted CLR image execution or resource-operation sentry claimed");
        var reclaimed = ReclaimPair(session, caller.Value, server.Value);
        if (!reclaimed.IsSuccess || !session.Options.TraceEnabled) return reclaimed;
        EmulatorConsole.StageDetail("generated SIP runtime traces captured before teardown; session dispatch/publication have no dedicated runtime trace events");
        foreach (var owner in new[] { caller.Value, server.Value })
        {
            EmulatorConsole.StageDetail($"generated SIP trace owner {owner.ProcessId.Value}");
            if (!RuntimeInspector.WriteTraceEvents(session, owner.ProcessId.Value, session.Options.TraceCapacity))
                return KernelBootStageResult.Failure("Generated SIP trace snapshot is unavailable.");
        }
        return KernelBootStageResult.Success();
    }

    // Cooperatively pumps this bounded copied-value service after the real runtime
    // transport enqueues its request. Every envelope and response remains runtime-owned.
    private sealed class CopiedCounterPump(RuntimeKernel runtime, ProcessHandle caller, ProcessHandle server,
        EndpointSessionHandle session, CancellationToken cancellation) : ISipClientRuntimeTransport
    {
        private readonly RuntimeSipClientTransport _transport = new(runtime, caller, session, cancellation);
        public int DispatchCount { get; private set; }
        public bool CancelNextInvocation { get; set; }
        public bool RequestLateCancellation { get; set; }
        public ResponseEnvelope Invoke(uint messageId, object? payload = null) => InvokeAsync(messageId, payload).AsTask().GetAwaiter().GetResult();
        public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? payload = null) => InvokeAsync(messageId, payload, cancellation);
        public async ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? payload, CancellationToken token)
        {
            var response = _transport.InvokeAsync(messageId, payload, token);
            if (!response.IsCompleted)
            {
                var received = runtime.ReceiveSessionRequest(server, session);
                if (!received.IsSuccess) throw new InvalidOperationException($"Receive failed: {received.Error}: {received.Message}");
                if (CancelNextInvocation)
                {
                    CancelNextInvocation = false;
                    var scope = runtime.CreateCancellationScope(caller);
                    if (!scope.IsSuccess) throw new InvalidOperationException($"Scope failed: {scope.Error}: {scope.Message}");
                    var bound = runtime.BindSessionCancellationScope(caller, received.Value!.Invocation, scope.Value!.Scope);
                    if (!bound.IsSuccess) throw new InvalidOperationException($"Scope binding failed: {bound.Error}: {bound.Message}");
                    var requested = runtime.RequestSessionCancellation(caller, received.Value.Invocation, scope.Value.Scope);
                    if (!requested.IsSuccess) throw new InvalidOperationException($"Cancellation failed: {requested.Error}: {requested.Message}");
                    var refused = runtime.AcceptSessionInvocation(server, received.Value.Invocation, allowInFlightCancellation: false);
                    if (refused.IsSuccess || refused.Error != KernelError.InvalidTransition)
                        throw new InvalidOperationException("Cancelled invocation was not refused before acceptance.");
                    var settled = runtime.AcceptSessionCancellation(server, received.Value.Invocation);
                    if (!settled.IsSuccess) throw new InvalidOperationException($"Cancellation acceptance failed: {settled.Error}: {settled.Message}");
                    var cancelled = runtime.CancelSessionResponse(server, received.Value.Invocation);
                    if (!cancelled.IsSuccess) throw new InvalidOperationException($"Cancellation settlement failed: {cancelled.Error}: {cancelled.Message}");
                    return await response.ConfigureAwait(false);
                }
                var accepted = runtime.AcceptSessionInvocation(server, received.Value!.Invocation, allowInFlightCancellation: false);
                if (!accepted.IsSuccess) throw new InvalidOperationException($"Accept failed: {accepted.Error}: {accepted.Message}");
                if (RequestLateCancellation)
                {
                    RequestLateCancellation = false;
                    var scope = runtime.CreateCancellationScope(caller);
                    if (!scope.IsSuccess) throw new InvalidOperationException($"Late scope failed: {scope.Error}: {scope.Message}");
                    var bound = runtime.BindSessionCancellationScope(caller, received.Value.Invocation, scope.Value!.Scope);
                    if (!bound.IsSuccess) throw new InvalidOperationException($"Late scope binding failed: {bound.Error}: {bound.Message}");
                    var requested = runtime.RequestSessionCancellation(caller, received.Value.Invocation, scope.Value.Scope);
                    if (!requested.IsSuccess) throw new InvalidOperationException($"Late cancellation failed: {requested.Error}: {requested.Message}");
                    if (requested.Value!.Disposition != CancellationDisposition.TooLateEffectMayExist)
                        throw new InvalidOperationException($"Unexpected late cancellation disposition: {requested.Value.Disposition}");
                    var observed = runtime.QuerySessionCancellation(server, received.Value.Invocation);
                    if (!observed.IsSuccess || observed.Value)
                        throw new InvalidOperationException("Non-cancellable acceptance did not win the late cancellation race.");
                }
                if (received.Value.Request.MessageId != CounterBindings.IncrementMessageId || received.Value.Request.Payload is not CounterValue request)
                    throw new InvalidOperationException("Generated counter request shape mismatch.");
                var answer = CounterBindings.Dispatch(request);
                DispatchCount++;
                var published = runtime.PublishSessionResponse(server, received.Value.Invocation, answer);
                if (!published.IsSuccess) throw new InvalidOperationException($"Publication failed: {published.Error}: {published.Message}");
            }
            return await response.ConfigureAwait(false);
        }
        public ResponseEnvelope InvokeOwnershipPair(uint id, object first, object second) => throw new NotSupportedException("Counter contract has no ownership-pair messages.");
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint id, object first, object second) => throw new NotSupportedException("Counter contract has no ownership-pair messages.");
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint id, object first, object second, CancellationToken token) => throw new NotSupportedException("Counter contract has no ownership-pair messages.");
    }
}
