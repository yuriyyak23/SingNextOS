using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

/// <summary>
/// Built-in emulator workloads. Each demo exercises one runtime subsystem end to end
/// and fails the boot stage (or shell command) when the authority graph misbehaves,
/// so they double as smoke tests of the emulated kernel.
/// </summary>
public static class DemoWorkloads
{
    public static KernelBootStageResult Run(EmulatorDemo demo, EmulatorSession session) => demo switch
    {
        EmulatorDemo.Ipc => RunIpc(session),
        EmulatorDemo.Regions => RunRegions(session),
        EmulatorDemo.Capabilities => RunCapabilities(session),
        EmulatorDemo.Lifecycle => RunLifecycle(session),
        _ => KernelBootStageResult.Failure($"Unknown demo workload '{demo}'."),
    };

    /// <summary>Channel creation, protocol-checked Ping/Tick exchange, Close transition.</summary>
    public static KernelBootStageResult RunIpc(EmulatorSession session)
    {
        if (session.Runtime is null) return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");
        var runtime = session.Runtime;

        var client = session.SpawnProcess("singplus.emulator.ipc.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!client.IsSuccess) return Failure("spawn client", client.Error, client.Message);
        var server = session.SpawnProcess("singplus.emulator.ipc.server", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!server.IsSuccess) return Failure("spawn server", server.Error, server.Message);
        EmulatorConsole.StageDetail($"client pid={client.Value.ProcessId.Value}, server pid={server.Value.ProcessId.Value}");

        var channel = runtime.CreateChannel(client.Value, server.Value, EmulatorSession.CreatePingPongProtocol(), capacity: 8);
        if (!channel.IsSuccess) return Failure("create channel", channel.Error, channel.Message);
        var (left, right) = channel.Value;
        session.Channels.Add(new ChannelRecord(
            left.ChannelId.Value, client.Value, server.Value, left, right, Capacity: 8));
        EmulatorConsole.StageDetail($"channel {left.ChannelId.Value} established (capacity 8)");

        var ping = runtime.Send(client.Value, server.Value, left, messageId: 1);
        if (!ping.IsSuccess) return Failure("send Ping", ping.Error, ping.Message);
        var pingReceived = runtime.Receive(server.Value, right);
        if (!pingReceived.IsSuccess || pingReceived.Value!.MessageId != 1)
            return KernelBootStageResult.Failure($"Ping delivery mismatch: {pingReceived.Message ?? "unexpected envelope"}.");
        EmulatorConsole.StageDetail($"ping seq={ping.Value!.Sequence} delivered to server");

        var tick = runtime.Send(server.Value, client.Value, right, messageId: 2, payload: 1337UL);
        if (!tick.IsSuccess) return Failure("send Tick", tick.Error, tick.Message);
        var tickReceived = runtime.Receive(client.Value, left);
        if (!tickReceived.IsSuccess || tickReceived.Value!.MessageId != 2 ||
            tickReceived.Value.Payload is not ulong tickValue || tickValue != 1337UL)
            return KernelBootStageResult.Failure("Tick delivery mismatch.");
        EmulatorConsole.StageDetail($"tick(1337) seq={tick.Value!.Sequence} delivered to client");

        var close = runtime.Send(client.Value, server.Value, left, messageId: 3);
        if (!close.IsSuccess) return Failure("send Close", close.Error, close.Message);
        var afterClose = runtime.Send(client.Value, server.Value, left, messageId: 1);
        if (afterClose.IsSuccess)
            return KernelBootStageResult.Failure("Protocol violation: Ping accepted after Close.");
        EmulatorConsole.StageDetail($"close seq={close.Value!.Sequence}; post-close ping rejected ({afterClose.Error})");

        return ReclaimPair(session, client.Value, server.Value);
    }

    /// <summary>Owned buffer allocation, pattern fill, ownership transfer, release.</summary>
    public static KernelBootStageResult RunRegions(EmulatorSession session)
    {
        if (session.Runtime is null) return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");
        if (session.SystemProcess is not { } system)
            return KernelBootStageResult.Failure("System process is unavailable.");
        var runtime = session.Runtime;

        var worker = session.SpawnProcess("singplus.emulator.regions.worker", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!worker.IsSuccess) return Failure("spawn worker", worker.Error, worker.Message);

        var buffer = runtime.AllocateBuffer<byte>(system, length: 4096);
        if (!buffer.IsSuccess) return Failure("allocate buffer", buffer.Error, buffer.Message);
        var owned = buffer.Value!;
        for (var index = 0; index < owned.Length; index++) owned.Span[index] = (byte)(index & 0xFF);
        var expected = Checksum(owned.Span);
        EmulatorConsole.StageDetail(
            $"allocated region {owned.Handle.RegionId.Value} (4096 bytes) for system process, checksum {expected}");

        var transferred = runtime.TransferRegion(system, worker.Value, owned);
        if (!transferred.IsSuccess) return Failure("transfer region", transferred.Error, transferred.Message);
        var moved = transferred.Value!;
        var observed = Checksum(moved.Span);
        if (observed != expected || owned.IsValid)
            return KernelBootStageResult.Failure("Ownership transfer corrupted the buffer or left the source token live.");
        EmulatorConsole.StageDetail(
            $"ownership moved to pid={worker.Value.ProcessId.Value} as region {moved.Handle.RegionId.Value}, checksum intact");

        var released = runtime.ReleaseRegion(worker.Value, moved);
        if (!released.IsSuccess) return Failure("release region", released.Error, released.Message);
        EmulatorConsole.StageDetail("region released by worker");

        var reclaimed = session.TerminateAndReclaim(worker.Value);
        return reclaimed.IsSuccess
            ? KernelBootStageResult.Success()
            : Failure("reclaim worker", reclaimed.Error, reclaimed.Message);
    }

    /// <summary>Capability mint, rights validation, delegation, revocation.</summary>
    public static KernelBootStageResult RunCapabilities(EmulatorSession session)
    {
        if (session.Runtime is null) return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");
        var runtime = session.Runtime;

        var auditor = session.SpawnProcess("singplus.emulator.caps.auditor", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!auditor.IsSuccess) return Failure("spawn auditor", auditor.Error, auditor.Message);
        var clerk = session.SpawnProcess("singplus.emulator.caps.clerk", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!clerk.IsSuccess) return Failure("spawn clerk", clerk.Error, clerk.Message);

        var resolvedAuditor = runtime.Processes.Resolve(auditor.Value);
        if (!resolvedAuditor.IsSuccess) return Failure("resolve auditor", resolvedAuditor.Error, resolvedAuditor.Message);
        var auditorDomain = resolvedAuditor.Value!.DomainId;

        var traceRead = runtime.MintCapability(
            auditorDomain, auditor.Value, ResourceKind.KernelService, CapabilityResourceIds.TraceInspection,
            CapabilityRights.Read);
        if (!traceRead.IsSuccess) return Failure("mint trace capability", traceRead.Error, traceRead.Message);
        EmulatorConsole.StageDetail($"minted trace-inspection read capability {traceRead.Value!.CapabilityId.Value} for auditor");

        var validated = runtime.ValidateCapability(auditor.Value, traceRead.Value.CapabilityId, CapabilityRights.Read);
        if (!validated.IsSuccess) return Failure("validate trace capability", validated.Error, validated.Message);

        var budgetCap = runtime.MintCapability(
            auditorDomain, auditor.Value, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure | CapabilityRights.Delegate);
        if (!budgetCap.IsSuccess) return Failure("mint budget capability", budgetCap.Error, budgetCap.Message);

        var wrongRights = runtime.ValidateCapability(auditor.Value, traceRead.Value.CapabilityId, CapabilityRights.Configure);
        if (wrongRights.IsSuccess)
            return KernelBootStageResult.Failure("Rights escalation: read-only capability satisfied a Configure check.");
        EmulatorConsole.StageDetail($"read-only capability correctly refused Configure ({wrongRights.Error})");

        var delegated = runtime.DelegateCapability(
            auditor.Value, clerk.Value, budgetCap.Value!.CapabilityId, CapabilityRights.Configure);
        if (!delegated.IsSuccess) return Failure("delegate budget capability", delegated.Error, delegated.Message);
        var clerkValidated = runtime.ValidateCapability(clerk.Value, delegated.Value!.CapabilityId, CapabilityRights.Configure);
        if (!clerkValidated.IsSuccess)
            return Failure("validate delegated capability", clerkValidated.Error, clerkValidated.Message);
        EmulatorConsole.StageDetail($"budget capability delegated to clerk as {delegated.Value.CapabilityId.Value}");

        var revoked = runtime.RevokeCapability(traceRead.Value.CapabilityId);
        if (!revoked.IsSuccess) return Failure("revoke trace capability", revoked.Error, revoked.Message);
        var afterRevoke = runtime.ValidateCapability(auditor.Value, traceRead.Value.CapabilityId, CapabilityRights.Read);
        if (afterRevoke.IsSuccess)
            return KernelBootStageResult.Failure("Revoked capability still validates.");
        EmulatorConsole.StageDetail($"capability {traceRead.Value.CapabilityId.Value} revoked; validation now fails ({afterRevoke.Error})");

        return ReclaimPair(session, auditor.Value, clerk.Value);
    }

    /// <summary>Full lifecycle walk: spawn, park, resume, terminate, terminal receipt.</summary>
    public static KernelBootStageResult RunLifecycle(EmulatorSession session)
    {
        if (session.Runtime is null) return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");
        var runtime = session.Runtime;

        var spawned = session.SpawnProcess("singplus.emulator.lifecycle.subject", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!spawned.IsSuccess) return Failure("spawn subject", spawned.Error, spawned.Message);
        var handle = spawned.Value;
        EmulatorConsole.StageDetail($"subject pid={handle.ProcessId.Value} running");

        var parked = runtime.ParkProcess(handle);
        if (!parked.IsSuccess) return Failure("park", parked.Error, parked.Message);
        if (!CheckState(runtime, handle, ProcessState.Parked, out var stateError)) return KernelBootStageResult.Failure(stateError!);
        EmulatorConsole.StageDetail("subject parked");

        // A parked process must refuse new effects.
        var effectWhileParked = runtime.AllocateBuffer<byte>(handle, 16);
        if (effectWhileParked.IsSuccess)
            return KernelBootStageResult.Failure("Parked process accepted a new region effect.");
        EmulatorConsole.StageDetail($"parked process refused new effects ({effectWhileParked.Error})");

        var resumed = runtime.ResumeProcess(handle);
        if (!resumed.IsSuccess) return Failure("resume", resumed.Error, resumed.Message);
        if (!CheckState(runtime, handle, ProcessState.Running, out stateError)) return KernelBootStageResult.Failure(stateError!);
        EmulatorConsole.StageDetail("subject resumed");

        var reclaimed = session.TerminateAndReclaim(handle);
        if (!reclaimed.IsSuccess) return Failure("terminate", reclaimed.Error, reclaimed.Message);

        var terminal = runtime.Processes.ResolveTerminalState(handle);
        if (!terminal.IsSuccess || terminal.Value != ProcessState.Exited)
            return KernelBootStageResult.Failure("Terminal receipt for the exited generation is missing.");
        EmulatorConsole.StageDetail("subject exited; terminal receipt recorded");
        return KernelBootStageResult.Success();
    }

    private static KernelBootStageResult ReclaimPair(EmulatorSession session, ProcessHandle first, ProcessHandle second)
    {
        var firstResult = session.TerminateAndReclaim(first);
        if (!firstResult.IsSuccess) return Failure("reclaim first process", firstResult.Error, firstResult.Message);
        var secondResult = session.TerminateAndReclaim(second);
        if (!secondResult.IsSuccess) return Failure("reclaim second process", secondResult.Error, secondResult.Message);
        EmulatorConsole.StageDetail("demo processes reclaimed");
        return KernelBootStageResult.Success();
    }

    private static bool CheckState(RuntimeKernel runtime, ProcessHandle handle, ProcessState expected, out string? error)
    {
        var resolved = runtime.Processes.Resolve(handle);
        if (!resolved.IsSuccess)
        {
            error = $"Process resolution failed: {resolved.Message}";
            return false;
        }
        if (resolved.Value!.State != expected)
        {
            error = $"Expected state {expected}, observed {resolved.Value.State}.";
            return false;
        }
        error = null;
        return true;
    }

    private static ulong Checksum(ReadOnlySpan<byte> bytes)
    {
        ulong sum = 0;
        foreach (var value in bytes) sum = unchecked(sum * 31 + value);
        return sum;
    }

    private static KernelBootStageResult Failure(string operation, KernelError error, string? message) =>
        KernelBootStageResult.Failure($"Demo step '{operation}' failed ({error}): {message ?? "no detail"}.");
}
