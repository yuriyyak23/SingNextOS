using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private sealed class TelemetrySubscriptionRecord
    {
        public required TelemetrySubscriptionAdmission Admission { get; set; }
        public required CapabilityId? InspectionCapability { get; init; }
        public required BudgetReservationHandle? BudgetReservation { get; init; }
        public Queue<StructuredTelemetrySnapshot> Queue { get; } = new();
        public ulong Dropped { get; set; }
    }

    private readonly object _telemetryGate = new();
    private readonly Dictionary<TelemetrySubscriptionId, TelemetrySubscriptionRecord> _telemetrySubscriptions = [];
    private ulong _nextTelemetrySubscriptionId = 1;
    private long _telemetryCaptureSequence;

    public KernelResult<StructuredTelemetrySnapshot> ProjectTelemetry(
        ProcessHandle requester,
        ProcessHandle subject,
        TelemetryProjectionClass projection = TelemetryProjectionClass.SelfOperational,
        CapabilityId? inspectionCapability = null)
    {
        var access = ValidateTelemetryProjection(requester, subject, projection, inspectionCapability);
        return access.IsSuccess
            ? BuildTelemetry(subject, projection, supervisor: null)
            : KernelResult<StructuredTelemetrySnapshot>.Fail(access.Error, access.Message!);
    }

    internal KernelResult<StructuredTelemetrySnapshot> ProjectSupervisorTelemetry(
        ProcessHandle subject,
        ServiceSupervisorSnapshot supervisor) =>
        BuildTelemetry(subject, TelemetryProjectionClass.ServiceAggregate, supervisor);

    public KernelResult<TelemetrySubscriptionAdmission> StartTelemetrySubscription(
        ProcessHandle owner,
        ProcessHandle subject,
        TelemetryProjectionClass projection,
        int capacity,
        TelemetrySubscriptionOverflowPolicy overflowPolicy,
        CapabilityId? inspectionCapability = null)
    {
        if (capacity <= 0 || capacity > StructuredTelemetryContract.MaximumSubscriptionEntries ||
            !Enum.IsDefined(overflowPolicy))
            return KernelResult<TelemetrySubscriptionAdmission>.Fail(KernelError.InvalidMessage, "Telemetry subscription configuration is invalid or unbounded.");
        var access = ValidateTelemetryProjection(owner, subject, projection, inspectionCapability);
        if (!access.IsSuccess) return KernelResult<TelemetrySubscriptionAdmission>.Fail(access.Error, access.Message!);
        var component = _components.Values.SingleOrDefault(record => record.Process == owner);
        if (component is null || component.Manifest.TelemetryPolicy.Visibility == ServiceTelemetryVisibility.None)
            return KernelResult<TelemetrySubscriptionAdmission>.Fail(KernelError.ProjectionDenied, "The owning manifest does not admit telemetry buffering.");
        var bytes = checked((ulong)capacity * StructuredTelemetryContract.EstimatedSnapshotBytes);
        if (bytes > component.Manifest.TelemetryPolicy.MaximumBufferedBytes)
            return KernelResult<TelemetrySubscriptionAdmission>.Fail(KernelError.BudgetExceeded, "Subscription exceeds the manifest telemetry-buffer policy.");
        var budget = ReserveAttachedBudget(owner, [new(ServiceBudgetDimension.TraceTelemetryBufferBytes, bytes)],
            BudgetReservationLifetime.TraceTelemetryBuffer);
        if (!budget.IsSuccess) return KernelResult<TelemetrySubscriptionAdmission>.Fail(budget.Error, budget.Message!);

        lock (_telemetryGate)
        {
            if (_nextTelemetrySubscriptionId == 0)
            {
                _ = ReleaseAttachedBudget(owner, budget.Value);
                return KernelResult<TelemetrySubscriptionAdmission>.Fail(KernelError.CapacityExhausted, "Telemetry subscription identity space is exhausted.");
            }
            var handle = new TelemetrySubscriptionHandle(new(_nextTelemetrySubscriptionId++), new(1));
            var admission = new TelemetrySubscriptionAdmission(handle, owner, subject, projection, capacity, bytes, overflowPolicy, TelemetrySubscriptionState.Active);
            _telemetrySubscriptions.Add(handle.SubscriptionId, new()
            {
                Admission = admission,
                InspectionCapability = inspectionCapability,
                BudgetReservation = budget.Value,
            });
            return KernelResult<TelemetrySubscriptionAdmission>.Ok(admission);
        }
    }

    public KernelResult SampleTelemetrySubscription(ProcessHandle owner, TelemetrySubscriptionHandle handle)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        TelemetrySubscriptionAdmission admission;
        CapabilityId? capability;
        lock (_telemetryGate)
        {
            var resolved = ResolveTelemetrySubscriptionLocked(owner, handle);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            admission = resolved.Value!.Admission;
            capability = resolved.Value.InspectionCapability;
            if (admission.State != TelemetrySubscriptionState.Active)
                return KernelResult.Fail(KernelError.TelemetryStopped, "Telemetry subscription is not active.");
        }

        var projected = ProjectTelemetry(owner, admission.Subject, admission.Projection, capability);
        if (!projected.IsSuccess) return KernelResult.Fail(projected.Error, projected.Message!);
        lock (_telemetryGate)
        {
            var resolved = ResolveTelemetrySubscriptionLocked(owner, handle);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.Admission.State != TelemetrySubscriptionState.Active)
                return KernelResult.Fail(KernelError.TelemetryStopped, "Telemetry subscription closed while sampling.");
            if (record.Queue.Count >= record.Admission.Capacity)
            {
                record.Dropped++;
                if (record.Admission.OverflowPolicy == TelemetrySubscriptionOverflowPolicy.RejectSample)
                    return KernelResult.Fail(KernelError.TelemetryBackpressure, "The bounded telemetry subscription is full.");
                if (record.Admission.OverflowPolicy == TelemetrySubscriptionOverflowPolicy.StopSubscription)
                {
                    record.Admission = record.Admission with { State = TelemetrySubscriptionState.OverflowStopped };
                    return KernelResult.Fail(KernelError.TelemetryStopped, "Telemetry subscription stopped on overflow.");
                }
                record.Queue.Dequeue();
            }
            record.Queue.Enqueue(projected.Value!);
            return KernelResult.Ok();
        }
    }

    public KernelResult<TelemetrySubscriptionBatch> ReadTelemetrySubscription(
        ProcessHandle owner,
        TelemetrySubscriptionHandle handle)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<TelemetrySubscriptionBatch>.Fail(process.Error, process.Message!);
        lock (_telemetryGate)
        {
            var resolved = ResolveTelemetrySubscriptionLocked(owner, handle);
            if (!resolved.IsSuccess) return KernelResult<TelemetrySubscriptionBatch>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            var snapshots = record.Queue.ToArray();
            record.Queue.Clear();
            return KernelResult<TelemetrySubscriptionBatch>.Ok(new(record.Admission, snapshots, record.Dropped, record.Dropped == 0));
        }
    }

    public KernelResult<TelemetrySubscriptionAdmission> CloseTelemetrySubscription(
        ProcessHandle owner,
        TelemetrySubscriptionHandle handle)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<TelemetrySubscriptionAdmission>.Fail(process.Error, process.Message!);
        BudgetReservationHandle? reservation;
        TelemetrySubscriptionAdmission admission;
        lock (_telemetryGate)
        {
            var resolved = ResolveTelemetrySubscriptionLocked(owner, handle);
            if (!resolved.IsSuccess) return KernelResult<TelemetrySubscriptionAdmission>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            record.Admission = record.Admission with { State = TelemetrySubscriptionState.Closed };
            admission = record.Admission;
            reservation = record.BudgetReservation;
        }
        if (reservation is { } exact)
        {
            var released = ReleaseAttachedBudget(owner, exact);
            if (!released.IsSuccess) return KernelResult<TelemetrySubscriptionAdmission>.Fail(released.Error, released.Message!);
        }
        return KernelResult<TelemetrySubscriptionAdmission>.Ok(admission);
    }

    private KernelResult ValidateTelemetryProjection(
        ProcessHandle requester,
        ProcessHandle subject,
        TelemetryProjectionClass projection,
        CapabilityId? inspectionCapability)
    {
        if (!Enum.IsDefined(projection)) return KernelResult.Fail(KernelError.InvalidMessage, "Telemetry projection class is invalid.");
        var caller = Processes.Resolve(requester);
        if (!caller.IsSuccess) return KernelResult.Fail(caller.Error, caller.Message!);
        var target = Processes.Resolve(subject);
        if (!target.IsSuccess) return KernelResult.Fail(target.Error, target.Message!);
        var component = _components.Values.SingleOrDefault(record => record.Process == subject);
        if (requester == subject && projection == TelemetryProjectionClass.SelfOperational)
            return component is not null && component.Manifest.TelemetryPolicy.Visibility >= ServiceTelemetryVisibility.Self
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.ProjectionDenied, "The manifest does not admit self operational telemetry.");
        if (requester == subject && projection == TelemetryProjectionClass.ServiceAggregate)
            return component is not null && component.Manifest.TelemetryPolicy.Visibility >= ServiceTelemetryVisibility.ServiceAggregate
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.ProjectionDenied, "The manifest does not admit service aggregate telemetry.");
        if (inspectionCapability is not { } capabilityId)
            return KernelResult.Fail(KernelError.ProjectionDenied, "Cross-service or privileged telemetry requires a dedicated capability.");
        var capability = ValidateCapability(requester, capabilityId, CapabilityRights.Read);
        return capability.IsSuccess && capability.Value!.ResourceKind == ResourceKind.KernelService &&
               capability.Value.ResourceId == CapabilityResourceIds.TelemetryInspection
            ? KernelResult.Ok()
            : KernelResult.Fail(capability.IsSuccess ? KernelError.ProjectionDenied : capability.Error,
                capability.IsSuccess ? "The capability does not authorize telemetry inspection." : capability.Message!);
    }

    private KernelResult<StructuredTelemetrySnapshot> BuildTelemetry(
        ProcessHandle subject,
        TelemetryProjectionClass projection,
        ServiceSupervisorSnapshot? supervisor)
    {
        var process = Processes.Resolve(subject);
        if (!process.IsSuccess) return KernelResult<StructuredTelemetrySnapshot>.Fail(process.Error, process.Message!);
        var component = _components.Values.SingleOrDefault(record => record.Process == subject);
        var owner = new RegionOwner(process.Value!.DomainId, subject.Generation);
        var regions = Regions.InspectionSnapshot().Where(region => region.Region.Owner == owner && region.Region.State != RegionState.Released).ToArray();
        var pinned = regions.Count(region => region.Borrow is not null || region.BackingLease is not null ||
                                             region.PlatformMappingReserved || region.ExternalBorrowReadGrantReserved ||
                                             region.Uses.Any(static use => use.State == RegionUseState.Active));
        var operations = ExternalOperations.InspectionSnapshot().Where(operation => operation.Principal == owner).ToArray();
        var scopes = CancellationScopes.InspectionSnapshot(subject);
        var trace = Traces.InspectionSummary(subject);
        var ipc = Channels.InspectionSummary(subject);
        BudgetUsage[] usage = [];
        if (component is not null)
        {
            var budget = Budgets.Query(component.ProcessBudget);
            if (budget.IsSuccess) usage = budget.Value!.Usage.ToArray();
        }
        int committed;
        int failed;
        int deleted;
        ulong stored;
        lock (_checkpointGate)
        {
            var checkpoints = _checkpoints.Values.Where(record => record.SourceProcess == subject).ToArray();
            committed = checkpoints.Count(record => record.State == CheckpointLifecycleState.Committed);
            failed = checkpoints.Count(record => record.State == CheckpointLifecycleState.Failed);
            deleted = checkpoints.Count(record => record.State == CheckpointLifecycleState.Deleted);
            stored = (ulong)checkpoints.Where(record => record.State == CheckpointLifecycleState.Committed && record.Image is not null)
                .Sum(record => (long)record.Image!.LogicalState.Length + record.Image.Regions.Sum(region => (long)region.Content.Length));
        }
        var service = supervisor is null ? null : new TelemetryServiceMetrics(
            supervisor.State, supervisor.Health?.State, supervisor.RestartAttemptsInWindow,
            supervisor.State == ServiceLifecycleState.Degraded
                ? supervisor.Dependencies.Count(binding => binding.Kind == ServiceDependencyKind.Optional)
                : 0,
            supervisor.State is ServiceLifecycleState.Quarantined or ServiceLifecycleState.CrashLoop || supervisor.BlockingReason is not null);
        var snapshot = new StructuredTelemetrySnapshot(
            subject, projection, checked((ulong)Interlocked.Increment(ref _telemetryCaptureSequence)), _operabilityTimeProvider.GetTimestamp(), usage,
            new(regions.Aggregate(0UL, static (sum, region) => checked(sum + (ulong)region.Region.ByteLength)), regions.Length, pinned, pinned,
                ipc.Channels, component?.Sessions.Count ?? 0, ipc.QueuedMessages),
            new(operations.Count(operation => operation.State == ExternalOperationState.Prepared),
                operations.Count(operation => operation.State == ExternalOperationState.Admitted),
                operations.Count(operation => operation.State == ExternalOperationState.Submitted),
                operations.Count(operation => operation.State == ExternalOperationState.DeviceComplete),
                operations.Count(operation => operation.State == ExternalOperationState.Visible),
                operations.Count(operation => operation.State == ExternalOperationState.Published),
                operations.Count(operation => operation.State == ExternalOperationState.Released),
                operations.Count(operation => operation.State != ExternalOperationState.Released &&
                                              operation.Disposition is ExternalOperationDisposition.CancellationPending or ExternalOperationDisposition.ProviderLost or ExternalOperationDisposition.Faulted)),
            new(scopes.Count(scope => scope.Disposition == CancellationDisposition.Active),
                scopes.Count(scope => scope.Disposition == CancellationDisposition.CancellationRequested),
                scopes.Count(scope => scope.Timeout == TimeoutDisposition.ExpiredWaitingMayStop),
                scopes.Count(scope => scope.Disposition == CancellationDisposition.TooLateEffectMayExist),
                scopes.Count(scope => scope.Disposition == CancellationDisposition.ProviderEffectContained)),
            new(committed, failed, deleted, stored), new(trace.Sessions, trace.BufferedEvents, trace.DroppedEvents), service,
            projection == TelemetryProjectionClass.PrivilegedSystemDiagnostics ? 0 : 1);
        return KernelResult<StructuredTelemetrySnapshot>.Ok(snapshot);
    }

    private KernelResult<TelemetrySubscriptionRecord> ResolveTelemetrySubscriptionLocked(
        ProcessHandle owner,
        TelemetrySubscriptionHandle handle)
    {
        if (!_telemetrySubscriptions.TryGetValue(handle.SubscriptionId, out var record))
            return KernelResult<TelemetrySubscriptionRecord>.Fail(KernelError.TelemetrySubscriptionNotFound, "Telemetry subscription was not found.");
        if (record.Admission.Subscription != handle)
            return KernelResult<TelemetrySubscriptionRecord>.Fail(KernelError.StaleGeneration, "Telemetry subscription generation is stale.");
        return record.Admission.Owner == owner
            ? KernelResult<TelemetrySubscriptionRecord>.Ok(record)
            : KernelResult<TelemetrySubscriptionRecord>.Fail(KernelError.ProjectionDenied, "Telemetry subscription belongs to another process generation.");
    }

    private void CloseTelemetrySubscriptionsForProcess(ProcessHandle process)
    {
        (ProcessHandle Owner, BudgetReservationHandle? Reservation)[] closing;
        lock (_telemetryGate)
        {
            closing = _telemetrySubscriptions.Values
                .Where(record => record.Admission.Owner == process || record.Admission.Subject == process)
                .Select(record => (record.Admission.Owner, record.BudgetReservation))
                .ToArray();
            foreach (var record in _telemetrySubscriptions.Values.Where(record => record.Admission.Owner == process || record.Admission.Subject == process))
                record.Admission = record.Admission with { State = TelemetrySubscriptionState.Closed };
        }
        foreach (var item in closing)
            if (item.Reservation is { } reservation) _ = ReleaseAttachedBudget(item.Owner, reservation);
    }
}
