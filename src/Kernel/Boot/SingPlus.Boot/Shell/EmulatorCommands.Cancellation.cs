using System.Globalization;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool TryReadCancelScope(EmulatorSession session, string text, out CancellationScopeHandle scope)
    {
        scope = default;
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ||
            !session.CancellationScopes.TryGetValue(id, out scope))
            return Fail(KernelError.InvalidTransition, "Unknown shell cancellation scope.");
        return true;
    }

    private static bool CreateCancelScope(EmulatorSession session, string[] args)
    {
        const string usage = "usage: cancel-scope <pid> [parentScopeId] [--after-ms 1..86400000]";
        if (args.Length < 1 || !TryReadPid(args, 0, out ulong pid))
            return Fail(KernelError.InvalidTransition, usage);
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        KernelResult<ProcessHandle> owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        CancellationScopeHandle? parent = null;
        long? milliseconds = null;
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--after-ms")
            {
                if (milliseconds is not null || ++index >= args.Length ||
                    !long.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out long value) ||
                    value is < 1 or > 86_400_000)
                    return Fail(KernelError.InvalidTransition, usage);
                milliseconds = value;
            }
            else
            {
                if (parent is not null) return Fail(KernelError.InvalidTransition, usage);
                if (!TryReadCancelScope(session, args[index], out CancellationScopeHandle handle)) return false;
                parent = handle;
            }
        }
        MonotonicDeadline? deadline = null;
        if (milliseconds is { } after)
        {
            TimeProvider clock = session.RuntimeClock;
            long ticks = checked((long)decimal.Ceiling((decimal)after * clock.TimestampFrequency / 1000));
            deadline = new MonotonicDeadline(checked(clock.GetTimestamp() + ticks));
        }
        KernelResult<CancellationObservation> created = runtime.CreateCancellationScope(owner.Value, deadline, parent);
        if (!created.IsSuccess) return Fail(created.Error, created.Message);
        session.CancellationScopes.Add(created.Value!.Scope.ScopeId.Value, created.Value.Scope);
        WriteCancelObservation(created.Value);
        return true;
    }

    private static bool ObserveCancelScope(EmulatorSession session, string[] args, bool request)
    {
        if (args.Length != 2 || !TryReadPid(args, 0, out ulong pid))
            return Fail(KernelError.InvalidTransition, "usage: cancel-show/cancel-request <pid> <scopeId>");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (!TryReadCancelScope(session, args[1], out CancellationScopeHandle scope)) return false;
        KernelResult<ProcessHandle> owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        KernelResult<CancellationObservation> observed = request ? runtime.RequestCancellation(owner.Value, scope) : runtime.ObserveCancellation(owner.Value, scope);
        if (!observed.IsSuccess) return Fail(observed.Error, observed.Message);
        WriteCancelObservation(observed.Value!);
        if (observed.Value!.Disposition == CancellationDisposition.Stale)
            return Fail(KernelError.StaleGeneration, "Cancellation scope owner or generation is stale.");
        return true;
    }

    private static void WriteCancelObservation(CancellationObservation value)
    {
        EmulatorConsole.Info($"[kernel] scope {value.Scope.ScopeId.Value}:{value.Scope.Generation.Value}, pid={value.Owner.ProcessId.Value}, parent={value.Parent?.ScopeId.Value.ToString() ?? "none"}, seq={value.Sequence}, requested={value.CancellationRequested}, disposition={value.Disposition}, timeout={value.Timeout}, deadline={value.EffectiveDeadline?.Timestamp.ToString() ?? "none"}\r\n");
        EmulatorConsole.Info("[kernel] cancellation observation grants no effect/reclaim authority\r\n");
    }
}
