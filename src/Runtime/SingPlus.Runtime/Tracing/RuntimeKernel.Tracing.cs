using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<TraceSessionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _traceBudgetReservations = [];

    public KernelResult<TraceSessionAdmission> StartTraceSession(
        ProcessHandle owner,
        int producerCapacity,
        TraceOverflowPolicy overflowPolicy = TraceOverflowPolicy.DropWithMarker)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(process.Error, process.Message!);
        var effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(effect.Error, effect.Message!);
        var bytes = checked((ulong)producerCapacity * 256UL);
        var budget = ReserveAttachedBudget(owner, [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, bytes)],
            BudgetReservationLifetime.TraceTelemetryBuffer);
        if (!budget.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(budget.Error, budget.Message!);
        var started = Traces.Start(owner, producerCapacity, overflowPolicy, TraceVisibilityClass.Self);
        if (!started.IsSuccess)
        {
            _ = ReleaseAttachedBudget(owner, budget.Value);
            return started;
        }
        if (budget.Value is { } reservation)
            _traceBudgetReservations.Add(started.Value!.Session, (owner, reservation));
        return started;
    }

    public KernelResult<TraceSnapshot> InspectTrace(
        ProcessHandle requester,
        TraceSessionHandle session,
        CapabilityId? inspectionCapability = null)
    {
        var process = Processes.Resolve(requester);
        if (!process.IsSuccess) return KernelResult<TraceSnapshot>.Fail(process.Error, process.Message!);
        var snapshot = Traces.Snapshot(session);
        if (!snapshot.IsSuccess) return snapshot;
        if (snapshot.Value!.Session.Owner == requester) return snapshot;
        if (inspectionCapability is not { } capabilityId)
            return KernelResult<TraceSnapshot>.Fail(KernelError.ProjectionDenied, "Cross-process trace inspection requires a dedicated capability.");
        var capability = ValidateCapability(requester, capabilityId, CapabilityRights.Read);
        if (!capability.IsSuccess) return KernelResult<TraceSnapshot>.Fail(capability.Error, capability.Message!);
        return capability.Value!.ResourceKind == ResourceKind.KernelService &&
               capability.Value.ResourceId == CapabilityResourceIds.TraceInspection
            ? snapshot
            : KernelResult<TraceSnapshot>.Fail(KernelError.ProjectionDenied, "The capability does not authorize trace inspection.");
    }

    public KernelResult<TraceSessionAdmission> StopTraceSession(ProcessHandle owner, TraceSessionHandle session)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(process.Error, process.Message!);
        var stopped = Traces.Stop(owner, session);
        if (stopped.IsSuccess && _traceBudgetReservations.Remove(session, out var charge))
            _ = ReleaseAttachedBudget(charge.Owner, charge.Reservation);
        return stopped;
    }

    public KernelResult RecordTraceSemanticEvent(
        ProcessHandle subject,
        TraceEventKind kind,
        TraceCausalContext context,
        TraceSemanticData data)
    {
        var process = Processes.Resolve(subject);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return effect;
        return Traces.Record(subject, kind, context.Correlation, context.ParentCorrelation, data);
    }

    private void CloseTraceSessionsForProcess(ProcessHandle owner)
    {
        foreach (var session in Traces.StopAllForProcess(owner))
        {
            if (_traceBudgetReservations.Remove(session, out var charge))
                _ = ReleaseAttachedBudget(charge.Owner, charge.Reservation);
        }
    }

    private void RecordTrace(
        ProcessHandle subject,
        TraceEventKind kind,
        TraceCausalContext? context,
        string resourceClass,
        string resourceCorrelation,
        string state,
        string outcome)
    {
        var selected = context ?? new(new($"{resourceClass}:{resourceCorrelation}"));
        _ = Traces.Record(subject, kind, selected.Correlation, selected.ParentCorrelation,
            new(resourceClass, resourceCorrelation, state, outcome));
    }
}
