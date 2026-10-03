using System.Globalization;
using SingPlus.Boot.Demos;
using SingPlus.Boot.Diagnostics;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Shell;

/// <summary>
/// Interactive/scriptable command surface of the emulator. Every handler reports
/// kernel errors verbatim so authority-graph refusals stay observable.
/// </summary>
public static partial class EmulatorCommands
{
    public sealed record Entry(string Name, string Usage, string Description, Func<EmulatorSession, string[], bool> Handler);

    public static IReadOnlyList<Entry> All { get; } = BuildEntries();

    public static bool TryFind(string name, out Entry entry)
    {
        foreach (var candidate in All)
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
        entry = null!;
        return false;
    }

    private static IReadOnlyList<Entry> BuildEntries() =>
    [
        new("status", "status", "Emulated machine summary (uptime, counts, options).",
            (session, _) => Inspect(session, RuntimeInspector.WriteStatus)),
        new("env", "env", "Host environment facts captured at boot.",
            (session, _) => Inspect(session, RuntimeInspector.WriteEnvironment)),
        new("ps", "ps", "Live process table.",
            (session, _) => Inspect(session, RuntimeInspector.WriteProcesses)),
        new("inspect", "inspect <pid>", "Process detail: manifest, digest, channels, regions, capabilities.",
            (session, args) => WithPid(session, args, pid => RuntimeInspector.WriteProcessDetail(session, pid))),
        new("domains", "domains", "Active authority domains.",
            (session, _) => Inspect(session, RuntimeInspector.WriteDomains)),
        new("components", "components", "Admitted SIP components with live lifecycle state.",
            (session, _) => Inspect(session, RuntimeInspector.WriteComponents)),
        new("verify-guest", "verify-guest <HCEXE path>", "Inspect bounded HCEXE compatibility with the bundled runtime package; grants no authority.", VerifyGuestArtifact),
        new("verify-sip", "verify-sip <imagePath> <Type::Method>", "Run ManagedCap static verification without admission or execution.", VerifySip),
        new("watch", "watch <pid> [samples] [intervalMs] [inspectionCapabilityId]", "Capture bounded read-only authority summaries (defaults: 5 samples, 250 ms).", WatchAuthority),
        new("why-pinned", "why-pinned <pid> <operationId> [inspectionCapabilityId]", "Explain visible runtime external-operation pins.", ExplainExternalAuthority),
        new("why-move", "why-move <pid> <regionId> [inspectionCapabilityId]", "Explain visible runtime Region MOVE blockers.",
            (session, args) => ExplainRegionAuthority(session, args, move: true)),
        new("why-free", "why-free <pid> <regionId> [inspectionCapabilityId]", "Explain visible runtime Region reclaim blockers.",
            (session, args) => ExplainRegionAuthority(session, args, move: false)),
        new("why-reclaim", "why-reclaim <pid> [inspectionCapabilityId]", "Explain visible runtime process reclaim blockers; grants no mutation authority.",
            (session, args) => InspectAuthority(session, args, explainReclaim: true)),
        new("authority", "authority <pid> [inspectionCapabilityId]", "Capture the real authority graph; optional dedicated capability permits system scope.", (session, args) => InspectAuthority(session, args)),
        new("platform", "platform", "Actual platform feature manifest for the selected profile.",
            (session, _) => Inspect(session, RuntimeInspector.WritePlatform)),

        new("spawn", "spawn <identity> [--admitted] [--role user|sip|driver|kernel] [--memory gc|sipregion|noheap]",
            "Create, admit and start a new emulator-owned process.", Spawn),
        new("start", "start <pid>", "Start an admitted process through runtime execution checks.",
            (session, args) => Lifecycle(session, args, static (runtime, handle) => runtime.StartProcess(handle), "started")),
        new("platform-bind", "platform-bind <pid>", "Bind an admitted process to the installed provider; must precede start.", BindPlatform),
        new("platform-policy", "platform-policy <pid>", "Configure a model background execution policy before start.", ConfigurePlatformPolicy),
        new("park", "park <pid>", "Park a running process (new effects are refused while parked).",
            (session, args) => Lifecycle(session, args, static (runtime, handle) => runtime.ParkProcess(handle), "parked")),
        new("resume", "resume <pid>", "Resume a parked process.",
            (session, args) => Lifecycle(session, args, static (runtime, handle) => runtime.ResumeProcess(handle), "resumed")),
        new("stop", "stop <pid>", "Terminate a process and observe teardown to completion.",
            (session, args) => Terminate(session, args, fault: false)),
        new("fault", "fault <pid>", "Fault a process and observe teardown to completion.",
            (session, args) => Terminate(session, args, fault: true)),

        new("channel", "channel <leftPid> <rightPid> [capacity]", "Create a ping-pong protocol channel between two live processes.", (session, args) => CreateChannel(session, args)),
        new("service-register", "service-register <pid> <name>", "Register the Boot ping-pong contract with exact Read admission authority.", RegisterShellService),
        new("service", "service <name>", "Discover the current runtime service descriptor.", (session, args) => ShowShellService(session, args, false)),
        new("service-state", "service-state <name> <accepting|draining|unavailable>", "Set runtime service availability through the host control plane.", (session, args) => ShowShellService(session, args, true)),
        new("session-open", "session-open <pid> <serviceName> [capabilityId ...] [--lifetime-ms N]", "Open a real capability-gated EndpointSession.", OpenShellSession),
        new("session-close", "session-close <pid> <sessionId>", "Close an exact tracked session; runtime checks the supplied owner.", CloseShellSession),
        new("channels", "channels", "List tracked channels with protocol state.", 
            (session, _) => Inspect(session, RuntimeInspector.WriteChannels)),
        new("send", "send <fromPid> <channelId> ping | tick <n> | close", "Send a protocol message on a tracked channel.", Send),
        new("recv", "recv <pid> <channelId>", "Receive the next envelope for a process on a tracked channel.", Receive),

        new("borrow-channel", "borrow-channel <leftPid> <rightPid> [capacity]", "Create a read-only buffer borrow protocol channel.",
            (session, args) => CreateChannel(session, args, borrow: true)),
        new("borrow", "borrow <ownerPid> <channelId> <regionId>", "Queue a read-only borrow; recv delivers the lease.", SendBorrow),
        new("borrows", "borrows", "Show delivered borrow leases and their current validity.", ShowBorrows),
        new("borrow-return", "borrow-return <borrowerPid> <regionId>", "Return a delivered borrow lease.",
            (session, args) => CompleteBorrow(session, args, revoke: false)),
        new("borrow-revoke", "borrow-revoke <ownerPid> <regionId>", "Revoke a delivered borrow lease.",
            (session, args) => CompleteBorrow(session, args, revoke: true)),
        new("dump", "dump <pid> <regionId> [offset] [count]", "Read up to 256 bytes through the owner's or borrower's token.", DumpBuffer),

        new("cancel-scope", "cancel-scope <pid> [parentScopeId] [--after-ms milliseconds]", "Create a real cancellation scope, optionally inheriting a parent.", CreateCancelScope),
        new("cancel-request", "cancel-request <pid> <scopeId>", "Request cancellation; does not release resources.",
            (session, args) => ObserveCancelScope(session, args, request: true)),
        new("cancel-show", "cancel-show <pid> <scopeId>", "Observe cancellation coordination state from runtime.",
            (session, args) => ObserveCancelScope(session, args, request: false)),
        new("external-prepare", "external-prepare <pid> <regionId>", "Prepare a pre-submit staged operation over an entire tracked buffer.", PrepareExternal),
        new("external-admit", "external-admit <pid> <operationId> [scopeId]", "Acquire the operation's exclusive Region use and attached budget.",
            (session, args) => ExternalAction(session, args, "admit")),
        new("external-cancel", "external-cancel <pid> <operationId> [scopeId]", "Cancel before provider submission; release remains separate.",
            (session, args) => ExternalAction(session, args, "cancel")),
        new("external-release", "external-release <pid> <operationId>", "Release a cancelled local operation's Region use and budget.",
            (session, args) => ExternalAction(session, args, "release")),
        new("external", "external <pid> <operationId>", "Query exact operation state and runtime transition history.",
            (session, args) => ExternalAction(session, args, "query")),
        new("region-uses", "region-uses [regionId]", "Read current runtime Region use descriptors.", ShowRegionUses),

        new("regions", "regions", "Live memory region table.",
            (session, _) => Inspect(session, RuntimeInspector.WriteRegions)),
        new("alloc", "alloc <pid> <bytes>", "Allocate a tracked byte buffer owned by a process.", Allocate),
        new("fill", "fill <regionId> <byte>", "Fill a tracked buffer with a byte pattern (decimal or 0x..).", Fill),
        new("transfer", "transfer <regionId> <toPid>", "Move buffer ownership to another process.", Transfer),
        new("free", "free <regionId>", "Release a tracked buffer.", Free),

        new("caps", "caps <pid>", "Capabilities held by the process domain.",
            (session, args) => WithPid(session, args, pid => RuntimeInspector.WriteCapabilities(session, pid))),
        new("mint", "mint <pid> <resourceId> [rights]", "Mint a KernelService capability (rights e.g. Read,Configure).", Mint),
        new("revoke", "revoke <capabilityId>", "Revoke a capability by id.", Revoke),

        new("budget", "budget", "Show the system budget account and all admitted process budgets.",
            (session, _) => Inspect(session, RuntimeInspector.WriteBudgets)),
        new("budget-reserve", "budget-reserve <pid> <dimension> <amount>", "Reserve a real local-resource budget; does not grant effect authority.", ReserveLocalBudget),
        new("budget-release", "budget-release <pid> <reservationId>", "Release an explicit tracked local reservation.", ReleaseLocalBudget),
        new("budget-reservations", "budget-reservations", "Query current runtime state of explicit emulator reservations.", ShowBudgetReservations),
        new("budget-admit", "budget-admit <pid>", "Admit a process budget for a live process (requires --budget boot).", BudgetAdmit),

        new("trace", "trace", "List active and last completed trace sessions per process.",
            (session, _) => Inspect(session, RuntimeInspector.WriteTraceSessions)),
        new("trace-start", "trace-start <pid> [capacity]", "Start a trace session for a live process.", TraceStart),
        new("trace-stop", "trace-stop <pid>", "Stop the trace session of a process.", TraceStop),
        new("trace-show", "trace-show <pid> [count]", "Show active or last completed trace events (also after process exit).", TraceShow),

        new("demo", "demo <ipc|regions|caps|lifecycle|borrows|platform|compute|compute-cancel|compute-fault|platform-regions|external|sessions|child-execute|child-domain|backing|generated-sip|service-restart|supervised-restart|all>", "Run a built-in runtime workload now.", RunDemo),
    ];

