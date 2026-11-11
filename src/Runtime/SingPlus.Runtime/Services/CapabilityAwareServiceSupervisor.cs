using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class ManagedServiceDefinition
{
    public ManagedServiceDefinition(
        ComponentAdmissionPlan admission,
        string primaryServiceName,
        ServiceReadinessPolicy readinessPolicy = ServiceReadinessPolicy.HardDependenciesBound,
        ServiceFailurePropagationPolicy dependencyFailurePolicy = ServiceFailurePropagationPolicy.DegradeDependent,
        Func<ulong, ComponentAdmissionPlan>? replacementFactory = null)
    {
        Admission = admission ?? throw new ArgumentNullException(nameof(admission));
        if (string.IsNullOrWhiteSpace(primaryServiceName)) throw new ArgumentException("Primary service name is required.", nameof(primaryServiceName));
        if (!Enum.IsDefined(readinessPolicy)) throw new ArgumentOutOfRangeException(nameof(readinessPolicy));
        if (!Enum.IsDefined(dependencyFailurePolicy)) throw new ArgumentOutOfRangeException(nameof(dependencyFailurePolicy));
        if (!admission.Manifest.ProvidedContracts.Any(provided => string.Equals(provided.ServiceName, primaryServiceName, StringComparison.Ordinal)))
            throw new ArgumentException("Primary service must be declared by the manifest.", nameof(primaryServiceName));
        PrimaryServiceName = primaryServiceName;
        ReadinessPolicy = readinessPolicy;
        DependencyFailurePolicy = dependencyFailurePolicy;
        ReplacementFactory = replacementFactory;
    }

    public ComponentAdmissionPlan Admission { get; }
    public string PrimaryServiceName { get; }
    public ServiceReadinessPolicy ReadinessPolicy { get; }
    public ServiceFailurePropagationPolicy DependencyFailurePolicy { get; }
    public Func<ulong, ComponentAdmissionPlan>? ReplacementFactory { get; }
    public ComponentIdentity Identity => Admission.Manifest.Identity;
}

public sealed class CapabilityAwareServiceSupervisor
{
    private sealed class Record(ManagedServiceDefinition definition)
    {
        public ManagedServiceDefinition Definition { get; set; } = definition;
        public ServiceLifecycleState State { get; set; } = ServiceLifecycleState.Declared;
        public ServiceInstanceHandle? Instance { get; set; }
        public ServiceHealthSnapshot? Health { get; set; }
        public List<ServiceDependencyBinding> Bindings { get; } = [];
        public Dictionary<ServiceContractIdentity, ServiceInstanceHandle> ProvidedInstances { get; } = [];
        public List<long> RestartAttempts { get; } = [];
        public long? RestartEligibleTimestamp { get; set; }
        public string? BlockingReason { get; set; }
        public bool RestartAfterDrain { get; set; }
        public bool RestartDispatchPending { get; set; }
    }

    private readonly RuntimeKernel _kernel;
    private sealed record DependencyRebindWork(ManagedServiceDefinition Definition, ServiceInstanceHandle Dependent,
        ServiceLifecycleState State, ServiceDependencyBinding[] Bindings, ServiceEndpointDescriptor Descriptor,
        ServiceDependencyRequirementV1 Dependency, ServiceInstanceHandle Provider);
    private readonly ProcessHandle _principal;
    private readonly CapabilityId _controlCapability;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ComponentIdentity, Record> _records = [];
    private readonly object _gate = new();
    internal Action? BeforeFailureIntentQualificationHook { get; set; }
    internal Action? BeforeStartIntentQualificationHook { get; set; }
    internal Action? BeforeFailureSnapshotQualificationHook { get; set; }
    internal Action? BeforeReplacementRetirementQualificationHook { get; set; }
    internal Action? AfterFailureCompletionQualificationHook { get; set; }

    internal CapabilityAwareServiceSupervisor(
        RuntimeKernel kernel,
        ProcessHandle principal,
        CapabilityId controlCapability,
        TimeProvider timeProvider)
    {
        _kernel = kernel;
        _principal = principal;
        _controlCapability = controlCapability;
        _timeProvider = timeProvider;
    }

