using System.Globalization;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool InspectAuthority(EmulatorSession session, string[] args, bool explainReclaim = false)
    {
        if (args.Length is < 1 or > 2 || !TryReadPid(args, 0, out var pid))
        {
            EmulatorConsole.Error($"[emulator] usage: {(explainReclaim ? "why-reclaim" : "authority")} <pid> [inspectionCapabilityId]\r\n");
            return false;
        }
        if (session.Runtime is not { } runtime)
        {
            EmulatorConsole.Error("[emulator] runtime is unavailable\r\n");
            return false;
        }
        CapabilityId? capability = null;
        if (args.Length == 2)
        {
            if (!ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                EmulatorConsole.Error("[emulator] inspection capability id must be numeric\r\n");
                return false;
            }
            capability = new CapabilityId(id);
        }
        var principal = session.ResolvePid(pid);
        if (!principal.IsSuccess) return Fail(principal.Error, principal.Message);
        var inspector = runtime.CreateAuthorityInspector(principal.Value, capability);
        if (!inspector.IsSuccess) return Fail(inspector.Error, inspector.Message);
        if (explainReclaim)
        {
            var explained = inspector.Value!.WhyReclaimBlocked(principal.Value);
            if (!explained.IsSuccess) return Fail(explained.Error, explained.Message);
            WriteAuthorityExplanation($"reclaim pid={pid}", explained.Value!);
            return true;
        }
        var captured = inspector.Value!.Capture();
        if (!captured.IsSuccess) return Fail(captured.Error, captured.Message);
        var snapshot = captured.Value!;
        EmulatorConsole.Info($"[authority] capture={snapshot.CaptureSequence} scope={snapshot.Scope} consistency={snapshot.Consistency}; nodes={snapshot.Nodes.Count}, edges={snapshot.Edges.Count}, redacted={snapshot.RedactedFactCount}\r\n");
        EmulatorConsole.Table(["node", "kind", "state", "generation", "stale", "redacted"],
            snapshot.Nodes.Take(100).Select(static node => (IReadOnlyList<string>)new[]
            {
                node.Id.Value, node.Kind.ToString(), node.State, node.Generation.ToString(),
                node.Stale.ToString(), node.Redacted.ToString(),
            }).ToArray());
        EmulatorConsole.Table(["source", "relationship", "target", "redacted"],
            snapshot.Edges.Take(100).Select(static edge => (IReadOnlyList<string>)new[]
            {
                edge.Source.Value, edge.Kind.ToString(), edge.Target.Value, edge.Redacted.ToString(),
            }).ToArray());
        if (snapshot.Nodes.Count > 100 || snapshot.Edges.Count > 100)
            EmulatorConsole.Detail($"[authority] omitted {Math.Max(0, snapshot.Nodes.Count - 100)} nodes and {Math.Max(0, snapshot.Edges.Count - 100)} edges\r\n");
        return true;
    }
    private static bool ExplainRegionAuthority(EmulatorSession session, string[] args, bool move)
    {
        var command = move ? "why-move" : "why-free";
        if (args.Length is < 2 or > 3 || !TryReadPid(args, 0, out var pid) ||
            !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var regionId))
            return Fail(KernelError.InvalidTransition, $"usage: {command} <pid> <regionId> [inspectionCapabilityId]");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (!session.Buffers.TryGetValue(regionId, out var buffer))
            return Fail(KernelError.InvalidTransition, "Unknown tracked region.");
        CapabilityId? capability = null;
        if (args.Length == 3)
        {
            if (!ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return Fail(KernelError.InvalidTransition, "Inspection capability id must be numeric.");
            capability = new CapabilityId(id);
        }
        var principal = session.ResolvePid(pid);
        if (!principal.IsSuccess) return Fail(principal.Error, principal.Message);
        var inspector = runtime.CreateAuthorityInspector(principal.Value, capability);
        if (!inspector.IsSuccess) return Fail(inspector.Error, inspector.Message);
        var explained = move ? inspector.Value!.WhyMoveBlocked(buffer.Handle) : inspector.Value!.WhyReclaimBlocked(buffer.Handle);
        if (!explained.IsSuccess) return Fail(explained.Error, explained.Message);
        WriteAuthorityExplanation($"{command} region={regionId}", explained.Value!);
        return true;
    }

    private static bool ExplainExternalAuthority(EmulatorSession session, string[] args)
    {
        if (args.Length is < 2 or > 3 || !TryReadPid(args, 0, out var pid) ||
            !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var operationId))
            return Fail(KernelError.InvalidTransition, "usage: why-pinned <pid> <operationId> [inspectionCapabilityId]");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (!session.ExternalOperations.TryGetValue(operationId, out var tracked))
            return Fail(KernelError.InvalidTransition, "Unknown tracked external operation.");
        CapabilityId? capability = null;
        if (args.Length == 3)
        {
            if (!ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return Fail(KernelError.InvalidTransition, "Inspection capability id must be numeric.");
            capability = new CapabilityId(id);
        }
        var principal = session.ResolvePid(pid);
        if (!principal.IsSuccess) return Fail(principal.Error, principal.Message);
        var inspector = runtime.CreateAuthorityInspector(principal.Value, capability);
        if (!inspector.IsSuccess) return Fail(inspector.Error, inspector.Message);
        var explained = inspector.Value!.WhyExternalOperationPinned(tracked.Operation);
        if (!explained.IsSuccess) return Fail(explained.Error, explained.Message);
        WriteAuthorityExplanation($"pinned operation={operationId}:{tracked.Operation.Generation.Value}", explained.Value!);
        return true;
    }

    private static void WriteAuthorityExplanation(string subject, AuthorityInspectionExplanation value)
    {
        EmulatorConsole.Info($"[authority] {subject}: blocked={value.IsBlocked}, reasons={value.Reasons.Count}, scope={value.Snapshot.Scope}, consistency={value.Snapshot.Consistency}\r\n");
        EmulatorConsole.Table(["reason", "blocking-node", "detail", "redacted"],
            value.Reasons.Take(100).Select(static reason => (IReadOnlyList<string>)new[]
            {
                reason.Kind.ToString(), reason.BlockingNode?.Value ?? "none", reason.Detail, reason.Redacted.ToString(),
            }).ToArray());
        if (value.Reasons.Count > 100)
            EmulatorConsole.Detail($"[authority] {value.Reasons.Count - 100} reasons omitted\r\n");
        EmulatorConsole.Info("[authority] observation only; absence of visible blockers grants no mutation authority\r\n");
    }

}