    private static bool Inspect(EmulatorSession session, Action<EmulatorSession> view)
    {
        view(session);
        return true;
    }

    private static bool WithPid(EmulatorSession session, string[] args, Action<ulong> action)
    {
        if (!TryReadPid(args, 0, out var pid)) return false;
        action(pid);
        return true;
    }

    private static bool Spawn(EmulatorSession session, string[] args)
    {
        if (args.Length < 1)
        {
            EmulatorConsole.Error("[emulator] usage: spawn <identity> [--role r] [--memory m]\r\n");
            return false;
        }

        var identity = args[0];
        var start = true;
        var role = ExecutionRole.User;
        var memory = MemoryProfile.ManagedGc;
        for (var index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--role", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                if (!TryParseRole(args[++index], out role)) return false;
            }
            else if (string.Equals(args[index], "--memory", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                if (!TryParseMemory(args[++index], out memory)) return false;
            }
            else if (string.Equals(args[index], "--admitted", StringComparison.Ordinal) && start)
            {
                start = false;
            }
            else
            {
                EmulatorConsole.Error($"[emulator] unrecognized spawn argument '{args[index]}'\r\n");
                return false;
            }
        }

        var spawned = session.SpawnProcess(identity, role, memory, start);
        if (!spawned.IsSuccess)
        {
            EmulatorConsole.Error($"[kernel] spawn failed ({spawned.Error}): {spawned.Message}\r\n");
            return false;
        }

        EmulatorConsole.Ok($"[kernel] process {spawned.Value.ProcessId.Value} (generation {spawned.Value.Generation}) {(start ? "running" : "admitted")} as '{identity}'\r\n");
        return true;
    }