    public KernelResult Register(ManagedServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return control;
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            return _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
            {
                if (_records.ContainsKey(definition.Identity))
                    return KernelResult.Fail(KernelError.DuplicateIdentity, $"Managed service '{definition.Identity.Name}' is already registered.");
                _records.Add(definition.Identity, new(definition));
                KernelResult graph = ValidateAcyclicGraphLocked();
                if (!graph.IsSuccess)
                {
                    _records.Remove(definition.Identity);
                    return graph;
                }
                return KernelResult.Ok();
            });
        }
    }

    public KernelResult<ServiceStartReceipt[]> StartAll()
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceStartReceipt[]>.Fail(control.Error, control.Message!);
        ComponentIdentity[] identities;
        lock (_gate) identities = TopologicalOrderLocked().ToArray();
        List<ServiceStartReceipt> receipts = new List<ServiceStartReceipt>(identities.Length);
        foreach (ComponentIdentity identity in identities)
        {
            KernelResult<ServiceStartReceipt> started = Start(identity);
            if (!started.IsSuccess) return KernelResult<ServiceStartReceipt[]>.Fail(started.Error, started.Message!);
            receipts.Add(started.Value!);
        }
        return KernelResult<ServiceStartReceipt[]>.Ok(receipts.ToArray());
    }

    public KernelResult<ServiceStartReceipt> Start(ComponentIdentity identity)
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(control.Error, control.Message!);

        ManagedServiceDefinition definition;
        ComponentIdentity[] hardProviders;
        lock (_gate)
        {
            if (!_records.TryGetValue(identity, out Record? record))
                return KernelResult<ServiceStartReceipt>.Fail(KernelError.ComponentNotFound, $"Managed service '{identity.Name}' is not registered.");
            if (record.State is ServiceLifecycleState.Ready or ServiceLifecycleState.Degraded)
                return KernelResult<ServiceStartReceipt>.Ok(new(new(identity, record.Definition.Admission.Manifest.NormalizedDigest), Snapshot(record)));
            if (record.State != ServiceLifecycleState.Declared)
                return KernelResult<ServiceStartReceipt>.Fail(KernelError.InvalidTransition, $"Managed service state {record.State} cannot start.");
            definition = record.Definition;
            hardProviders = ResolveProviderIdentitiesLocked(definition, ServiceDependencyKind.Hard).ToArray();
            if (hardProviders.Length != definition.Admission.Manifest.Dependencies.Count(dependency => dependency.Kind == ServiceDependencyKind.Hard))
                return KernelResult<ServiceStartReceipt>.Fail(KernelError.DependencyUnavailable, "Every hard dependency must be provided by exactly one registered managed service.");
        }

        foreach (ComponentIdentity provider in hardProviders)
        {
            KernelResult<ServiceStartReceipt> started = Start(provider);
            if (!started.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(KernelError.DependencyUnavailable, started.Message ?? "Hard dependency failed to start.");
        }

        BeforeStartIntentQualificationHook?.Invoke();
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult intent = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    Record record = _records[identity];
                    if (record.State != ServiceLifecycleState.Declared || !ReferenceEquals(record.Definition, definition))
                        return KernelResult.Fail(KernelError.InvalidTransition, "Managed definition changed during start.");
                    record.State = ServiceLifecycleState.Admitting;
                    record.Health = null;
                    record.BlockingReason = null;
                    return KernelResult.Ok();
                });
            if (!intent.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(intent.Error, intent.Message!);
        }

        KernelResult<ComponentLifecycleSnapshot> admitted = _kernel.AdmitComponent(definition.Admission, _principal, _controlCapability);
        if (!admitted.IsSuccess)
        {
            lock (_gate)
            {
                if (_records.TryGetValue(identity, out Record? record) &&
                    ReferenceEquals(record.Definition, definition) && record.State == ServiceLifecycleState.Admitting)
                {
                    record.State = ServiceLifecycleState.Failed;
                    record.BlockingReason = admitted.Message;
                    record.Health = null;
                }
            }
            return KernelResult<ServiceStartReceipt>.Fail(admitted.Error, admitted.Message!);
        }

        return CompleteAdmission(identity, definition, admitted.Value!);
    }

    public KernelResult<ServiceSupervisorSnapshot> Query(ComponentIdentity identity)
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(control.Error, control.Message!);
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ServiceSupervisorSnapshot? snapshot = null;
            KernelResult committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    if (!_records.TryGetValue(identity, out Record? record))
                        return KernelResult.Fail(KernelError.ComponentNotFound, $"Managed service '{identity.Name}' is not registered.");
                    snapshot = Snapshot(record);
                    return KernelResult.Ok();
                });
            return committed.IsSuccess ? KernelResult<ServiceSupervisorSnapshot>.Ok(snapshot!)
                : KernelResult<ServiceSupervisorSnapshot>.Fail(committed.Error, committed.Message!);
        }
    }

    public KernelResult<StructuredTelemetrySnapshot> ProjectTelemetry(ServiceInstanceHandle instance)
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<StructuredTelemetrySnapshot>.Fail(control.Error, control.Message!);
        ServiceSupervisorSnapshot snapshot;
        lock (_gate)
        {
            KernelResult<Record> exact = ResolveExactLocked(instance);
            if (!exact.IsSuccess) return KernelResult<StructuredTelemetrySnapshot>.Fail(exact.Error, exact.Message!);
            snapshot = Snapshot(exact.Value!);
        }
        return _kernel.ProjectSupervisorTelemetry(_principal, _controlCapability, instance.Process, snapshot);
    }

    public KernelResult<ServiceHealthSnapshot> ReportHealth(ServiceInstanceHandle instance, ServiceHealthState state, string reason)
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(control.Error, control.Message!);
        if (!Enum.IsDefined(state)) return KernelResult<ServiceHealthSnapshot>.Fail(KernelError.InvalidTransition, "Health state is undefined.");
        ServiceHealthSnapshot health;
        Record sourceRecord;
        ServiceLifecycleState sourceState;
        ServiceHealthSnapshot? sourceHealth;
        string? sourceReason;
        // Validate the local instance before observing time, then revalidate after the callback.
        lock (_gate)
        {
            KernelResult<Record> initial = ResolveExactLocked(instance);
            if (!initial.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(initial.Error, initial.Message!);
            sourceRecord = initial.Value!;
            sourceState = sourceRecord.State;
            sourceHealth = sourceRecord.Health;
            sourceReason = sourceRecord.BlockingReason;
        }
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceHealthSnapshot>.Fail(KernelError.PlatformFaulted, "Supervisor health clock observation failed.");
        }
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ServiceHealthSnapshot observation = new ServiceHealthSnapshot(instance, state, timestamp, reason ?? string.Empty);
            KernelResult committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    if (!ReferenceEquals(exact.Value, sourceRecord) || exact.Value!.State != sourceState ||
                        exact.Value.Health != sourceHealth || exact.Value.BlockingReason != sourceReason)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Health source changed during clock observation.");
                    exact.Value.Health = observation;
                    if (state == ServiceHealthState.Degraded && exact.Value.State == ServiceLifecycleState.Ready)
                        exact.Value.State = ServiceLifecycleState.Degraded;
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(committed.Error, committed.Message!);
            health = observation;
        }
        if (state is ServiceHealthState.Unhealthy or ServiceHealthState.Failed)
        {
            KernelResult<ServiceSupervisorSnapshot> failed = HandleFailure(instance, reason ?? string.Empty, sourceRecord, sourceState, health, sourceReason);
            if (!failed.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(failed.Error, failed.Message!);
            AfterFailureCompletionQualificationHook?.Invoke();
            health = failed.Value!.Health ?? health;
        }
        return KernelResult<ServiceHealthSnapshot>.Ok(health);
    }

    public KernelResult<ServiceDrainReceipt> Drain(ServiceInstanceHandle instance, bool planned = true) =>
        DrainCore(instance, planned, expectedSource: null);

    private KernelResult<ServiceDrainReceipt> DrainCore(ServiceInstanceHandle instance, bool planned,
        Func<KernelResult>? expectedSource)
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(control.Error, control.Message!);
        Record sourceRecord;
        ServiceLifecycleState sourceState;
        ServiceHealthSnapshot? sourceHealth;
        string? sourceReason;
        lock (_gate)
        {
            KernelResult<Record> initial = ResolveExactLocked(instance);
            if (!initial.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(initial.Error, initial.Message!);
            sourceRecord = initial.Value!;
            sourceState = sourceRecord.State;
            sourceHealth = sourceRecord.Health;
            sourceReason = sourceRecord.BlockingReason;
        }
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.PlatformFaulted, "Drain request clock observation failed.");
        }
        ComponentIdentity identity = default;
        ServiceHealthSnapshot health = new ServiceHealthSnapshot(instance, ServiceHealthState.Draining, timestamp,
            planned ? "Planned drain requested." : "Failure drain requested.");
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    if (!ReferenceEquals(exact.Value, sourceRecord) || exact.Value!.State != sourceState ||
                        exact.Value.Health != sourceHealth || exact.Value.BlockingReason != sourceReason)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Drain request source changed during clock observation.");
                    // Trusted pure policy source check shares the actual drain
                    // intent commit, after the last clock callback.
                    if (expectedSource is not null)
                    {
                        KernelResult policySource = expectedSource();
                        if (!policySource.IsSuccess) return policySource;
                    }
                    identity = exact.Value!.Definition.Identity;
                    exact.Value.State = ServiceLifecycleState.Draining;
                    exact.Value.Health = health;
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(committed.Error, committed.Message!);
        }

        KernelResult<ComponentLifecycleSnapshot> drained = _kernel.DrainComponent(identity, instance.Process);
        return CompleteDrain(identity, instance, planned, drained);
    }

    public KernelResult<ServiceDrainReceipt> Drain(
        ServiceInstanceHandle instance,
        CancellationScopeHandle scope,
        ServiceDrainTimeoutPolicy timeoutPolicy,
        bool planned = true)
    {
        if (!Enum.IsDefined(timeoutPolicy))
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.InvalidMessage, "Service drain timeout policy is invalid.");
        KernelResult<CancellationObservation> temporal = _kernel.ObserveCancellation(instance.Process, scope);
        if (!temporal.IsSuccess)
            return KernelResult<ServiceDrainReceipt>.Fail(temporal.Error, temporal.Message!);
        if (temporal.Value!.Disposition == CancellationDisposition.Stale)
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.StaleGeneration, "Service drain cancellation scope is stale.");
        if (temporal.Value.Timeout != TimeoutDisposition.ExpiredWaitingMayStop)
            return Drain(instance, planned);

        if (timeoutPolicy is ServiceDrainTimeoutPolicy.ContinueDraining or ServiceDrainTimeoutPolicy.RequireProvenContainment)
            return Drain(instance, planned);

        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(control.Error, control.Message!);
        Record sourceRecord;
        ServiceLifecycleState sourceState;
        ServiceHealthSnapshot? sourceHealth;
        string? sourceReason;
        lock (_gate)
        {
            KernelResult<Record> initial = ResolveExactLocked(instance);
            if (!initial.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(initial.Error, initial.Message!);
            sourceRecord = initial.Value!;
            sourceState = sourceRecord.State;
            sourceHealth = sourceRecord.Health;
            sourceReason = sourceRecord.BlockingReason;
        }
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.PlatformFaulted, "Drain timeout clock observation failed.");
        }
        bool quarantine = timeoutPolicy == ServiceDrainTimeoutPolicy.Quarantine;
        ServiceHealthSnapshot health = new ServiceHealthSnapshot(instance,
            quarantine ? ServiceHealthState.Quarantined : ServiceHealthState.Draining, timestamp,
            "Drain deadline expired; no closure or reclaim was inferred.");
        const string blockingReason = "Drain deadline expired before exact closure/containment.";
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ServiceDrainReceipt? receipt = null;
            KernelResult committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    Record record = exact.Value!;
                    if (!ReferenceEquals(record, sourceRecord) || record.State != sourceState ||
                        record.Health != sourceHealth || record.BlockingReason != sourceReason)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Drain timeout source changed during clock observation.");
                    record.State = quarantine ? ServiceLifecycleState.Quarantined : ServiceLifecycleState.Draining;
                    record.Health = health;
                    record.BlockingReason = blockingReason;
                    receipt = new ServiceDrainReceipt(new(instance, planned), Snapshot(record), ClosureProven: false);
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(committed.Error, committed.Message!);
            return quarantine ? KernelResult<ServiceDrainReceipt>.Ok(receipt!) :
                KernelResult<ServiceDrainReceipt>.Fail(KernelError.ReplacementBlocked, blockingReason);
        }
    }
    public KernelResult<ServiceReplacementReceipt> Replace(ServiceInstanceHandle previous, ManagedServiceDefinition next)
    {
        ArgumentNullException.ThrowIfNull(next);
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(control.Error, control.Message!);
        Record? dispatchRecord = null;
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult validation = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(previous);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    KernelResult contract = ValidateReplacementDefinition(exact.Value!, previous, next);
                    if (!contract.IsSuccess) return contract;
                    if (exact.Value!.RestartDispatchPending)
                        return KernelResult.Fail(KernelError.ReplacementBlocked, "Exact replacement dispatch is already pending.");
                    exact.Value.RestartDispatchPending = true;
                    dispatchRecord = exact.Value;
                    return KernelResult.Ok();
                });
            if (!validation.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(validation.Error, validation.Message!);
        }

        try
        {
            KernelResult<ServiceDrainReceipt> drained = Drain(previous, planned: true);
            if (!drained.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(drained.Error, drained.Message!);
            if (!drained.Value!.ClosureProven)
                return KernelResult<ServiceReplacementReceipt>.Fail(KernelError.ReplacementBlocked, "Old generation has not reached exact closure/containment.");
            return AdmitReplacement(previous, next);
        }
        finally
        {
            lock (_gate) dispatchRecord!.RestartDispatchPending = false;
        }
    }

    public KernelResult<ServiceSupervisorSnapshot> Observe(ComponentIdentity identity)
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(control.Error, control.Message!);
        ServiceLifecycleState state;
        ServiceInstanceHandle? instance;
        long? eligible;
        bool restartAfterDrain;
        ManagedServiceDefinition definitionAtObservation;
        lock (_gate)
        {
            if (!_records.TryGetValue(identity, out Record? record))
                return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.ComponentNotFound, $"Managed service '{identity.Name}' is not registered.");
            state = record.State;
            instance = record.Instance;
            eligible = record.RestartEligibleTimestamp;
            restartAfterDrain = record.RestartAfterDrain;
            definitionAtObservation = record.Definition;
        }

        // Failed admission has no published service instance. Its original plan
        // still identifies the existing component owner and retained rollback.
        if (state == ServiceLifecycleState.Failed && instance is null)
        {
            KernelResult<ComponentLifecycleSnapshot> cleanup = _kernel.ObserveFailedComponentAdmission(definitionAtObservation.Admission);
            if (!cleanup.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(cleanup.Error, cleanup.Message!);
            lock (_gate)
            {
                if (!_records.TryGetValue(identity, out Record? current) ||
                    !ReferenceEquals(current.Definition, definitionAtObservation) || current.State != state || current.Instance is not null)
                    return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.StaleGeneration, "Failed admission changed during cleanup observation.");
                return KernelResult<ServiceSupervisorSnapshot>.Ok(Snapshot(current));
            }
        }

        if (state == ServiceLifecycleState.Stopped && restartAfterDrain)
        {
            KernelResult scheduled = ScheduleRestart(identity);
            if (!scheduled.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(scheduled.Error, scheduled.Message!);
        }
        // Quarantine records uncertainty, not an irreversible terminal verdict.
        // Progress the real closure consumer; only its exact reclaimable result
        // may move this generation to Stopped and permit restart scheduling.
        if (state is ServiceLifecycleState.Draining or ServiceLifecycleState.Quarantined &&
            instance is { } drainingInstance)
        {
            KernelResult<ComponentLifecycleSnapshot> observed = _kernel.ObserveComponentTeardown(identity);
            KernelResult<ServiceDrainReceipt> completion = CompleteDrain(identity, drainingInstance, planned: !restartAfterDrain, observed);
            if (!completion.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(completion.Error, completion.Message!);
            if (completion.Value!.ClosureProven && restartAfterDrain)
            {
                KernelResult scheduled = ScheduleRestart(identity);
                if (!scheduled.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(scheduled.Error, scheduled.Message!);
            }
        }

        lock (_gate)
        {
            Record record = _records[identity];
            state = record.State;
            eligible = record.RestartEligibleTimestamp;
        }
        if (state == ServiceLifecycleState.RestartBackoff && eligible is { } timestamp)
        {
            long now;
            try { now = _timeProvider.GetTimestamp(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.PlatformFaulted, "Restart eligibility clock failed.");
            }
            if (now >= timestamp)
            {
                ManagedServiceDefinition? definition = null;
                ServiceInstanceHandle prior = default;
                Record? dispatchRecord = null;
                lock (_gate)
                {
                    KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
                    if (!principal.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.SupervisorDenied, principal.Message!);
                    KernelResult captured = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                        principal.Value!.DomainId, _principal.Generation, () =>
                        {
                            if (!_records.TryGetValue(identity, out Record? current) || current.State != ServiceLifecycleState.RestartBackoff ||
                                current.RestartEligibleTimestamp != eligible || current.Instance != instance)
                                return KernelResult.Fail(KernelError.StaleGeneration, "Restart source changed during eligibility observation.");
                            definition = current.Definition;
                            prior = current.Instance!.Value;
                            if (prior.Process.Generation == ulong.MaxValue)
                                return KernelResult.Fail(KernelError.StaleGeneration, "Restart process generation capacity is exhausted.");
                            if (current.RestartDispatchPending)
                                return KernelResult.Fail(KernelError.ReplacementBlocked, "Exact restart dispatch is already pending.");
                            current.RestartDispatchPending = true;
                            dispatchRecord = current;
                            return KernelResult.Ok();
                        });
                    if (!captured.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(captured.Error, captured.Message!);
                }
                try
                {
                    ManagedServiceDefinition replacement;
                    try
                    {
                        Func<ulong, ComponentAdmissionPlan>? factory = definition!.ReplacementFactory;
                        if (factory is null) return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.ReplacementBlocked, "Restart factory is absent.");
                        ulong generation = checked(prior.Process.Generation + 1);
                        replacement = new ManagedServiceDefinition(factory(generation), definition.PrimaryServiceName,
                            definition.ReadinessPolicy, definition.DependencyFailurePolicy, factory);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                    {
                        return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.PlatformFaulted, "Fresh-admission factory failed; restart dispatch was not committed.");
                    }
                    KernelResult<ServiceReplacementReceipt> replaced = AdmitReplacement(prior, replacement);
                    if (!replaced.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(replaced.Error, replaced.Message!);
                    }
                finally
                {
                    lock (_gate) dispatchRecord!.RestartDispatchPending = false;
                }
            }
        }

        return Query(identity);
    }
    private KernelResult<ServiceSupervisorSnapshot> HandleFailure(ServiceInstanceHandle instance, string reason,
        Record sourceRecord, ServiceLifecycleState sourceState, ServiceHealthSnapshot sourceHealth, string? sourceReason)
    {
        BeforeFailureIntentQualificationHook?.Invoke();
        ComponentIdentity identity = default;
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess)
                return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult admitted = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    Record record = exact.Value!;
                    if (!ReferenceEquals(record, sourceRecord) || record.State != sourceState ||
                        record.Health != sourceHealth || record.BlockingReason != sourceReason)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Failure source changed after health publication.");
                    identity = record.Definition.Identity;
                    record.State = ServiceLifecycleState.Failed;
                    record.RestartAfterDrain = true;
                    record.BlockingReason = reason;
                    return KernelResult.Ok();
                });
            if (!admitted.IsSuccess)
                return KernelResult<ServiceSupervisorSnapshot>.Fail(admitted.Error, admitted.Message!);
        }

        KernelResult propagation = PropagateDependencyFailure(identity, instance);
        KernelResult<ComponentLifecycleSnapshot> faulted = _kernel.FaultComponent(identity, instance.Process);
        KernelResult<ServiceDrainReceipt> drain = CompleteDrain(identity, instance, planned: false, faulted);
        if (!propagation.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(propagation.Error, propagation.Message!);
        if (!drain.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(drain.Error, drain.Message!);
        if (drain.Value!.ClosureProven)
        {
            KernelResult scheduled = ScheduleRestart(identity);
            if (!scheduled.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(scheduled.Error, scheduled.Message!);
        }
        BeforeFailureSnapshotQualificationHook?.Invoke();
        lock (_gate)
        {
            KernelResult<Record> exact = ResolveExactLocked(instance);
            return exact.IsSuccess ? KernelResult<ServiceSupervisorSnapshot>.Ok(Snapshot(exact.Value!))
                : KernelResult<ServiceSupervisorSnapshot>.Fail(exact.Error, exact.Message!);
        }
    }

    private KernelResult<ServiceStartReceipt> CompleteAdmission(
        ComponentIdentity identity,
        ManagedServiceDefinition definition,
        ComponentLifecycleSnapshot component)
    {
        ProcessHandle expectedProcess = new ProcessHandle(definition.Admission.Manifest.Process.ProcessId,
            definition.Admission.Manifest.Process.Generation);
        if (component.Identity != identity || component.Process != expectedProcess ||
            component.ManifestDigest != definition.Admission.Manifest.NormalizedDigest)
            return KernelResult<ServiceStartReceipt>.Fail(KernelError.StaleGeneration, "Admission result does not match the exact managed definition.");
        Dictionary<ServiceContractIdentity, ServiceInstanceHandle> providedInstances = new Dictionary<ServiceContractIdentity, ServiceInstanceHandle>();
        ServiceInstanceHandle instance = default;
        foreach (ProvidedServiceManifestV1 provided in definition.Admission.Manifest.ProvidedContracts)
        {
            KernelResult<ServiceEndpointDescriptor> descriptor = _kernel.ResolveByServiceName(provided.ServiceName);
            if (!descriptor.IsSuccess)
                return CompensateIncompleteAdmission(identity, definition, component.Process, descriptor.Error, descriptor.Message!);
            ServiceInstanceHandle providedInstance = component.ServiceInstances.SingleOrDefault(candidate => candidate.ServiceId == descriptor.Value!.Service.Id && candidate.Generation == descriptor.Value.Generation && candidate.Process == component.Process);
            if (providedInstance == default)
                return CompensateIncompleteAdmission(identity, definition, component.Process, KernelError.ServiceNotFound, $"Provided service '{provided.ServiceName}' was not materialized by component admission.");
            providedInstances.Add(provided.Contract, providedInstance);
            if (string.Equals(provided.ServiceName, definition.PrimaryServiceName, StringComparison.Ordinal))
                instance = providedInstance;
        }
        if (instance == default)
            return CompensateIncompleteAdmission(identity, definition, component.Process, KernelError.ServiceNotFound, "Primary service instance was not materialized by component admission.");

        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return CompensateIncompleteAdmission(identity, definition, component.Process,
                KernelError.PlatformFaulted, "Admission readiness clock observation failed.");
        }
        KernelResult committed;
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            ServiceInstanceHandle[] dependencyInstances = definition.Admission.Manifest.Dependencies
                .Select(dependency => ResolveProviderRecordLocked(dependency.Contract)?.ProvidedInstances.GetValueOrDefault(dependency.Contract))
                .Where(provider => provider is not null && provider.Value != default)
                .Select(provider => provider!.Value).Distinct().ToArray();
            committed = !principal.IsSuccess ? KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!) :
                _kernel.CommitComponentReadiness(component, dependencyInstances, () => _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation, () =>
                    {
                        if (!_records.TryGetValue(identity, out Record? record) || !ReferenceEquals(record.Definition, definition))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Managed definition changed before readiness publication.");
                        if (record.State is not ServiceLifecycleState.Admitting and not ServiceLifecycleState.RestartBackoff)
                            return KernelResult.Fail(KernelError.InvalidTransition, "Supervisor state changed during component admission.");
                        List<ServiceDependencyBinding> bindings = [];
                        bool degraded = component.Admission.Disposition == ManifestAdmissionDisposition.Degraded;
                        foreach (ServiceDependencyRequirementV1 dependency in definition.Admission.Manifest.Dependencies)
                        {
                            Record? provider = ResolveProviderRecordLocked(dependency.Contract);
                            if (provider is not null && provider.ProvidedInstances.TryGetValue(dependency.Contract, out ServiceInstanceHandle providerInstance))
                                bindings.Add(new(instance, providerInstance, dependency.Contract, dependency.Kind));
                            else if (dependency.Kind == ServiceDependencyKind.Optional)
                                degraded = true;
                            else if (definition.ReadinessPolicy == ServiceReadinessPolicy.HardDependenciesBound)
                                return KernelResult.Fail(KernelError.DependencyUnavailable, "Hard dependency is not bound to an exact ready generation.");
                        }
                        ServiceHealthSnapshot health = new ServiceHealthSnapshot(instance,
                            degraded ? ServiceHealthState.Degraded : ServiceHealthState.Ready, timestamp,
                            degraded ? "Service admitted with explicit optional degradation." : "Service and required dependencies are ready.");
                        record.Instance = instance;
                        record.ProvidedInstances.Clear();
                        foreach (KeyValuePair<ServiceContractIdentity, ServiceInstanceHandle> provided in providedInstances) record.ProvidedInstances.Add(provided.Key, provided.Value);
                        record.Bindings.Clear();
                        record.Bindings.AddRange(bindings);
                        record.State = degraded ? ServiceLifecycleState.Degraded : ServiceLifecycleState.Ready;
                        record.Health = health;
                        record.RestartEligibleTimestamp = null;
                        record.RestartAfterDrain = false;
                        record.BlockingReason = null;
                        return KernelResult.Ok();
                    }));
        }
        if (!committed.IsSuccess)
            return CompensateIncompleteAdmission(identity, definition, component.Process, committed.Error, committed.Message!);
        KernelResult rebound = RebindDependents(identity, instance);
        if (!rebound.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(rebound.Error, rebound.Message!);
        KernelResult<ServiceSupervisorSnapshot> observed = Query(identity);
        if (!observed.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(observed.Error, observed.Message!);
        if (observed.Value!.Instance != instance || observed.Value.ManifestDigest != component.ManifestDigest)
            return KernelResult<ServiceStartReceipt>.Fail(KernelError.StaleGeneration, "Published admission changed before receipt observation.");
        return KernelResult<ServiceStartReceipt>.Ok(new(new(identity, definition.Admission.Manifest.NormalizedDigest), observed.Value));
    }

    private KernelResult<ServiceDrainReceipt> CompleteDrain(
        ComponentIdentity identity,
        ServiceInstanceHandle instance,
        bool planned,
        KernelResult<ComponentLifecycleSnapshot> result)
    {
        if (result.IsSuccess && (result.Value!.Identity != identity || result.Value.Process != instance.Process))
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.StaleGeneration, "Closure snapshot does not match the exact service process.");
        bool closure = result.IsSuccess && result.Value!.State == ComponentLifecycleState.Reclaimable;
        bool unsafeOutcome = !closure && IsUnsafeTeardown(
            identity, result.IsSuccess ? KernelError.None : result.Error);
        ServiceLifecycleState state = closure ? ServiceLifecycleState.Stopped :
            unsafeOutcome ? ServiceLifecycleState.Quarantined : ServiceLifecycleState.Draining;
        ServiceHealthState healthState = closure ? ServiceHealthState.Stopped :
            unsafeOutcome ? ServiceHealthState.Quarantined : ServiceHealthState.Draining;
        string reason = closure ? "Old process/provider authority reached exact reclaimable closure." :
            result.Message ?? (unsafeOutcome ? "External effect closure is ambiguous." : "Exact teardown is still draining.");
        Record sourceRecord;
        ServiceLifecycleState sourceState;
        ServiceHealthSnapshot? sourceHealth;
        string? sourceReason;
        lock (_gate)
        {
            KernelResult<Record> exact = ResolveExactLocked(instance);
            if (!exact.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(exact.Error, exact.Message!);
            sourceRecord = exact.Value!;
            sourceState = sourceRecord.State;
            sourceHealth = sourceRecord.Health;
            sourceReason = sourceRecord.BlockingReason;
        }
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.PlatformFaulted, "Drain completion clock observation failed.");
        }
        ServiceHealthSnapshot health = new ServiceHealthSnapshot(instance, healthState, timestamp, reason);
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ServiceDrainReceipt? receipt = null;
            KernelResult committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    Record record = exact.Value!;
                    if (record.Definition.Identity != identity)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Drain identity changed before publication.");
                    if (!ReferenceEquals(record, sourceRecord) || record.State != sourceState ||
                        record.Health != sourceHealth || record.BlockingReason != sourceReason)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Drain source changed during completion observation.");
                    record.State = state;
                    record.Health = health;
                    record.BlockingReason = closure ? null :
                        unsafeOutcome ? result.Message ?? result.Error.ToString() : result.Message;
                    // Failure drains use their existing policy-scoped batch
                    // propagation. Planned closure independently invalidates
                    // bindings for every exact service of the old process.
                    if (closure && planned)
                    {
                        HashSet<ServiceInstanceHandle> closedProviders = record.ProvidedInstances.Values.Append(instance).ToHashSet();
                        foreach (Record? dependent in _records.Values.Where(candidate =>
                                     candidate.Instance is not null &&
                                     candidate.Bindings.Any(binding => closedProviders.Contains(binding.Provider))))
                        {
                            dependent.Bindings.RemoveAll(binding => closedProviders.Contains(binding.Provider));
                            if (dependent.State is ServiceLifecycleState.Ready or ServiceLifecycleState.Degraded)
                            {
                                dependent.State = ServiceLifecycleState.Degraded;
                                dependent.Health = new(dependent.Instance!.Value, ServiceHealthState.Degraded,
                                    timestamp, "Exact dependency provider reached closure; fresh binding is required.");
                            }
                        }
                    }
                    receipt = new ServiceDrainReceipt(new(instance, planned), Snapshot(record), closure);
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(committed.Error, committed.Message!);
            if (!result.IsSuccess && !unsafeOutcome && result.Error != KernelError.PlatformBindingDraining)
                return KernelResult<ServiceDrainReceipt>.Fail(result.Error, result.Message!);
            return KernelResult<ServiceDrainReceipt>.Ok(receipt!);
        }
    }
    private KernelResult<ServiceReplacementReceipt> AdmitReplacement(ServiceInstanceHandle previous, ManagedServiceDefinition next)
    {
        KernelResult control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(control.Error, control.Message!);
        lock (_gate)
        {
            KernelResult<Record> exact = ResolveExactLocked(previous);
            if (!exact.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(exact.Error, exact.Message!);
            KernelResult contract = ValidateReplacementDefinition(exact.Value!, previous, next);
            if (!contract.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(contract.Error, contract.Message!);
        }
        KernelResult preflight;
        try { preflight = _kernel.ValidateReplacementAdmission(next.Admission, previous.Process); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceReplacementReceipt>.Fail(KernelError.PlatformFaulted, "Replacement admission preflight failed.");
        }
        if (!preflight.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(preflight.Error, preflight.Message!);
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult validated = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(previous);
                    return exact.IsSuccess ? ValidateReplacementDefinition(exact.Value!, previous, next)
                        : KernelResult.Fail(exact.Error, exact.Message!);
                });
            if (!validated.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(validated.Error, validated.Message!);
        }
        BeforeReplacementRetirementQualificationHook?.Invoke();
        lock (_gate)
        {
            KernelResult retired = _kernel.CommitSupervisorReplacementRetirement(next.Identity, previous.Process,
                _principal, _controlCapability, () =>
                {
                    KernelResult<Record> exact = ResolveExactLocked(previous);
                    return exact.IsSuccess ? ValidateReplacementDefinition(exact.Value!, previous, next)
                        : KernelResult.Fail(exact.Error, exact.Message!);
                }, () =>
                {
                    Record record = _records[next.Identity];
                    record.Definition = next;
                    record.State = ServiceLifecycleState.Admitting;
                    record.Bindings.Clear();
                    record.ProvidedInstances.Clear();
                    record.Health = null;
                    record.BlockingReason = null;
                });
            if (!retired.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(retired.Error, retired.Message!);
        }

        KernelResult<ComponentLifecycleSnapshot> admitted = _kernel.AdmitComponent(next.Admission, _principal, _controlCapability);
        if (!admitted.IsSuccess)
        {
            lock (_gate)
            {
                // Retain the original error, but do not overwrite a newer drain
                // or publication with this admission attempt's failure fact.
                if (_records.TryGetValue(next.Identity, out Record? record) &&
                    ReferenceEquals(record.Definition, next) && record.State == ServiceLifecycleState.Admitting)
                {
                    record.State = ServiceLifecycleState.Failed;
                    record.BlockingReason = admitted.Message;
                }
            }
            return KernelResult<ServiceReplacementReceipt>.Fail(admitted.Error, admitted.Message!);
        }
        KernelResult<ServiceStartReceipt> completed = CompleteAdmission(next.Identity, next, admitted.Value!);
        if (!completed.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(completed.Error, completed.Message!);
        return KernelResult<ServiceReplacementReceipt>.Ok(new(new(previous, next.Admission.Manifest.NormalizedDigest), completed.Value!.Snapshot, true));
    }

    private KernelResult ScheduleRestart(ComponentIdentity identity)
    {
        Record record;
        long[] attempts;
        ServiceInstanceHandle? instance;
        ServiceHealthSnapshot? sourceHealth;
        string? sourceReason;
        lock (_gate)
        {
            if (!_records.TryGetValue(identity, out record!)) return KernelResult.Fail(KernelError.ComponentNotFound, "Restart record is absent.");
            if (record.State != ServiceLifecycleState.Stopped || !record.RestartAfterDrain)
                return KernelResult.Fail(KernelError.ReplacementBlocked, "Restart scheduling requires completed old-generation drain.");
            attempts = record.RestartAttempts.ToArray();
            instance = record.Instance;
            sourceHealth = record.Health;
            sourceReason = record.BlockingReason;
        }
        ServiceRestartPolicyV1 policy = record.Definition.Admission.Manifest.RestartPolicy;
        bool disabled = policy.Mode == ServiceRestartMode.Never || record.Definition.ReplacementFactory is null;
        long now = 0;
        long? eligible = null;
        long[] retained = attempts;
        ulong delay = 0;
        bool exhausted = false;
        if (!disabled)
        {
            try
            {
                now = _timeProvider.GetTimestamp();
                retained = attempts.Where(timestamp => _timeProvider.GetElapsedTime(timestamp, now).TotalMilliseconds <= policy.WindowMilliseconds).ToArray();
                exhausted = retained.Length >= policy.MaximumAttempts;
                if (!exhausted)
                {
                    ulong multiplier = 1UL << Math.Min(retained.Length, 30);
                    ulong scaled = policy.InitialBackoffMilliseconds > ulong.MaxValue / multiplier
                        ? ulong.MaxValue : policy.InitialBackoffMilliseconds * multiplier;
                    delay = Math.Min(policy.MaximumBackoffMilliseconds, scaled);
                    eligible = AddMilliseconds(now, delay);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult.Fail(KernelError.PlatformFaulted, "Restart clock/window/backoff observation failed; scheduling was not committed.");
            }
        }
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            return _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    if (!_records.TryGetValue(identity, out Record? current) || !ReferenceEquals(current, record) ||
                        current.Instance != instance || current.State != ServiceLifecycleState.Stopped ||
                        !current.RestartAfterDrain || !current.RestartAttempts.SequenceEqual(attempts) ||
                        current.Health != sourceHealth || current.BlockingReason != sourceReason)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Restart source changed during observation.");
                    if (disabled)
                    {
                        current.State = ServiceLifecycleState.Failed;
                        current.BlockingReason = policy.Mode == ServiceRestartMode.Never ? "Restart policy is Never." : "Fresh-admission replacement factory is absent.";
                        return KernelResult.Ok();
                    }
                    current.RestartAttempts.Clear();
                    current.RestartAttempts.AddRange(retained);
                    if (exhausted)
                    {
                        current.State = ServiceLifecycleState.CrashLoop;
                        current.Health = new(instance!.Value, ServiceHealthState.Failed, now, "Bounded restart window is exhausted.");
                        current.BlockingReason = "Crash-loop restart limit reached.";
                    }
                    else
                    {
                        current.RestartAttempts.Add(now);
                        current.RestartEligibleTimestamp = eligible;
                        current.State = ServiceLifecycleState.RestartBackoff;
                        current.Health = new(instance!.Value, ServiceHealthState.Stopped, now, $"Fresh admission eligible after bounded backoff ({delay} ms).");
                    }
                    return KernelResult.Ok();
                });
        }
    }
    private KernelResult RebindDependents(ComponentIdentity providerIdentity, ServiceInstanceHandle expectedProvider)
    {
        DependencyRebindWork[] work;
        ManagedServiceDefinition providerDefinition;
        lock (_gate)
        {
            if (!_records.TryGetValue(providerIdentity, out Record? provider) || provider.Instance != expectedProvider)
                return KernelResult.Fail(KernelError.StaleGeneration, "Dependency provider changed before rebind capture.");
            providerDefinition = provider.Definition;
            HashSet<ServiceContractIdentity> provided = provider.Definition.Admission.Manifest.ProvidedContracts.Select(item => item.Contract).ToHashSet();
            (Record record, ServiceDependencyRequirementV1 dependency)[] candidates = _records.Values
                .Where(record => record.Definition.Identity != providerIdentity && record.Instance is not null && record.State is ServiceLifecycleState.Ready or ServiceLifecycleState.Degraded)
                .SelectMany(record => record.Definition.Admission.Manifest.Dependencies.Where(dependency => provided.Contains(dependency.Contract)).Select(dependency => (record, dependency)))
                .ToArray();
            List<DependencyRebindWork> resolvedWork = new List<DependencyRebindWork>();
            foreach ((Record record, ServiceDependencyRequirementV1 dependency) item in candidates)
            {
                KernelResult<ServiceEndpointDescriptor[]> resolved = _kernel.ResolveByContract(item.dependency.Contract);
                if (!resolved.IsSuccess || resolved.Value!.Length != 1 ||
                    !provider.ProvidedInstances.TryGetValue(item.dependency.Contract, out ServiceInstanceHandle exactProvider))
                    return KernelResult.Fail(KernelError.DependencyUnavailable, "Rebind requires one exact declared provider.");
                ServiceEndpointDescriptor descriptor = resolved.Value[0];
                if (descriptor.Service.Id != exactProvider.ServiceId || descriptor.Generation != exactProvider.Generation ||
                    exactProvider.Process != expectedProvider.Process || exactProvider.Domain != expectedProvider.Domain)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Resolved dependency descriptor does not match the captured provider.");
                resolvedWork.Add(new(item.record.Definition, item.record.Instance!.Value, item.record.State,
                    item.record.Bindings.ToArray(), descriptor, item.dependency, exactProvider));
            }
            work = resolvedWork.ToArray();
        }

        for (int index = 0; index < work.Length; index++)
        {
            DependencyRebindWork item = work[index];
            long timestamp;
            try { timestamp = _timeProvider.GetTimestamp(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult.Fail(KernelError.PlatformFaulted, "Dependency rebind clock failed before dispatch.");
            }
            lock (_gate)
            {
                KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
                if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
                KernelResult admitted = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation,
                    () => ValidateDependencyRebindLocked(providerIdentity, expectedProvider, providerDefinition, item));
                if (!admitted.IsSuccess) return admitted;
            }
            KernelResult<EndpointSessionHandle> rebound = _kernel.RebindComponentDependency(item.Definition.Identity, item.Dependent.Process,
                item.Descriptor, _principal, _controlCapability);
            lock (_gate)
            {
                KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
                if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
                KernelResult published = _kernel.CommitServiceDependencyReadiness([item.Provider, item.Dependent], () =>
                    _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation, () =>
                    {
                        KernelResult exact = ValidateDependencyRebindLocked(providerIdentity, expectedProvider, providerDefinition, item);
                        if (!exact.IsSuccess) return exact;
                        Record dependent = ResolveExactLocked(item.Dependent).Value!;
                        if (!rebound.IsSuccess)
                        {
                            dependent.State = ServiceLifecycleState.Degraded;
                            dependent.Health = new(item.Dependent, ServiceHealthState.Degraded, timestamp,
                                rebound.Message ?? "Dependency rebind failed.");
                            return KernelResult.Ok();
                        }
                        List<ServiceDependencyBinding> bindings = item.Bindings.Where(binding => binding.Contract != item.Dependency.Contract).ToList();
                        bindings.Add(new(item.Dependent, item.Provider, item.Dependency.Contract, item.Dependency.Kind));
                        bool ready = item.State == ServiceLifecycleState.Degraded &&
                            AllHardDependenciesBoundLocked(item.Definition, bindings);
                        if (ready)
                        {
                            KernelResult readiness = _kernel.CommitServiceDependencyReadiness(
                                bindings.Select(binding => binding.Provider).Distinct().ToArray(), () => KernelResult.Ok());
                            if (!readiness.IsSuccess) return readiness;
                        }
                        return _kernel.CommitRunningComponentPublication(
                            ready || item.State == ServiceLifecycleState.Ready
                                ? bindings.Select(binding => binding.Provider.Process).Append(item.Dependent.Process).ToArray()
                                : [item.Provider.Process, item.Dependent.Process], () =>
                        {
                            dependent.Bindings.Clear();
                            dependent.Bindings.AddRange(bindings);
                            if (ready)
                            {
                                dependent.State = ServiceLifecycleState.Ready;
                                dependent.Health = new(item.Dependent, ServiceHealthState.Ready, timestamp,
                                    "Dependency rebound to the fresh exact generation.");
                            }
                            return KernelResult.Ok();
                        });
                    }));
                if (!published.IsSuccess) return published;
                // Only our committed publication advances snapshots for further
                // contracts on this same dependent. External changes still fail
                // the next exact comparison.
                Record current = ResolveExactLocked(item.Dependent).Value!;
                for (int next = index + 1; next < work.Length; next++)
                    if (work[next].Dependent == item.Dependent)
                        work[next] = work[next] with { State = current.State, Bindings = current.Bindings.ToArray() };
            }
        }
        return KernelResult.Ok();
    }

    private KernelResult ValidateDependencyRebindLocked(ComponentIdentity providerIdentity,
        ServiceInstanceHandle expectedProvider, ManagedServiceDefinition providerDefinition, DependencyRebindWork item)
    {
        if (!_records.TryGetValue(providerIdentity, out Record? provider) || provider.Instance != expectedProvider ||
            !ReferenceEquals(provider.Definition, providerDefinition) ||
            provider.State is not ServiceLifecycleState.Ready and not ServiceLifecycleState.Degraded ||
            !provider.ProvidedInstances.TryGetValue(item.Dependency.Contract, out ServiceInstanceHandle provided) || provided != item.Provider)
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact dependency provider changed before rebind commit.");
        KernelResult<Record> exact = ResolveExactLocked(item.Dependent);
        if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
        Record dependent = exact.Value!;
        if (!ReferenceEquals(dependent.Definition, item.Definition) || !dependent.Bindings.SequenceEqual(item.Bindings))
            return KernelResult.Fail(KernelError.StaleGeneration, "Dependent definition or bindings changed before rebind commit.");
        return dependent.State == item.State ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InvalidTransition, "Dependent lifecycle changed before rebind commit.");
    }

    private KernelResult<ServiceStartReceipt> CompensateIncompleteAdmission(
        ComponentIdentity identity, ManagedServiceDefinition definition, ProcessHandle process, KernelError error, string message)
    {
        // Cleanup consumes only the admitted generation; identity cannot redirect it.
        KernelResult<ComponentLifecycleSnapshot> drained = _kernel.DrainComponent(identity, process);
        bool closure = drained.IsSuccess && drained.Value!.Reclaimable;
        bool unsafeOutcome = !closure && IsUnsafeTeardown(identity, drained.IsSuccess ? KernelError.None : drained.Error);
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (principal.IsSuccess)
                _ = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation, () =>
                    {
                        if (!_records.TryGetValue(identity, out Record? record) || !ReferenceEquals(record.Definition, definition))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Compensation definition has been replaced.");
                        if (record.State is not ServiceLifecycleState.Admitting and not ServiceLifecycleState.RestartBackoff)
                            return KernelResult.Fail(KernelError.InvalidTransition, "Compensation cannot overwrite a published service instance.");
                        record.State = closure ? ServiceLifecycleState.Stopped :
                            unsafeOutcome ? ServiceLifecycleState.Quarantined : ServiceLifecycleState.Draining;
                        record.BlockingReason = closure ? message : drained.Message ?? message;
                        return KernelResult.Ok();
                    });
        }
        return KernelResult<ServiceStartReceipt>.Fail(error, message);
    }
    private KernelResult PropagateDependencyFailure(ComponentIdentity failedIdentity, ServiceInstanceHandle failed)
    {
        (ServiceInstanceHandle Instance, ManagedServiceDefinition Definition, ServiceLifecycleState State,
            ServiceHealthSnapshot? Health, string? Reason, ServiceDependencyBinding[] Bindings)[] dependents;
        ManagedServiceDefinition providerDefinition;
        HashSet<ServiceInstanceHandle> failedProviders;
        lock (_gate)
        {
            if (!_records.TryGetValue(failedIdentity, out Record? provider) || provider.Instance != failed)
                return KernelResult.Fail(KernelError.StaleGeneration, "Failed provider generation changed before propagation.");
            providerDefinition = provider.Definition;
            failedProviders = provider.ProvidedInstances.Values.Append(failed).ToHashSet();
            dependents = _records.Values
                .Where(record => record.Instance is not null && record.Bindings.Any(binding => failedProviders.Contains(binding.Provider)))
                .Select(record => (record.Instance!.Value, record.Definition, record.State,
                    record.Health, record.BlockingReason, record.Bindings.ToArray())).ToArray();
        }
        (ServiceInstanceHandle Instance, ManagedServiceDefinition Definition, ServiceLifecycleState State, ServiceHealthSnapshot? Health, string? Reason, ServiceDependencyBinding[] Bindings)[] degraded = dependents.Where(item => item.Definition.DependencyFailurePolicy == ServiceFailurePropagationPolicy.DegradeDependent).ToArray();
        long timestamp = 0;
        if (degraded.Length != 0)
        {
            try { timestamp = _timeProvider.GetTimestamp(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult.Fail(KernelError.PlatformFaulted, "Dependency failure clock observation failed.");
            }
        }
        lock (_gate)
        {
            KernelResult<SingProcess> principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    if (!_records.TryGetValue(failedIdentity, out Record? provider) || provider.Instance != failed ||
                        provider.State != ServiceLifecycleState.Failed || !ReferenceEquals(provider.Definition, providerDefinition) ||
                        !failedProviders.SetEquals(provider.ProvidedInstances.Values.Append(failed)))
                        return KernelResult.Fail(KernelError.StaleGeneration, "Failed provider changed before dependency publication.");
                    // Validate the entire captured batch before publishing any peer change.
                    foreach ((ServiceInstanceHandle Instance, ManagedServiceDefinition Definition, ServiceLifecycleState State, ServiceHealthSnapshot? Health, string? Reason, ServiceDependencyBinding[] Bindings) item in dependents)
                    {
                        KernelResult<Record> exact = ResolveExactLocked(item.Instance);
                        if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                        Record record = exact.Value!;
                        if (!ReferenceEquals(record.Definition, item.Definition) || !record.Bindings.Any(binding => failedProviders.Contains(binding.Provider)))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Dependent definition or binding changed before propagation.");
                        if (record.State != item.State)
                            return KernelResult.Fail(KernelError.InvalidTransition, "Dependent state changed before propagation.");
                        if (record.Health != item.Health || record.BlockingReason != item.Reason ||
                            !record.Bindings.SequenceEqual(item.Bindings))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Dependent source changed during propagation observation.");
                    }
                    foreach ((ServiceInstanceHandle Instance, ManagedServiceDefinition Definition, ServiceLifecycleState State, ServiceHealthSnapshot? Health, string? Reason, ServiceDependencyBinding[] Bindings) item in degraded)
                    {
                        Record record = ResolveExactLocked(item.Instance).Value!;
                        record.State = ServiceLifecycleState.Degraded;
                        record.Health = new(item.Instance, ServiceHealthState.Degraded, timestamp, "Exact hard dependency generation failed.");
                    }
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess) return committed;
        }
        foreach ((ServiceInstanceHandle Instance, ManagedServiceDefinition Definition, ServiceLifecycleState State, ServiceHealthSnapshot? Health, string? Reason, ServiceDependencyBinding[] Bindings) item in dependents.Where(item => item.Definition.DependencyFailurePolicy != ServiceFailurePropagationPolicy.DegradeDependent))
        {
            KernelResult<ServiceDrainReceipt> drained = DrainCore(item.Instance,
                planned: item.Definition.DependencyFailurePolicy == ServiceFailurePropagationPolicy.StopDependent,
                expectedSource: () =>
                {
                    if (!_records.TryGetValue(failedIdentity, out Record? provider) || provider.Instance != failed ||
                        provider.State != ServiceLifecycleState.Failed || !ReferenceEquals(provider.Definition, providerDefinition) ||
                        !failedProviders.SetEquals(provider.ProvidedInstances.Values.Append(failed)))
                        return KernelResult.Fail(KernelError.StaleGeneration, "Failure policy provider changed before drain dispatch.");
                    KernelResult<Record> exact = ResolveExactLocked(item.Instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    Record record = exact.Value!;
                    return ReferenceEquals(record.Definition, item.Definition) && record.State == item.State &&
                           record.Health == item.Health && record.BlockingReason == item.Reason &&
                           record.Bindings.SequenceEqual(item.Bindings)
                        ? KernelResult.Ok()
                        : KernelResult.Fail(KernelError.StaleGeneration, "Failure policy peer changed before drain dispatch.");
                });
            if (!drained.IsSuccess) return KernelResult.Fail(drained.Error, drained.Message!);
        }
        return KernelResult.Ok();
    }
    private KernelResult ValidateControl()
    {
        KernelResult<CapabilityDescriptorV1> capability = _kernel.ValidateCapability(_principal, _controlCapability, CapabilityRights.Configure | CapabilityRights.Execute);
        if (!capability.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, capability.Message ?? "Supervisor control capability is invalid.");
        return capability.Value!.ResourceKind == ResourceKind.KernelService &&
               string.Equals(capability.Value.ResourceId, CapabilityResourceIds.ServiceSupervisor, StringComparison.Ordinal)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.SupervisorDenied, "Capability does not authorize service-supervisor control.");
    }

    private KernelResult<Record> ResolveExactLocked(ServiceInstanceHandle instance)
    {
        Record? record = _records.Values.SingleOrDefault(candidate => candidate.Instance == instance);
        return record is null
            ? KernelResult<Record>.Fail(KernelError.StaleGeneration, "Service instance handle is stale or unknown.")
            : KernelResult<Record>.Ok(record);
    }

    private KernelResult ValidateReplacementDefinition(Record record, ServiceInstanceHandle previous, ManagedServiceDefinition next)
    {
        if (next.Identity != record.Definition.Identity)
            return KernelResult.Fail(KernelError.InvalidManifest, "Replacement must preserve the managed component identity.");
        SingProcessManifestV1 process = next.Admission.Manifest.Process;
        if (previous.Process.Generation == ulong.MaxValue ||
            process.ProcessId != previous.Process.ProcessId || process.Generation != previous.Process.Generation + 1)
            return KernelResult.Fail(KernelError.StaleGeneration, "Replacement must use the same process identity and exactly the next generation.");
        if (!string.Equals(next.PrimaryServiceName, record.Definition.PrimaryServiceName, StringComparison.Ordinal))
            return KernelResult.Fail(KernelError.InvalidManifest, "Replacement must preserve the primary service identity.");
        return ValidateAcyclicGraphLocked(next);
    }

    private KernelResult ValidateAcyclicGraphLocked(ManagedServiceDefinition? replacement = null)
    {
        // Evaluate the prospective definitions without publishing a temporary
        // record or changing the authoritative managed graph.
        Dictionary<ComponentIdentity, ManagedServiceDefinition> definitions = _records.Values.Select(record =>
            replacement is not null && record.Definition.Identity == replacement.Identity
                ? replacement : record.Definition).ToDictionary(definition => definition.Identity);
        IGrouping<ServiceContractIdentity, (ServiceContractIdentity Contract, ComponentIdentity Identity)>? duplicateProvider = definitions.Values
            .SelectMany(definition => definition.Admission.Manifest.ProvidedContracts.Select(provided => (provided.Contract, definition.Identity)))
            .GroupBy(item => item.Contract)
            .FirstOrDefault(group => group.Select(item => item.Identity).Distinct().Count() > 1);
        if (duplicateProvider is not null)
            return KernelResult.Fail(KernelError.InvalidManifest, $"Contract '{duplicateProvider.Key.Name}/{duplicateProvider.Key.Version}' has multiple managed providers.");
        HashSet<ComponentIdentity> visiting = new HashSet<ComponentIdentity>();
        HashSet<ComponentIdentity> visited = new HashSet<ComponentIdentity>();
        bool Visit(ComponentIdentity identity)
        {
            if (!visiting.Add(identity)) return false;
            if (visited.Contains(identity)) { visiting.Remove(identity); return true; }
            foreach (ServiceDependencyRequirementV1 dependency in definitions[identity].Admission.Manifest.Dependencies)
            foreach (ManagedServiceDefinition? provider in definitions.Values.Where(candidate =>
                         candidate.Admission.Manifest.ProvidedContracts.Any(provided => provided.Contract == dependency.Contract)))
                if (!Visit(provider.Identity)) return false;
            visiting.Remove(identity);
            visited.Add(identity);
            return true;
        }
        return definitions.Keys.All(Visit)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InvalidManifest, "Managed service dependency graph contains a cycle.");
    }

    private IEnumerable<ComponentIdentity> TopologicalOrderLocked()
    {
        HashSet<ComponentIdentity> visited = new HashSet<ComponentIdentity>();
        List<ComponentIdentity> result = new List<ComponentIdentity>();
        void Visit(ComponentIdentity identity)
        {
            if (!visited.Add(identity)) return;
            foreach (ComponentIdentity provider in ResolveProviderIdentitiesLocked(_records[identity].Definition, null)) Visit(provider);
            result.Add(identity);
        }
        foreach (ComponentIdentity identity in _records.Keys.OrderBy(item => item.Name, StringComparer.Ordinal)) Visit(identity);
        return result;
    }

    private IEnumerable<ComponentIdentity> ResolveProviderIdentitiesLocked(ManagedServiceDefinition definition, ServiceDependencyKind? kind) =>
        definition.Admission.Manifest.Dependencies
            .Where(dependency => kind is null || dependency.Kind == kind)
            .Select(dependency => _records.Values.SingleOrDefault(candidate => candidate.Definition.Admission.Manifest.ProvidedContracts.Any(provided => provided.Contract == dependency.Contract)))
            .Where(record => record is not null)
            .Select(record => record!.Definition.Identity);

    private Record? ResolveProviderRecordLocked(ServiceContractIdentity contract) =>
        _records.Values.SingleOrDefault(candidate =>
            candidate.Instance is not null &&
            candidate.State is ServiceLifecycleState.Ready or ServiceLifecycleState.Degraded &&
            candidate.Definition.Admission.Manifest.ProvidedContracts.Any(provided => provided.Contract == contract));

    private bool AllHardDependenciesBoundLocked(ManagedServiceDefinition definition, IReadOnlyList<ServiceDependencyBinding> bindings) =>
        definition.Admission.Manifest.Dependencies
            .Where(dependency => dependency.Kind == ServiceDependencyKind.Hard)
            .All(dependency => ResolveProviderRecordLocked(dependency.Contract) is { } provider &&
                provider.ProvidedInstances.TryGetValue(dependency.Contract, out ServiceInstanceHandle exact) &&
                bindings.Any(binding => binding.Contract == dependency.Contract && binding.Provider == exact));

    private bool IsUnsafeTeardown(ComponentIdentity identity, KernelError error)
    {
        if (error is KernelError.ExternalEffectUncontained or KernelError.PlatformFaulted or KernelError.Quarantined)
            return true;
        KernelResult<ComponentReclaimDiagnosticSnapshot> diagnostic = _kernel.QueryComponentReclaimDiagnostics(identity);
        return diagnostic.IsSuccess && diagnostic.Value!.BlockingDependency?.ExternalState is
            ReclaimExternalState.ExternalStateUnknown or ReclaimExternalState.Quarantined;
    }

    private ServiceSupervisorSnapshot Snapshot(Record record) =>
        new(record.Definition.Identity, record.Instance, record.State, record.Health, record.Bindings.ToArray(),
            (uint)record.RestartAttempts.Count, record.RestartEligibleTimestamp,
            record.Definition.Admission.Manifest.NormalizedDigest, record.BlockingReason);

    private long AddMilliseconds(long timestamp, ulong milliseconds)
    {
        long frequency = _timeProvider.TimestampFrequency;
        if (frequency <= 0) throw new InvalidOperationException("Restart timestamp frequency must be positive.");
        long delta = checked((long)Math.Ceiling(milliseconds * (double)frequency / 1000d));
        return checked(timestamp + delta);
    }
}

public sealed partial class RuntimeKernel
{
    public KernelResult<CapabilityAwareServiceSupervisor> CreateServiceSupervisor(
        ProcessHandle principal,
        CapabilityId controlCapability,
        TimeProvider? timeProvider = null)
    {
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(principal, controlCapability, CapabilityRights.Configure | CapabilityRights.Execute);
        if (!capability.IsSuccess)
            return KernelResult<CapabilityAwareServiceSupervisor>.Fail(KernelError.SupervisorDenied, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.KernelService ||
            !string.Equals(capability.Value.ResourceId, CapabilityResourceIds.ServiceSupervisor, StringComparison.Ordinal))
            return KernelResult<CapabilityAwareServiceSupervisor>.Fail(KernelError.SupervisorDenied, "Capability does not authorize service-supervisor control.");
        return KernelResult<CapabilityAwareServiceSupervisor>.Ok(new(this, principal, controlCapability, timeProvider ?? TimeProvider.System));
    }
}
