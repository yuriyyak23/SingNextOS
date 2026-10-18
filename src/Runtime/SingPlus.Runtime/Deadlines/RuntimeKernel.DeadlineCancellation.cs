using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<CancellationObservation> CreateCancellationScope(
        ProcessHandle owner,
        MonotonicDeadline? deadline = null,
        CancellationScopeHandle? parent = null)
    {
        var timestamp = CancellationScopes.CaptureTimestamp(deadline, parent);
        KernelResult<CancellationObservation> created;
        lock (_platformMemoryUseGate)
        {
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess)
                return KernelResult<CancellationObservation>.Fail(process.Error, process.Message!);
            var effect = EnsureProcessAcceptsNewEffects(process.Value!);
            if (!effect.IsSuccess)
                return KernelResult<CancellationObservation>.Fail(effect.Error, effect.Message!);
            created = CancellationScopes.Create(owner, deadline, parent, timestamp);
        }
        if (created.IsSuccess)
            RecordTrace(owner, TraceEventKind.DeadlineCancellation, null, "cancellation-scope",
                created.Value!.Scope.ScopeId.Value.ToString(), created.Value.Disposition.ToString(), "created");
        return created;
    }

    public KernelResult<CancellationObservation> RequestCancellation(
        ProcessHandle owner,
        CancellationScopeHandle scope)
    {
        var timestamp = CancellationScopes.CaptureTimestamp(scope);
        KernelResult<CancellationObservation> requested;
        lock (_platformMemoryUseGate)
        {
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess)
                return process.Error == KernelError.StaleGeneration
                    ? KernelResult<CancellationObservation>.Ok(new(scope, owner, null, null, false,
                        TimeoutDisposition.NotExpired, CancellationDisposition.Stale, 0))
                    : KernelResult<CancellationObservation>.Fail(process.Error, process.Message!);
            requested = CancellationScopes.Request(owner, scope, timestamp);
        }
        if (requested.IsSuccess)
            RecordTrace(owner, TraceEventKind.DeadlineCancellation, null, "cancellation-scope",
                scope.ScopeId.Value.ToString(), requested.Value!.Disposition.ToString(), "requested");
        return requested;
    }

    public KernelResult<CancellationObservation> ObserveCancellation(
        ProcessHandle owner,
        CancellationScopeHandle scope)
    {
        var timestamp = CancellationScopes.CaptureTimestamp(scope);
        lock (_platformMemoryUseGate)
        {
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess)
                return process.Error == KernelError.StaleGeneration
                    ? KernelResult<CancellationObservation>.Ok(new(scope, owner, null, null, false,
                        TimeoutDisposition.NotExpired, CancellationDisposition.Stale, 0))
                    : KernelResult<CancellationObservation>.Fail(process.Error, process.Message!);
            return CancellationScopes.Observe(owner, scope, timestamp);
        }
    }
}
