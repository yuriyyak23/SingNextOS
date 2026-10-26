using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool StartHostedCounter(EmulatorSession session, string[] args)
    {
        if (args.Length != 1) return Fail(KernelError.InvalidTransition, "usage: counter-start <name>");
        KernelResult<HostedCounterApp> started = HostedCounterApp.Start(session, args[0]);
        if (!started.IsSuccess) return Fail(started.Error, started.Message);
        HostedCounterApp app = started.Value!;
        EmulatorConsole.Ok($"[hosted] {args[0]} started: caller={app.Caller.ProcessId.Value}, server={app.Server.ProcessId.Value}, session={app.Session.SessionId.Value}, capability={app.AdmissionCapability.Value}; generated SIP, cooperative host CLR\r\n");
        return true;
    }

    private static bool CallHostedCounter(EmulatorSession session, string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[1], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value))
            return Fail(KernelError.InvalidMessage, "usage: counter-call <name> <Int32>");
        if (!session.HostedCounters.TryGetValue(args[0], out HostedCounterApp? app))
            return Fail(KernelError.InvalidTransition, "Unknown hosted application.");
        KernelResult<int> called = app.Invoke(session, value);
        if (!called.IsSuccess) return Fail(called.Error, called.Message);
        EmulatorConsole.Ok($"[hosted] {args[0]}: {value} -> {called.Value}; accepted, dispatched, published through runtime session; calls={app.Calls}\r\n");
        return true;
    }

    private static bool StopHostedCounter(EmulatorSession session, string[] args)
    {
        if (args.Length != 1) return Fail(KernelError.InvalidTransition, "usage: counter-stop <name>");
        if (!session.HostedCounters.TryGetValue(args[0], out HostedCounterApp? app))
            return Fail(KernelError.InvalidTransition, "Unknown hosted application.");
        KernelResult stopped = app.Stop(session);
        if (!stopped.IsSuccess) return Fail(stopped.Error, stopped.Message);
        session.HostedCounters.Remove(args[0]);
        EmulatorConsole.Ok($"[hosted] {args[0]} stopped: session closed, caller and server reclaimed\r\n");
        return true;
    }

    private static bool ShowHostedCounters(EmulatorSession session, string[] args)
    {
        if (args.Length != 0) return Fail(KernelError.InvalidTransition, "usage: counters");
        foreach ((string? name, HostedCounterApp? app) in session.HostedCounters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            EmulatorConsole.Info($"[hosted] {name}: caller={app.Caller.ProcessId.Value}, server={app.Server.ProcessId.Value}, session={app.Session.SessionId.Value}, calls={app.Calls}, last={app.LastResult?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, state={(app.Closing ? "closing" : "open")}\r\n");
        EmulatorConsole.Info($"[hosted] {session.HostedCounters.Count} cooperative generated SIP application(s)\r\n");
        return true;
    }
}