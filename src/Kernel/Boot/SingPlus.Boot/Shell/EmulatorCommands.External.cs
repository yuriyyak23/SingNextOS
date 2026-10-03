using System.Globalization;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool PrepareExternal(EmulatorSession session, string[] args)
    {
        if (args.Length != 2)
            return Fail(KernelError.InvalidTransition, "usage: external-prepare <pid> <regionId>");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (!TryReadPid(args, 0, out var pid) || !TryReadBuffer(session, args, 1, out var buffer)) return false;
        var owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        if (owner.Value != buffer.Owner) return Fail(KernelError.WrongRegionOwner, "Process does not own the tracked buffer.");
        var prepared = runtime.PrepareExternalOperation(owner.Value,
            [new OperationRegionUseRequest(buffer.Handle, RegionUseMode.ExclusiveWrite, new RegionUseRange(0, buffer.Length))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged);
        if (!prepared.IsSuccess) return Fail(prepared.Error, prepared.Message);
        var operation = prepared.Value!.Operation;
        session.ExternalOperations.Add(operation.OperationId.Value, (owner.Value, operation));
        EmulatorConsole.Ok($"[kernel] external operation {operation.OperationId.Value}:{operation.Generation.Value} prepared; no provider submission\r\n");
        return true;
    }

    private static bool ExternalAction(EmulatorSession session, string[] args, string action)
    {
        if ((args.Length != 2 && !(args.Length == 3 && action is "admit" or "cancel")) || !TryReadPid(args, 0, out var pid) ||
            !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return Fail(KernelError.InvalidTransition, $"usage: external{(action == "query" ? "" : "-" + action)} <pid> <operationId>");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (!session.ExternalOperations.TryGetValue(id, out var tracked))
            return Fail(KernelError.InvalidTransition, "Operation is not a shell-created pre-submit operation.");
        var owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        CancellationScopeHandle? scope = null;
        if (args.Length == 3)
        {
            if (!TryReadCancelScope(session, args[2], out var readScope)) return false;
            scope = readScope;
        }
        // Do not substitute the captured owner: runtime must check the supplied principal.
        if (action == "admit")
        {
            var admitted = runtime.AdmitExternalOperation(owner.Value, tracked.Operation, default, cancellationScope: scope);
            if (!admitted.IsSuccess) return Fail(admitted.Error, admitted.Message);
        }
        else if (action == "cancel")
        {
            if (scope is { } boundScope)
            {
                var requested = runtime.RequestExternalOperationCancellation(owner.Value, tracked.Operation, boundScope, false);
                if (!requested.IsSuccess) return Fail(requested.Error, requested.Message);
                WriteCancelObservation(requested.Value!);
                if (requested.Value!.Disposition == CancellationDisposition.Stale)
                    return Fail(KernelError.StaleGeneration, "Cancellation scope is stale.");
            }
            else
            {
                var cancelled = runtime.CancelExternalOperation(owner.Value, tracked.Operation, false);
                if (!cancelled.IsSuccess) return Fail(cancelled.Error, cancelled.Message);
            }
        }
        else if (action == "release")
        {
            var released = runtime.ReleaseExternalOperation(owner.Value, tracked.Operation, default);
            if (!released.IsSuccess) return Fail(released.Error, released.Message);
        }
        var queried = runtime.QueryExternalOperation(owner.Value, tracked.Operation);
        if (!queried.IsSuccess) return Fail(queried.Error, queried.Message);
        var value = queried.Value!;
        EmulatorConsole.Info($"[kernel] external #{id}:{value.Operation.Generation.Value}: {value.State}, {value.Disposition}; binding={(value.Binding is null ? "none" : "present")}; boundary={value.EffectBoundary}\r\n");
        EmulatorConsole.Table(["sequence", "from", "to", "event"], value.Transitions.Select(static item =>
            (IReadOnlyList<string>)new[] { item.Sequence.ToString(), item.From.ToString(), item.To.ToString(), item.Event }).ToArray());
        return true;
    }

    private static bool ShowRegionUses(EmulatorSession session, string[] args)
    {
        ulong? region = null;
        if (args.Length > 1 || (args.Length == 1 && !ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return Fail(KernelError.InvalidTransition, "usage: region-uses [regionId]");
        if (args.Length == 1) region = ulong.Parse(args[0], CultureInfo.InvariantCulture);
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        var uses = runtime.Regions.SnapshotUses().Where(use => region is null || use.Region.RegionId.Value == region).ToArray();
        EmulatorConsole.Table(["use", "region", "region gen", "domain", "owner gen", "mode", "offset", "length", "state"],
            uses.Select(static use => (IReadOnlyList<string>)new[] { use.Handle.UseId.Value.ToString(),
                use.Region.RegionId.Value.ToString(), use.Region.Generation.Value.ToString(), use.Principal.DomainId.Value.ToString(),
                use.Principal.ProcessGeneration.ToString(), use.Mode.ToString(), use.Range.Offset.ToString(),
                use.Range.Length.ToString(), use.State.ToString() }).ToArray());
        EmulatorConsole.Info("[emulator] host-debug snapshot; Region use protects runtime effects/reclaim, not CLR Span access\r\n");
        return true;
    }
}
