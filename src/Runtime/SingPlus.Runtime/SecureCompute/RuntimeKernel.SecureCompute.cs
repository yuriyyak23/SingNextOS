using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private sealed class SecureRecord(SecureDomainHandle handle, ProcessHandle owner, PlatformAuthorityBridge.SecureDomainBinding binding)
    {
        public SecureDomainHandle Handle { get; } = handle;
        public ProcessHandle Owner { get; } = owner;
        public PlatformAuthorityBridge.SecureDomainBinding Binding { get; } = binding;
        public SecureDomainState State { get; set; } = SecureDomainState.Created;
        public List<CapabilityId> Capabilities { get; } = [];
        public Dictionary<PlatformRegionMappingId, PlatformRegionMapping> Regions { get; } = [];
    }
    private readonly Dictionary<SecureDomainId, SecureRecord> _secureDomainRecords = [];
    private ulong _nextSecureDomainId = 1;

    public KernelResult<SecureDomainAuthoritySet> CreateSecureDomain(ProcessHandle owner, CapabilityId createCapability, PlatformDomainBinding parent, SecureDomainProfile profile)
    {
        var capability = ValidateCapability(owner, createCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult<SecureDomainAuthoritySet>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.SecureCompute || capability.Value.ResourceId != SecureComputeResourceIds.Create)
            return KernelResult<SecureDomainAuthoritySet>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize secure-domain creation.");
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<SecureDomainAuthoritySet>.Fail(process.Error, process.Message!);
        var created = PlatformAuthority.CreateSecureDomain(parent, PlatformIdentity(process.Value!), profile);
        if (!created.IsSuccess) return KernelResult<SecureDomainAuthoritySet>.Fail(created.Error, created.Message!);
        var handle = new SecureDomainHandle(new(_nextSecureDomainId++), new(1));
        var record = new SecureRecord(handle, owner, created.Value!);
        _secureDomainRecords.Add(handle.DomainId, record);
        var configure = MintCapability(process.Value!.DomainId, owner, ResourceKind.SecureCompute, SecureComputeResourceIds.Domain(handle.DomainId), CapabilityRights.Configure).Value!.CapabilityId;
        var memory = MintCapability(process.Value.DomainId, owner, ResourceKind.SecureCompute, SecureComputeResourceIds.Memory(handle.DomainId), CapabilityRights.Map).Value!.CapabilityId;
        var execute = MintCapability(process.Value.DomainId, owner, ResourceKind.SecureCompute, SecureComputeResourceIds.Domain(handle.DomainId), CapabilityRights.Execute).Value!.CapabilityId;
        var evidence = MintCapability(process.Value.DomainId, owner, ResourceKind.Evidence, SecureComputeResourceIds.Evidence(handle.DomainId), CapabilityRights.Read).Value!.CapabilityId;
        record.Capabilities.AddRange([configure, memory, execute, evidence]);
        return KernelResult<SecureDomainAuthoritySet>.Ok(new(handle, configure, memory, execute, evidence));
    }

    public KernelResult BindSecureRegion(ProcessHandle owner, SecureDomainHandle domain, CapabilityId memoryCapability, PlatformRegionMapping mapping, PlatformSecureRegionClass regionClass)
    {
        var record = ResolveSecure(owner, domain, memoryCapability, SecureComputeResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State is not (SecureDomainState.Created or SecureDomainState.Configured or SecureDomainState.Parked))
            return KernelResult.Fail(KernelError.InvalidTransition, "Secure regions can only be bound while the domain is not running or draining.");
        var result = PlatformAuthority.BindSecureRegion(record.Value.Binding, mapping, regionClass);
        if (result.IsSuccess) { record.Value.Regions.Add(mapping.MappingId, mapping); record.Value.State = SecureDomainState.Configured; }
        return result;
    }

    public KernelResult CloseSecureRegion(ProcessHandle owner, SecureDomainHandle domain, CapabilityId memoryCapability, PlatformRegionMapping mapping)
    {
        var record = ResolveSecure(owner, domain, memoryCapability, SecureComputeResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (!record.Value!.Regions.TryGetValue(mapping.MappingId, out var exact) || exact != mapping)
            return KernelResult.Fail(KernelError.StaleGeneration, "Secure-region mapping is absent or stale.");
        var result = PlatformAuthority.UnbindSecureRegion(record.Value.Binding, mapping);
        if (result.IsSuccess) record.Value.Regions.Remove(mapping.MappingId);
        else record.Value.State = SecureDomainState.Quarantined;
        return result;
    }

    public KernelResult<EvidenceRecord> ReadSecureDomainEvidence(ProcessHandle owner, SecureDomainHandle domain,
        CapabilityId evidenceCapability, PlatformDomainBinding parent, EvidenceIdentity identity,
        EvidenceVisibilityClass visibility, EvidenceFreshness minimumFreshness)
    {
        var record = ResolveSecure(owner, domain, evidenceCapability, SecureComputeResourceIds.Evidence(domain.DomainId), CapabilityRights.Read);
        if (!record.IsSuccess) return KernelResult<EvidenceRecord>.Fail(record.Error, record.Message!);
        if (record.Value!.State is SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed)
            return KernelResult<EvidenceRecord>.Fail(KernelError.PlatformFaulted, "Secure-domain evidence context is no longer live.");
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<EvidenceRecord>.Fail(process.Error, process.Message!);
        if (parent != record.Value!.Binding.Parent)
            return KernelResult<EvidenceRecord>.Fail(KernelError.WrongPlatformDomain, "Evidence parent does not match the exact secure-domain parent.");
        return PlatformAuthority.ReadEvidence(parent, PlatformIdentity(process.Value!), null, identity, visibility, minimumFreshness,
            new EvidenceDomainIdentity(domain.DomainId.Value, domain.Generation.Value));
    }

    public KernelResult TransitionSecureDomain(ProcessHandle owner, SecureDomainHandle domain, CapabilityId executeCapability, PlatformSecureDomainTransition transition)
    {
        var record = ResolveSecure(owner, domain, executeCapability, SecureComputeResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        var expected = transition switch { PlatformSecureDomainTransition.Start => SecureDomainState.Configured, PlatformSecureDomainTransition.Park => SecureDomainState.Running, PlatformSecureDomainTransition.Resume => SecureDomainState.Parked, _ => record.Value!.State };
        if (record.Value!.State != expected) return KernelResult.Fail(KernelError.InvalidTransition, "Secure-domain transition is invalid.");
        var result = PlatformAuthority.TransitionSecureDomain(record.Value.Binding, transition);
        if (result.IsSuccess) record.Value.State = transition switch { PlatformSecureDomainTransition.Start => SecureDomainState.Running, PlatformSecureDomainTransition.Park => SecureDomainState.Parked, PlatformSecureDomainTransition.Resume => SecureDomainState.Running, _ => SecureDomainState.Draining };
        return result;
    }

    public KernelResult DestroySecureDomain(ProcessHandle owner, SecureDomainHandle domain, CapabilityId configureCapability)
    {
        var record = ResolveSecure(owner, domain, configureCapability, SecureComputeResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.Regions.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure regions must close before secure-domain destruction.");
        if (record.Value.State is SecureDomainState.Quarantined or SecureDomainState.Faulted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Quarantined secure-domain authority remains pinned.");
        record.Value.State = SecureDomainState.Draining;
        var drain = PlatformAuthority.TransitionSecureDomain(record.Value.Binding, PlatformSecureDomainTransition.BeginDrain);
        if (!drain.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return drain; }
        var close = PlatformAuthority.RevokeSecureDomain(record.Value.Binding);
        if (!close.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return close; }
        foreach (var capability in record.Value.Capabilities) _ = RevokeCapability(capability);
        record.Value.State = SecureDomainState.Closed;
        _secureDomainRecords.Remove(domain.DomainId);
        return KernelResult.Ok();
    }

    private KernelResult CloseSecureDomainsForProcess(ProcessHandle owner)
    {
        foreach (var record in _secureDomainRecords.Values.Where(record => record.Owner == owner).OrderBy(record => record.Handle.DomainId.Value).ToArray())
        {
            foreach (var mapping in record.Regions.Values.OrderBy(mapping => mapping.MappingId.Value).ToArray())
            {
                var unbind = PlatformAuthority.UnbindSecureRegion(record.Binding, mapping);
                if (!unbind.IsSuccess) { record.State = SecureDomainState.Quarantined; return unbind; }
                record.Regions.Remove(mapping.MappingId);
            }
            record.State = SecureDomainState.Draining;
            var drain = PlatformAuthority.TransitionSecureDomain(record.Binding, PlatformSecureDomainTransition.BeginDrain);
            if (!drain.IsSuccess) { record.State = SecureDomainState.Quarantined; return drain; }
            var close = PlatformAuthority.RevokeSecureDomain(record.Binding);
            if (!close.IsSuccess) { record.State = SecureDomainState.Quarantined; return close; }
            foreach (var capability in record.Capabilities) _ = RevokeCapability(capability);
            _secureDomainRecords.Remove(record.Handle.DomainId);
        }
        return KernelResult.Ok();
    }

    private void QuarantineSecureDomainsForPlatformBackendReset()
    {
        foreach (var record in _secureDomainRecords.Values) record.State = SecureDomainState.Quarantined;
    }

    private KernelResult<SecureRecord> ResolveSecure(ProcessHandle owner, SecureDomainHandle domain, CapabilityId capabilityId, string resource, CapabilityRights rights)
    {
        var capability = ValidateCapability(owner, capabilityId, rights);
        if (!capability.IsSuccess) return KernelResult<SecureRecord>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind is not (ResourceKind.SecureCompute or ResourceKind.Evidence) || capability.Value.ResourceId != resource)
            return KernelResult<SecureRecord>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize the exact secure-domain resource.");
        if (!_secureDomainRecords.TryGetValue(domain.DomainId, out var record)) return KernelResult<SecureRecord>.Fail(KernelError.PlatformBindingNotFound, "Secure domain was not found.");
        if (record.Handle != domain || record.Owner != owner) return KernelResult<SecureRecord>.Fail(KernelError.StaleGeneration, "Secure domain is stale or owned by another process.");
        return KernelResult<SecureRecord>.Ok(record);
    }
}
