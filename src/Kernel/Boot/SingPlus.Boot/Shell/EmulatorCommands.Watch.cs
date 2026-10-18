using System.Globalization;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool WatchAuthority(EmulatorSession session, string[] args)
    {
        const string usage = "usage: watch <pid> [samples 1..20] [intervalMs 1..1000] [inspectionCapabilityId]";
        if (args.Length is < 1 or > 4 || !TryReadPid(args, 0, out var pid))
            return Fail(KernelError.InvalidTransition, usage);
        var samples = 5;
        var interval = 250;
        if (args.Length >= 2 && (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out samples) || samples is < 1 or > 20))
            return Fail(KernelError.InvalidTransition, usage);
        if (args.Length >= 3 && (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval is < 1 or > 1000))
            return Fail(KernelError.InvalidTransition, usage);
        CapabilityId? capability = null;
        if (args.Length == 4)
        {
            if (!ulong.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return Fail(KernelError.InvalidTransition, usage);
            capability = new CapabilityId(id);
        }
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        var principal = session.ResolvePid(pid);
        if (!principal.IsSuccess) return Fail(principal.Error, principal.Message);
        var created = runtime.CreateAuthorityInspector(principal.Value, capability);
        if (!created.IsSuccess) return Fail(created.Error, created.Message);
        var inspector = created.Value!;
        for (var sample = 1; sample <= samples; sample++)
        {
            var captured = inspector.Capture(AuthorityInspectionConsistency.PointInTimeBestEffort);
            if (!captured.IsSuccess) return Fail(captured.Error, captured.Message);
            var snapshot = captured.Value!;
            EmulatorConsole.Info($"[watch] sample={sample}/{samples} capture={snapshot.CaptureSequence} scope={snapshot.Scope} consistency={snapshot.Consistency}; nodes={snapshot.Nodes.Count}, edges={snapshot.Edges.Count}, redacted={snapshot.RedactedFactCount}\r\n");
            var groups = snapshot.Nodes.GroupBy(static node => (node.Kind, node.State))
                .OrderBy(static group => group.Key.Kind).ThenBy(static group => group.Key.State, StringComparer.Ordinal).ToArray();
            EmulatorConsole.Table(["kind", "state", "count", "stale", "redacted"],
                groups.Take(100).Select(static group => (IReadOnlyList<string>)new[]
                {
                    group.Key.Kind.ToString(), group.Key.State, group.Count().ToString(),
                    group.Count(static node => node.Stale).ToString(), group.Count(static node => node.Redacted).ToString(),
                }).ToArray());
            if (groups.Length > 100) EmulatorConsole.Detail($"[watch] omitted {groups.Length - 100} groups\r\n");
            if (sample < samples) Thread.Sleep(interval);
        }
        return true;
    }
}
