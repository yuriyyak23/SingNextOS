using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<ComponentIdentity, ComponentAdmissionRecord> _components = [];

    public KernelResult<ComponentLifecycleSnapshot> AdmitComponent(ComponentAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var manifest = plan.Manifest;
        var imports = ValidateComponentContractImports(plan);
        if (!imports.IsSuccess) return KernelResult<ComponentLifecycleSnapshot>.Fail(imports.Error, imports.Message!);
        var evaluation = EvaluateComponentAdmissionCore(plan);
        // Dependency availability is evidence at preflight time. Preserve the
        // existing admission/rollback lifecycle so a hard dependency loss is
        // recorded on the component rather than becoming a second authority
        // decision in the evaluator.
        if (evaluation.Error is { } evaluationError && evaluationError != KernelError.ServiceNotFound)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(evaluationError, evaluation.Message!);

        var processPreflight = Processes.ValidateCreation(manifest.Process);
        if (!processPreflight.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(processPreflight.Error, processPreflight.Message!);

        var processHandle = new ProcessHandle(manifest.Process.ProcessId, manifest.Process.Generation);
        var limits = manifest.BudgetRequests.Select(static request => new BudgetAmount(request.Dimension, request.Limit)).ToArray();
        var hierarchy = Budgets.CreateProcessHierarchy(processHandle,
            $"service:{manifest.Identity.Name}:{manifest.Process.Generation}",
            $"process:{processHandle.ProcessId.Value}:{processHandle.Generation}", limits);
        if (!hierarchy.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(hierarchy.Error, hierarchy.Message!);
        var admittedBudgetEvidence = evaluation.Result;
        foreach (var request in manifest.BudgetRequests)
            admittedBudgetEvidence = MarkRequirementGranted(
                admittedBudgetEvidence,
                ManifestRequirementKind.Budget,
                $"{request.Dimension}:{request.Limit}",
                "Exact capacity was allocated in the generation-bound budget hierarchy; the reservation remains non-authoritative.");

        var record = new ComponentAdmissionRecord
        {
            Plan = plan,
            Manifest = manifest,
            Process = processHandle,
            Admission = admittedBudgetEvidence,
            State = ComponentLifecycleState.Admitting,
            ServiceBudget = hierarchy.Value!.ServiceBudget,
            ProcessBudget = hierarchy.Value!.ProcessBudget,
        };
        _components.Add(manifest.Identity, record);

        var created = CreateProcess(manifest.Process);
        if (!created.IsSuccess) return FailAdmission(record, created.Error, created.Message!);
        record.State = ComponentLifecycleState.Created;

        foreach (var grant in plan.Grants)
        {
            var minted = MintCapability(grant.IssuerDomain, processHandle, grant.Requirement.ResourceKind, grant.Requirement.ResourceId, grant.Requirement.Rights);
            if (!minted.IsSuccess) return Rollback(record, minted.Error, minted.Message!);
            record.Capabilities.Add(minted.Value!.CapabilityId);
            record.Admission = MarkRequirementGranted(
                record.Admission,
                ManifestRequirementKind.LocalCapability,
                $"{grant.Requirement.ResourceKind}:{grant.Requirement.ResourceId}:{grant.Requirement.Rights}",
                "Exact live capability was materialized by the kernel; its identity is intentionally absent from admission evidence.");
        }

        var admitted = AdmitProcess(processHandle);
        if (!admitted.IsSuccess) return Rollback(record, admitted.Error, admitted.Message!);

        if (RequiresPlatformAuthorityDomain(manifest.ResourceRequirements))
        {
            var binding = BindPlatformAuthorityDomain(processHandle);
            if (!binding.IsSuccess) return Rollback(record, binding.Error, binding.Message!);
            record.PlatformBinding = binding.Value!;
            record.Admission = MarkRequirementGranted(
                record.Admission,
                ManifestRequirementKind.PlatformAuthorityDomain,
                "platform-authority-domain",
                "Exact provider-neutral authority binding was materialized; provider-private identity is omitted.");
        }

        if (plan.DriverResources is { } resources)
        {
            var materialized = MaterializeDriverResources(record, resources);
            if (!materialized.IsSuccess) return Rollback(record, materialized.Error, materialized.Message!);
        }

        foreach (var registration in plan.ProvidedServices)
        {
            var service = RegisterService(processHandle, registration.Manifest.ServiceName, registration.Manifest.Contract, registration.Protocol, registration.ResponseProtocol, registration.SessionRequirements);
            if (!service.IsSuccess) return Rollback(record, service.Error, service.Message!);
            record.Services.Add(service.Value!);
        }

        foreach (var dependency in manifest.Dependencies)
        {
            var required = dependency.Contract;
            var candidates = ResolveByContract(required);
            if (!candidates.IsSuccess || candidates.Value!.Length != 1)
            {
                if (dependency.Kind == ServiceDependencyKind.Optional)
                {
                    record.Admission = DegradeOptionalDependency(record.Admission, dependency, "Optional dependency did not resolve exactly once during binding.");
                    continue;
                }
                return Rollback(record, KernelError.ServiceNotFound, $"Required contract '{required.Name}/{required.Version}' did not resolve exactly once.");
            }
            var session = OpenSession(processHandle, candidates.Value[0], record.Capabilities);
            if (!session.IsSuccess)
            {
                if (dependency.Kind == ServiceDependencyKind.Optional)
                {
                    record.Admission = DegradeOptionalDependency(record.Admission, dependency, $"Optional dependency binding was unavailable: {session.Error}.");
                    continue;
                }
                return Rollback(record, session.Error, session.Message!);
            }
            record.Sessions.Add(session.Value);
            record.DependencySessions[required] = session.Value;
        }

        record.State = ComponentLifecycleState.Starting;
        var started = StartProcess(processHandle);
        if (!started.IsSuccess) return Rollback(record, started.Error, started.Message!);
        record.State = ComponentLifecycleState.Running;
        return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
    }

    public KernelResult<ComponentLifecycleSnapshot> QueryComponent(ComponentIdentity identity) =>
        _components.TryGetValue(identity, out var record)
            ? KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot)
            : KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");

    public KernelResult<DeviceResourceSetSnapshot> QueryComponentDeviceResources(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out var record) || record.DeviceResources is null)
            return KernelResult<DeviceResourceSetSnapshot>.Fail(KernelError.ComponentNotFound, "Component does not own a materialized driver device resource set.");
        return KernelResult<DeviceResourceSetSnapshot>.Ok(record.DeviceResources.Snapshot);
    }

    public KernelResult<PlatformOwnedRegionSliceMapping> MapComponentDriverOwnedRegion(
        ComponentIdentity identity,
        CapabilityId regionCapability,
        RegionHandle region,
        long offset,
        long length,
        PlatformMemoryAccess access)
    {
        var authority = ResolveDriverComponentAuthority(identity);
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
        var authority = ResolveDriverComponentAuthority(identity);
        if (!authority.IsSuccess)
            return KernelResult<PlatformDmaGrant>.Fail(authority.Error, authority.Message!);
        var policy = authority.Value.Resources.DmaPolicy;
        if (policy is null)
            return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformDenied, "Driver component does not declare DMA authority.");
        if (!policy.Allows(direction, length))
            return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformDenied, "DMA request exceeds the component's exact direction or transfer-size policy.");

        var grant = BindPlatformDma(
            authority.Value.Process,
            authority.Value.Resources.DeviceLease,
            mapping,
            offset,
            length,
            direction);
        if (grant.IsSuccess)
            authority.Value.Resources.AddDma(grant.Value!);
        return grant;
    }

    public KernelResult RevokeComponentDriverDma(
        ComponentIdentity identity,
        PlatformDmaGrant grant)
    {
        var authority = ResolveDriverComponentAuthority(identity);
        if (!authority.IsSuccess) return KernelResult.Fail(authority.Error, authority.Message!);
        var tracked = authority.Value.Resources.ActiveDmaGrants.Any(candidate => candidate.GrantId == grant.GrantId && candidate.Generation == grant.Generation);
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
        if (!_components.TryGetValue(identity, out var record))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
        if (expectedProcess is { } expected && record.Process != expected)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.StaleGeneration,
                "Component teardown request targets a different process generation.");
        if (record.State == ComponentLifecycleState.Reclaimable)
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        if (record.State is not ComponentLifecycleState.Running and not ComponentLifecycleState.Draining and not ComponentLifecycleState.Faulted)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.InvalidTransition, $"Component state {record.State} cannot enter crash teardown.");

        if (record.State == ComponentLifecycleState.Running)
        {
            record.State = ComponentLifecycleState.Draining;
            var teardownResult = faulted ? FaultProcess(record.Process) : TerminateProcess(record.Process);
            if (teardownResult.IsSuccess)
            {
                FinalizeComponentReclaim(record);
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
            }

            var teardown = QueryProcessTeardown(record.Process);
            if (teardown.IsSuccess)
                ApplyComponentTeardown(record, teardown.Value!);

            if (teardownResult.Error == KernelError.PlatformBindingDraining)
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);

            record.State = ComponentLifecycleState.Faulted;
            record.Failure = teardownResult.Error;
            return KernelResult<ComponentLifecycleSnapshot>.Fail(teardownResult.Error, teardownResult.Message!);
        }

        return ObserveComponentTeardownRecord(record);
    }

    public KernelResult<ComponentLifecycleSnapshot> ObserveComponentTeardown(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out var record))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
        return ObserveComponentTeardownRecord(record);
    }

    private KernelResult<ComponentLifecycleSnapshot> ObserveComponentTeardownRecord(ComponentAdmissionRecord record)
    {
        if (record.State == ComponentLifecycleState.Reclaimable)
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        if (record.State is not ComponentLifecycleState.Draining and not ComponentLifecycleState.Faulted)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.InvalidTransition, "Component teardown has not started.");

        // Retry through the existing lifecycle consumer with its authoritative
        // terminal target. Mere observation cannot rerun composed preparation.
        var tracked = QueryProcessTeardown(record.Process);
        if (!tracked.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(tracked.Error, tracked.Message!);
        var advanced = BeginOrAdvanceProcessTeardown(record.Process, tracked.Value!.TargetTerminalState);
        if (advanced.IsSuccess)
        {
            FinalizeComponentReclaim(record);
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        }

        var teardown = QueryProcessTeardown(record.Process);
        if (!teardown.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(teardown.Error, teardown.Message!);
        ApplyComponentTeardown(record, teardown.Value!);
        return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
    }

    internal KernelResult RetireReclaimableComponent(ComponentIdentity identity, ProcessHandle exactProcess)
    {
        if (!_components.TryGetValue(identity, out var record))
            return KernelResult.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
        if (record.Process != exactProcess)
            return KernelResult.Fail(KernelError.StaleGeneration, "Component process generation is stale.");
        if (record.State != ComponentLifecycleState.Reclaimable)
            return KernelResult.Fail(KernelError.ReplacementBlocked, "Component is not exactly reclaimable.");
        var retiredBudget = Budgets.RetireProcessHierarchy(record.Process, record.ProcessBudget, record.ServiceBudget);
        if (!retiredBudget.IsSuccess) return retiredBudget;
        _components.Remove(identity);
        return KernelResult.Ok();
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
        if (!_components.TryGetValue(identity, out var record) || record.State != ComponentLifecycleState.Running)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.ComponentNotFound, "Running dependent component was not found.");
        if (record.Process != expectedProcess)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.StaleGeneration, "Dependency rebind target process generation is stale.");
        if (source is { } control)
        {
            var admitted = ValidateSupervisorSessionSource(control.Principal, control.Capability);
            if (!admitted.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(admitted.Error, admitted.Message!);
        }
        var dependency = record.Manifest.Dependencies.SingleOrDefault(candidate => candidate.Contract == exactProvider.Contract);
        if (dependency.Contract == default)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidManifest, "Dependency is not declared by the component manifest.");
        var preflight = PreflightDependencySession(record.Process, exactProvider, record.Capabilities);
        if (!preflight.IsSuccess)
            return KernelResult<EndpointSessionHandle>.Fail(preflight.Error, preflight.Message!);

        bool hadPrevious;
        EndpointSessionHandle previous;
        lock (_platformMemoryUseGate)
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

        lock (_platformMemoryUseGate)
        {
            if (!_components.TryGetValue(identity, out var current) || !ReferenceEquals(current, record) ||
                current.Process != expectedProcess || current.State != ComponentLifecycleState.Running)
                return KernelResult<EndpointSessionHandle>.Fail(KernelError.StaleGeneration, "Dependency target changed before routing commit.");
            if (hadPrevious)
            {
                if (!record.DependencySessions.TryGetValue(exactProvider.Contract, out var tracked) || tracked != previous)
                    return KernelResult<EndpointSessionHandle>.Fail(KernelError.StaleGeneration, "Dependency routing changed during old session closure.");
                record.DependencySessions.Remove(exactProvider.Contract);
                record.Sessions.Remove(previous);
            }
            else if (record.DependencySessions.ContainsKey(exactProvider.Contract))
                return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Another admission already bound this dependency.");

            // Lifetime-free local creation has no provider/clock callback. Keep
            // process teardown outside until the actual session is accounted.
            var session = OpenSessionCore(record.Process, exactProvider, record.Capabilities, 8, lifetime: null, source);
            if (!session.IsSuccess)
                return KernelResult<EndpointSessionHandle>.Fail(session.Error, session.Message!);
            record.DependencySessions.Add(exactProvider.Contract, session.Value);
            record.Sessions.Add(session.Value);
            // A component drain request can precede its process-owner transition.
            // Retain the admitted session but do not return a running receipt.
            return record.State == ComponentLifecycleState.Running
                ? KernelResult<EndpointSessionHandle>.Ok(session.Value)
                : KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Component drain requested during local session admission.");
        }
    }

    internal KernelResult<(ProcessHandle Process, PlatformDomainBinding Binding, CapabilityId ComputeCapability)> ResolveComputeComponentAuthority(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out var record) || record.State != ComponentLifecycleState.Running || record.PlatformBinding is not { } binding)
            return KernelResult<(ProcessHandle, PlatformDomainBinding, CapabilityId)>.Fail(KernelError.ComponentNotFound, "Running compute component with platform authority was not found.");
        foreach (var capability in record.Capabilities)
        {
            var validation = ValidateCapability(record.Process, capability, CapabilityRights.Execute);
            if (validation.IsSuccess && validation.Value!.ResourceKind == ResourceKind.Compute && validation.Value.ResourceId == CapabilityResourceIds.Dsc1Copy)
                return KernelResult<(ProcessHandle, PlatformDomainBinding, CapabilityId)>.Ok((record.Process, binding, capability));
        }
        return KernelResult<(ProcessHandle, PlatformDomainBinding, CapabilityId)>.Fail(KernelError.MissingCapability, "Compute component lacks exact DSC1 Copy Execute authority.");
    }

    internal KernelResult<(ProcessHandle Process, PlatformDomainBinding Binding, DeviceResourceSet Resources)> ResolveDriverComponentAuthority(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out var record) ||
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

    internal void UntrackComponentDriverDma(PlatformDmaGrant grant)
    {
        foreach (var record in _components.Values)
            record.DeviceResources?.RemoveDma(grant);
    }

    private KernelResult<ComponentLifecycleSnapshot> Rollback(ComponentAdmissionRecord record, KernelError error, string message)
    {
        record.State = ComponentLifecycleState.Draining;
        // Stop every admitted local source before cleanup can call a provider or
        // return with an ambiguous effect. This owner transition proves no closure;
        // the existing cascade and resource records remain responsible for cleanup.
        foreach (var capability in record.Capabilities)
            _ = CapabilityAuthority.Revoke(capability);
        Services.UnregisterForProvider(record.Process);
        // The existing process teardown owner stops admissions, tracks every provider
        // resource and supplies the exact closure snapshot consumed by Observe.
        // A failed cleanup is no longer stranded before teardown-record creation.
        var terminated = TerminateProcess(record.Process);
        if (terminated.IsSuccess)
            FinalizeComponentReclaim(record);
        else
        {
            var teardown = QueryProcessTeardown(record.Process);
            if (teardown.IsSuccess)
                ApplyComponentTeardown(record, teardown.Value!);
            else
                record.State = ComponentLifecycleState.Faulted;
        }

        var failure = !terminated.IsSuccess && terminated.Error != KernelError.PlatformBindingDraining
            ? terminated.Error : error;
        record.Failure = failure;
        return KernelResult<ComponentLifecycleSnapshot>.Fail(failure,
            failure == error ? message : terminated.Message!);
    }

    private KernelResult<ComponentLifecycleSnapshot> FailAdmission(ComponentAdmissionRecord record, KernelError error, string message)
    {
        record.Failure = error;
        // This component did not create its process. A colliding live generation may
        // own resources charged to the already attached hierarchy; refusal is not closure.
        record.State = ComponentLifecycleState.Faulted;
        return KernelResult<ComponentLifecycleSnapshot>.Fail(error, message);
    }

    private KernelResult MaterializeDriverResources(ComponentAdmissionRecord record, ComponentDriverResourcePlan plan)
    {
        if (record.PlatformBinding is not { } binding)
            return KernelResult.Fail(KernelError.InvalidManifest, "Driver resources require an explicit component platform authority domain.");

        var deviceRights = ToCapabilityRights(plan.DeviceRights);
        var deviceCapability = FindComponentCapability(record, ResourceKind.Device, plan.DeviceResourceId, deviceRights);
        if (!deviceCapability.IsSuccess) return KernelResult.Fail(deviceCapability.Error, deviceCapability.Message!);
        var device = BindPlatformDevice(record.Process, binding, deviceCapability.Value, plan.DeviceRights);
        if (!device.IsSuccess) return KernelResult.Fail(device.Error, device.Message!);

        var resources = new DeviceResourceSet(record.Process, plan.DeviceResourceId, deviceCapability.Value, device.Value!, plan.DmaPolicy);
        record.DeviceResources = resources;

        foreach (var mmio in plan.Mmio)
        {
            var rights = CapabilityRights.Map | ToCapabilityRights(mmio.Access);
            var capability = FindComponentCapability(record, ResourceKind.MmioRegion, mmio.CapabilityResourceId, rights);
            if (!capability.IsSuccess) return KernelResult.Fail(capability.Error, capability.Message!);
            var lease = BindPlatformMmio(record.Process, resources.DeviceLease, capability.Value, mmio.Offset, mmio.Length, mmio.Access);
            if (!lease.IsSuccess) return KernelResult.Fail(lease.Error, lease.Message!);
            resources.AddMmio(new MmioResourceGrant(mmio.CapabilityResourceId, capability.Value, lease.Value!));
        }

        foreach (var irq in plan.Irqs)
        {
            var capability = FindComponentCapability(record, ResourceKind.Irq, irq.CapabilityResourceId, CapabilityRights.Signal);
            if (!capability.IsSuccess) return KernelResult.Fail(capability.Error, capability.Message!);
            var endpoint = CreateKernelEventEndpoint(record.Process);
            if (!endpoint.IsSuccess) return KernelResult.Fail(endpoint.Error, endpoint.Message!);
            var bindingResult = BindPlatformInterrupt(record.Process, resources.DeviceLease, capability.Value, endpoint.Value!);
            if (!bindingResult.IsSuccess)
            {
                _ = CloseKernelEventEndpoint(record.Process, endpoint.Value!);
                return KernelResult.Fail(bindingResult.Error, bindingResult.Message!);
            }
            resources.AddIrq(new IrqResourceGrant(irq.CapabilityResourceId, capability.Value, bindingResult.Value!, endpoint.Value!));
        }

        return KernelResult.Ok();
    }

    private KernelResult<CapabilityId> FindComponentCapability(
        ComponentAdmissionRecord record,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        foreach (var capabilityId in record.Capabilities)
        {
            var capability = ValidateCapability(record.Process, capabilityId, rights);
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
        var manifest = QueryPlatformFeatures();
        foreach (var requirement in requirements)
        {
            if (!Enum.IsDefined(requirement.Family))
            {
                if (requirement.Criticality == ManifestRequirementCriticality.Optional) continue;
                return KernelResult.Fail(KernelError.PlatformUnsupported, $"Unknown mandatory platform feature {(int)requirement.Family} is unsupported.");
            }
            var family = (PlatformFeatureFamily)requirement.Family;
            var availability = (PlatformFeatureAvailability)requirement.Availability;
            var feature = manifest.Resolve(family);
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
        var expected = requirements
            .Where(static requirement => requirement.Kind == ComponentResourceRequirementKind.LocalCapability)
            .Select(static requirement => requirement.ToCapabilityRequirement())
            .ToArray();

        var grantsByResource = new Dictionary<(ResourceKind Kind, string ResourceId), ComponentCapabilityGrant>();
        foreach (var grant in grants)
        {
            var requirement = grant.Requirement;
            if (!Enum.IsDefined(requirement.ResourceKind) || string.IsNullOrWhiteSpace(requirement.ResourceId) || requirement.Rights == CapabilityRights.None)
                return KernelResult.Fail(KernelError.InvalidManifest, "Component capability grants must name a defined local resource and non-empty rights.");
            if (!grantsByResource.TryAdd((requirement.ResourceKind, requirement.ResourceId), grant))
                return KernelResult.Fail(KernelError.InvalidManifest, $"Component admission plan contains duplicate grants for {requirement.ResourceKind}/{requirement.ResourceId}.");
        }

        foreach (var requirement in expected)
        {
            if (!grantsByResource.TryGetValue((requirement.ResourceKind, requirement.ResourceId), out var grant))
                return KernelResult.Fail(KernelError.MissingCapability, $"Component resource {requirement.ResourceKind}/{requirement.ResourceId} has no exact local capability grant.");
            if (grant.Requirement.Rights != requirement.Rights)
                return KernelResult.Fail(KernelError.InvalidManifest, $"Component grant rights for {requirement.ResourceKind}/{requirement.ResourceId} must exactly equal the manifest requirement.");
        }

        if (grantsByResource.Count != expected.Length)
        {
            var expectedKeys = expected.Select(static requirement => (requirement.ResourceKind, requirement.ResourceId)).ToHashSet();
            var extra = grantsByResource.Keys.First(key => !expectedKeys.Contains(key));
            return KernelResult.Fail(KernelError.InvalidManifest, $"Component admission plan grants undeclared resource {extra.Kind}/{extra.ResourceId}.");
        }

        return KernelResult.Ok();
    }

    private static KernelResult ValidateDriverResourcePlan(ServiceManifestV1 manifest, ComponentDriverResourcePlan? plan)
    {
        var local = manifest.ResourceRequirements
            .Where(static requirement => requirement.Kind == ComponentResourceRequirementKind.LocalCapability)
            .Select(static requirement => requirement.ToCapabilityRequirement())
            .ToArray();
        var hardware = local.Where(static requirement => requirement.ResourceKind is ResourceKind.Device or ResourceKind.MmioRegion or ResourceKind.Irq or ResourceKind.Dma).ToArray();

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

        var expected = new Dictionary<(ResourceKind Kind, string ResourceId), CapabilityRights>();
        var deviceRights = ToCapabilityRights(plan.DeviceRights);
        expected.Add((ResourceKind.Device, plan.DeviceResourceId), deviceRights);

        foreach (var mmio in plan.Mmio)
        {
            if (!CapabilityResourceIds.TryParseMmioRegion(mmio.CapabilityResourceId, out var parsed) ||
                !string.Equals(parsed.DeviceResourceId, plan.DeviceResourceId, StringComparison.Ordinal) ||
                mmio.Offset > parsed.ByteLength - mmio.Length)
                return KernelResult.Fail(KernelError.InvalidManifest, "Driver MMIO plans must use a canonical region for the exact admitted device and contained range.");
            expected.Add((ResourceKind.MmioRegion, mmio.CapabilityResourceId), CapabilityRights.Map | ToCapabilityRights(mmio.Access));
        }
        foreach (var irq in plan.Irqs)
        {
            if (!CapabilityResourceIds.TryParseIrq(irq.CapabilityResourceId, out var parsed) ||
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
        foreach (var requirement in hardware)
        {
            if (!expected.TryGetValue((requirement.ResourceKind, requirement.ResourceId), out var rights) || rights != requirement.Rights)
                return KernelResult.Fail(KernelError.InvalidManifest, $"Driver hardware authority {requirement.ResourceKind}/{requirement.ResourceId} is broader than or absent from its DeviceResourceSet plan.");
        }

        return KernelResult.Ok();
    }

    private static bool HasPlatformRequirement(ServiceManifestV1 manifest, ComponentPlatformFeatureFamily family, uint minimumVersion) =>
        manifest.PlatformRequirements.Any(requirement => requirement.Family == family && requirement.MinimumContractVersion >= minimumVersion && requirement.Criticality == ManifestRequirementCriticality.Mandatory);

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        var result = CapabilityRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0) result |= CapabilityRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0) result |= CapabilityRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0) result |= CapabilityRights.Configure;
        return result;
    }

    private static CapabilityRights ToCapabilityRights(PlatformMmioAccess access)
    {
        var result = CapabilityRights.None;
        if ((access & PlatformMmioAccess.Read) != 0) result |= CapabilityRights.Read;
        if ((access & PlatformMmioAccess.Write) != 0) result |= CapabilityRights.Write;
        return result;
    }

    private static bool RequiresPlatformAuthorityDomain(IReadOnlyList<ComponentResourceRequirementV1> requirements) =>
        requirements.Any(static requirement => requirement.Kind == ComponentResourceRequirementKind.PlatformAuthorityDomain);

    private static void ApplyComponentTeardown(ComponentAdmissionRecord record, ProcessTeardownSnapshot teardown)
    {
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

    private static void FinalizeComponentReclaim(ComponentAdmissionRecord record)
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
