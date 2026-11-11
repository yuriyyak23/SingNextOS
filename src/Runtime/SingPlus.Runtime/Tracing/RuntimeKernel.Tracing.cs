using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<TraceSessionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _traceBudgetReservations = [];
    private readonly object _traceBudgetReservationsGate = new();

    public KernelResult<TraceSessionAdmission> StartTraceSession(
        ProcessHandle owner,
        int producerCapacity,
        TraceOverflowPolicy overflowPolicy = TraceOverflowPolicy.DropWithMarker)
    {
        lock (_platformMemoryUseGate)
            return StartTraceSessionLocked(owner, producerCapacity, overflowPolicy);
    }

    private KernelResult<TraceSessionAdmission> StartTraceSessionLocked(
        ProcessHandle owner, int producerCapacity, TraceOverflowPolicy overflowPolicy)
    {
        KernelResult<SingProcess> process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(process.Error, process.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(effect.Error, effect.Message!);
        ulong bytes = checked((ulong)producerCapacity * 256UL);
        KernelResult<BudgetReservationHandle?> budget = ReserveAttachedBudget(owner, [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, bytes)],
            BudgetReservationLifetime.TraceTelemetryBuffer);
        if (!budget.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(budget.Error, budget.Message!);
        // Budget trace publication can invoke the configured clock and reenter
        // lifecycle code. The admission lock does not preserve that permission.
        KernelResult<SingProcess> fresh = Processes.Resolve(owner);
        KernelResult freshEffect = fresh.IsSuccess ? EnsureProcessAcceptsNewEffects(fresh.Value!) : KernelResult.Fail(fresh.Error, fresh.Message!);
        if (!freshEffect.IsSuccess)
        {
            _ = ReleaseAttachedBudget(owner, budget.Value);
            return KernelResult<TraceSessionAdmission>.Fail(freshEffect.Error, freshEffect.Message!);
        }
        KernelResult<TraceSessionAdmission> started = Traces.Start(owner, producerCapacity, overflowPolicy, TraceVisibilityClass.Self);
        if (!started.IsSuccess)
        {
            _ = ReleaseAttachedBudget(owner, budget.Value);
            return started;
        }
        if (budget.Value is { } reservation)
            lock (_traceBudgetReservationsGate)
                _traceBudgetReservations.Add(started.Value!.Session, (owner, reservation));
        return started;
    }

    public KernelResult<TraceSnapshot> InspectTrace(
        ProcessHandle requester,
        TraceSessionHandle session,
        CapabilityId? inspectionCapability = null)
    {
        KernelResult<SingProcess> process = Processes.Resolve(requester);
        if (!process.IsSuccess) return KernelResult<TraceSnapshot>.Fail(process.Error, process.Message!);
        KernelResult<TraceSnapshot> snapshot = Traces.Snapshot(session);
        if (!snapshot.IsSuccess) return snapshot;
        if (snapshot.Value!.Session.Owner == requester) return snapshot;
        if (inspectionCapability is not { } capabilityId)
            return KernelResult<TraceSnapshot>.Fail(KernelError.ProjectionDenied, "Cross-process trace inspection requires a dedicated capability.");
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(requester, capabilityId, CapabilityRights.Read);
        if (!capability.IsSuccess) return KernelResult<TraceSnapshot>.Fail(capability.Error, capability.Message!);
        return capability.Value!.ResourceKind == ResourceKind.KernelService &&
               capability.Value.ResourceId == CapabilityResourceIds.TraceInspection
            ? snapshot
            : KernelResult<TraceSnapshot>.Fail(KernelError.ProjectionDenied, "The capability does not authorize trace inspection.");
    }

    public KernelResult<TraceSessionAdmission> StopTraceSession(ProcessHandle owner, TraceSessionHandle session)
    {
        KernelResult<SingProcess> process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(process.Error, process.Message!);
        KernelResult<TraceSessionAdmission> stopped = Traces.Stop(owner, session);
        if (stopped.IsSuccess) ReleaseTraceBudget(session);
        return stopped;
    }

    public KernelResult RecordTraceSemanticEvent(
        ProcessHandle subject,
        TraceEventKind kind,
        TraceCausalContext context,
        TraceSemanticData data)
    {
        KernelResult<SingProcess> process = Processes.Resolve(subject);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return effect;
        return Traces.Record(subject, kind, context.Correlation, context.ParentCorrelation, data);
    }

    private void CloseTraceSessionsForProcess(ProcessHandle owner)
    {
        _ = Traces.StopAllForProcess(owner);
        // Previously stopped sessions can still hold refused accounting. The
        // actual teardown consumer routes those exact captured charges as well.
        TraceSessionHandle[] sessions;
        lock (_traceBudgetReservationsGate)
            sessions = _traceBudgetReservations.Where(item => item.Value.Owner == owner)
                .Select(item => item.Key).ToArray();
        foreach (TraceSessionHandle session in sessions) ReleaseTraceBudget(session);
    }

    private void ReleaseTraceBudget(TraceSessionHandle session)
    {
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge;
        lock (_traceBudgetReservationsGate)
            if (!_traceBudgetReservations.TryGetValue(session, out charge)) return;
        if (!ReleaseAttachedBudget(charge.Owner, charge.Reservation).IsSuccess) return;
        lock (_traceBudgetReservationsGate)
            if (_traceBudgetReservations.TryGetValue(session, out (ProcessHandle Owner, BudgetReservationHandle Reservation) current) && current == charge)
                _traceBudgetReservations.Remove(session);
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
        TraceCausalContext selected = context ?? new(new($"{resourceClass}:{resourceCorrelation}"));
        _ = Traces.Record(subject, kind, selected.Correlation, selected.ParentCorrelation,
            new(resourceClass, resourceCorrelation, state, outcome));
    }
}
