using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ComponentIdentity, ComponentAdmissionRecord> _components = new();
    private readonly object _componentAdmissionGate = new();
    internal Action? BeforeComponentAdmissionCommitQualificationHook { get; set; }
    internal Action? BeforeComponentProcessCreationQualificationHook { get; set; }
    internal Action? BeforeComponentGrantMintQualificationHook { get; set; }
    internal Action? BeforeComponentProcessAdmissionQualificationHook { get; set; }
    internal Action? BeforeComponentPlatformBindingQualificationHook { get; set; }
    internal Action? BeforeComponentServiceRegistrationQualificationHook { get; set; }
    internal Action? BeforeComponentDependencySessionQualificationHook { get; set; }
    internal Action? BeforeComponentExecutionStartQualificationHook { get; set; }
    internal Action? BeforeComponentDriverDeviceQualificationHook { get; set; }
    internal Action? BeforeComponentDriverMmioQualificationHook { get; set; }
    internal Action? BeforeComponentDriverIrqEndpointQualificationHook { get; set; }
    internal Action<KernelEventEndpoint>? BeforeComponentDriverIrqBindingQualificationHook { get; set; }
    internal Action? BeforeComponentTeardownPublicationQualificationHook { get; set; }
    internal Action? BeforeComponentFailurePublicationQualificationHook { get; set; }
    internal Action? BeforeComponentRollbackPublicationQualificationHook { get; set; }
    internal Action? BeforeComponentRunningPublicationQualificationHook { get; set; }
    internal Action? BeforeComponentRollbackCleanupQualificationHook { get; set; }
    internal Action? BeforeComponentRollbackSourceStopQualificationHook { get; set; }
    internal Action? BeforeComponentStartingPublicationQualificationHook { get; set; }
    internal Action? BeforeComponentCreatedPublicationQualificationHook { get; set; }
    internal Action? BeforeComponentCapabilityTrackingQualificationHook { get; set; }
    internal Action? BeforeComponentDriverTrackingQualificationHook { get; set; }
    internal Action? BeforeComponentDmaTrackingQualificationHook { get; set; }
    internal Action? BeforeComponentServiceTrackingQualificationHook { get; set; }
    internal Action? BeforeComponentSessionTrackingQualificationHook { get; set; }
    internal Action? BeforeComponentBindingTrackingQualificationHook { get; set; }
    internal Action? BeforeComponentOptionalEvidenceQualificationHook { get; set; }
    internal Action? AfterComputeComponentAuthoritySnapshotQualificationHook { get; set; }
    internal Action? BeforeComponentTeardownIntentQualificationHook { get; set; }
    internal Action? AfterComponentTeardownIntentQualificationHook { get; set; }
    internal Action? BeforeComponentTeardownRecordQueryQualificationHook { get; set; }

    internal KernelResult CommitServiceDependencyReadiness(IReadOnlyList<ServiceInstanceHandle> expected, Func<KernelResult> commit)
    {
        lock (_platformMemoryUseGate)
        {
            foreach (ServiceInstanceHandle instance in expected)
            {
                KernelResult<SingProcess> process = Processes.Resolve(instance.Process);
                if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                if (process.Value!.State != ProcessState.Running || _processTeardowns.ContainsKey(instance.Process))
                    return KernelResult.Fail(KernelError.InvalidTransition, "Dependency provider is no longer running at publication.");
            }
            return Services.CommitDependencyReadiness(expected, commit);
        }
    }

    internal KernelResult CommitComponentReadiness(ComponentLifecycleSnapshot expected,
        IReadOnlyList<ServiceInstanceHandle> dependencies, Func<KernelResult> commit)
    {
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
                KernelResult<SingProcess> process = Processes.Resolve(expected.Process);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            if (process.Value!.State != ProcessState.Running || _processTeardowns.ContainsKey(expected.Process) ||
                !_components.TryGetValue(expected.Identity, out ComponentAdmissionRecord? record) || record.Process != expected.Process ||
                record.Manifest.NormalizedDigest != expected.ManifestDigest || record.State != ComponentLifecycleState.Running)
                return KernelResult.Fail(KernelError.InvalidTransition, "Exact component is no longer running at readiness publication.");
            return CommitServiceDependencyReadiness(dependencies, () => Services.CommitProviderReadiness(expected.Process, expected.ServiceInstances, () =>
                CapabilityAuthority.CommitProcessAdmission(process.Value.DomainId, expected.Process.Generation,
                    record.Manifest.Process.RequiredCapabilities.ToArray(), () =>
                        CommitRunningComponentPublication(dependencies.Select(instance => instance.Process).Append(expected.Process).ToArray(), commit))));
        }
    }

    // Called after owner admission locks are held. The consumer writes local metadata only.
    // Snapshot gates serialize publication with component intent, without blocking intent
    // behind a provider callback or the global component admission gate.
    internal KernelResult CommitRunningComponentPublication(IReadOnlyList<ProcessHandle> processes, Func<KernelResult> commit)
    {
        ProcessHandle[] expected = processes.Distinct().OrderBy(process => process.ProcessId.Value).ThenBy(process => process.Generation).ToArray();
        ComponentAdmissionRecord[] records = _components.Values.Where(record => expected.Contains(record.Process))
            .OrderBy(record => record.Process.ProcessId.Value).ThenBy(record => record.Process.Generation).ToArray();
        if (records.Length != expected.Length)
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact component readiness record is missing.");
        return CommitAt(0);

        KernelResult CommitAt(int index)
        {
            if (index == records.Length) return commit();
            ComponentAdmissionRecord record = records[index];
            lock (record.SnapshotGate)
            {
                if (!_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                    record.State != ComponentLifecycleState.Running)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Exact component has requested teardown before readiness publication.");
                return CommitAt(index + 1);
            }
        }
    }

    public KernelResult<ComponentLifecycleSnapshot> AdmitComponent(ComponentAdmissionPlan plan) => AdmitComponentCore(plan, null);

    internal KernelResult<ComponentLifecycleSnapshot> AdmitComponent(ComponentAdmissionPlan plan, ProcessHandle principal, CapabilityId control) =>
        AdmitComponentCore(plan, (principal, control));

    private KernelResult<ComponentLifecycleSnapshot> AdmitComponentCore(ComponentAdmissionPlan plan,
        (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? restoreCommit = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ServiceManifestV1 manifest = plan.Manifest;
        KernelResult imports = ValidateComponentContractImports(plan);
        if (!imports.IsSuccess) return KernelResult<ComponentLifecycleSnapshot>.Fail(imports.Error, imports.Message!);
        ComponentAdmissionEvaluation evaluation = EvaluateComponentAdmissionCore(plan);
        // Dependency availability is evidence at preflight time. Preserve the
        // existing admission/rollback lifecycle so a hard dependency loss is
        // recorded on the component rather than becoming a second authority
        // decision in the evaluator.
        if (evaluation.Error is { } evaluationError && evaluationError != KernelError.ServiceNotFound)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(evaluationError, evaluation.Message!);

        KernelResult processPreflight = Processes.ValidateCreation(manifest.Process);
        if (!processPreflight.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(processPreflight.Error, processPreflight.Message!);

        ProcessHandle processHandle = new ProcessHandle(manifest.Process.ProcessId, manifest.Process.Generation);
        BeforeComponentAdmissionCommitQualificationHook?.Invoke();
        ComponentAdmissionRecord record = null!;
        KernelResult allocation;
        if (restoreCommit is not null)
            allocation = restoreCommit(Allocate);
        else if (source is null)
        {
            lock (_componentAdmissionGate) allocation = Allocate();
        }
        else
        {
            lock (_platformMemoryUseGate)
            lock (_componentAdmissionGate)
            {
                    (ProcessHandle Principal, CapabilityId Capability) controller = source.Value;
                    KernelResult<SingProcess> principal = Processes.Resolve(controller.Principal);
                if (!principal.IsSuccess)
                    allocation = KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
                else
                {
                        KernelResult effect = EnsureProcessAcceptsNewEffects(principal.Value!);
                    allocation = effect.IsSuccess ? CapabilityAuthority.CommitSupervisorComponentAllocation(controller.Capability,
                        principal.Value!.DomainId, controller.Principal.Generation, Allocate)
                        : KernelResult.Fail(KernelError.SupervisorDenied, effect.Message!);
                }
            }
        }
        if (!allocation.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(allocation.Error, allocation.Message!);

        KernelResult Allocate()
        {
            if (_components.ContainsKey(manifest.Identity))
                return KernelResult.Fail(KernelError.DuplicateIdentity, "Component identity was claimed before admission commit.");
            BudgetAmount[] limits = manifest.BudgetRequests.Select(static request => new BudgetAmount(request.Dimension, request.Limit)).ToArray();
            KernelResult<ProcessBudgetAdmission> hierarchy = Budgets.CreateProcessHierarchy(processHandle,
                $"service:{manifest.Identity.Name}:{manifest.Process.Generation}",
                $"process:{processHandle.ProcessId.Value}:{processHandle.Generation}", limits);
            if (!hierarchy.IsSuccess)
                return KernelResult.Fail(hierarchy.Error, hierarchy.Message!);
            ManifestAdmissionResultV1 admittedBudgetEvidence = evaluation.Result;
            foreach (ServiceBudgetRequestV1 request in manifest.BudgetRequests)
                admittedBudgetEvidence = MarkRequirementGranted(
                    admittedBudgetEvidence,
                    ManifestRequirementKind.Budget,
                    $"{request.Dimension}:{request.Limit}",
                    "Exact capacity was allocated in the generation-bound budget hierarchy; the reservation remains non-authoritative.");

            record = new ComponentAdmissionRecord
            {
                Plan = plan,
                Manifest = manifest,
                Process = processHandle,
                Admission = admittedBudgetEvidence,
                State = ComponentLifecycleState.Admitting,
                ServiceBudget = hierarchy.Value!.ServiceBudget,
                ProcessBudget = hierarchy.Value!.ProcessBudget,
            };
            _components.TryAdd(manifest.Identity, record);
            return KernelResult.Ok();
        }

        BeforeComponentProcessCreationQualificationHook?.Invoke();
        KernelResult<SingPlus.Sip.SingProcess> created;
        if (restoreCommit is not null)
        {
            created = CreateProcessCore(manifest.Process, source: null, out bool invoked, sourceCommit: restoreCommit);
            if (!created.IsSuccess && !invoked)
                return RefuseUncreatedComponent(record, created.Error, created.Message!);
        }
        else if (source is { } creator)
        {
            created = CreateProcess(manifest.Process, creator.Principal, creator.Capability, out bool invoked);
            if (!created.IsSuccess && !invoked)
                return RefuseUncreatedComponent(record, created.Error, created.Message!);
        }
        else created = CreateProcess(manifest.Process);
        if (!created.IsSuccess) return FailAdmission(record, created.Error, created.Message!);
        BeforeComponentCreatedPublicationQualificationHook?.Invoke();
        KernelResult createdPublication;
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
                KernelResult<SingProcess> currentProcess = Processes.Resolve(processHandle);
            if (!currentProcess.IsSuccess)
                createdPublication = KernelResult.Fail(currentProcess.Error, currentProcess.Message!);
            else if (!ReferenceEquals(currentProcess.Value, created.Value) || currentProcess.Value!.State != ProcessState.Created ||
                _processTeardowns.ContainsKey(processHandle))
                createdPublication = KernelResult.Fail(KernelError.InvalidTransition, "Created process changed before component publication.");
            else if (!_components.TryGetValue(manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                record.State != ComponentLifecycleState.Admitting)
                createdPublication = KernelResult.Fail(KernelError.StaleGeneration, "Exact component changed before created publication.");
            else
            {
                KernelResult PublishCreated()
                {
                    record.State = ComponentLifecycleState.Created;
                    return KernelResult.Ok();
                }
                createdPublication = restoreCommit is null ? PublishCreated() : restoreCommit(PublishCreated);
            }
        }
        if (!createdPublication.IsSuccess) return Rollback(record, createdPublication.Error, createdPublication.Message!);

        foreach (ComponentCapabilityGrant grant in plan.Grants)
        {
            BeforeComponentGrantMintQualificationHook?.Invoke();
            KernelResult<CapabilityDescriptorV1> minted = MintCapabilityCore(grant.IssuerDomain, processHandle, grant.Requirement.ResourceKind,
                grant.Requirement.ResourceId, grant.Requirement.Rights,
                sourceCommitConsumer: restoreCommit is not null
                    ? commit => CommitRestoreValue(restoreCommit, commit)
                    : source is { } controller
                    ? commit => CommitSupervisorGrantMint(controller.Principal, controller.Capability, commit)
                    : null);
            if (!minted.IsSuccess) return Rollback(record, minted.Error, minted.Message!);
            BeforeComponentCapabilityTrackingQualificationHook?.Invoke();
            KernelResult tracked;
            lock (_platformMemoryUseGate)
            lock (_componentAdmissionGate)
            {
                    KernelResult<SingProcess> currentProcess = Processes.Resolve(processHandle);
                if (!currentProcess.IsSuccess)
                    tracked = KernelResult.Fail(currentProcess.Error, currentProcess.Message!);
                else if (currentProcess.Value!.State != ProcessState.Created || _processTeardowns.ContainsKey(processHandle) ||
                    !_components.TryGetValue(manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                    record.State != ComponentLifecycleState.Created)
                    tracked = KernelResult.Fail(KernelError.InvalidTransition, "Component changed before minted capability tracking.");
                else
                {
                    lock (record.SnapshotGate) record.Capabilities.Add(minted.Value!.CapabilityId);
                    record.Admission = MarkRequirementGranted(
                record.Admission,
                ManifestRequirementKind.LocalCapability,
                $"{grant.Requirement.ResourceKind}:{grant.Requirement.ResourceId}:{grant.Requirement.Rights}",
                "Exact live capability was materialized by the kernel; its identity is intentionally absent from admission evidence.");
                    tracked = KernelResult.Ok();
                }
            }
            if (!tracked.IsSuccess) return Rollback(record, tracked.Error, tracked.Message!);
        }

        BeforeComponentProcessAdmissionQualificationHook?.Invoke();
        KernelResult admitted = AdmitProcessCore(processHandle, source, sourceCommit: restoreCommit);
        if (!admitted.IsSuccess) return Rollback(record, admitted.Error, admitted.Message!);

        if (RequiresPlatformAuthorityDomain(manifest.ResourceRequirements))
        {
            BeforeComponentPlatformBindingQualificationHook?.Invoke();
            KernelResult<PlatformDomainBinding> binding = BindPlatformDomainCore(processHandle, executionAttached: false, source, sourceCommit: restoreCommit);
            if (!binding.IsSuccess) return Rollback(record, binding.Error, binding.Message!);
            BeforeComponentBindingTrackingQualificationHook?.Invoke();
            KernelResult tracked = CommitComponentAdmissionTracking(record, () =>
            {
                KernelResult exact = PlatformAuthority.ValidateDomain(binding.Value!, PlatformIdentity(created.Value!));
                if (!exact.IsSuccess) return exact;
                record.PlatformBinding = binding.Value!;
                record.Admission = MarkRequirementGranted(
                record.Admission,
                ManifestRequirementKind.PlatformAuthorityDomain,
                "platform-authority-domain",
                "Exact provider-neutral authority binding was materialized; provider-private identity is omitted.");
                return KernelResult.Ok();
            });
            if (!tracked.IsSuccess) return Rollback(record, tracked.Error, tracked.Message!);
        }

        if (plan.DriverResources is { } resources)
        {
            KernelResult materialized = MaterializeDriverResources(record, resources, source, restoreCommit);
            if (!materialized.IsSuccess) return Rollback(record, materialized.Error, materialized.Message!);
        }

        foreach (ComponentProvidedServiceRegistration registration in plan.ProvidedServices)
        {
            BeforeComponentServiceRegistrationQualificationHook?.Invoke();
            KernelResult<ServiceEndpointDescriptor> service = RegisterServiceCore(processHandle, registration.Manifest.ServiceName, registration.Manifest.Contract,
                registration.Protocol, registration.ResponseProtocol, registration.SessionRequirements, source, sourceCommit: restoreCommit);
            if (!service.IsSuccess) return Rollback(record, service.Error, service.Message!);
            BeforeComponentServiceTrackingQualificationHook?.Invoke();
            KernelResult tracked = CommitComponentAdmissionTracking(record, () =>
            {
                KernelResult<ServiceRegistry.Record> exact = Services.Resolve(service.Value!);
                if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                lock (record.SnapshotGate) record.Services.Add(service.Value!);
                return KernelResult.Ok();
            });
            if (!tracked.IsSuccess) return Rollback(record, tracked.Error, tracked.Message!);
        }

        foreach (ServiceDependencyRequirementV1 dependency in manifest.Dependencies)
        {
            ServiceContractIdentity required = dependency.Contract;
            KernelResult<ServiceEndpointDescriptor[]> candidates = ResolveByContract(required);
            if (!candidates.IsSuccess || candidates.Value!.Length != 1)
            {
                if (dependency.Kind == ServiceDependencyKind.Optional)
                {
                    BeforeComponentOptionalEvidenceQualificationHook?.Invoke();
                    KernelResult evidence = CommitComponentAdmissionTracking(record, () =>
                    {
                        record.Admission = DegradeOptionalDependency(record.Admission, dependency, "Optional dependency did not resolve exactly once during binding.");
                        return KernelResult.Ok();
                    });
                    if (!evidence.IsSuccess) return Rollback(record, evidence.Error, evidence.Message!);
                    continue;
                }
                return Rollback(record, KernelError.ServiceNotFound, $"Required contract '{required.Name}/{required.Version}' did not resolve exactly once.");
            }
            CapabilityId[] admissionCapabilities;
            lock (_componentAdmissionGate) admissionCapabilities = record.Capabilities.ToArray();
            BeforeComponentDependencySessionQualificationHook?.Invoke();
            KernelResult<EndpointSessionHandle> session = OpenSessionCore(processHandle, candidates.Value[0], admissionCapabilities, 8, null, source, sourceCommit: restoreCommit);
            if (!session.IsSuccess)
            {
                if (restoreCommit is not null)
                {
                    KernelResult currentSource = restoreCommit(KernelResult.Ok);
                    if (!currentSource.IsSuccess) return Rollback(record, currentSource.Error, currentSource.Message!);
                }
                if (source is not null && session.Error == KernelError.SupervisorDenied)
                    return Rollback(record, session.Error, session.Message!);
                if (dependency.Kind == ServiceDependencyKind.Optional)
                {
                    BeforeComponentOptionalEvidenceQualificationHook?.Invoke();
                    KernelResult evidence = CommitComponentAdmissionTracking(record, () =>
                    {
                        record.Admission = DegradeOptionalDependency(record.Admission, dependency, $"Optional dependency binding was unavailable: {session.Error}.");
                        return KernelResult.Ok();
                    });
                    if (!evidence.IsSuccess) return Rollback(record, evidence.Error, evidence.Message!);
                    continue;
                }
                return Rollback(record, session.Error, session.Message!);
            }
            BeforeComponentSessionTrackingQualificationHook?.Invoke();
            KernelResult tracked = CommitComponentAdmissionTracking(record, () =>
            {
                KernelResult<EndpointSessionRegistry.Record> exact = EndpointSessions.Resolve(session.Value, processHandle);
                if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                lock (record.SnapshotGate)
                {
                    record.Sessions.Add(session.Value);
                    record.DependencySessions[required] = session.Value;
                }
                return KernelResult.Ok();
            });
            if (!tracked.IsSuccess) return Rollback(record, tracked.Error, tracked.Message!);
        }

        CapabilityRequirementV1[] requirements = manifest.Process.RequiredCapabilities.ToArray();
        KernelResult CommitStartupSource(Func<KernelResult> commit) => restoreCommit is null
            ? CommitControllerExecution(source, commit) : restoreCommit(commit);
        BeforeComponentStartingPublicationQualificationHook?.Invoke();
        KernelResult starting;
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
                KernelResult<SingProcess> process = Processes.Resolve(processHandle);
            if (!process.IsSuccess)
                starting = KernelResult.Fail(process.Error, process.Message!);
            else if (process.Value!.State != ProcessState.Admitted || _processTeardowns.ContainsKey(processHandle))
                starting = KernelResult.Fail(KernelError.InvalidTransition, "Component process cannot enter startup.");
            else if (!_components.TryGetValue(manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                record.State != ComponentLifecycleState.Created)
                starting = KernelResult.Fail(KernelError.StaleGeneration, "Exact component changed before startup.");
            else
                starting = CommitStartupSource(() => CapabilityAuthority.CommitProcessAdmission(process.Value.DomainId, processHandle.Generation, requirements, () =>
                {
                    record.State = ComponentLifecycleState.Starting;
                    return KernelResult.Ok();
                }));
        }
        if (!starting.IsSuccess) return Rollback(record, starting.Error, starting.Message!);
        BeforeComponentExecutionStartQualificationHook?.Invoke();
        KernelResult started = StartProcessCore(processHandle, source, sourceCommit: restoreCommit);
        if (!started.IsSuccess) return Rollback(record, started.Error, started.Message!);
        BeforeComponentRunningPublicationQualificationHook?.Invoke();
        KernelResult published;
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> process = Processes.Resolve(processHandle);
            if (!process.IsSuccess)
                published = KernelResult.Fail(process.Error, process.Message!);
            else if (process.Value!.State != ProcessState.Running || _processTeardowns.ContainsKey(processHandle))
                published = KernelResult.Fail(KernelError.InvalidTransition, "Component process is no longer running at publication.");
            else
            {
                lock (_componentAdmissionGate)
                {
                    if (!_components.TryGetValue(manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                        record.State != ComponentLifecycleState.Starting)
                        published = KernelResult.Fail(KernelError.StaleGeneration, "Exact component changed before running publication.");
                    else
                    {
                        published = CommitStartupSource(() => CapabilityAuthority.CommitProcessAdmission(process.Value.DomainId, processHandle.Generation, requirements, () =>
                        {
                            record.State = ComponentLifecycleState.Running;
                            return KernelResult.Ok();
                        }));
                    }
                }
            }
        }
        if (!published.IsSuccess) return Rollback(record, published.Error, published.Message!);
        return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
    }

    private static KernelResult<T> CommitRestoreValue<T>(Func<Func<KernelResult>, KernelResult> restoreCommit,
        Func<KernelResult<T>> commit)
    {
        KernelResult<T> result = KernelResult<T>.Fail(KernelError.InvalidTransition, "Restore owner commit was not invoked.");
        KernelResult admitted = restoreCommit(() =>
        {
            result = commit();
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        });
        return admitted.IsSuccess ? result : KernelResult<T>.Fail(admitted.Error, admitted.Message!);
    }

    private KernelResult<ComponentLifecycleSnapshot> RefuseUncreatedComponent(ComponentAdmissionRecord record, KernelError error, string message)
    {
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
            if (_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) && ReferenceEquals(current, record) &&
                record.State == ComponentLifecycleState.Admitting)
            {
                record.Failure = error;
                record.State = ComponentLifecycleState.Faulted;
                // Not-invoked describes our creation, not another incarnation writer.
                // ValidateCreation is the actual registry's fresh no-foreign-incarnation check.
                if (Processes.ValidateCreation(record.Manifest.Process).IsSuccess)
                {
                        KernelResult retired = Budgets.RetireProcessHierarchy(record.Process, record.ProcessBudget, record.ServiceBudget);
                    if (retired.IsSuccess) _components.TryRemove(record.Manifest.Identity, out _);
                }
            }
        }
        return KernelResult<ComponentLifecycleSnapshot>.Fail(error, message);
    }

    public KernelResult<ComponentLifecycleSnapshot> QueryComponent(ComponentIdentity identity) =>
        _components.TryGetValue(identity, out ComponentAdmissionRecord? record)
            ? KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot)
            : KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");

    public KernelResult<DeviceResourceSetSnapshot> QueryComponentDeviceResources(ComponentIdentity identity)
    {
        lock (_componentAdmissionGate)
        {
        if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? record) || record.DeviceResources is not { } resources)
            return KernelResult<DeviceResourceSetSnapshot>.Fail(KernelError.ComponentNotFound, "Component does not own a materialized driver device resource set.");
        return KernelResult<DeviceResourceSetSnapshot>.Ok(resources.Snapshot);
        }
    }

    public KernelResult<PlatformOwnedRegionSliceMapping> MapComponentDriverOwnedRegion(
        ComponentIdentity identity,
        CapabilityId regionCapability,
        RegionHandle region,
        long offset,
        long length,
        PlatformMemoryAccess access)
    {
        KernelResult<(ProcessHandle Process, PlatformDomainBinding Binding, DeviceResourceSet Resources)> authority = ResolveDriverComponentAuthority(identity);
        if (!authority.IsSuccess)
            return KernelResult<PlatformOwnedRegionSliceMapping>.Fail(authority.Error, authority.Message!);
        return MapPlatformOwnedRegionSlice(
            authority.Value.Process,
            authority.Value.Binding,
            regionCapability,
            region,
            offset,
            length,
            access);
    }

    public KernelResult<PlatformDmaGrant> BindComponentDriverDma(
        ComponentIdentity identity,
        PlatformOwnedRegionSliceMapping mapping,
        long offset,
        long length,
        PlatformDmaDirection direction)
    {
        KernelResult<(ProcessHandle Process, PlatformDomainBinding Binding, DeviceResourceSet Resources)> authority = ResolveDriverComponentAuthority(identity);
        if (!authority.IsSuccess)
            return KernelResult<PlatformDmaGrant>.Fail(authority.Error, authority.Message!);
        DriverDmaPolicy? policy = authority.Value.Resources.DmaPolicy;
        if (policy is null)
            return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformDenied, "Driver component does not declare DMA authority.");
        if (!policy.Allows(direction, length))
            return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformDenied, "DMA request exceeds the component's exact direction or transfer-size policy.");

        KernelResult<PlatformDmaGrant> grant = BindPlatformDma(
            authority.Value.Process,
            authority.Value.Resources.DeviceLease,
            mapping,
            offset,
            length,
            direction);
        if (!grant.IsSuccess) return grant;
        BeforeComponentDmaTrackingQualificationHook?.Invoke();
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
                KernelResult<SingProcess> process = Processes.Resolve(authority.Value.Process);
            if (!process.IsSuccess) return KernelResult<PlatformDmaGrant>.Fail(process.Error, process.Message!);
            if (process.Value!.State != ProcessState.Running || _processTeardowns.ContainsKey(authority.Value.Process) ||
                !_components.TryGetValue(identity, out ComponentAdmissionRecord? current) || current.Process != authority.Value.Process ||
                current.State != ComponentLifecycleState.Running || !ReferenceEquals(current.DeviceResources, authority.Value.Resources))
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.InvalidTransition, "Driver changed before DMA receipt tracking.");
                KernelResult usable = ValidateDmaGrantRegionUsability(grant.Value!, PlatformIdentity(process.Value));
            if (!usable.IsSuccess) return KernelResult<PlatformDmaGrant>.Fail(usable.Error, usable.Message!);
            authority.Value.Resources.AddDma(grant.Value!);
        }
        return grant;
    }

    public KernelResult RevokeComponentDriverDma(
        ComponentIdentity identity,
        PlatformDmaGrant grant)
    {
        KernelResult<(ProcessHandle Process, PlatformDomainBinding Binding, DeviceResourceSet Resources)> authority = ResolveDriverComponentAuthority(identity);
        if (!authority.IsSuccess) return KernelResult.Fail(authority.Error, authority.Message!);
        bool tracked;
        lock (_componentAdmissionGate)
            tracked = authority.Value.Resources.ActiveDmaGrants.Any(candidate => candidate.GrantId == grant.GrantId && candidate.Generation == grant.Generation);
        if (!tracked)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "DMA grant is not owned by this driver component resource set.");
        return RevokePlatformDma(authority.Value.Process, grant);
    }

    public KernelResult<ComponentLifecycleSnapshot> FaultComponent(ComponentIdentity identity) =>
        BeginComponentTeardown(identity, faulted: true);

    internal KernelResult<ComponentLifecycleSnapshot> FaultComponent(ComponentIdentity identity, ProcessHandle expectedProcess) =>
        BeginComponentTeardown(identity, faulted: true, expectedProcess);

    internal KernelResult<ComponentLifecycleSnapshot> DrainComponent(ComponentIdentity identity) =>
        BeginComponentTeardown(identity, faulted: false);

    internal KernelResult<ComponentLifecycleSnapshot> DrainComponent(ComponentIdentity identity, ProcessHandle expectedProcess) =>
        BeginComponentTeardown(identity, faulted: false, expectedProcess);

    private KernelResult<ComponentLifecycleSnapshot> BeginComponentTeardown(
        ComponentIdentity identity, bool faulted, ProcessHandle? expectedProcess = null)
    {
        if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? record))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
        if (expectedProcess is { } expected && record.Process != expected)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.StaleGeneration,
                "Component teardown request targets a different process generation.");
        BeforeComponentTeardownIntentQualificationHook?.Invoke();
        bool begin;
        lock (record.SnapshotGate)
        {
            if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record))
                return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.StaleGeneration, "Exact component changed before teardown intent.");
            if (record.State == ComponentLifecycleState.Reclaimable)
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
            if (record.State is not ComponentLifecycleState.Running and not ComponentLifecycleState.Draining and not ComponentLifecycleState.Faulted)
                return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.InvalidTransition, $"Component state {record.State} cannot enter crash teardown.");
            begin = record.State == ComponentLifecycleState.Running;
            if (begin) record.State = ComponentLifecycleState.Draining;
        }
        if (begin)
        {
            AfterComponentTeardownIntentQualificationHook?.Invoke();
            KernelResult teardownResult = faulted ? FaultProcess(record.Process) : TerminateProcess(record.Process);
            if (teardownResult.IsSuccess)
            {
                FinalizeComponentReclaim(record);
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
            }

            // A concurrent owner teardown may already have retired this exact process.
            // Only its retained terminal receipt establishes closure.
            if (Processes.ResolveTerminalState(record.Process).IsSuccess)
            {
                FinalizeComponentReclaim(record);
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
            }

            KernelResult<ProcessTeardownSnapshot> teardown = QueryProcessTeardown(record.Process);
            if (teardown.IsSuccess)
                ApplyComponentTeardown(record, teardown.Value!);

            if (teardownResult.Error == KernelError.PlatformBindingDraining)
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);

            BeforeComponentFailurePublicationQualificationHook?.Invoke();
            lock (_componentAdmissionGate)
            {
                if (_components.TryGetValue(identity, out ComponentAdmissionRecord? current) && ReferenceEquals(current, record) &&
                    record.State != ComponentLifecycleState.Reclaimable)
                {
                    record.State = ComponentLifecycleState.Faulted;
                    record.Failure = teardownResult.Error;
                }
            }
            return KernelResult<ComponentLifecycleSnapshot>.Fail(teardownResult.Error, teardownResult.Message!);
        }

        return ObserveComponentTeardownRecord(record);
    }

    public KernelResult<ComponentLifecycleSnapshot> ObserveComponentTeardown(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? record))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
        return ObserveComponentTeardownRecord(record);
    }

    internal KernelResult<ComponentLifecycleSnapshot> ObserveFailedComponentAdmission(ComponentAdmissionPlan original)
    {
        if (!_components.TryGetValue(original.Manifest.Identity, out ComponentAdmissionRecord? record) || !ReferenceEquals(record.Plan, original))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.StaleGeneration, "Original failed admission owner is unavailable.");
        return ObserveComponentTeardownRecord(record);
    }

    private KernelResult<ComponentLifecycleSnapshot> ObserveComponentTeardownRecord(ComponentAdmissionRecord record)
    {
        if (record.State == ComponentLifecycleState.Reclaimable)
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        if (record.State is not ComponentLifecycleState.Draining and not ComponentLifecycleState.Faulted)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.InvalidTransition, "Component teardown has not started.");
        if (Processes.ResolveTerminalState(record.Process).IsSuccess)
        {
            FinalizeComponentReclaim(record);
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        }

        // Retry through the existing lifecycle consumer with its authoritative
        // terminal target. Mere observation cannot rerun composed preparation.
        BeforeComponentTeardownRecordQueryQualificationHook?.Invoke();
        KernelResult<ProcessTeardownSnapshot> tracked = QueryProcessTeardown(record.Process);
        if (!tracked.IsSuccess)
        {
            if (Processes.ResolveTerminalState(record.Process).IsSuccess)
            {
                FinalizeComponentReclaim(record);
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
            }
            return KernelResult<ComponentLifecycleSnapshot>.Fail(tracked.Error, tracked.Message!);
        }
        KernelResult advanced = BeginOrAdvanceProcessTeardown(record.Process, tracked.Value!.TargetTerminalState);
        if (advanced.IsSuccess)
        {
            FinalizeComponentReclaim(record);
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        }

        KernelResult<ProcessTeardownSnapshot> teardown = QueryProcessTeardown(record.Process);
        if (!teardown.IsSuccess)
        {
            if (Processes.ResolveTerminalState(record.Process).IsSuccess)
            {
                FinalizeComponentReclaim(record);
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
            }
            return KernelResult<ComponentLifecycleSnapshot>.Fail(teardown.Error, teardown.Message!);
        }
        BeforeComponentTeardownPublicationQualificationHook?.Invoke();
        ApplyComponentTeardown(record, teardown.Value!);
        return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
    }

    internal KernelResult CommitSupervisorReplacementRetirement(ComponentIdentity identity,
        ProcessHandle exactProcess, ProcessHandle principal, CapabilityId control,
        Func<KernelResult> validateSource, Action publishIntent)
    {
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
                KernelResult<SingProcess> process = Processes.Resolve(principal);
            if (!process.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, process.Message!);
            return CapabilityAuthority.CommitSupervisorRecordPublication(control,
                process.Value!.DomainId, principal.Generation, () =>
                {
                    // These callbacks are trusted local supervisor record operations.
                    KernelResult source = validateSource();
                    if (!source.IsSuccess) return source;
                    KernelResult retired = RetireReclaimableComponent(identity, exactProcess);
                    if (!retired.IsSuccess) return retired;
                    publishIntent();
                    return KernelResult.Ok();
                });
        }
    }

    internal KernelResult RetireReclaimableComponent(ComponentIdentity identity, ProcessHandle exactProcess)
    {
        lock (_componentAdmissionGate)
        {
            if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? record))
                return KernelResult.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
            if (record.Process != exactProcess)
                return KernelResult.Fail(KernelError.StaleGeneration, "Component process generation is stale.");
            if (record.State != ComponentLifecycleState.Reclaimable)
                return KernelResult.Fail(KernelError.ReplacementBlocked, "Component is not exactly reclaimable.");
            KernelResult retiredBudget = Budgets.RetireProcessHierarchy(record.Process, record.ProcessBudget, record.ServiceBudget);
            if (!retiredBudget.IsSuccess) return retiredBudget;
            _components.TryRemove(identity, out _);
            return KernelResult.Ok();
            }
    }

    internal KernelResult<EndpointSessionHandle> RebindComponentDependency(
        ComponentIdentity identity,
        ProcessHandle expectedProcess,
        ServiceEndpointDescriptor exactProvider) =>
        RebindComponentDependencyCore(identity, expectedProcess, exactProvider, source: null);

    internal KernelResult<EndpointSessionHandle> RebindComponentDependency(ComponentIdentity identity,
        ProcessHandle expectedProcess, ServiceEndpointDescriptor exactProvider, ProcessHandle principal, CapabilityId source) =>
        RebindComponentDependencyCore(identity, expectedProcess, exactProvider, (principal, source));

    private KernelResult<EndpointSessionHandle> RebindComponentDependencyCore(ComponentIdentity identity,
        ProcessHandle expectedProcess, ServiceEndpointDescriptor exactProvider,
        (ProcessHandle Principal, CapabilityId Capability)? source)
    {
        if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? record) || record.State != ComponentLifecycleState.Running)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.ComponentNotFound, "Running dependent component was not found.");
        if (record.Process != expectedProcess)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.StaleGeneration, "Dependency rebind target process generation is stale.");
        if (source is { } control)
        {
            KernelResult admitted = ValidateSupervisorSessionSource(control.Principal, control.Capability);
            if (!admitted.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(admitted.Error, admitted.Message!);
        }
        ServiceDependencyRequirementV1 dependency = record.Manifest.Dependencies.SingleOrDefault(candidate => candidate.Contract == exactProvider.Contract);
        if (dependency.Contract == default)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidManifest, "Dependency is not declared by the component manifest.");
        CapabilityId[] capabilities;
        lock (_componentAdmissionGate) capabilities = record.Capabilities.ToArray();
        KernelResult preflight = PreflightDependencySession(record.Process, exactProvider, capabilities);
        if (!preflight.IsSuccess)
            return KernelResult<EndpointSessionHandle>.Fail(preflight.Error, preflight.Message!);

        bool hadPrevious;
        EndpointSessionHandle previous;
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
            hadPrevious = record.DependencySessions.TryGetValue(exactProvider.Contract, out previous);
        if (hadPrevious)
        {
            KernelResult closed;
            try { closed = CloseSession(record.Process, previous, allowAlreadyClosed: true); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult<EndpointSessionHandle>.Fail(KernelError.PlatformFaulted, "Dependency session closure outcome is unknown; accounting is retained.");
            }
            if (!closed.IsSuccess)
                return KernelResult<EndpointSessionHandle>.Fail(closed.Error, closed.Message!);
        }

        // Observe after preflight/old closure, outside owner commit locks.
        BeforeComponentDependencySessionQualificationHook?.Invoke();

        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
            if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                current.Process != expectedProcess || current.State != ComponentLifecycleState.Running)
                return KernelResult<EndpointSessionHandle>.Fail(KernelError.StaleGeneration, "Dependency target changed before routing commit.");
            if (hadPrevious)
            {
                if (!record.DependencySessions.TryGetValue(exactProvider.Contract, out EndpointSessionHandle tracked) || tracked != previous)
                    return KernelResult<EndpointSessionHandle>.Fail(KernelError.StaleGeneration, "Dependency routing changed during old session closure.");
                lock (record.SnapshotGate)
                {
                    record.DependencySessions.Remove(exactProvider.Contract);
                    record.Sessions.Remove(previous);
                }
            }
            else if (record.DependencySessions.ContainsKey(exactProvider.Contract))
                return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Another admission already bound this dependency.");

                // Lifetime-free local creation has no provider/clock callback. Keep
                // process teardown outside until the actual session is accounted.
                KernelResult<EndpointSessionHandle> session = OpenSessionCore(record.Process, exactProvider, record.Capabilities.ToArray(), 8, lifetime: null, source);
            if (!session.IsSuccess)
                return KernelResult<EndpointSessionHandle>.Fail(session.Error, session.Message!);
            lock (record.SnapshotGate)
            {
                record.DependencySessions.Add(exactProvider.Contract, session.Value);
                record.Sessions.Add(session.Value);
            }
            // A component drain request can precede its process-owner transition.
            // Retain the admitted session but do not return a running receipt.
            return record.State == ComponentLifecycleState.Running
                ? KernelResult<EndpointSessionHandle>.Ok(session.Value)
                : KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Component drain requested during local session admission.");
        }
    }

    internal KernelResult<(ProcessHandle Process, PlatformDomainBinding Binding, CapabilityId ComputeCapability)> ResolveComputeComponentAuthority(ComponentIdentity identity)
    {
        ComponentAdmissionRecord record;
        PlatformDomainBinding binding;
        CapabilityId[] capabilities;
        lock (_componentAdmissionGate)
        {
        if (!_components.TryGetValue(identity, out record!) || record.State != ComponentLifecycleState.Running || record.PlatformBinding is not { } captured)
            return KernelResult<(ProcessHandle, PlatformDomainBinding, CapabilityId)>.Fail(KernelError.ComponentNotFound, "Running compute component with platform authority was not found.");
        binding = captured;
        capabilities = record.Capabilities.ToArray();
        }
        AfterComputeComponentAuthoritySnapshotQualificationHook?.Invoke();
        foreach (CapabilityId capability in capabilities)
        {
            KernelResult<CapabilityDescriptorV1> validation = ValidateCapability(record.Process, capability, CapabilityRights.Execute);
            if (validation.IsSuccess && validation.Value!.ResourceKind == ResourceKind.Compute && validation.Value.ResourceId == CapabilityResourceIds.Dsc1Copy)
                return KernelResult<(ProcessHandle, PlatformDomainBinding, CapabilityId)>.Ok((record.Process, binding, capability));
        }
        return KernelResult<(ProcessHandle, PlatformDomainBinding, CapabilityId)>.Fail(KernelError.MissingCapability, "Compute component lacks exact DSC1 Copy Execute authority.");
    }

    internal KernelResult<(ProcessHandle Process, PlatformDomainBinding Binding, DeviceResourceSet Resources)> ResolveDriverComponentAuthority(ComponentIdentity identity)
    {
        lock (_componentAdmissionGate)
        {
        if (!_components.TryGetValue(identity, out ComponentAdmissionRecord? record) ||
            record.State != ComponentLifecycleState.Running ||
            record.PlatformBinding is not { } binding ||
            record.DeviceResources is not { } resources)
        {
            return KernelResult<(ProcessHandle, PlatformDomainBinding, DeviceResourceSet)>.Fail(
                KernelError.ComponentNotFound,
                "Running driver component with a materialized device resource set was not found.");
        }
        return KernelResult<(ProcessHandle, PlatformDomainBinding, DeviceResourceSet)>.Ok((record.Process, binding, resources));
        }
    }

    internal void UntrackComponentDriverDma(PlatformDmaGrant grant)
    {
        lock (_componentAdmissionGate)
        {
        foreach (ComponentAdmissionRecord record in _components.Values)
            record.DeviceResources?.RemoveDma(grant);
        }
    }

    private KernelResult<ComponentLifecycleSnapshot> Rollback(ComponentAdmissionRecord record, KernelError error, string message)
    {
        CapabilityId[] capabilities;
        lock (_componentAdmissionGate)
        {
            if (!_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record))
                return KernelResult<ComponentLifecycleSnapshot>.Fail(error, message);
            if (record.State != ComponentLifecycleState.Reclaimable)
                record.State = ComponentLifecycleState.Draining;
            capabilities = record.Capabilities.ToArray();
        }
        BeforeComponentRollbackSourceStopQualificationHook?.Invoke();
        // Stop every admitted local source before cleanup can call a provider or
        // return with an ambiguous effect. This owner transition proves no closure;
        // the existing cascade and resource records remain responsible for cleanup.
        foreach (CapabilityId capability in capabilities)
            _ = CapabilityAuthority.Revoke(capability);
        Services.UnregisterForProvider(record.Process);
        // The existing process teardown owner stops admissions, tracks every provider
        // resource and supplies the exact closure snapshot consumed by Observe.
        // A failed cleanup is no longer stranded before teardown-record creation.
        // The process owner retains an exact terminal receipt after removing the
        // transient teardown record. Consume it rather than dispatching cleanup again.
        KernelResult<ProcessState> terminal = Processes.ResolveTerminalState(record.Process);
        BeforeComponentRollbackCleanupQualificationHook?.Invoke();
        KernelResult terminated = terminal.IsSuccess ? KernelResult.Ok() : TerminateProcess(record.Process);
        bool missingTeardown = false;
        if (terminated.IsSuccess)
            FinalizeComponentReclaim(record);
        else
        {
            KernelResult<ProcessTeardownSnapshot> teardown = QueryProcessTeardown(record.Process);
            if (teardown.IsSuccess)
                ApplyComponentTeardown(record, teardown.Value!);
            else
                missingTeardown = true;
        }

        KernelError failure = !terminated.IsSuccess && terminated.Error != KernelError.PlatformBindingDraining
            ? terminated.Error : error;
        BeforeComponentRollbackPublicationQualificationHook?.Invoke();
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
            if (_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) && ReferenceEquals(current, record))
            {
                if (Processes.ResolveTerminalState(record.Process).IsSuccess)
                    FinalizeComponentReclaim(record);
                if (missingTeardown && record.State != ComponentLifecycleState.Reclaimable)
                    record.State = ComponentLifecycleState.Faulted;
                // V1 retains the original failure as metadata even after exact reclaim.
                record.Failure = failure;
            }
        }
        return KernelResult<ComponentLifecycleSnapshot>.Fail(failure,
            failure == error ? message : terminated.Message!);
    }

    private KernelResult<ComponentLifecycleSnapshot> FailAdmission(ComponentAdmissionRecord record, KernelError error, string message)
    {
        // This component did not create its process. A colliding live generation may
        // own resources charged to the already attached hierarchy; refusal is not closure.
        lock (_componentAdmissionGate)
        {
            if (_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) && ReferenceEquals(current, record) &&
                record.State != ComponentLifecycleState.Reclaimable)
            {
                record.Failure = error;
                record.State = ComponentLifecycleState.Faulted;
            }
        }
        return KernelResult<ComponentLifecycleSnapshot>.Fail(error, message);
    }

    private KernelResult CommitComponentAdmissionTracking(ComponentAdmissionRecord record, Func<KernelResult> commit)
    {
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
                KernelResult<SingProcess> process = Processes.Resolve(record.Process);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            if (process.Value!.State != ProcessState.Admitted || _processTeardowns.ContainsKey(record.Process) ||
                !_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                record.State != ComponentLifecycleState.Created)
                return KernelResult.Fail(KernelError.InvalidTransition, "Component changed before local admission tracking.");
            return commit();
        }
    }

    private KernelResult MaterializeDriverResources(ComponentAdmissionRecord record, ComponentDriverResourcePlan plan,
        (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? restoreCommit = null)
    {
        if (record.PlatformBinding is not { } binding)
            return KernelResult.Fail(KernelError.InvalidManifest, "Driver resources require an explicit component platform authority domain.");

        CapabilityRights deviceRights = ToCapabilityRights(plan.DeviceRights);
        KernelResult<CapabilityId> deviceCapability = FindComponentCapability(record, ResourceKind.Device, plan.DeviceResourceId, deviceRights);
        if (!deviceCapability.IsSuccess) return KernelResult.Fail(deviceCapability.Error, deviceCapability.Message!);
        BeforeComponentDriverDeviceQualificationHook?.Invoke();
        KernelResult<PlatformDeviceLease> device = BindPlatformDeviceCore(record.Process, binding, deviceCapability.Value, plan.DeviceRights, source, sourceCommit: restoreCommit);
        if (!device.IsSuccess) return KernelResult.Fail(device.Error, device.Message!);

        DeviceResourceSet resources = new DeviceResourceSet(record.Process, plan.DeviceResourceId, deviceCapability.Value, device.Value!, plan.DmaPolicy);
        KernelResult tracked = CommitComponentDriverTracking(record, null, () => record.DeviceResources = resources, restoreCommit);
        if (!tracked.IsSuccess) return tracked;

        foreach (DriverMmioResourcePlan mmio in plan.Mmio)
        {
            CapabilityRights rights = CapabilityRights.Map | ToCapabilityRights(mmio.Access);
            KernelResult<CapabilityId> capability = FindComponentCapability(record, ResourceKind.MmioRegion, mmio.CapabilityResourceId, rights);
            if (!capability.IsSuccess) return KernelResult.Fail(capability.Error, capability.Message!);
            BeforeComponentDriverMmioQualificationHook?.Invoke();
            RegionHandle? backingRegion=null;
            KernelResult<PlatformMmioLease> lease;
            if(mmio.SoftwareRegisterBacking)
            {
                CapabilityResourceIds.TryParseMmioRegion(mmio.CapabilityResourceId,out MmioRegionResourceId description);
                List<BudgetTraceObservation> observations = new List<BudgetTraceObservation>();
                KernelResult<OwnedBuffer<byte>> buffer = default;
                KernelResult AllocateBacking()
                {
                    buffer = AllocateBufferCore<byte>(record.Process, checked((int)description.ByteLength), observations);
                    return buffer.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(buffer.Error, buffer.Message!);
                }
                KernelResult authorized;
                try
                {
                    authorized = CommitComponentAdmissionTracking(record, () => restoreCommit is null
                        ? CommitControllerMmio(source, AllocateBacking)
                        : restoreCommit(AllocateBacking));
                }
                finally { EmitBudgetObservations(observations); }
                if (!authorized.IsSuccess) return authorized;
                if(!buffer.IsSuccess)return KernelResult.Fail(buffer.Error,buffer.Message!);
                backingRegion=buffer.Value!.Handle;
                lease=BindPlatformMmioBackingCore(record.Process,resources.DeviceLease,capability.Value,mmio.Offset,mmio.Length,mmio.Access,buffer.Value,source,sourceCommit:restoreCommit);
            }
            else lease = BindPlatformMmioCore(record.Process, resources.DeviceLease, capability.Value, mmio.Offset, mmio.Length, mmio.Access, source, sourceCommit: restoreCommit);
            if (!lease.IsSuccess) return KernelResult.Fail(lease.Error, lease.Message!);
            tracked = CommitComponentDriverTracking(record, resources,
                () => resources.AddMmio(new MmioResourceGrant(mmio.CapabilityResourceId, capability.Value, lease.Value!,backingRegion)), restoreCommit);
            if (!tracked.IsSuccess) return tracked;
        }

        foreach (DriverIrqResourcePlan irq in plan.Irqs)
        {
            KernelResult<CapabilityId> capability = FindComponentCapability(record, ResourceKind.Irq, irq.CapabilityResourceId, CapabilityRights.Signal);
            if (!capability.IsSuccess) return KernelResult.Fail(capability.Error, capability.Message!);
            BeforeComponentDriverIrqEndpointQualificationHook?.Invoke();
            KernelResult<KernelEventEndpoint> endpoint = CreateComponentIrqEndpoint(record.Process, source, sourceCommit: restoreCommit);
            if (!endpoint.IsSuccess) return KernelResult.Fail(endpoint.Error, endpoint.Message!);
            BeforeComponentDriverIrqBindingQualificationHook?.Invoke(endpoint.Value!);
            KernelResult<PlatformIrqBinding> bindingResult = BindPlatformInterruptCore(record.Process, resources.DeviceLease, capability.Value, endpoint.Value!, source, sourceCommit: restoreCommit);
            if (!bindingResult.IsSuccess)
            {
                _ = CloseKernelEventEndpoint(record.Process, endpoint.Value!);
                return KernelResult.Fail(bindingResult.Error, bindingResult.Message!);
            }
            tracked = CommitComponentDriverTracking(record, resources,
                () => resources.AddIrq(new IrqResourceGrant(irq.CapabilityResourceId, capability.Value, bindingResult.Value!, endpoint.Value!)), restoreCommit);
            if (!tracked.IsSuccess) return tracked;
        }

        return KernelResult.Ok();
    }

    private KernelResult CommitComponentDriverTracking(ComponentAdmissionRecord record, DeviceResourceSet? expected, Action publish,
        Func<Func<KernelResult>, KernelResult>? restoreCommit = null)
    {
        BeforeComponentDriverTrackingQualificationHook?.Invoke();
        lock (_platformMemoryUseGate)
        lock (_componentAdmissionGate)
        {
                KernelResult<SingProcess> process = Processes.Resolve(record.Process);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            if (process.Value!.State != ProcessState.Admitted || _processTeardowns.ContainsKey(record.Process) ||
                !_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                record.State != ComponentLifecycleState.Created || !ReferenceEquals(record.DeviceResources, expected))
                return KernelResult.Fail(KernelError.InvalidTransition, "Component changed before driver receipt tracking.");
            // Only local metadata writes. The kernel/provider owners already retain
            // the exact receipts for cleanup even when this publication is refused.
            KernelResult Publish()
            {
                publish();
                return KernelResult.Ok();
            }
            return restoreCommit is null ? Publish() : restoreCommit(Publish);
        }
    }

    private KernelResult<CapabilityId> FindComponentCapability(
        ComponentAdmissionRecord record,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        CapabilityId[] capabilities;
        lock (_componentAdmissionGate) capabilities = record.Capabilities.ToArray();
        foreach (CapabilityId capabilityId in capabilities)
        {
            KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(record.Process, capabilityId, rights);
            if (capability.IsSuccess && capability.Value!.ResourceKind == kind &&
                string.Equals(capability.Value.ResourceId, resourceId, StringComparison.Ordinal))
                return KernelResult<CapabilityId>.Ok(capabilityId);
        }
        return KernelResult<CapabilityId>.Fail(KernelError.MissingCapability, $"Component lacks exact {kind}/{resourceId} ({rights}) authority.");
    }

    private static bool RegistrationsMatchManifest(ServiceManifestV1 manifest, IReadOnlyList<ComponentProvidedServiceRegistration> registrations) =>
        manifest.ProvidedContracts.Select(x => (x.ServiceName, x.Contract)).OrderBy(x => x.ServiceName, StringComparer.Ordinal)
            .SequenceEqual(registrations.Select(x => (x.Manifest.ServiceName, x.Manifest.Contract)).OrderBy(x => x.ServiceName, StringComparer.Ordinal));

    private KernelResult ValidatePlatformRequirements(IReadOnlyList<PlatformRequirementV1> requirements)
    {
        if (requirements.Count == 0) return KernelResult.Ok();
        PlatformFeatureManifest manifest = QueryPlatformFeatures();
        foreach (PlatformRequirementV1 requirement in requirements)
        {
            if (!Enum.IsDefined(requirement.Family))
            {
                if (requirement.Criticality == ManifestRequirementCriticality.Optional) continue;
                return KernelResult.Fail(KernelError.PlatformUnsupported, $"Unknown mandatory platform feature {(int)requirement.Family} is unsupported.");
            }
            PlatformFeatureFamily family = (PlatformFeatureFamily)requirement.Family;
            PlatformFeatureAvailability availability = (PlatformFeatureAvailability)requirement.Availability;
            PlatformFeatureDescriptor feature = manifest.Resolve(family);
            if ((feature.ContractVersion < requirement.MinimumContractVersion || feature.Availability != availability) &&
                requirement.Criticality == ManifestRequirementCriticality.Mandatory)
                return KernelResult.Fail(KernelError.PlatformUnsupported, $"Component requires exact platform feature {requirement.Family} v{requirement.MinimumContractVersion}+ as {requirement.Availability}; provider reports v{feature.ContractVersion} {feature.Availability}.");
        }
        return KernelResult.Ok();
    }

    private static KernelResult ValidateResourcePlan(
        IReadOnlyList<ComponentResourceRequirementV1> requirements,
        IReadOnlyList<ComponentCapabilityGrant> grants)
    {
        CapabilityRequirementV1[] expected = requirements
            .Where(static requirement => requirement.Kind == ComponentResourceRequirementKind.LocalCapability)
            .Select(static requirement => requirement.ToCapabilityRequirement())
            .ToArray();

        Dictionary<(ResourceKind Kind, string ResourceId), ComponentCapabilityGrant> grantsByResource = new Dictionary<(ResourceKind Kind, string ResourceId), ComponentCapabilityGrant>();
        foreach (ComponentCapabilityGrant grant in grants)
        {
            CapabilityRequirementV1 requirement = grant.Requirement;
            if (!Enum.IsDefined(requirement.ResourceKind) || string.IsNullOrWhiteSpace(requirement.ResourceId) || requirement.Rights == CapabilityRights.None)
                return KernelResult.Fail(KernelError.InvalidManifest, "Component capability grants must name a defined local resource and non-empty rights.");
            if (!grantsByResource.TryAdd((requirement.ResourceKind, requirement.ResourceId), grant))
                return KernelResult.Fail(KernelError.InvalidManifest, $"Component admission plan contains duplicate grants for {requirement.ResourceKind}/{requirement.ResourceId}.");
        }

        foreach (CapabilityRequirementV1 requirement in expected)
        {
            if (!grantsByResource.TryGetValue((requirement.ResourceKind, requirement.ResourceId), out ComponentCapabilityGrant? grant))
                return KernelResult.Fail(KernelError.MissingCapability, $"Component resource {requirement.ResourceKind}/{requirement.ResourceId} has no exact local capability grant.");
            if (grant.Requirement.Rights != requirement.Rights)
                return KernelResult.Fail(KernelError.InvalidManifest, $"Component grant rights for {requirement.ResourceKind}/{requirement.ResourceId} must exactly equal the manifest requirement.");
        }

        if (grantsByResource.Count != expected.Length)
        {
            HashSet<(ResourceKind ResourceKind, string ResourceId)> expectedKeys = expected.Select(static requirement => (requirement.ResourceKind, requirement.ResourceId)).ToHashSet();
            (ResourceKind Kind, string ResourceId) extra = grantsByResource.Keys.First(key => !expectedKeys.Contains(key));
            return KernelResult.Fail(KernelError.InvalidManifest, $"Component admission plan grants undeclared resource {extra.Kind}/{extra.ResourceId}.");
        }

        return KernelResult.Ok();
    }

    private static KernelResult ValidateDriverResourcePlan(ServiceManifestV1 manifest, ComponentDriverResourcePlan? plan)
    {
        CapabilityRequirementV1[] local = manifest.ResourceRequirements
            .Where(static requirement => requirement.Kind == ComponentResourceRequirementKind.LocalCapability)
            .Select(static requirement => requirement.ToCapabilityRequirement())
            .ToArray();
        CapabilityRequirementV1[] hardware = local.Where(static requirement => requirement.ResourceKind is ResourceKind.Device or ResourceKind.MmioRegion or ResourceKind.Irq or ResourceKind.Dma).ToArray();

        if (plan is null)
        {
            return hardware.Length == 0
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.InvalidManifest, "Driver hardware capabilities require an explicit component DeviceResourceSet plan; ambient device authority is forbidden.");
        }
        if (manifest.Process.ExecutionRole != ExecutionRole.Driver)
            return KernelResult.Fail(KernelError.InvalidManifest, "A component DeviceResourceSet may only be materialized for an ExecutionRole.Driver process.");
        if (!RequiresPlatformAuthorityDomain(manifest.ResourceRequirements))
            return KernelResult.Fail(KernelError.InvalidManifest, "Driver resources require an explicit PlatformAuthorityDomain component resource.");
        if (!HasPlatformRequirement(manifest, ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion) ||
            !HasPlatformRequirement(manifest, ComponentPlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion))
            return KernelResult.Fail(KernelError.InvalidManifest, "Driver resources require NeutralDomains and IoDomainBinding platform requirements.");

        Dictionary<(ResourceKind Kind, string ResourceId), CapabilityRights> expected = new Dictionary<(ResourceKind Kind, string ResourceId), CapabilityRights>();
        CapabilityRights deviceRights = ToCapabilityRights(plan.DeviceRights);
        expected.Add((ResourceKind.Device, plan.DeviceResourceId), deviceRights);

        foreach (DriverMmioResourcePlan mmio in plan.Mmio)
        {
            if (!CapabilityResourceIds.TryParseMmioRegion(mmio.CapabilityResourceId, out MmioRegionResourceId parsed) ||
                !string.Equals(parsed.DeviceResourceId, plan.DeviceResourceId, StringComparison.Ordinal) ||
                mmio.Offset > parsed.ByteLength - mmio.Length)
                return KernelResult.Fail(KernelError.InvalidManifest, "Driver MMIO plans must use a canonical region for the exact admitted device and contained range.");
            expected.Add((ResourceKind.MmioRegion, mmio.CapabilityResourceId), CapabilityRights.Map | ToCapabilityRights(mmio.Access));
        }
        foreach (DriverIrqResourcePlan irq in plan.Irqs)
        {
            if (!CapabilityResourceIds.TryParseIrq(irq.CapabilityResourceId, out IrqResourceId parsed) ||
                !string.Equals(parsed.DeviceResourceId, plan.DeviceResourceId, StringComparison.Ordinal))
                return KernelResult.Fail(KernelError.InvalidManifest, "Driver IRQ plans must use a canonical source for the exact admitted device.");
            expected.Add((ResourceKind.Irq, irq.CapabilityResourceId), CapabilityRights.Signal);
        }

        if (plan.Mmio.Count > 0 && !HasPlatformRequirement(manifest, ComponentPlatformFeatureFamily.MmioMapping, PlatformMmioLeaseContract.ContractVersion))
            return KernelResult.Fail(KernelError.InvalidManifest, "Driver MMIO resources require a MmioMapping platform requirement.");
        if (plan.Irqs.Count > 0 && !HasPlatformRequirement(manifest, ComponentPlatformFeatureFamily.IrqBinding, PlatformIrqBindingContract.ContractVersion))
            return KernelResult.Fail(KernelError.InvalidManifest, "Driver IRQ resources require an IrqBinding platform requirement.");
        if (plan.DmaPolicy is not null && !HasPlatformRequirement(manifest, ComponentPlatformFeatureFamily.DmaMapping, PlatformDmaGrantContract.ContractVersion))
            return KernelResult.Fail(KernelError.InvalidManifest, "Driver DMA policy requires a DmaMapping platform requirement.");

        if (hardware.Length != expected.Count)
            return KernelResult.Fail(KernelError.InvalidManifest, "Every driver Device/MMIO/IRQ capability must be represented exactly once by the DeviceResourceSet plan; separate ambient DMA capabilities are not accepted.");
        foreach (CapabilityRequirementV1 requirement in hardware)
        {
            if (!expected.TryGetValue((requirement.ResourceKind, requirement.ResourceId), out CapabilityRights rights) || rights != requirement.Rights)
                return KernelResult.Fail(KernelError.InvalidManifest, $"Driver hardware authority {requirement.ResourceKind}/{requirement.ResourceId} is broader than or absent from its DeviceResourceSet plan.");
        }

        return KernelResult.Ok();
    }

    private static bool HasPlatformRequirement(ServiceManifestV1 manifest, ComponentPlatformFeatureFamily family, uint minimumVersion) =>
        manifest.PlatformRequirements.Any(requirement => requirement.Family == family && requirement.MinimumContractVersion >= minimumVersion && requirement.Criticality == ManifestRequirementCriticality.Mandatory);

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        CapabilityRights result = CapabilityRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0) result |= CapabilityRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0) result |= CapabilityRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0) result |= CapabilityRights.Configure;
        return result;
    }

    private static CapabilityRights ToCapabilityRights(PlatformMmioAccess access)
    {
        CapabilityRights result = CapabilityRights.None;
        if ((access & PlatformMmioAccess.Read) != 0) result |= CapabilityRights.Read;
        if ((access & PlatformMmioAccess.Write) != 0) result |= CapabilityRights.Write;
        return result;
    }

    private static bool RequiresPlatformAuthorityDomain(IReadOnlyList<ComponentResourceRequirementV1> requirements) =>
        requirements.Any(static requirement => requirement.Kind == ComponentResourceRequirementKind.PlatformAuthorityDomain);

    private void ApplyComponentTeardown(ComponentAdmissionRecord record, ProcessTeardownSnapshot teardown)
    {
        lock (_componentAdmissionGate)
        {
            if (!_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record) ||
                record.State == ComponentLifecycleState.Reclaimable)
                return;
            if (teardown.LocalReclaimCompleted)
            {
                FinalizeComponentReclaim(record);
                return;
            }
            if (teardown.Phase == ProcessTeardownPhase.PlatformFaulted)
            {
                record.State = ComponentLifecycleState.Faulted;
                record.Failure = teardown.BlockingError ?? KernelError.PlatformFaulted;
                return;
            }
            record.State = ComponentLifecycleState.Draining;
            record.Failure = null;
        }
    }

    private void FinalizeComponentReclaim(ComponentAdmissionRecord record)
    {
        lock (_componentAdmissionGate)
        {
            if (!_components.TryGetValue(record.Manifest.Identity, out ComponentAdmissionRecord? current) || !ReferenceEquals(current, record)) return;
            lock (record.SnapshotGate)
            {
            record.PlatformBinding = null;
            record.DeviceResources = null;
            record.Capabilities.Clear();
            record.Sessions.Clear();
            record.DependencySessions.Clear();
            record.Services.Clear();
            record.State = ComponentLifecycleState.Reclaimable;
            record.Failure = null;
            }
        }
    }
}
