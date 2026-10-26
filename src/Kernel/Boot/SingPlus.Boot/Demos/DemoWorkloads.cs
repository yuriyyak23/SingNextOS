using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

/// <summary>
/// Built-in emulator workloads. Each demo exercises one runtime subsystem end to end
/// and fails the boot stage (or shell command) when the authority graph misbehaves,
/// so they double as smoke tests of the emulated kernel.
/// </summary>
public static partial class DemoWorkloads
{
    public static KernelBootStageResult Run(EmulatorDemo demo, EmulatorSession session) => demo switch
    {
        EmulatorDemo.Ipc => RunIpc(session),
        EmulatorDemo.Regions => RunRegions(session),
        EmulatorDemo.Capabilities => RunCapabilities(session),
        EmulatorDemo.Lifecycle => RunLifecycle(session),
        EmulatorDemo.Borrows => RunBorrows(session),
        EmulatorDemo.Platform => RunPlatform(session),
        EmulatorDemo.Compute => RunCompute(session),
        EmulatorDemo.ComputeCancel => RunCompute(session, cancel: true),
        EmulatorDemo.ComputeFault => RunCompute(session, fault: true),
        EmulatorDemo.PlatformRegions => RunPlatformRegions(session),
        EmulatorDemo.External => RunExternal(session),
        EmulatorDemo.Sessions => RunSessions(session),
        EmulatorDemo.ChildExecute => RunChildDomain(session, execute: true),
        EmulatorDemo.ChildDomain => RunChildDomain(session),
        EmulatorDemo.VirtualDomain => RunVirtualDomain(session),
        EmulatorDemo.Backing => RunBacking(session),
        EmulatorDemo.GeneratedSip => RunGeneratedSip(session),
        EmulatorDemo.HostedService => RunHostedService(session),
        EmulatorDemo.ServiceRestart => RunServiceRestart(session),
        EmulatorDemo.SupervisedRestart => RunSupervisedRestart(session),
        _ => KernelBootStageResult.Failure($"Unknown demo workload '{demo}'."),
    };