    private static bool Lifecycle(
        EmulatorSession session, string[] args,
        Func<RuntimeKernel, ProcessHandle, KernelResult> operation, string pastTense)
    {
        if (session.Runtime is null || !TryReadPid(args, 0, out var pid)) return false;
        var handle = session.ResolvePid(pid);
        if (!handle.IsSuccess) return Fail(handle.Error, handle.Message);

        var result = operation(session.Runtime, handle.Value);
        if (!result.IsSuccess) return Fail(result.Error, result.Message);

        EmulatorConsole.Ok($"[kernel] process {pid} {pastTense}\r\n");
        return true;
    }

    private static bool Terminate(EmulatorSession session, string[] args, bool fault)
    {
        if (!TryReadPid(args, 0, out var pid)) return false;
        var handle = session.ResolvePid(pid);
        if (!handle.IsSuccess) return Fail(handle.Error, handle.Message);

        var result = session.TerminateAndReclaim(handle.Value, fault);
        if (!result.IsSuccess) return Fail(result.Error, result.Message);

        EmulatorConsole.Ok($"[kernel] process {pid} {(fault ? "faulted" : "exited")}; teardown complete\r\n");
        return true;
    }

    private static bool CreateChannel(EmulatorSession session, string[] args, bool borrow = false)
    {
        if (borrow && args.Length is < 2 or > 3)
        {
            EmulatorConsole.Error("[emulator] usage: borrow-channel <leftPid> <rightPid> [capacity]\r\n");
            return false;
        }
        if (session.Runtime is null) return false;
        if (!TryReadPid(args, 0, out var leftPid) || !TryReadPid(args, 1, out var rightPid)) return false;

        var capacity = 8;
        if (args.Length > 2 &&
            (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out capacity) ||
             capacity <= 0 || capacity > 1024))
        {
            EmulatorConsole.Error("[emulator] channel capacity must be an integer between 1 and 1024\r\n");
            return false;
        }

