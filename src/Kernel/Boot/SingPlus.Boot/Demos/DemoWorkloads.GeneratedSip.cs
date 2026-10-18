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

}
