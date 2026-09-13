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
        if (!manifest.MatchesImage(plan.Image.Span))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentDigestMismatch, "Component image digest does not match its manifest.");
        if (_components.ContainsKey(manifest.Identity))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.DuplicateIdentity, $"Component '{manifest.Identity.Name}' is already tracked.");
        if (!RegistrationsMatchManifest(manifest, plan.ProvidedServices))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.InvalidManifest, "Provided service registrations do not exactly match the component manifest.");
        var platformRequirements = ValidatePlatformRequirements(manifest.PlatformRequirements);
        if (!platformRequirements.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(platformRequirements.Error, platformRequirements.Message!);
        var resourcePlan = ValidateResourcePlan(manifest.ResourceRequirements, plan.Grants);
        if (!resourcePlan.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(resourcePlan.Error, resourcePlan.Message!);
        var driverPlan = ValidateDriverResourcePlan(manifest, plan.DriverResources);
        if (!driverPlan.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(driverPlan.Error, driverPlan.Message!);

        var processHandle = new ProcessHandle(manifest.Process.ProcessId, manifest.Process.Generation);
        var record = new ComponentAdmissionRecord { Manifest = manifest, Process = processHandle, State = ComponentLifecycleState.Admitting };
        _components.Add(manifest.Identity, record);

        var created = CreateProcess(manifest.Process);
        if (!created.IsSuccess) return FailAdmission(record, created.Error, created.Message!);
        record.State = ComponentLifecycleState.Created;

        foreach (var grant in plan.Grants)
        {
            var minted = MintCapability(grant.IssuerDomain, processHandle, grant.Requirement.ResourceKind, grant.Requirement.ResourceId, grant.Requirement.Rights);
            if (!minted.IsSuccess) return Rollback(record, minted.Error, minted.Message!);
            record.Capabilities.Add(minted.Value!.CapabilityId);
        }

        var admitted = AdmitProcess(processHandle);
        if (!admitted.IsSuccess) return Rollback(record, admitted.Error, admitted.Message!);

        if (RequiresPlatformAuthorityDomain(manifest.ResourceRequirements))
        {
            var binding = BindPlatformAuthorityDomain(processHandle);
            if (!binding.IsSuccess) return Rollback(record, binding.Error, binding.Message!);
            record.PlatformBinding = binding.Value!;
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

        foreach (var required in manifest.RequiredContracts)
        {
            var candidates = ResolveByContract(required);
            if (!candidates.IsSuccess || candidates.Value!.Length != 1)
                return Rollback(record, KernelError.ServiceNotFound, $"Required contract '{required.Name}/{required.Version}' did not resolve exactly once.");
            var session = OpenSession(processHandle, candidates.Value[0], record.Capabilities);
            if (!session.IsSuccess) return Rollback(record, session.Error, session.Message!);
            record.Sessions.Add(session.Value);
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

    public KernelResult<ComponentLifecycleSnapshot> FaultComponent(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out var record))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
        if (record.State == ComponentLifecycleState.Reclaimable)
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        if (record.State is not ComponentLifecycleState.Running and not ComponentLifecycleState.Draining and not ComponentLifecycleState.Faulted)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.InvalidTransition, $"Component state {record.State} cannot enter crash teardown.");

        if (record.State == ComponentLifecycleState.Running)
        {
            record.State = ComponentLifecycleState.Draining;
            var fault = FaultProcess(record.Process);
            if (fault.IsSuccess)
            {
                FinalizeComponentReclaim(record);
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
            }

            var teardown = QueryProcessTeardown(record.Process);
            if (teardown.IsSuccess)
                ApplyComponentTeardown(record, teardown.Value!);

            if (fault.Error == KernelError.PlatformBindingDraining)
                return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);

            record.State = ComponentLifecycleState.Faulted;
            record.Failure = fault.Error;
            return KernelResult<ComponentLifecycleSnapshot>.Fail(fault.Error, fault.Message!);
        }

        return ObserveComponentTeardown(identity);
    }

    public KernelResult<ComponentLifecycleSnapshot> ObserveComponentTeardown(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out var record))
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");
        if (record.State == ComponentLifecycleState.Reclaimable)
            return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
        if (record.State is not ComponentLifecycleState.Draining and not ComponentLifecycleState.Faulted)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(KernelError.InvalidTransition, "Component teardown has not started.");

        var teardown = ObserveProcessTeardown(record.Process);
        if (!teardown.IsSuccess)
            return KernelResult<ComponentLifecycleSnapshot>.Fail(teardown.Error, teardown.Message!);
        ApplyComponentTeardown(record, teardown.Value!);
        return KernelResult<ComponentLifecycleSnapshot>.Ok(record.Snapshot);
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
        foreach (var session in record.Sessions.AsEnumerable().Reverse()) _ = CloseSession(record.Process, session);
        Services.UnregisterForProvider(record.Process);
        if (record.DeviceResources is not null)
        {
            var released = ReleaseDriverResources(record);
            if (!released.IsSuccess)
            {
                record.State = ComponentLifecycleState.Faulted;
                record.Failure = released.Error;
                return KernelResult<ComponentLifecycleSnapshot>.Fail(released.Error, released.Message!);
            }
        }
        if (record.PlatformBinding is { } binding)
        {
            var revoked = RevokePlatformDomain(record.Process, binding);
            if (!revoked.IsSuccess)
            {
                record.State = ComponentLifecycleState.Faulted;
                record.Failure = revoked.Error;
                return KernelResult<ComponentLifecycleSnapshot>.Fail(revoked.Error, revoked.Message!);
            }
            record.PlatformBinding = null;
        }
        foreach (var capability in record.Capabilities.AsEnumerable().Reverse()) _ = RevokeCapability(capability);
        var terminated = TerminateProcess(record.Process);
        record.Failure = error;
        record.State = terminated.IsSuccess ? ComponentLifecycleState.Reclaimable : ComponentLifecycleState.Faulted;
        return KernelResult<ComponentLifecycleSnapshot>.Fail(error, message);
    }

    private KernelResult<ComponentLifecycleSnapshot> FailAdmission(ComponentAdmissionRecord record, KernelError error, string message)
    {
        record.Failure = error;
        record.State = ComponentLifecycleState.Reclaimable;
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

    private KernelResult ReleaseDriverResources(ComponentAdmissionRecord record)
    {
        var resources = record.DeviceResources;
        if (resources is null) return KernelResult.Ok();

        foreach (var dma in resources.ActiveDmaGrants.Reverse().ToArray())
        {
            var revoked = RevokePlatformDma(record.Process, dma);
            if (!revoked.IsSuccess) return revoked;
        }
        foreach (var irq in resources.Irqs.Reverse().ToArray())
        {
            var revoked = RevokePlatformInterrupt(record.Process, irq.Binding);
            if (!revoked.IsSuccess) return revoked;
            var closed = CloseKernelEventEndpoint(record.Process, irq.EventEndpoint);
            if (!closed.IsSuccess) return closed;
        }
        foreach (var mmio in resources.Mmio.Reverse().ToArray())
        {
            var revoked = RevokePlatformMmio(record.Process, mmio.Lease);
            if (!revoked.IsSuccess) return revoked;
        }
        var device = RevokePlatformDevice(record.Process, resources.DeviceLease);
        if (!device.IsSuccess) return device;
        record.DeviceResources = null;
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
            var family = (PlatformFeatureFamily)requirement.Family;
            var availability = (PlatformFeatureAvailability)requirement.Availability;
            var feature = manifest.Resolve(family);
            if (feature.ContractVersion < requirement.MinimumContractVersion || feature.Availability != availability)
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
        manifest.PlatformRequirements.Any(requirement => requirement.Family == family && requirement.MinimumContractVersion >= minimumVersion);

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
        record.Services.Clear();
        record.State = ComponentLifecycleState.Reclaimable;
        record.Failure = null;
    }
}