        var left = session.ResolvePid(leftPid);
        if (!left.IsSuccess) return Fail(left.Error, left.Message);
        var right = session.ResolvePid(rightPid);
        if (!right.IsSuccess) return Fail(right.Error, right.Message);

        var created = session.Runtime.CreateChannel(left.Value, right.Value, borrow ? EmulatorSession.CreateBorrowProtocol() : EmulatorSession.CreatePingPongProtocol(), capacity);
        if (!created.IsSuccess) return Fail(created.Error, created.Message);

        session.Channels.Add(new ChannelRecord(
            created.Value.Left.ChannelId.Value, left.Value, right.Value,
            created.Value.Left, created.Value.Right, capacity));
        EmulatorConsole.Ok($"[kernel] channel {created.Value.Left.ChannelId.Value} open between {leftPid} and {rightPid} (capacity {capacity})\r\n");
        return true;
    }

    private static bool Send(EmulatorSession session, string[] args)
    {
        if (session.Runtime is null) return false;
        if (!TryReadPid(args, 0, out var fromPid) || !TryReadChannel(session, args, 1, out var channel)) return false;
        if (args.Length < 3)
        {
            EmulatorConsole.Error("[emulator] usage: send <fromPid> <channelId> ping | tick <n> | close\r\n");
            return false;
        }

        var from = session.ResolvePid(fromPid);
        if (!from.IsSuccess) return Fail(from.Error, from.Message);
        if (from.Value != channel.Left && from.Value != channel.Right)
        {
            EmulatorConsole.Error($"[emulator] process {fromPid} does not own an endpoint of channel {channel.ChannelId}\r\n");
            return false;
        }

        uint messageId;
        object? payload = null;
        switch (args[2].ToLowerInvariant())
        {
            case "ping":
                messageId = 1;
                break;
            case "tick":
            {
                if (args.Length < 4 ||
                    !ulong.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    EmulatorConsole.Error("[emulator] tick requires an unsigned 64-bit value\r\n");
                    return false;
                }
                messageId = 2;
                payload = value;
                break;
            }
            case "close":
                messageId = 3;
                break;
            default:
                EmulatorConsole.Error($"[emulator] unknown message '{args[2]}'; expected ping, tick <n> or close\r\n");
                return false;
        }

        var peer = channel.PeerOf(from.Value);
        var sent = session.Runtime.Send(from.Value, peer, channel.EndpointFor(from.Value), messageId, payload);
        if (!sent.IsSuccess) return Fail(sent.Error, sent.Message);

        EmulatorConsole.Ok($"[kernel] message {messageId} queued on channel {channel.ChannelId} with sequence {sent.Value!.Sequence}\r\n");
        return true;
    }

    private static bool Receive(EmulatorSession session, string[] args)
    {
        if (session.Runtime is null) return false;
        if (!TryReadPid(args, 0, out var pid) || !TryReadChannel(session, args, 1, out var channel)) return false;

        var handle = session.ResolvePid(pid);
        if (!handle.IsSuccess) return Fail(handle.Error, handle.Message);
        if (handle.Value != channel.Left && handle.Value != channel.Right)
        {
            EmulatorConsole.Error($"[emulator] process {pid} does not own an endpoint of channel {channel.ChannelId}\r\n");
            return false;
        }

        var received = session.Runtime.Receive(handle.Value, channel.EndpointFor(handle.Value));
        if (!received.IsSuccess) return Fail(received.Error, received.Message);

        var envelope = received.Value!;
        if (envelope.Payload is BorrowLease<byte> lease)
        {
            session.Borrows[lease.Handle.Region.RegionId.Value] = new BorrowRecord(channel.PeerOf(handle.Value), handle.Value, lease);
            EmulatorConsole.Ok($"[kernel] read-only borrow delivered: region {lease.Handle.Region.RegionId.Value}, lease generation {lease.Handle.Generation.Value}, borrower {pid}\r\n");
            return true;
        }
        var payload = envelope.Payload switch
        {
            null => string.Empty,
            ulong value => $" payload={value}",
            { } other => $" payload={other}",
        };
        EmulatorConsole.Ok($"[kernel] channel {channel.ChannelId} delivered message {envelope.MessageId} (sequence {envelope.Sequence}){payload}\r\n");
        return true;
    }

    private static bool Allocate(EmulatorSession session, string[] args)
    {
        if (!TryReadPid(args, 0, out var pid)) return false;
        if (args.Length < 2 ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            length <= 0 || length > 64 * 1024 * 1024)
        {
            EmulatorConsole.Error("[emulator] usage: alloc <pid> <bytes> (1..67108864)\r\n");
            return false;
        }

        var handle = session.ResolvePid(pid);
        if (!handle.IsSuccess) return Fail(handle.Error, handle.Message);

        var record = session.AllocateTrackedBuffer(handle.Value, length);
        if (!record.IsSuccess) return Fail(record.Error, record.Message);

        EmulatorConsole.Ok($"[kernel] region {record.Value!.Handle.RegionId.Value} allocated: {length} bytes owned by pid {pid}\r\n");
        return true;
    }

    private static bool Fill(EmulatorSession session, string[] args)
    {
        if (!TryReadBuffer(session, args, 0, out var record)) return false;
        if (args.Length < 2 || !TryParseByte(args[1], out var value))
        {
            EmulatorConsole.Error("[emulator] usage: fill <regionId> <byte> (decimal or 0x..)\r\n");
            return false;
        }

        try
        {
            record.Buffer.Span.Fill(value);
        }
        catch (InvalidOperationException exception)
        {
            EmulatorConsole.Error($"[kernel] buffer is not writable: {exception.Message}\r\n");
            return false;
        }

        EmulatorConsole.Ok($"[kernel] region {record.Handle.RegionId.Value} filled with 0x{value:X2}\r\n");
        return true;
    }

    private static bool Transfer(EmulatorSession session, string[] args)
    {
        if (session.Runtime is null) return false;
        if (!TryReadBuffer(session, args, 0, out var record) || !TryReadPid(args, 1, out var targetPid)) return false;

        var target = session.ResolvePid(targetPid);
        if (!target.IsSuccess) return Fail(target.Error, target.Message);

        var transferred = session.Runtime.TransferRegion(record.Owner, target.Value, record.Buffer);
        if (!transferred.IsSuccess) return Fail(transferred.Error, transferred.Message);

        var moved = transferred.Value!;
        session.Buffers.Remove(record.Handle.RegionId.Value);
        session.Buffers[moved.Handle.RegionId.Value] = new BufferRecord(moved.Handle, target.Value, moved.Length, moved);
        EmulatorConsole.Ok(
            $"[kernel] region {record.Handle.RegionId.Value} consumed; ownership moved to pid {targetPid} as region {moved.Handle.RegionId.Value}\r\n");
        return true;
    }

    private static bool Free(EmulatorSession session, string[] args)
    {
        if (session.Runtime is null) return false;
        if (!TryReadBuffer(session, args, 0, out var record)) return false;

        var released = session.Runtime.ReleaseRegion(record.Owner, record.Buffer);
        if (!released.IsSuccess) return Fail(released.Error, released.Message);

        session.Buffers.Remove(record.Handle.RegionId.Value);
        EmulatorConsole.Ok($"[kernel] region {record.Handle.RegionId.Value} released\r\n");
        return true;
    }

    private static bool Mint(EmulatorSession session, string[] args)
    {
        if (session.Runtime is null) return false;
        if (!TryReadPid(args, 0, out var pid)) return false;
        if (args.Length < 2)
        {
            EmulatorConsole.Error("[emulator] usage: mint <pid> <resourceId> [rights]\r\n");
            return false;
        }

        var rights = CapabilityRights.Read;
        if (args.Length > 2 && !Enum.TryParse(args[2], ignoreCase: true, out rights))
        {
            EmulatorConsole.Error($"[emulator] cannot parse rights '{args[2]}' (e.g. Read or Read,Configure)\r\n");
            return false;
        }

        var handle = session.ResolvePid(pid);
        if (!handle.IsSuccess) return Fail(handle.Error, handle.Message);
        var process = session.Runtime.Processes.Resolve(handle.Value);
        if (!process.IsSuccess) return Fail(process.Error, process.Message);

        var minted = session.Runtime.MintCapability(
            process.Value!.DomainId, handle.Value, ResourceKind.KernelService, args[1], rights);
        if (!minted.IsSuccess) return Fail(minted.Error, minted.Message);

        EmulatorConsole.Ok(
            $"[kernel] capability {minted.Value!.CapabilityId.Value} minted for pid {pid}: {args[1]} ({rights})\r\n");
        return true;
    }

    private static bool Revoke(EmulatorSession session, string[] args)
    {
        if (session.Runtime is null) return false;
        if (args.Length < 1 ||
            !ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var capabilityValue))
        {
            EmulatorConsole.Error("[emulator] usage: revoke <capabilityId>\r\n");
            return false;
        }

        var revoked = session.Runtime.RevokeCapability(new CapabilityId(capabilityValue));
        if (!revoked.IsSuccess) return Fail(revoked.Error, revoked.Message);

        EmulatorConsole.Ok($"[kernel] capability {capabilityValue} revoked\r\n");
        return true;
    }

    private static bool BudgetAdmit(EmulatorSession session, string[] args)
    {
        if (!TryReadPid(args, 0, out var pid)) return false;
        var handle = session.ResolvePid(pid);
        if (!handle.IsSuccess) return Fail(handle.Error, handle.Message);

        var admitted = session.AdmitBudgetFor(handle.Value, $"shell:{pid}");
        if (!admitted.IsSuccess) return Fail(admitted.Error, admitted.Message);

        EmulatorConsole.Ok($"[kernel] process budget admitted for pid {pid}\r\n");
        return true;
    }

    private static bool TraceStart(EmulatorSession session, string[] args)
    {
        if (!TryReadPid(args, 0, out var pid)) return false;

        var capacity = EmulatorOptions.DefaultTraceCapacity;
        if (args.Length > 1 &&
            (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out capacity) ||
             capacity <= 0 || capacity > EmulatorOptions.MaximumTraceCapacity))
        {
            EmulatorConsole.Error($"[emulator] capacity must be 1..{EmulatorOptions.MaximumTraceCapacity}\r\n");
            return false;
        }

        var handle = session.ResolvePid(pid);
        if (!handle.IsSuccess) return Fail(handle.Error, handle.Message);

        var started = session.StartTrace(handle.Value, capacity);
        if (!started.IsSuccess) return Fail(started.Error, started.Message);

        EmulatorConsole.Ok($"[kernel] trace session {started.Value!.Session.SessionId.Value} active for pid {pid}\r\n");
        return true;
    }

    private static bool TraceStop(EmulatorSession session, string[] args)
    {
        if (!TryReadPid(args, 0, out var pid)) return false;
        var stopped = session.StopTrace(pid);
        if (!stopped.IsSuccess) return Fail(stopped.Error, stopped.Message);

        EmulatorConsole.Ok($"[kernel] trace session for pid {pid} stopped\r\n");
        return true;
    }

    private static bool TraceShow(EmulatorSession session, string[] args)
    {
        if (!TryReadPid(args, 0, out var pid)) return false;

        var count = 32;
        if (args.Length > 1 &&
            (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count <= 0))
        {
            EmulatorConsole.Error("[emulator] count must be a positive integer\r\n");
            return false;
        }

        return RuntimeInspector.WriteTraceEvents(session, pid, count);
    }

    private static bool RunDemo(EmulatorSession session, string[] args)
    {
        if (args.Length < 1)
        {
            EmulatorConsole.Error("[emulator] usage: demo <ipc|regions|caps|lifecycle|borrows|platform|compute|compute-cancel|compute-fault|platform-regions|external|sessions|child-execute|child-domain|backing|generated-sip|service-restart|supervised-restart|all>\r\n");
            return false;
        }

        if (string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var demo in Enum.GetValues<EmulatorDemo>())
                if (demo is not EmulatorDemo.ChildExecute and not EmulatorDemo.ChildDomain and not EmulatorDemo.PlatformRegions and not EmulatorDemo.SupervisedRestart and not EmulatorDemo.Platform and not EmulatorDemo.Compute and not EmulatorDemo.ComputeCancel and not EmulatorDemo.ComputeFault && !RunSingleDemo(session, demo)) return false;
            return true;
        }

        if (!EmulatorOptions.TryParseDemo(args[0], out var selected))
        {
            EmulatorConsole.Error($"[emulator] unknown demo '{args[0]}'\r\n");
            return false;
        }
        return RunSingleDemo(session, selected);
    }

    private static bool RunSingleDemo(EmulatorSession session, EmulatorDemo demo)
    {
        EmulatorConsole.Info($"[demo] running '{EmulatorOptions.DemoName(demo)}'\r\n");
        var result = DemoWorkloads.Run(demo, session);
        if (result.IsSuccess)
        {
            EmulatorConsole.Ok($"[demo] '{EmulatorOptions.DemoName(demo)}' passed\r\n");
            return true;
        }

        EmulatorConsole.Error($"[demo] '{EmulatorOptions.DemoName(demo)}' failed: {result.Error}\r\n");
        return false;
    }

    private static bool TryReadPid(string[] args, int index, out ulong pid)
    {
        pid = 0;
        if (args.Length <= index ||
            !ulong.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out pid))
        {
            EmulatorConsole.Error("[emulator] a numeric process id argument is required\r\n");
            return false;
        }
        return true;
    }

    private static bool TryReadChannel(EmulatorSession session, string[] args, int index, out ChannelRecord channel)
    {
        channel = null!;
        if (args.Length <= index ||
            !ulong.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var channelId))
        {
            EmulatorConsole.Error("[emulator] a numeric channel id argument is required\r\n");
            return false;
        }

        foreach (var candidate in session.Channels)
            if (candidate.ChannelId == channelId)
            {
                channel = candidate;
                return true;
            }

        EmulatorConsole.Error($"[emulator] channel {channelId} is not tracked by this emulator session\r\n");
        return false;
    }

    private static bool TryReadBuffer(EmulatorSession session, string[] args, int index, out BufferRecord record)
    {
        record = null!;
        if (args.Length <= index ||
            !ulong.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var regionId))
        {
            EmulatorConsole.Error("[emulator] a numeric region id argument is required\r\n");
            return false;
        }

        if (session.Buffers.TryGetValue(regionId, out record!)) return true;

        EmulatorConsole.Error($"[emulator] region {regionId} is not a tracked emulator buffer\r\n");
        return false;
    }

    private static bool TryParseByte(string text, out byte value)
    {
        value = 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseRole(string text, out ExecutionRole role)
    {
        role = text.ToLowerInvariant() switch
        {
            "user" => ExecutionRole.User,
            "sip" => ExecutionRole.Sip,
            "driver" => ExecutionRole.Driver,
            "kernel" => ExecutionRole.Kernel,
            _ => (ExecutionRole)(-1),
        };
        if (!Enum.IsDefined(role))
        {
            EmulatorConsole.Error($"[emulator] unknown role '{text}'\r\n");
            return false;
        }
        return true;
    }

    private static bool TryParseMemory(string text, out MemoryProfile memory)
    {
        memory = text.ToLowerInvariant() switch
        {
            "gc" => MemoryProfile.ManagedGc,
            "sipregion" or "sip" => MemoryProfile.SipRegion,
            "noheap" => MemoryProfile.KernelNoHeap,
            _ => (MemoryProfile)(-1),
        };
        if (!Enum.IsDefined(memory))
        {
            EmulatorConsole.Error($"[emulator] unknown memory profile '{text}'\r\n");
            return false;
        }
        return true;
    }

    private static bool Fail(KernelError error, string? message)
    {
        EmulatorConsole.Error($"[kernel] {error}: {message ?? "no detail"}\r\n");
        return false;
    }
}
