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
            return _records.TryGetValue(identity, out var record)
                ? KernelResult<ServiceSupervisorSnapshot>.Ok(Snapshot(record))
                : KernelResult<ServiceSupervisorSnapshot>.Fail(KernelError.ComponentNotFound, $"Managed service '{identity.Name}' is not registered.");
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
        return _kernel.ProjectSupervisorTelemetry(instance.Process, snapshot);
    }

    public KernelResult<ServiceHealthSnapshot> ReportHealth(ServiceInstanceHandle instance, ServiceHealthState state, string reason)
    {
        var control = ValidateControl();
        if (!control.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(control.Error, control.Message!);
        if (!Enum.IsDefined(state)) return KernelResult<ServiceHealthSnapshot>.Fail(KernelError.InvalidTransition, "Health state is undefined.");
        ComponentIdentity identity;
        ServiceHealthSnapshot health;
        lock (_gate)
        {
            var exact = ResolveExactLocked(instance);
            if (!exact.IsSuccess) return KernelResult<ServiceHealthSnapshot>.Fail(exact.Error, exact.Message!);
            identity = exact.Value!.Definition.Identity;
            health = new(instance, state, _timeProvider.GetTimestamp(), reason ?? string.Empty);
            exact.Value.Health = health;
            if (state == ServiceHealthState.Degraded && exact.Value.State == ServiceLifecycleState.Ready)
                exact.Value.State = ServiceLifecycleState.Degraded;
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
        ComponentIdentity identity;
        lock (_gate)
        {
            var exact = ResolveExactLocked(instance);
            if (!exact.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(exact.Error, exact.Message!);
            identity = exact.Value!.Definition.Identity;
            exact.Value.State = ServiceLifecycleState.Draining;
            exact.Value.Health = Health(exact.Value, ServiceHealthState.Draining, planned ? "Planned drain requested." : "Failure drain requested.");
        }

        var drained = _kernel.DrainComponent(identity);
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
            var exact = ResolveExactLocked(instance);
            if (!exact.IsSuccess) return KernelResult<ServiceDrainReceipt>.Fail(exact.Error, exact.Message!);
            var record = exact.Value!;
            record.State = timeoutPolicy == ServiceDrainTimeoutPolicy.Quarantine
                ? ServiceLifecycleState.Quarantined
                : ServiceLifecycleState.Draining;
            record.Health = Health(
                record,
                timeoutPolicy == ServiceDrainTimeoutPolicy.Quarantine ? ServiceHealthState.Quarantined : ServiceHealthState.Draining,
                "Drain deadline expired; no closure or reclaim was inferred.");
            record.BlockingReason = "Drain deadline expired before exact closure/containment.";
            var receipt = new ServiceDrainReceipt(new(instance, planned), Snapshot(record), ClosureProven: false);
            return timeoutPolicy == ServiceDrainTimeoutPolicy.Quarantine
                ? KernelResult<ServiceDrainReceipt>.Ok(receipt)
                : KernelResult<ServiceDrainReceipt>.Fail(KernelError.ReplacementBlocked, record.BlockingReason);
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

        if (state == ServiceLifecycleState.Draining && instance is { } drainingInstance)
        {
            var observed = _kernel.ObserveComponentTeardown(identity);
            var completion = CompleteDrain(identity, drainingInstance, planned: !restartAfterDrain, observed);
            if (!completion.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(completion.Error, completion.Message!);
            if (completion.Value!.ClosureProven && restartAfterDrain)
                ScheduleRestart(identity);
        }

        lock (_gate)
        {
            var record = _records[identity];
            state = record.State;
            eligible = record.RestartEligibleTimestamp;
        }
        if (state == ServiceLifecycleState.RestartBackoff && eligible is { } timestamp && _timeProvider.GetTimestamp() >= timestamp)
        {
            ManagedServiceDefinition? replacement = null;
            ServiceInstanceHandle prior;
            lock (_gate)
            {
                var record = _records[identity];
                prior = record.Instance!.Value;
                var generation = checked(prior.Process.Generation + 1);
                if (record.Definition.ReplacementFactory is { } factory)
                {
                    try
                    {
                        replacement = new ManagedServiceDefinition(factory(generation), record.Definition.PrimaryServiceName, record.Definition.ReadinessPolicy, record.Definition.DependencyFailurePolicy, record.Definition.ReplacementFactory);
                    }
                    catch (Exception exception)
                    {
                        record.State = ServiceLifecycleState.Failed;
                        record.BlockingReason = $"Fresh-admission factory failed: {exception.Message}";
                    }
                }
                else
                {
                    record.State = ServiceLifecycleState.Failed;
                    record.BlockingReason = "Restart policy has no fresh-admission factory.";
                }
            }
            if (replacement is not null)
            {
                var replaced = AdmitReplacement(prior, replacement);
                if (!replaced.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(replaced.Error, replaced.Message!);
            }
        }

        lock (_gate) return KernelResult<ServiceSupervisorSnapshot>.Ok(Snapshot(_records[identity]));
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

        PropagateDependencyFailure(identity);
        var faulted = _kernel.FaultComponent(identity);
        var drain = CompleteDrain(identity, instance, planned: false, faulted);
        if (!drain.IsSuccess) return KernelResult<ServiceSupervisorSnapshot>.Fail(drain.Error, drain.Message!);
        if (drain.Value!.ClosureProven) ScheduleRestart(identity);
        lock (_gate) return KernelResult<ServiceSupervisorSnapshot>.Ok(Snapshot(_records[identity]));
    }

    private KernelResult<ServiceStartReceipt> CompleteAdmission(
        ComponentIdentity identity,
        ManagedServiceDefinition definition,
        ComponentLifecycleSnapshot component)
    {
        var providedInstances = new Dictionary<ServiceContractIdentity, ServiceInstanceHandle>();
        ServiceInstanceHandle instance = default;
        foreach (var provided in definition.Admission.Manifest.ProvidedContracts)
        {
            var descriptor = _kernel.ResolveByServiceName(provided.ServiceName);
            if (!descriptor.IsSuccess)
                return CompensateIncompleteAdmission(identity, descriptor.Error, descriptor.Message!);
            var providedInstance = component.ServiceInstances.SingleOrDefault(candidate => candidate.ServiceId == descriptor.Value!.Service.Id);
            if (providedInstance == default)
                return CompensateIncompleteAdmission(identity, KernelError.ServiceNotFound, $"Provided service '{provided.ServiceName}' was not materialized by component admission.");
            providedInstances.Add(provided.Contract, providedInstance);
            if (string.Equals(provided.ServiceName, definition.PrimaryServiceName, StringComparison.Ordinal))
                instance = providedInstance;
        }
        if (instance == default)
            return CompensateIncompleteAdmission(identity, KernelError.ServiceNotFound, "Primary service instance was not materialized by component admission.");

        List<ServiceDependencyBinding> bindings = [];
        var degraded = component.Admission.Disposition == ManifestAdmissionDisposition.Degraded;
        KernelError? completionError = null;
        string? completionMessage = null;
        lock (_gate)
        {
            var record = _records[identity];
            if (record.State != ServiceLifecycleState.Admitting && record.State != ServiceLifecycleState.RestartBackoff)
            {
                completionError = KernelError.InvalidTransition;
                completionMessage = "Supervisor state changed during component admission.";
            }
            else
            {
                record.State = ServiceLifecycleState.Starting;
                foreach (var dependency in definition.Admission.Manifest.Dependencies)
                {
                    var provider = ResolveProviderRecordLocked(dependency.Contract);
                    if (provider is not null && provider.ProvidedInstances.TryGetValue(dependency.Contract, out var providerInstance))
                        bindings.Add(new(instance, providerInstance, dependency.Contract, dependency.Kind));
                    else if (dependency.Kind == ServiceDependencyKind.Optional)
                        degraded = true;
                    else if (definition.ReadinessPolicy == ServiceReadinessPolicy.HardDependenciesBound)
                    {
                        completionError = KernelError.DependencyUnavailable;
                        completionMessage = "Hard dependency is not bound to an exact ready generation.";
                        break;
                    }
                }
                if (completionError is null)
                {
                    record.Instance = instance;
                    record.ProvidedInstances.Clear();
                    foreach (var provided in providedInstances) record.ProvidedInstances.Add(provided.Key, provided.Value);
                    record.Bindings.Clear();
                    record.Bindings.AddRange(bindings);
                    record.State = degraded ? ServiceLifecycleState.Degraded : ServiceLifecycleState.Ready;
                    record.Health = Health(record, degraded ? ServiceHealthState.Degraded : ServiceHealthState.Ready,
                        degraded ? "Service admitted with explicit optional degradation." : "Service and required dependencies are ready.");
                    record.RestartEligibleTimestamp = null;
                    record.RestartAfterDrain = false;
                    record.BlockingReason = null;
                }
            }
        }
        if (completionError is { } error)
            return CompensateIncompleteAdmission(identity, error, completionMessage!);
        RebindDependents(identity);
        lock (_gate)
        {
            var snapshot = Snapshot(_records[identity]);
            return KernelResult<ServiceStartReceipt>.Ok(new(new(identity, definition.Admission.Manifest.NormalizedDigest), snapshot));
        }
    }

    private KernelResult<ServiceDrainReceipt> CompleteDrain(
        ComponentIdentity identity,
        ServiceInstanceHandle instance,
        bool planned,
        KernelResult<ComponentLifecycleSnapshot> result)
    {
        var closure = result.IsSuccess && result.Value!.State == ComponentLifecycleState.Reclaimable;
        var unsafeOutcome = !closure && IsUnsafeTeardown(
            identity,
            result.IsSuccess ? KernelError.None : result.Error);
        lock (_gate)
        {
            var record = _records[identity];
            if (closure)
            {
                record.State = ServiceLifecycleState.Stopped;
                record.Health = Health(record, ServiceHealthState.Stopped, "Old process/provider authority reached exact reclaimable closure.");
                record.BlockingReason = null;
            }
            else if (unsafeOutcome)
            {
                record.State = ServiceLifecycleState.Quarantined;
                record.Health = Health(record, ServiceHealthState.Quarantined, result.Message ?? "External effect closure is ambiguous.");
                record.BlockingReason = result.Message ?? result.Error.ToString();
            }
            else
            {
                record.State = ServiceLifecycleState.Draining;
                record.Health = Health(record, ServiceHealthState.Draining, result.Message ?? "Exact teardown is still draining.");
                record.BlockingReason = result.Message;
            }
            var receipt = new ServiceDrainReceipt(new(instance, planned), Snapshot(record), closure);
            if (!result.IsSuccess && !unsafeOutcome && result.Error != KernelError.PlatformBindingDraining)
                return KernelResult<ServiceDrainReceipt>.Fail(result.Error, result.Message!);
            return KernelResult<ServiceDrainReceipt>.Ok(receipt);
        }
    }

    private KernelResult<ServiceReplacementReceipt> AdmitReplacement(ServiceInstanceHandle previous, ManagedServiceDefinition next)
    {
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

    private void ScheduleRestart(ComponentIdentity identity)
    {
        lock (_gate)
        {
            var record = _records[identity];
            var policy = record.Definition.Admission.Manifest.RestartPolicy;
            if (policy.Mode == ServiceRestartMode.Never || record.Definition.ReplacementFactory is null)
            {
                record.State = ServiceLifecycleState.Failed;
                record.BlockingReason = policy.Mode == ServiceRestartMode.Never ? "Restart policy is Never." : "Fresh-admission replacement factory is absent.";
                return;
            }
            var now = _timeProvider.GetTimestamp();
            record.RestartAttempts.RemoveAll(timestamp => _timeProvider.GetElapsedTime(timestamp, now).TotalMilliseconds > policy.WindowMilliseconds);
            if (record.RestartAttempts.Count >= policy.MaximumAttempts)
            {
                record.State = ServiceLifecycleState.CrashLoop;
                record.Health = Health(record, ServiceHealthState.Failed, "Bounded restart window is exhausted.");
                record.BlockingReason = "Crash-loop restart limit reached.";
                return;
            }
            var exponent = Math.Min(record.RestartAttempts.Count, 30);
            var multiplier = 1UL << exponent;
            var scaled = policy.InitialBackoffMilliseconds > ulong.MaxValue / multiplier
                ? ulong.MaxValue
                : policy.InitialBackoffMilliseconds * multiplier;
            var delay = Math.Min(policy.MaximumBackoffMilliseconds, scaled);
            record.RestartAttempts.Add(now);
            record.RestartEligibleTimestamp = AddMilliseconds(now, delay);
            record.State = ServiceLifecycleState.RestartBackoff;
            record.Health = Health(record, ServiceHealthState.Stopped, $"Fresh admission eligible after bounded backoff ({delay} ms).");
        }
    }

    private void RebindDependents(ComponentIdentity providerIdentity)
    {
        (ComponentIdentity Identity, ServiceEndpointDescriptor Descriptor, ServiceDependencyRequirementV1 Dependency)[] work;
        lock (_gate)
        {
            if (!_records.TryGetValue(providerIdentity, out var provider) || provider.Instance is null) return;
            var provided = provider.Definition.Admission.Manifest.ProvidedContracts.Select(item => item.Contract).ToHashSet();
            var candidates = _records.Values
                .Where(record => record.Definition.Identity != providerIdentity && record.Instance is not null && record.State is ServiceLifecycleState.Ready or ServiceLifecycleState.Degraded)
                .SelectMany(record => record.Definition.Admission.Manifest.Dependencies.Where(dependency => provided.Contains(dependency.Contract)).Select(dependency => (record.Definition.Identity, dependency)))
                .ToArray();
            var resolvedWork = new List<(ComponentIdentity, ServiceEndpointDescriptor, ServiceDependencyRequirementV1)>();
            foreach (var item in candidates)
            {
                var resolved = _kernel.ResolveByContract(item.dependency.Contract);
                if (resolved.IsSuccess && resolved.Value!.Length == 1)
                    resolvedWork.Add((item.Identity, resolved.Value[0], item.dependency));
            }
            work = resolvedWork.ToArray();
        }

        foreach (var item in work)
        {
            var rebound = _kernel.RebindComponentDependency(item.Identity, item.Descriptor);
            lock (_gate)
            {
                var dependent = _records[item.Identity];
                if (!rebound.IsSuccess)
                {
                    dependent.State = ServiceLifecycleState.Degraded;
                    dependent.Health = Health(dependent, ServiceHealthState.Degraded, rebound.Message ?? "Dependency rebind failed.");
                    continue;
                }
                var provider = _records[providerIdentity];
                dependent.Bindings.RemoveAll(binding => binding.Contract == item.Dependency.Contract);
                dependent.Bindings.Add(new(dependent.Instance!.Value, provider.ProvidedInstances[item.Dependency.Contract], item.Dependency.Contract, item.Dependency.Kind));
                if (dependent.State == ServiceLifecycleState.Degraded && AllHardDependenciesBoundLocked(dependent))
                {
                    dependent.State = ServiceLifecycleState.Ready;
                    dependent.Health = Health(dependent, ServiceHealthState.Ready, "Dependency rebound to the fresh exact generation.");
                }
            }
        }
    }

    private KernelResult<ServiceStartReceipt> CompensateIncompleteAdmission(ComponentIdentity identity, KernelError error, string message)
    {
        var drained = _kernel.DrainComponent(identity);
        var unsafeOutcome = !drained.IsSuccess && IsUnsafeTeardown(identity, drained.Error);
        lock (_gate)
        {
            var record = _records[identity];
            if (drained.IsSuccess && drained.Value!.State == ComponentLifecycleState.Reclaimable)
            {
                record.State = ServiceLifecycleState.Stopped;
                record.BlockingReason = message;
            }
            else if (unsafeOutcome)
            {
                record.State = ServiceLifecycleState.Quarantined;
                record.BlockingReason = drained.Message ?? message;
            }
            else
            {
                record.State = ServiceLifecycleState.Draining;
                record.BlockingReason = drained.Message ?? message;
            }
        }
        return KernelResult<ServiceStartReceipt>.Fail(error, message);
    }

    private void PropagateDependencyFailure(ComponentIdentity failedIdentity)
    {
        (ServiceInstanceHandle Instance, ServiceFailurePropagationPolicy Policy)[] dependents;
        lock (_gate)
        {
            dependents = _records.Values
                .Where(record => record.Instance is not null && record.Bindings.Any(binding =>
                    _records[failedIdentity].Instance is { } failed && binding.Provider == failed))
                .Select(record => (record.Instance!.Value, record.Definition.DependencyFailurePolicy))
                .ToArray();
            foreach (var item in dependents.Where(item => item.Policy == ServiceFailurePropagationPolicy.DegradeDependent))
            {
                var record = ResolveExactLocked(item.Instance).Value!;
                record.State = ServiceLifecycleState.Degraded;
                record.Health = Health(record, ServiceHealthState.Degraded, "Exact hard dependency generation failed.");
            }
        }
        foreach (var item in dependents.Where(item => item.Policy != ServiceFailurePropagationPolicy.DegradeDependent))
            _ = Drain(item.Instance, planned: item.Policy == ServiceFailurePropagationPolicy.StopDependent);
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

    private static bool AllHardDependenciesBoundLocked(Record record) =>
        record.Definition.Admission.Manifest.Dependencies
            .Where(dependency => dependency.Kind == ServiceDependencyKind.Hard)
            .All(dependency => record.Bindings.Any(binding => binding.Contract == dependency.Contract));

    private bool IsUnsafeTeardown(ComponentIdentity identity, KernelError error)
    {
        if (error is KernelError.ExternalEffectUncontained or KernelError.PlatformFaulted or KernelError.Quarantined)
            return true;
        var diagnostic = _kernel.QueryComponentReclaimDiagnostics(identity);
        return diagnostic.IsSuccess && diagnostic.Value!.BlockingDependency?.ExternalState is
            ReclaimExternalState.ExternalStateUnknown or ReclaimExternalState.Quarantined;
    }

    private ServiceHealthSnapshot Health(Record record, ServiceHealthState state, string reason) =>
        new(record.Instance ?? throw new InvalidOperationException("Health observation requires an exact service instance."), state, _timeProvider.GetTimestamp(), reason);

    private ServiceSupervisorSnapshot Snapshot(Record record) =>
        new(record.Definition.Identity, record.Instance, record.State, record.Health, record.Bindings.ToArray(),
            (uint)record.RestartAttempts.Count, record.RestartEligibleTimestamp,
            record.Definition.Admission.Manifest.NormalizedDigest, record.BlockingReason);

    private long AddMilliseconds(long timestamp, ulong milliseconds)
    {
        var delta = checked((long)Math.Ceiling(milliseconds * (double)_timeProvider.TimestampFrequency / 1000d));
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
