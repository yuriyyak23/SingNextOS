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
        public ulong PolicyGeneration { get; set; } = 1;
        public ulong ProtectionGeneration { get; set; } = 1;
        public List<CapabilityId> Capabilities { get; } = [];
        public Dictionary<PlatformRegionMappingId, PlatformRegionMapping> Regions { get; } = [];
        public bool TransitionInFlight { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool RegionMutationInFlight { get; set; }
    }
    private readonly Dictionary<SecureDomainId, SecureRecord> _secureDomainRecords = [];
    private ulong _nextSecureDomainId = 1;
    // Deterministic fault-injection seam; it never supplies authority or closure evidence.
    internal Action? BeforeSecureDomainLocalPublication { get; set; }
    // Deterministic fault-injection seam after provider transition, before local state commit.
    internal Action? BeforeSecureDomainTransitionLocalCommit { get; set; }
    // Deterministic fault-injection seam after provider region receipt, before local commit.
    internal Action? BeforeSecureRegionLocalCommit { get; set; }

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
        try { BeforeSecureDomainLocalPublication?.Invoke(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            _ = PlatformAuthority.RevokeSecureDomain(created.Value!);
            return KernelResult<SecureDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
                $"Secure-domain local publication failed before a handle was issued: {exception.Message}");
        }

        KernelResult<SecureDomainAuthoritySet>? localFailure = null;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            var publication = PlatformAuthority.ValidateSecureDomainLocalCommit(created.Value!,
                current.IsSuccess ? PlatformIdentity(current.Value!) : PlatformIdentity(process.Value!));
            if (!publication.IsSuccess)
                return KernelResult<SecureDomainAuthoritySet>.Fail(publication.Error, publication.Message!);
            var effect = current.IsSuccess ? EnsureProcessAcceptsNewEffects(current.Value!) :
                KernelResult.Fail(current.Error, current.Message!);
            if (!effect.IsSuccess)
                localFailure = KernelResult<SecureDomainAuthoritySet>.Fail(effect.Error, effect.Message!);
            else
            {
                var handle = new SecureDomainHandle(new(_nextSecureDomainId++), new(1));
                var mintedIds = new List<CapabilityId>(4);
                var specifications = new (ResourceKind Kind, string Resource, CapabilityRights Rights)[]
                {
                    (ResourceKind.SecureCompute, SecureComputeResourceIds.Domain(handle.DomainId), CapabilityRights.Configure),
                    (ResourceKind.SecureCompute, SecureComputeResourceIds.Memory(handle.DomainId), CapabilityRights.Map),
                    (ResourceKind.SecureCompute, SecureComputeResourceIds.Domain(handle.DomainId), CapabilityRights.Execute),
                    (ResourceKind.Evidence, SecureComputeResourceIds.Evidence(handle.DomainId), CapabilityRights.Read),
                };
                foreach (var specification in specifications)
                {
                    var minted = MintCapability(current.Value!.DomainId, owner, specification.Kind,
                        specification.Resource, specification.Rights);
                    if (!minted.IsSuccess)
                    {
                        localFailure = KernelResult<SecureDomainAuthoritySet>.Fail(minted.Error, minted.Message!);
                        break;
                    }
                    mintedIds.Add(minted.Value!.CapabilityId);
                }
                if (localFailure is not null)
                {
                    foreach (var mintedId in mintedIds) _ = RevokeCapability(mintedId);
                }
                else
                {
                    var record = new SecureRecord(handle, owner, created.Value!);
                    record.Capabilities.AddRange(mintedIds);
                    _secureDomainRecords.Add(handle.DomainId, record);
                    RecordTrace(owner, TraceEventKind.SecureDomainLifecycle, null, "secure-domain",
                        handle.DomainId.Value.ToString(), record.State.ToString(), "created");
                    return KernelResult<SecureDomainAuthoritySet>.Ok(new(handle,
                        mintedIds[0], mintedIds[1], mintedIds[2], mintedIds[3]));
                }
            }
        }

        var cleanup = PlatformAuthority.RevokeSecureDomain(created.Value!);
        return cleanup.IsSuccess ? localFailure!.Value : KernelResult<SecureDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
            "Secure-domain local publication failed and exact provider closure is unavailable; parent remains pinned.");
    }

    public KernelResult BindSecureRegion(ProcessHandle owner, SecureDomainHandle domain, CapabilityId memoryCapability, PlatformRegionMapping mapping, PlatformSecureRegionClass regionClass)
    {
        using var composedUse = PinComposedDomain(owner, secureDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveSecure(owner, domain, memoryCapability, SecureComputeResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        SecureDomainState expected;
        lock (_platformMemoryUseGate)
        {
            if (record.Value!.State is not (SecureDomainState.Created or SecureDomainState.Configured or SecureDomainState.Parked))
                return KernelResult.Fail(KernelError.InvalidTransition, "Secure regions can only be bound while the domain is not running or draining.");
            if (record.Value.TransitionInFlight || record.Value.ClosureInFlight || record.Value.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain lifecycle or region mutation is in flight.");
            expected = record.Value.State;
            record.Value.RegionMutationInFlight = true;
        }
        try
        {
            var result = PlatformAuthority.BindSecureRegion(record.Value.Binding, mapping, regionClass);
            if (result.IsSuccess) BeforeSecureRegionLocalCommit?.Invoke();
            lock (_platformMemoryUseGate)
            {
                if (!result.IsSuccess)
                {
                    if (!PlatformAuthority.ValidateSecureDomainLocalCommit(record.Value.Binding,
                        record.Value.Binding.Parent.Subject).IsSuccess)
                        record.Value.State = SecureDomainState.Quarantined;
                    return result;
                }
                if (record.Value.State != expected)
                {
                    record.Value.State = SecureDomainState.Quarantined;
                    PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value.Binding);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Secure-domain state changed before region binding publication.");
                }
                var live = PlatformAuthority.ValidateSecureDomainLocalCommit(record.Value.Binding,
                    record.Value.Binding.Parent.Subject);
                if (!live.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return live; }
                record.Value.Regions.Add(mapping.MappingId, mapping);
                record.Value.State = SecureDomainState.Configured;
                record.Value.ProtectionGeneration = checked(record.Value.ProtectionGeneration + 1);
                return KernelResult.Ok();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_platformMemoryUseGate)
            {
                record.Value!.State = SecureDomainState.Quarantined;
                PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value.Binding);
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-region bind may have taken effect before local publication: {exception.Message}");
        }
        finally
        {
            lock (_platformMemoryUseGate) record.Value!.RegionMutationInFlight = false;
        }
    }

    public KernelResult CloseSecureRegion(ProcessHandle owner, SecureDomainHandle domain, CapabilityId memoryCapability, PlatformRegionMapping mapping)
    {
        using var composedUse = PinComposedDomain(owner, secureDomain: domain, allowDraining: true);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveSecure(owner, domain, memoryCapability, SecureComputeResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        lock (_platformMemoryUseGate)
        {
            if (!record.Value!.Regions.TryGetValue(mapping.MappingId, out var exact) || exact != mapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Secure-region mapping is absent or stale.");
            if (record.Value.TransitionInFlight || record.Value.ClosureInFlight || record.Value.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain lifecycle or region mutation is in flight.");
            record.Value.RegionMutationInFlight = true;
        }
        try
        {
            var result = PlatformAuthority.UnbindSecureRegion(record.Value.Binding, mapping);
            if (result.IsSuccess) BeforeSecureRegionLocalCommit?.Invoke();
            lock (_platformMemoryUseGate)
            {
                if (!result.IsSuccess) { record.Value!.State = SecureDomainState.Quarantined; return result; }
                var live = PlatformAuthority.ValidateSecureDomainLocalCommit(record.Value!.Binding,
                    record.Value.Binding.Parent.Subject);
                if (!live.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return live; }
                record.Value.Regions.Remove(mapping.MappingId);
                record.Value.ProtectionGeneration = checked(record.Value.ProtectionGeneration + 1);
                return KernelResult.Ok();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_platformMemoryUseGate)
            {
                record.Value!.State = SecureDomainState.Quarantined;
                PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value.Binding);
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-region unbind may have taken effect before local closure publication: {exception.Message}");
        }
        finally
        {
            lock (_platformMemoryUseGate) record.Value!.RegionMutationInFlight = false;
        }
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
        using var composedUse = PinComposedDomain(owner, secureDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveSecure(owner, domain, executeCapability, SecureComputeResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        SecureDomainState expected;
        lock (_platformMemoryUseGate)
        {
            if (record.Value!.State is SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-domain state is quarantined or closed.");
            if (record.Value.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition is already in flight.");
            if (record.Value.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain closure is already in flight.");
            if (record.Value.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-region mutation is already in flight.");
            expected = transition switch { PlatformSecureDomainTransition.Start => SecureDomainState.Configured, PlatformSecureDomainTransition.Park => SecureDomainState.Running, PlatformSecureDomainTransition.Resume => SecureDomainState.Parked, _ => record.Value.State };
            if (record.Value.State != expected) return KernelResult.Fail(KernelError.InvalidTransition, "Secure-domain transition is invalid.");
            record.Value.TransitionInFlight = true;
        }

        KernelResult result;
        try
        {
            result = PlatformAuthority.TransitionSecureDomain(record.Value.Binding, transition);
            if (result.IsSuccess) BeforeSecureDomainTransitionLocalCommit?.Invoke();
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value!.Binding);
            result = KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-domain transition reached a local callback fault after possible provider effect: {exception.Message}");
        }
        lock (_platformMemoryUseGate)
        {
            record.Value!.TransitionInFlight = false;
            if (!result.IsSuccess)
            {
                record.Value.State = SecureDomainState.Quarantined;
                return result;
            }
            if (record.Value.State != expected)
            {
                record.Value.State = SecureDomainState.Quarantined;
                PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value.Binding);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Secure-domain state changed before transition publication.");
            }
            var live = PlatformAuthority.ValidateSecureDomainLocalCommit(record.Value.Binding,
                record.Value.Binding.Parent.Subject);
            if (!live.IsSuccess)
            {
                record.Value.State = SecureDomainState.Quarantined;
                return live;
            }
            record.Value.State = transition switch { PlatformSecureDomainTransition.Start => SecureDomainState.Running, PlatformSecureDomainTransition.Park => SecureDomainState.Parked, PlatformSecureDomainTransition.Resume => SecureDomainState.Running, _ => SecureDomainState.Draining };
            RecordTrace(owner, TraceEventKind.SecureDomainLifecycle, null, "secure-domain",
                domain.DomainId.Value.ToString(), record.Value.State.ToString(), transition.ToString());
            return KernelResult.Ok();
        }
    }

    public KernelResult DestroySecureDomain(ProcessHandle owner, SecureDomainHandle domain, CapabilityId configureCapability)
    {
        var record = ResolveSecure(owner, domain, configureCapability, SecureComputeResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        lock (_platformMemoryUseGate)
        {
            if (record.Value!.TransitionInFlight || record.Value.ClosureInFlight || record.Value.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Secure-domain transition or closure is already in flight.");
            record.Value.ClosureInFlight = true;
        }
        try
        {
            lock (_secureExecutionGate)
                if (record.Value!.State is not (SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed))
                    record.Value.State = SecureDomainState.Draining;
            var secureGuestDrain = CloseSecureGuestRegionsForSecureDomain(owner, domain);
            if (!secureGuestDrain.IsSuccess) return secureGuestDrain;
            var composedDrain = DrainSecureExecutions(owner, secureDomain: domain);
            if (!composedDrain.IsSuccess) return composedDrain;
            if (record.Value!.State is SecureDomainState.Quarantined or SecureDomainState.Faulted)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Quarantined secure-domain authority remains pinned.");
            record.Value.State = SecureDomainState.Draining;
            if (record.Value.Regions.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure regions must close before secure-domain destruction.");
            var drain = PlatformAuthority.TransitionSecureDomain(record.Value.Binding, PlatformSecureDomainTransition.BeginDrain);
            if (!drain.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return drain; }
            var close = PlatformAuthority.RevokeSecureDomain(record.Value.Binding);
            if (!close.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return close; }
            foreach (var capability in record.Value.Capabilities) _ = RevokeCapability(capability);
            record.Value.State = SecureDomainState.Closed;
            _secureDomainRecords.Remove(domain.DomainId);
            return KernelResult.Ok();
        }
        finally
        {
            lock (_platformMemoryUseGate) record.Value!.ClosureInFlight = false;
        }
    }

    private KernelResult CloseSecureDomainsForProcess(ProcessHandle owner)
    {
        foreach (var record in _secureDomainRecords.Values.Where(record => record.Owner == owner && record.State != SecureDomainState.Closed).OrderBy(record => record.Handle.DomainId.Value).ToArray())
        {
            if (record.TransitionInFlight || record.ClosureInFlight || record.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Secure-domain transition or closure must settle before process teardown.");
            var secureGuestDrain = CloseSecureGuestRegionsForSecureDomain(owner, record.Handle);
            if (!secureGuestDrain.IsSuccess) return secureGuestDrain;
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
        if (rights != CapabilityRights.Configure && rights != CapabilityRights.Read)
        {
            var composed = RevalidateComposedDomain(owner, secureDomain: domain);
            if (!composed.IsSuccess) return KernelResult<SecureRecord>.Fail(composed.Error, composed.Message!);
        }
        return KernelResult<SecureRecord>.Ok(record);
    }
}
