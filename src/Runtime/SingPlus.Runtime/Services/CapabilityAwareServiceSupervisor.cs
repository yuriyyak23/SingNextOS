using SingPlus.Contracts;

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
        var control = ValidateControl();
        if (!control.IsSuccess) return control;
        lock (_gate)
        {
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            return _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
            {
                if (_records.ContainsKey(definition.Identity))
                    return KernelResult.Fail(KernelError.DuplicateIdentity, $"Managed service '{definition.Identity.Name}' is already registered.");
                _records.Add(definition.Identity, new(definition));
                var graph = ValidateAcyclicGraphLocked();
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
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceStartReceipt[]>.Fail(control.Error, control.Message!);
        ComponentIdentity[] identities;
        lock (_gate) identities = TopologicalOrderLocked().ToArray();
        var receipts = new List<ServiceStartReceipt>(identities.Length);
        foreach (var identity in identities)
        {
            var started = Start(identity);
            if (!started.IsSuccess) return KernelResult<ServiceStartReceipt[]>.Fail(started.Error, started.Message!);
            receipts.Add(started.Value!);
        }
        return KernelResult<ServiceStartReceipt[]>.Ok(receipts.ToArray());
    }

    public KernelResult<ServiceStartReceipt> Start(ComponentIdentity identity)
    {
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(control.Error, control.Message!);

        ManagedServiceDefinition definition;
        ComponentIdentity[] hardProviders;
        lock (_gate)
        {
            if (!_records.TryGetValue(identity, out var record))
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

        foreach (var provider in hardProviders)
        {
            var started = Start(provider);
            if (!started.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(KernelError.DependencyUnavailable, started.Message ?? "Hard dependency failed to start.");
        }

        lock (_gate)
        {
            var record = _records[identity];
            if (record.State != ServiceLifecycleState.Declared || !ReferenceEquals(record.Definition, definition))
                return KernelResult<ServiceStartReceipt>.Fail(KernelError.InvalidTransition, "Managed definition changed during start.");
            record.State = ServiceLifecycleState.Admitting;
            record.Health = null;
            record.BlockingReason = null;
        }

        var admitted = _kernel.AdmitComponent(definition.Admission);
        if (!admitted.IsSuccess)
        {
            lock (_gate)
            {
                var record = _records[identity];
                record.State = ServiceLifecycleState.Failed;
                record.BlockingReason = admitted.Message;
                record.Health = null;
            }
            return KernelResult<ServiceStartReceipt>.Fail(admitted.Error, admitted.Message!);
        }

        return CompleteAdmission(identity, definition, admitted.Value!);
    }

    public KernelResult<ServiceSupervisorSnapshot> Query(ComponentIdentity identity)
    {
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(control.Error, control.Message!);
        lock (_gate)
        {
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ServiceSupervisorSnapshot? snapshot = null;
            var committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    if (!_records.TryGetValue(identity, out var record))
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
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<StructuredTelemetrySnapshot>.Fail(control.Error, control.Message!);
        ServiceSupervisorSnapshot snapshot;
        lock (_gate)
        {
            var exact = ResolveExactLocked(instance);
            if (!exact.IsSuccess) return KernelResult<StructuredTelemetrySnapshot>.Fail(exact.Error, exact.Message!);
            snapshot = Snapshot(exact.Value!);
        }
        return _kernel.ProjectSupervisorTelemetry(_principal, _controlCapability, instance.Process, snapshot);
    }

    public KernelResult<ServiceHealthSnapshot> ReportHealth(ServiceInstanceHandle instance, ServiceHealthState state, string reason)
    {
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(control.Error, control.Message!);
        if (!Enum.IsDefined(state)) return KernelResult<ServiceHealthSnapshot>.Fail(KernelError.InvalidTransition, "Health state is undefined.");
        ComponentIdentity identity;
        ServiceHealthSnapshot health;
        // Validate the local instance before observing time, then revalidate after the callback.
        lock (_gate)
        {
            var initial = ResolveExactLocked(instance);
            if (!initial.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(initial.Error, initial.Message!);
        }
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceHealthSnapshot>.Fail(KernelError.PlatformFaulted, "Supervisor health clock observation failed.");
        }
        lock (_gate)
        {
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ComponentIdentity committedIdentity = default;
            var observation = new ServiceHealthSnapshot(instance, state, timestamp, reason ?? string.Empty);
            var committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    var exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    committedIdentity = exact.Value!.Definition.Identity;
                    exact.Value.Health = observation;
                    if (state == ServiceHealthState.Degraded && exact.Value.State == ServiceLifecycleState.Ready)
                        exact.Value.State = ServiceLifecycleState.Degraded;
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(committed.Error, committed.Message!);
            identity = committedIdentity;
            health = observation;
        }
        if (state is ServiceHealthState.Unhealthy or ServiceHealthState.Failed)
        {
            var failed = HandleFailure(instance, reason ?? string.Empty);
            if (!failed.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(failed.Error, failed.Message!);
            lock (_gate) health = _records[identity].Health ?? health;
        }
        return KernelResult<ServiceHealthSnapshot>.Ok(health);
    }

    public KernelResult<ServiceDrainReceipt> Drain(ServiceInstanceHandle instance, bool planned = true)
    {
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(control.Error, control.Message!);
        lock (_gate)
        {
            var initial = ResolveExactLocked(instance);
            if (!initial.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(initial.Error, initial.Message!);
        }
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.PlatformFaulted, "Drain request clock observation failed.");
        }
        ComponentIdentity identity = default;
        var health = new ServiceHealthSnapshot(instance, ServiceHealthState.Draining, timestamp,
            planned ? "Planned drain requested." : "Failure drain requested.");
        lock (_gate)
        {
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            var committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    var exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    identity = exact.Value!.Definition.Identity;
                    exact.Value.State = ServiceLifecycleState.Draining;
                    exact.Value.Health = health;
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(committed.Error, committed.Message!);
        }

        var drained = _kernel.DrainComponent(identity, instance.Process);
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
        var temporal = _kernel.ObserveCancellation(instance.Process, scope);
        if (!temporal.IsSuccess)
            return KernelResult<ServiceDrainReceipt>.Fail(temporal.Error, temporal.Message!);
        if (temporal.Value!.Disposition == CancellationDisposition.Stale)
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.StaleGeneration, "Service drain cancellation scope is stale.");
        if (temporal.Value.Timeout != TimeoutDisposition.ExpiredWaitingMayStop)
            return Drain(instance, planned);

        if (timeoutPolicy is ServiceDrainTimeoutPolicy.ContinueDraining or ServiceDrainTimeoutPolicy.RequireProvenContainment)
            return Drain(instance, planned);

        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(control.Error, control.Message!);
        lock (_gate)
        {
            var initial = ResolveExactLocked(instance);
            if (!initial.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(initial.Error, initial.Message!);
        }
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.PlatformFaulted, "Drain timeout clock observation failed.");
        }
        var quarantine = timeoutPolicy == ServiceDrainTimeoutPolicy.Quarantine;
        var health = new ServiceHealthSnapshot(instance,
            quarantine ? ServiceHealthState.Quarantined : ServiceHealthState.Draining, timestamp,
            "Drain deadline expired; no closure or reclaim was inferred.");
        const string blockingReason = "Drain deadline expired before exact closure/containment.";
        lock (_gate)
        {
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ServiceDrainReceipt? receipt = null;
            var committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    var exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    var record = exact.Value!;
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
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(control.Error, control.Message!);
        lock (_gate)
        {
            var exact = ResolveExactLocked(previous);
            if (!exact.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(exact.Error, exact.Message!);
            var validation = ValidateReplacementDefinition(exact.Value!, previous, next);
            if (!validation.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(validation.Error, validation.Message!);
        }

        var drained = Drain(previous, planned: true);
        if (!drained.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(drained.Error, drained.Message!);
        if (!drained.Value!.ClosureProven)
            return KernelResult<ServiceReplacementReceipt>.Fail(KernelError.ReplacementBlocked, "Old generation has not reached exact closure/containment.");
        return AdmitReplacement(previous, next);
    }

    public KernelResult<ServiceSupervisorSnapshot> Observe(ComponentIdentity identity)
    {
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(control.Error, control.Message!);
        ServiceLifecycleState state;
        ServiceInstanceHandle? instance;
        long? eligible;
        bool restartAfterDrain;
        lock (_gate)
        {
            if (!_records.TryGetValue(identity, out var record))
                return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.ComponentNotFound, $"Managed service '{identity.Name}' is not registered.");
            state = record.State;
            instance = record.Instance;
            eligible = record.RestartEligibleTimestamp;
            restartAfterDrain = record.RestartAfterDrain;
        }

        if (state == ServiceLifecycleState.Stopped && restartAfterDrain)
        {
            var scheduled = ScheduleRestart(identity);
            if (!scheduled.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(scheduled.Error, scheduled.Message!);
        }
        // Quarantine records uncertainty, not an irreversible terminal verdict.
        // Progress the real closure consumer; only its exact reclaimable result
        // may move this generation to Stopped and permit restart scheduling.
        if (state is ServiceLifecycleState.Draining or ServiceLifecycleState.Quarantined &&
            instance is { } drainingInstance)
        {
            var observed = _kernel.ObserveComponentTeardown(identity);
            var completion = CompleteDrain(identity, drainingInstance, planned: !restartAfterDrain, observed);
            if (!completion.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(completion.Error, completion.Message!);
            if (completion.Value!.ClosureProven && restartAfterDrain)
            {
                var scheduled = ScheduleRestart(identity);
                if (!scheduled.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(scheduled.Error, scheduled.Message!);
            }
        }

        lock (_gate)
        {
            var record = _records[identity];
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
                lock (_gate)
                {
                    var principal = _kernel.Processes.Resolve(_principal);
                    if (!principal.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.SupervisorDenied, principal.Message!);
                    var captured = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                        principal.Value!.DomainId, _principal.Generation, () =>
                        {
                            if (!_records.TryGetValue(identity, out var current) || current.State != ServiceLifecycleState.RestartBackoff ||
                                current.RestartEligibleTimestamp != eligible || current.Instance != instance)
                                return KernelResult.Fail(KernelError.StaleGeneration, "Restart source changed during eligibility observation.");
                            definition = current.Definition;
                            prior = current.Instance!.Value;
                            return KernelResult.Ok();
                        });
                    if (!captured.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(captured.Error, captured.Message!);
                }
                ManagedServiceDefinition replacement;
                try
                {
                    var factory = definition!.ReplacementFactory;
                    if (factory is null) return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.ReplacementBlocked, "Restart factory is absent.");
                    var generation = checked(prior.Process.Generation + 1);
                    replacement = new ManagedServiceDefinition(factory(generation), definition.PrimaryServiceName,
                        definition.ReadinessPolicy, definition.DependencyFailurePolicy, factory);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    return KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.PlatformFaulted, "Fresh-admission factory failed; restart dispatch was not committed.");
                }
                var replaced = AdmitReplacement(prior, replacement);
                if (!replaced.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(replaced.Error, replaced.Message!);
            }
        }

        return Query(identity);
    }
    private KernelResult<ServiceSupervisorSnapshot> HandleFailure(ServiceInstanceHandle instance, string reason)
    {
        ComponentIdentity identity;
        lock (_gate)
        {
            var exact = ResolveExactLocked(instance);
            if (!exact.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(exact.Error, exact.Message!);
            identity = exact.Value!.Definition.Identity;
            exact.Value.State = ServiceLifecycleState.Failed;
            exact.Value.RestartAfterDrain = true;
            exact.Value.BlockingReason = reason;
        }

        var propagation = PropagateDependencyFailure(identity, instance);
        var faulted = _kernel.FaultComponent(identity, instance.Process);
        var drain = CompleteDrain(identity, instance, planned: false, faulted);
        if (!propagation.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(propagation.Error, propagation.Message!);
        if (!drain.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(drain.Error, drain.Message!);
        if (drain.Value!.ClosureProven)
        {
            var scheduled = ScheduleRestart(identity);
            if (!scheduled.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(scheduled.Error, scheduled.Message!);
        }
        lock (_gate) return KernelResult<ServiceSupervisorSnapshot>.Ok(Snapshot(_records[identity]));
    }

    private KernelResult<ServiceStartReceipt> CompleteAdmission(
        ComponentIdentity identity,
        ManagedServiceDefinition definition,
        ComponentLifecycleSnapshot component)
    {
        var expectedProcess = new ProcessHandle(definition.Admission.Manifest.Process.ProcessId,
            definition.Admission.Manifest.Process.Generation);
        if (component.Identity != identity || component.Process != expectedProcess ||
            component.ManifestDigest != definition.Admission.Manifest.NormalizedDigest)
            return KernelResult<ServiceStartReceipt>.Fail(KernelError.StaleGeneration, "Admission result does not match the exact managed definition.");
        var providedInstances = new Dictionary<ServiceContractIdentity, ServiceInstanceHandle>();
        ServiceInstanceHandle instance = default;
        foreach (var provided in definition.Admission.Manifest.ProvidedContracts)
        {
            var descriptor = _kernel.ResolveByServiceName(provided.ServiceName);
            if (!descriptor.IsSuccess)
                return CompensateIncompleteAdmission(identity, definition, component.Process, descriptor.Error, descriptor.Message!);
            var providedInstance = component.ServiceInstances.SingleOrDefault(candidate => candidate.ServiceId == descriptor.Value!.Service.Id && candidate.Generation == descriptor.Value.Generation && candidate.Process == component.Process);
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
            var principal = _kernel.Processes.Resolve(_principal);
            committed = !principal.IsSuccess ? KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!) :
                _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation, () =>
                    {
                        if (!_records.TryGetValue(identity, out var record) || !ReferenceEquals(record.Definition, definition))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Managed definition changed before readiness publication.");
                        if (record.State is not ServiceLifecycleState.Admitting and not ServiceLifecycleState.RestartBackoff)
                            return KernelResult.Fail(KernelError.InvalidTransition, "Supervisor state changed during component admission.");
                        List<ServiceDependencyBinding> bindings = [];
                        var degraded = component.Admission.Disposition == ManifestAdmissionDisposition.Degraded;
                        foreach (var dependency in definition.Admission.Manifest.Dependencies)
                        {
                            var provider = ResolveProviderRecordLocked(dependency.Contract);
                            if (provider is not null && provider.ProvidedInstances.TryGetValue(dependency.Contract, out var providerInstance))
                                bindings.Add(new(instance, providerInstance, dependency.Contract, dependency.Kind));
                            else if (dependency.Kind == ServiceDependencyKind.Optional)
                                degraded = true;
                            else if (definition.ReadinessPolicy == ServiceReadinessPolicy.HardDependenciesBound)
                                return KernelResult.Fail(KernelError.DependencyUnavailable, "Hard dependency is not bound to an exact ready generation.");
                        }
                        var health = new ServiceHealthSnapshot(instance,
                            degraded ? ServiceHealthState.Degraded : ServiceHealthState.Ready, timestamp,
                            degraded ? "Service admitted with explicit optional degradation." : "Service and required dependencies are ready.");
                        record.Instance = instance;
                        record.ProvidedInstances.Clear();
                        foreach (var provided in providedInstances) record.ProvidedInstances.Add(provided.Key, provided.Value);
                        record.Bindings.Clear();
                        record.Bindings.AddRange(bindings);
                        record.State = degraded ? ServiceLifecycleState.Degraded : ServiceLifecycleState.Ready;
                        record.Health = health;
                        record.RestartEligibleTimestamp = null;
                        record.RestartAfterDrain = false;
                        record.BlockingReason = null;
                        return KernelResult.Ok();
                    });
        }
        if (!committed.IsSuccess)
            return CompensateIncompleteAdmission(identity, definition, component.Process, committed.Error, committed.Message!);
        var rebound = RebindDependents(identity, instance);
        if (!rebound.IsSuccess) return KernelResult<ServiceStartReceipt>.Fail(rebound.Error, rebound.Message!);
        var observed = Query(identity);
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
        var closure = result.IsSuccess && result.Value!.State == ComponentLifecycleState.Reclaimable;
        var unsafeOutcome = !closure && IsUnsafeTeardown(
            identity, result.IsSuccess ? KernelError.None : result.Error);
        var state = closure ? ServiceLifecycleState.Stopped :
            unsafeOutcome ? ServiceLifecycleState.Quarantined : ServiceLifecycleState.Draining;
        var healthState = closure ? ServiceHealthState.Stopped :
            unsafeOutcome ? ServiceHealthState.Quarantined : ServiceHealthState.Draining;
        var reason = closure ? "Old process/provider authority reached exact reclaimable closure." :
            result.Message ?? (unsafeOutcome ? "External effect closure is ambiguous." : "Exact teardown is still draining.");
        long timestamp;
        try { timestamp = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<ServiceDrainReceipt>.Fail(KernelError.PlatformFaulted, "Drain completion clock observation failed.");
        }
        var health = new ServiceHealthSnapshot(instance, healthState, timestamp, reason);
        lock (_gate)
        {
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess)
                return KernelResult<ServiceDrainReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            ServiceDrainReceipt? receipt = null;
            var committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    var exact = ResolveExactLocked(instance);
                    if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                    var record = exact.Value!;
                    if (record.Definition.Identity != identity)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Drain identity changed before publication.");
                    record.State = state;
                    record.Health = health;
                    record.BlockingReason = closure ? null :
                        unsafeOutcome ? result.Message ?? result.Error.ToString() : result.Message;
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
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(control.Error, control.Message!);
        lock (_gate)
        {
            var exact = ResolveExactLocked(previous);
            if (!exact.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(exact.Error, exact.Message!);
            var contract = ValidateReplacementDefinition(exact.Value!, previous, next);
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
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(KernelError.SupervisorDenied, principal.Message!);
            var validated = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    var exact = ResolveExactLocked(previous);
                    return exact.IsSuccess ? ValidateReplacementDefinition(exact.Value!, previous, next)
                        : KernelResult.Fail(exact.Error, exact.Message!);
                });
            if (!validated.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(validated.Error, validated.Message!);
        }
        var retired = _kernel.RetireReclaimableComponent(next.Identity, previous.Process);
        if (!retired.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(retired.Error, retired.Message!);
        lock (_gate)
        {
            var record = _records[next.Identity];
            record.Definition = next;
            record.State = ServiceLifecycleState.Admitting;
            record.Bindings.Clear();
            record.ProvidedInstances.Clear();
            record.Health = null;
            record.BlockingReason = null;
        }

        var admitted = _kernel.AdmitComponent(next.Admission);
        if (!admitted.IsSuccess)
        {
            lock (_gate)
            {
                var record = _records[next.Identity];
                record.State = ServiceLifecycleState.Failed;
                record.BlockingReason = admitted.Message;
            }
            return KernelResult<ServiceReplacementReceipt>.Fail(admitted.Error, admitted.Message!);
        }
        var completed = CompleteAdmission(next.Identity, next, admitted.Value!);
        if (!completed.IsSuccess) return KernelResult<ServiceReplacementReceipt>.Fail(completed.Error, completed.Message!);
        return KernelResult<ServiceReplacementReceipt>.Ok(new(new(previous, next.Admission.Manifest.NormalizedDigest), completed.Value!.Snapshot, true));
    }

    private KernelResult ScheduleRestart(ComponentIdentity identity)
    {
        Record record;
        long[] attempts;
        ServiceInstanceHandle? instance;
        lock (_gate)
        {
            if (!_records.TryGetValue(identity, out record!)) return KernelResult.Fail(KernelError.ComponentNotFound, "Restart record is absent.");
            if (record.State != ServiceLifecycleState.Stopped || !record.RestartAfterDrain)
                return KernelResult.Fail(KernelError.ReplacementBlocked, "Restart scheduling requires completed old-generation drain.");
            attempts = record.RestartAttempts.ToArray();
            instance = record.Instance;
        }
        var policy = record.Definition.Admission.Manifest.RestartPolicy;
        var disabled = policy.Mode == ServiceRestartMode.Never || record.Definition.ReplacementFactory is null;
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
                    var multiplier = 1UL << Math.Min(retained.Length, 30);
                    var scaled = policy.InitialBackoffMilliseconds > ulong.MaxValue / multiplier
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
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            return _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    if (!_records.TryGetValue(identity, out var current) || !ReferenceEquals(current, record) ||
                        current.Instance != instance || current.State != ServiceLifecycleState.Stopped ||
                        !current.RestartAfterDrain || !current.RestartAttempts.SequenceEqual(attempts))
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
            if (!_records.TryGetValue(providerIdentity, out var provider) || provider.Instance != expectedProvider)
                return KernelResult.Fail(KernelError.StaleGeneration, "Dependency provider changed before rebind capture.");
            providerDefinition = provider.Definition;
            var provided = provider.Definition.Admission.Manifest.ProvidedContracts.Select(item => item.Contract).ToHashSet();
            var candidates = _records.Values
                .Where(record => record.Definition.Identity != providerIdentity && record.Instance is not null && record.State is ServiceLifecycleState.Ready or ServiceLifecycleState.Degraded)
                .SelectMany(record => record.Definition.Admission.Manifest.Dependencies.Where(dependency => provided.Contains(dependency.Contract)).Select(dependency => (record, dependency)))
                .ToArray();
            var resolvedWork = new List<DependencyRebindWork>();
            foreach (var item in candidates)
            {
                var resolved = _kernel.ResolveByContract(item.dependency.Contract);
                if (!resolved.IsSuccess || resolved.Value!.Length != 1 ||
                    !provider.ProvidedInstances.TryGetValue(item.dependency.Contract, out var exactProvider))
                    return KernelResult.Fail(KernelError.DependencyUnavailable, "Rebind requires one exact declared provider.");
                var descriptor = resolved.Value[0];
                if (descriptor.Service.Id != exactProvider.ServiceId || descriptor.Generation != exactProvider.Generation ||
                    exactProvider.Process != expectedProvider.Process || exactProvider.Domain != expectedProvider.Domain)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Resolved dependency descriptor does not match the captured provider.");
                resolvedWork.Add(new(item.record.Definition, item.record.Instance!.Value, item.record.State,
                    item.record.Bindings.ToArray(), descriptor, item.dependency, exactProvider));
            }
            work = resolvedWork.ToArray();
        }

        for (var index = 0; index < work.Length; index++)
        {
            var item = work[index];
            long timestamp;
            try { timestamp = _timeProvider.GetTimestamp(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult.Fail(KernelError.PlatformFaulted, "Dependency rebind clock failed before dispatch.");
            }
            lock (_gate)
            {
                var principal = _kernel.Processes.Resolve(_principal);
                if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
                var admitted = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation,
                    () => ValidateDependencyRebindLocked(providerIdentity, expectedProvider, providerDefinition, item));
                if (!admitted.IsSuccess) return admitted;
            }
            var rebound = _kernel.RebindComponentDependency(item.Definition.Identity, item.Dependent.Process,
                item.Descriptor, _principal, _controlCapability);
            lock (_gate)
            {
                var principal = _kernel.Processes.Resolve(_principal);
                if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
                var published = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation, () =>
                    {
                        var exact = ValidateDependencyRebindLocked(providerIdentity, expectedProvider, providerDefinition, item);
                        if (!exact.IsSuccess) return exact;
                        var dependent = ResolveExactLocked(item.Dependent).Value!;
                        if (!rebound.IsSuccess)
                        {
                            dependent.State = ServiceLifecycleState.Degraded;
                            dependent.Health = new(item.Dependent, ServiceHealthState.Degraded, timestamp,
                                rebound.Message ?? "Dependency rebind failed.");
                            return KernelResult.Ok();
                        }
                        var bindings = item.Bindings.Where(binding => binding.Contract != item.Dependency.Contract).ToList();
                        bindings.Add(new(item.Dependent, item.Provider, item.Dependency.Contract, item.Dependency.Kind));
                        var ready = item.State == ServiceLifecycleState.Degraded &&
                            AllHardDependenciesBoundLocked(item.Definition, bindings);
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
                if (!published.IsSuccess) return published;
                // Only our committed publication advances snapshots for further
                // contracts on this same dependent. External changes still fail
                // the next exact comparison.
                var current = ResolveExactLocked(item.Dependent).Value!;
                for (var next = index + 1; next < work.Length; next++)
                    if (work[next].Dependent == item.Dependent)
                        work[next] = work[next] with { State = current.State, Bindings = current.Bindings.ToArray() };
            }
        }
        return KernelResult.Ok();
    }

    private KernelResult ValidateDependencyRebindLocked(ComponentIdentity providerIdentity,
        ServiceInstanceHandle expectedProvider, ManagedServiceDefinition providerDefinition, DependencyRebindWork item)
    {
        if (!_records.TryGetValue(providerIdentity, out var provider) || provider.Instance != expectedProvider ||
            !ReferenceEquals(provider.Definition, providerDefinition) ||
            provider.State is not ServiceLifecycleState.Ready and not ServiceLifecycleState.Degraded ||
            !provider.ProvidedInstances.TryGetValue(item.Dependency.Contract, out var provided) || provided != item.Provider)
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact dependency provider changed before rebind commit.");
        var exact = ResolveExactLocked(item.Dependent);
        if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
        var dependent = exact.Value!;
        if (!ReferenceEquals(dependent.Definition, item.Definition) || !dependent.Bindings.SequenceEqual(item.Bindings))
            return KernelResult.Fail(KernelError.StaleGeneration, "Dependent definition or bindings changed before rebind commit.");
        return dependent.State == item.State ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InvalidTransition, "Dependent lifecycle changed before rebind commit.");
    }

    private KernelResult<ServiceStartReceipt> CompensateIncompleteAdmission(
        ComponentIdentity identity, ManagedServiceDefinition definition, ProcessHandle process, KernelError error, string message)
    {
        // Cleanup consumes only the admitted generation; identity cannot redirect it.
        var drained = _kernel.DrainComponent(identity, process);
        var closure = drained.IsSuccess && drained.Value!.Reclaimable;
        var unsafeOutcome = !closure && IsUnsafeTeardown(identity, drained.IsSuccess ? KernelError.None : drained.Error);
        lock (_gate)
        {
            var principal = _kernel.Processes.Resolve(_principal);
            if (principal.IsSuccess)
                _ = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                    principal.Value!.DomainId, _principal.Generation, () =>
                    {
                        if (!_records.TryGetValue(identity, out var record) || !ReferenceEquals(record.Definition, definition))
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
        (ServiceInstanceHandle Instance, ManagedServiceDefinition Definition, ServiceLifecycleState State)[] dependents;
        lock (_gate)
        {
            if (!_records.TryGetValue(failedIdentity, out var provider) || provider.Instance != failed)
                return KernelResult.Fail(KernelError.StaleGeneration, "Failed provider generation changed before propagation.");
            dependents = _records.Values
                .Where(record => record.Instance is not null && record.Bindings.Any(binding => binding.Provider == failed))
                .Select(record => (record.Instance!.Value, record.Definition, record.State)).ToArray();
        }
        var degraded = dependents.Where(item => item.Definition.DependencyFailurePolicy == ServiceFailurePropagationPolicy.DegradeDependent).ToArray();
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
            var principal = _kernel.Processes.Resolve(_principal);
            if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
            var committed = _kernel.CapabilityAuthority.CommitSupervisorRecordPublication(_controlCapability,
                principal.Value!.DomainId, _principal.Generation, () =>
                {
                    if (!_records.TryGetValue(failedIdentity, out var provider) || provider.Instance != failed ||
                        provider.State != ServiceLifecycleState.Failed)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Failed provider changed before dependency publication.");
                    // Validate the entire captured batch before publishing any peer change.
                    foreach (var item in dependents)
                    {
                        var exact = ResolveExactLocked(item.Instance);
                        if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                        var record = exact.Value!;
                        if (!ReferenceEquals(record.Definition, item.Definition) || !record.Bindings.Any(binding => binding.Provider == failed))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Dependent definition or binding changed before propagation.");
                        if (record.State != item.State)
                            return KernelResult.Fail(KernelError.InvalidTransition, "Dependent state changed before propagation.");
                    }
                    foreach (var item in degraded)
                    {
                        var record = ResolveExactLocked(item.Instance).Value!;
                        record.State = ServiceLifecycleState.Degraded;
                        record.Health = new(item.Instance, ServiceHealthState.Degraded, timestamp, "Exact hard dependency generation failed.");
                    }
                    return KernelResult.Ok();
                });
            if (!committed.IsSuccess) return committed;
        }
        foreach (var item in dependents.Where(item => item.Definition.DependencyFailurePolicy != ServiceFailurePropagationPolicy.DegradeDependent))
        {
            var drained = Drain(item.Instance, planned: item.Definition.DependencyFailurePolicy == ServiceFailurePropagationPolicy.StopDependent);
            if (!drained.IsSuccess) return KernelResult.Fail(drained.Error, drained.Message!);
        }
        return KernelResult.Ok();
    }
    private KernelResult ValidateControl()
    {
        var capability = _kernel.ValidateCapability(_principal, _controlCapability, CapabilityRights.Configure | CapabilityRights.Execute);
        if (!capability.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, capability.Message ?? "Supervisor control capability is invalid.");
        return capability.Value!.ResourceKind == ResourceKind.KernelService &&
               string.Equals(capability.Value.ResourceId, CapabilityResourceIds.ServiceSupervisor, StringComparison.Ordinal)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.SupervisorDenied, "Capability does not authorize service-supervisor control.");
    }

    private KernelResult<Record> ResolveExactLocked(ServiceInstanceHandle instance)
    {
        var record = _records.Values.SingleOrDefault(candidate => candidate.Instance == instance);
        return record is null
            ? KernelResult<Record>.Fail(KernelError.StaleGeneration, "Service instance handle is stale or unknown.")
            : KernelResult<Record>.Ok(record);
    }

    private KernelResult ValidateReplacementDefinition(Record record, ServiceInstanceHandle previous, ManagedServiceDefinition next)
    {
        if (next.Identity != record.Definition.Identity)
            return KernelResult.Fail(KernelError.InvalidManifest, "Replacement must preserve the managed component identity.");
        var process = next.Admission.Manifest.Process;
        if (process.ProcessId != previous.Process.ProcessId || process.Generation != checked(previous.Process.Generation + 1))
            return KernelResult.Fail(KernelError.StaleGeneration, "Replacement must use the same process identity and exactly the next generation.");
        if (!string.Equals(next.PrimaryServiceName, record.Definition.PrimaryServiceName, StringComparison.Ordinal))
            return KernelResult.Fail(KernelError.InvalidManifest, "Replacement must preserve the primary service identity.");
        return KernelResult.Ok();
    }

    private KernelResult ValidateAcyclicGraphLocked()
    {
        var duplicateProvider = _records.Values
            .SelectMany(record => record.Definition.Admission.Manifest.ProvidedContracts.Select(provided => (provided.Contract, record.Definition.Identity)))
            .GroupBy(item => item.Contract)
            .FirstOrDefault(group => group.Select(item => item.Identity).Distinct().Count() > 1);
        if (duplicateProvider is not null)
            return KernelResult.Fail(KernelError.InvalidManifest, $"Contract '{duplicateProvider.Key.Name}/{duplicateProvider.Key.Version}' has multiple managed providers.");
        var visiting = new HashSet<ComponentIdentity>();
        var visited = new HashSet<ComponentIdentity>();
        bool Visit(ComponentIdentity identity)
        {
            if (!visiting.Add(identity)) return false;
            if (visited.Contains(identity)) { visiting.Remove(identity); return true; }
            foreach (var provider in ResolveProviderIdentitiesLocked(_records[identity].Definition, null))
                if (!Visit(provider)) return false;
            visiting.Remove(identity);
            visited.Add(identity);
            return true;
        }
        return _records.Keys.All(Visit)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InvalidManifest, "Managed service dependency graph contains a cycle.");
    }

    private IEnumerable<ComponentIdentity> TopologicalOrderLocked()
    {
        var visited = new HashSet<ComponentIdentity>();
        var result = new List<ComponentIdentity>();
        void Visit(ComponentIdentity identity)
        {
            if (!visited.Add(identity)) return;
            foreach (var provider in ResolveProviderIdentitiesLocked(_records[identity].Definition, null)) Visit(provider);
            result.Add(identity);
        }
        foreach (var identity in _records.Keys.OrderBy(item => item.Name, StringComparer.Ordinal)) Visit(identity);
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
                provider.ProvidedInstances.TryGetValue(dependency.Contract, out var exact) &&
                bindings.Any(binding => binding.Contract == dependency.Contract && binding.Provider == exact));

    private bool IsUnsafeTeardown(ComponentIdentity identity, KernelError error)
    {
        if (error is KernelError.ExternalEffectUncontained or KernelError.PlatformFaulted or KernelError.Quarantined)
            return true;
        var diagnostic = _kernel.QueryComponentReclaimDiagnostics(identity);
        return diagnostic.IsSuccess && diagnostic.Value!.BlockingDependency?.ExternalState is
            ReclaimExternalState.ExternalStateUnknown or ReclaimExternalState.Quarantined;
    }

    private ServiceSupervisorSnapshot Snapshot(Record record) =>
        new(record.Definition.Identity, record.Instance, record.State, record.Health, record.Bindings.ToArray(),
            (uint)record.RestartAttempts.Count, record.RestartEligibleTimestamp,
            record.Definition.Admission.Manifest.NormalizedDigest, record.BlockingReason);

    private long AddMilliseconds(long timestamp, ulong milliseconds)
    {
        var frequency = _timeProvider.TimestampFrequency;
        if (frequency <= 0) throw new InvalidOperationException("Restart timestamp frequency must be positive.");
        var delta = checked((long)Math.Ceiling(milliseconds * (double)frequency / 1000d));
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
        var capability = ValidateCapability(principal, controlCapability, CapabilityRights.Configure | CapabilityRights.Execute);
        if (!capability.IsSuccess)
            return KernelResult<CapabilityAwareServiceSupervisor>.Fail(KernelError.SupervisorDenied, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.KernelService ||
            !string.Equals(capability.Value.ResourceId, CapabilityResourceIds.ServiceSupervisor, StringComparison.Ordinal))
            return KernelResult<CapabilityAwareServiceSupervisor>.Fail(KernelError.SupervisorDenied, "Capability does not authorize service-supervisor control.");
        return KernelResult<CapabilityAwareServiceSupervisor>.Ok(new(this, principal, controlCapability, timeProvider ?? TimeProvider.System));
    }
}