    /// <summary>Channel creation, protocol-checked Ping/Tick exchange, Close transition.</summary>
    public static KernelBootStageResult RunIpc(EmulatorSession session)
    {
        if (session.Runtime is null) return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");
        RuntimeKernel runtime = session.Runtime;

        KernelResult<ProcessHandle> client = session.SpawnProcess("singplus.emulator.ipc.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!client.IsSuccess) return Failure("spawn client", client.Error, client.Message);
        KernelResult<ProcessHandle> server = session.SpawnProcess("singplus.emulator.ipc.server", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!server.IsSuccess) return Failure("spawn server", server.Error, server.Message);
        EmulatorConsole.StageDetail($"client pid={client.Value.ProcessId.Value}, server pid={server.Value.ProcessId.Value}");

        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = runtime.CreateChannel(client.Value, server.Value, EmulatorSession.CreatePingPongProtocol(), capacity: 8);
        if (!channel.IsSuccess) return Failure("create channel", channel.Error, channel.Message);
        (ChannelEndpointHandle left, ChannelEndpointHandle right) = channel.Value;
        session.Channels.Add(new ChannelRecord(
            left.ChannelId.Value, client.Value, server.Value, left, right, Capacity: 8));
        EmulatorConsole.StageDetail($"channel {left.ChannelId.Value} established (capacity 8)");

        KernelResult<ChannelEnvelope> ping = runtime.Send(client.Value, server.Value, left, messageId: 1);
        if (!ping.IsSuccess) return Failure("send Ping", ping.Error, ping.Message);
        KernelResult<ChannelEnvelope> pingReceived = runtime.Receive(server.Value, right);
        if (!pingReceived.IsSuccess || pingReceived.Value!.MessageId != 1)
            return KernelBootStageResult.Failure($"Ping delivery mismatch: {pingReceived.Message ?? "unexpected envelope"}.");
        EmulatorConsole.StageDetail($"ping seq={ping.Value!.Sequence} delivered to server");

        KernelResult<ChannelEnvelope> tick = runtime.Send(server.Value, client.Value, right, messageId: 2, payload: 1337UL);
        if (!tick.IsSuccess) return Failure("send Tick", tick.Error, tick.Message);
        KernelResult<ChannelEnvelope> tickReceived = runtime.Receive(client.Value, left);
        if (!tickReceived.IsSuccess || tickReceived.Value!.MessageId != 2 ||
            tickReceived.Value.Payload is not ulong tickValue || tickValue != 1337UL)
            return KernelBootStageResult.Failure("Tick delivery mismatch.");
        EmulatorConsole.StageDetail($"tick(1337) seq={tick.Value!.Sequence} delivered to client");

        KernelResult<ChannelEnvelope> close = runtime.Send(client.Value, server.Value, left, messageId: 3);
        if (!close.IsSuccess) return Failure("send Close", close.Error, close.Message);
        KernelResult<ChannelEnvelope> afterClose = runtime.Send(client.Value, server.Value, left, messageId: 1);
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
        RuntimeKernel runtime = session.Runtime;

        KernelResult<ProcessHandle> worker = session.SpawnProcess("singplus.emulator.regions.worker", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!worker.IsSuccess) return Failure("spawn worker", worker.Error, worker.Message);

        KernelResult<OwnedBuffer<byte>> buffer = runtime.AllocateBuffer<byte>(system, length: 4096);
        if (!buffer.IsSuccess) return Failure("allocate buffer", buffer.Error, buffer.Message);
        OwnedBuffer<byte> owned = buffer.Value!;
        for (int index = 0; index < owned.Length; index++) owned.Span[index] = (byte)(index & 0xFF);
        ulong expected = Checksum(owned.Span);
        EmulatorConsole.StageDetail(
            $"allocated region {owned.Handle.RegionId.Value} (4096 bytes) for system process, checksum {expected}");

        KernelResult<OwnedBuffer<byte>> transferred = runtime.TransferRegion(system, worker.Value, owned);
        if (!transferred.IsSuccess) return Failure("transfer region", transferred.Error, transferred.Message);
        OwnedBuffer<byte> moved = transferred.Value!;
        ulong observed = Checksum(moved.Span);
        if (observed != expected || owned.IsValid)
            return KernelBootStageResult.Failure("Ownership transfer corrupted the buffer or left the source token live.");
        EmulatorConsole.StageDetail(
            $"ownership moved to pid={worker.Value.ProcessId.Value} as region {moved.Handle.RegionId.Value}, checksum intact");

        KernelResult released = runtime.ReleaseRegion(worker.Value, moved);
        if (!released.IsSuccess) return Failure("release region", released.Error, released.Message);
        EmulatorConsole.StageDetail("region released by worker");

        KernelResult reclaimed = session.TerminateAndReclaim(worker.Value);
        return reclaimed.IsSuccess
            ? KernelBootStageResult.Success()
            : Failure("reclaim worker", reclaimed.Error, reclaimed.Message);
    }

    /// <summary>Capability mint, rights validation, delegation, revocation.</summary>
    public static KernelBootStageResult RunCapabilities(EmulatorSession session)
    {
        if (session.Runtime is null) return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");
        RuntimeKernel runtime = session.Runtime;

        KernelResult<ProcessHandle> auditor = session.SpawnProcess("singplus.emulator.caps.auditor", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!auditor.IsSuccess) return Failure("spawn auditor", auditor.Error, auditor.Message);
        KernelResult<ProcessHandle> clerk = session.SpawnProcess("singplus.emulator.caps.clerk", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!clerk.IsSuccess) return Failure("spawn clerk", clerk.Error, clerk.Message);

        KernelResult<SingProcess> resolvedAuditor = runtime.Processes.Resolve(auditor.Value);
        if (!resolvedAuditor.IsSuccess) return Failure("resolve auditor", resolvedAuditor.Error, resolvedAuditor.Message);
        DomainId auditorDomain = resolvedAuditor.Value!.DomainId;

        KernelResult<CapabilityDescriptorV1> traceRead = runtime.MintCapability(
            auditorDomain, auditor.Value, ResourceKind.KernelService, CapabilityResourceIds.TraceInspection,
            CapabilityRights.Read);
        if (!traceRead.IsSuccess) return Failure("mint trace capability", traceRead.Error, traceRead.Message);
        EmulatorConsole.StageDetail($"minted trace-inspection read capability {traceRead.Value!.CapabilityId.Value} for auditor");

        KernelResult<CapabilityDescriptorV1> validated = runtime.ValidateCapability(auditor.Value, traceRead.Value.CapabilityId, CapabilityRights.Read);
        if (!validated.IsSuccess) return Failure("validate trace capability", validated.Error, validated.Message);

        KernelResult<CapabilityDescriptorV1> budgetCap = runtime.MintCapability(
            auditorDomain, auditor.Value, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure | CapabilityRights.Delegate);
        if (!budgetCap.IsSuccess) return Failure("mint budget capability", budgetCap.Error, budgetCap.Message);

        KernelResult<CapabilityDescriptorV1> wrongRights = runtime.ValidateCapability(auditor.Value, traceRead.Value.CapabilityId, CapabilityRights.Configure);
        if (wrongRights.IsSuccess)
            return KernelBootStageResult.Failure("Rights escalation: read-only capability satisfied a Configure check.");
        EmulatorConsole.StageDetail($"read-only capability correctly refused Configure ({wrongRights.Error})");

        KernelResult<CapabilityDescriptorV1> delegated = runtime.DelegateCapability(
            auditor.Value, clerk.Value, budgetCap.Value!.CapabilityId, CapabilityRights.Configure);
        if (!delegated.IsSuccess) return Failure("delegate budget capability", delegated.Error, delegated.Message);
        KernelResult<CapabilityDescriptorV1> clerkValidated = runtime.ValidateCapability(clerk.Value, delegated.Value!.CapabilityId, CapabilityRights.Configure);
        if (!clerkValidated.IsSuccess)
            return Failure("validate delegated capability", clerkValidated.Error, clerkValidated.Message);
        EmulatorConsole.StageDetail($"budget capability delegated to clerk as {delegated.Value.CapabilityId.Value}");

        KernelResult revoked = runtime.RevokeCapability(traceRead.Value.CapabilityId);
        if (!revoked.IsSuccess) return Failure("revoke trace capability", revoked.Error, revoked.Message);
        KernelResult<CapabilityDescriptorV1> afterRevoke = runtime.ValidateCapability(auditor.Value, traceRead.Value.CapabilityId, CapabilityRights.Read);
        if (afterRevoke.IsSuccess)
            return KernelBootStageResult.Failure("Revoked capability still validates.");
        EmulatorConsole.StageDetail($"capability {traceRead.Value.CapabilityId.Value} revoked; validation now fails ({afterRevoke.Error})");

        return ReclaimPair(session, auditor.Value, clerk.Value);
    }

    /// <summary>Full lifecycle walk: spawn, park, resume, terminate, terminal receipt.</summary>
    public static KernelBootStageResult RunLifecycle(EmulatorSession session)
    {
        if (session.Runtime is null) return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");
        RuntimeKernel runtime = session.Runtime;

        KernelResult<ProcessHandle> spawned = session.SpawnProcess("singplus.emulator.lifecycle.subject", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!spawned.IsSuccess) return Failure("spawn subject", spawned.Error, spawned.Message);
        ProcessHandle handle = spawned.Value;
        EmulatorConsole.StageDetail($"subject pid={handle.ProcessId.Value} running");

        KernelResult parked = runtime.ParkProcess(handle);
        if (!parked.IsSuccess) return Failure("park", parked.Error, parked.Message);
        if (!CheckState(runtime, handle, ProcessState.Parked, out string? stateError)) return KernelBootStageResult.Failure(stateError!);
        EmulatorConsole.StageDetail("subject parked");

        // A parked process must refuse new effects.
        KernelResult<OwnedBuffer<byte>> effectWhileParked = runtime.AllocateBuffer<byte>(handle, 16);
        if (effectWhileParked.IsSuccess)
            return KernelBootStageResult.Failure("Parked process accepted a new region effect.");
        EmulatorConsole.StageDetail($"parked process refused new effects ({effectWhileParked.Error})");

        KernelResult resumed = runtime.ResumeProcess(handle);
        if (!resumed.IsSuccess) return Failure("resume", resumed.Error, resumed.Message);
        if (!CheckState(runtime, handle, ProcessState.Running, out stateError)) return KernelBootStageResult.Failure(stateError!);
        EmulatorConsole.StageDetail("subject resumed");

        KernelResult reclaimed = session.TerminateAndReclaim(handle);
        if (!reclaimed.IsSuccess) return Failure("terminate", reclaimed.Error, reclaimed.Message);

        KernelResult<ProcessState> terminal = runtime.Processes.ResolveTerminalState(handle);
        if (!terminal.IsSuccess || terminal.Value != ProcessState.Exited)
            return KernelBootStageResult.Failure("Terminal receipt for the exited generation is missing.");
        EmulatorConsole.StageDetail("subject exited; terminal receipt recorded");
        return KernelBootStageResult.Success();
    }

    private static KernelBootStageResult ReclaimPair(EmulatorSession session, ProcessHandle first, ProcessHandle second)
    {
        KernelResult firstResult = session.TerminateAndReclaim(first);
        if (!firstResult.IsSuccess) return Failure("reclaim first process", firstResult.Error, firstResult.Message);
        KernelResult secondResult = session.TerminateAndReclaim(second);
        if (!secondResult.IsSuccess) return Failure("reclaim second process", secondResult.Error, secondResult.Message);
        EmulatorConsole.StageDetail("demo processes reclaimed");
        return KernelBootStageResult.Success();
    }

    private static bool CheckState(RuntimeKernel runtime, ProcessHandle handle, ProcessState expected, out string? error)
    {
        KernelResult<SingProcess> resolved = runtime.Processes.Resolve(handle);
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
        foreach (byte value in bytes) sum = unchecked(sum * 31 + value);
        return sum;
    }

    private static KernelBootStageResult Failure(string operation, KernelError error, string? message) =>
        KernelBootStageResult.Failure($"Demo step '{operation}' failed ({error}): {message ?? "no detail"}.");
}
