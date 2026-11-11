using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private sealed class SecureRecord
    {
        private readonly object _sourceGate;
        private SecureDomainState _state = SecureDomainState.Created;
        private ulong _policyGeneration = 1, _protectionGeneration = 1;
        private long _sourceRevision;
        private bool _sourceRevisionExhausted;
        public SecureRecord(SecureDomainHandle handle, ProcessHandle owner, PlatformAuthorityBridge.SecureDomainBinding binding, object sourceGate)
        { OriginalOwner = this; Handle = handle; Owner = owner; Binding = binding; _sourceGate = sourceGate; }
        public SecureRecord OriginalOwner { get; }
        public SecureDomainHandle Handle { get; }
        public ProcessHandle Owner { get; }
        public PlatformAuthorityBridge.SecureDomainBinding Binding { get; }
        private void AdvanceRevision()
        { if (_sourceRevision == long.MaxValue) _sourceRevisionExhausted = true; else _sourceRevision++; }
        public SecureDomainState State { get { lock (_sourceGate) return _state; } set { lock (_sourceGate) { AdvanceRevision(); _state = value; } } }
        public ulong PolicyGeneration { get { lock (_sourceGate) return _policyGeneration; } set { lock (_sourceGate) { AdvanceRevision(); _policyGeneration = value; } } }
        public ulong ProtectionGeneration { get { lock (_sourceGate) return _protectionGeneration; } set { lock (_sourceGate) { AdvanceRevision(); _protectionGeneration = value; } } }
        public long SourceRevision { get { lock (_sourceGate) return _sourceRevision; } }
        public bool SourceRevisionExhausted { get { lock (_sourceGate) return _sourceRevisionExhausted; } }
        public void AdvanceProtectionAfterClosure()
        {
            lock (_sourceGate)
            {
                AdvanceRevision();
                if (_protectionGeneration == ulong.MaxValue) _sourceRevisionExhausted = true;
                else _protectionGeneration++;
            }
        }
        public List<CapabilityId> Capabilities { get; } = [];
        public Dictionary<PlatformRegionMappingId, PlatformRegionMapping> Regions { get; } = [];
        public Dictionary<PlatformRegionMappingId, object> RegionBindingSources { get; } = [];
        public bool TransitionInFlight { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool RegionMutationInFlight { get; set; }
    }
    private readonly Dictionary<SecureDomainId, SecureRecord> _secureDomainRecords = [];
    internal sealed record SecureEffectSource(object OriginalOwner, long Revision);
    private bool HasOriginalSecureRecord(SecureRecord record)
    {
        lock (_platformMemoryUseGate)
            return ReferenceEquals(record.OriginalOwner, record) && _secureDomainRecords.TryGetValue(record.Handle.DomainId, out SecureRecord? current) && ReferenceEquals(current, record);
    }
    private KernelResult<SecureEffectSource> CaptureSecureEffectSource(SecureRecord record)
    {
        lock (_platformMemoryUseGate)
            return HasOriginalSecureRecord(record) && !record.SourceRevisionExhausted
                ? KernelResult<SecureEffectSource>.Ok(new(record, record.SourceRevision))
                : KernelResult<SecureEffectSource>.Fail(KernelError.StaleGeneration, "Original local secure owner/revision is unavailable.");
    }
    private KernelResult CommitSecureEffectSource(SecureEffectSource source, Func<KernelResult> commit)
    {
        lock (_platformMemoryUseGate)
            return source.OriginalOwner is SecureRecord record && HasOriginalSecureRecord(record) &&
                !record.SourceRevisionExhausted && record.SourceRevision == source.Revision
                ? commit() : KernelResult.Fail(KernelError.PlatformFaulted, "Captured local secure source changed.");
    }
    private ulong _nextSecureDomainId = 1;
    // Deterministic fault-injection seam; it never supplies authority or closure evidence.
    internal Action? BeforeSecureDomainLocalPublication { get; set; }
    // Deterministic fault-injection seam after provider transition, before local state commit.
    internal Action? BeforeSecureDomainTransitionLocalCommit { get; set; }
    // Deterministic fault-injection seam after provider region receipt, before local commit.
    internal Action? BeforeSecureRegionLocalCommit { get; set; }
    // Fault seam outside source/owner locks; cannot supply admission or closure.
    internal Action? BeforeSecureRegionAdmission { get; set; }
    // Fault seam outside owner locks after exact provider closure, before local completion.
    internal Action? BeforeSecureDomainClosureLocalCommit { get; set; }

    public KernelResult<SecureDomainAuthoritySet> CreateSecureDomain(ProcessHandle owner, CapabilityId createCapability, PlatformDomainBinding parent, SecureDomainProfile profile)
    {
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(owner, createCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult<SecureDomainAuthoritySet>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.SecureCompute || capability.Value.ResourceId != SecureComputeResourceIds.Create)
            return KernelResult<SecureDomainAuthoritySet>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize secure-domain creation.");
        KernelResult<SingProcess> process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<SecureDomainAuthoritySet>.Fail(process.Error, process.Message!);
        lock (_platformMemoryUseGate)
            if (_nextSecureDomainId == 0)
                return KernelResult<SecureDomainAuthoritySet>.Fail(KernelError.CapacityExhausted, "Secure handle identity space is exhausted before provider invocation.");
        SecureDomainHandle? reservedHandle = null;
        KernelResult Admit(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process.Value))
                    return KernelResult.Fail(KernelError.StaleHandle, "Secure creation subject changed.");
                KernelResult accepts = EnsureProcessAcceptsNewEffects(current.Value!);
                return accepts.IsSuccess ? commit() : accepts;
            }
        }
        KernelResult CommitSource(Func<KernelResult> commit) => CapabilityAuthority.CommitSecureDomainEffect(
            createCapability, process.Value!.DomainId, owner.Generation, SecureComputeResourceIds.Create, () =>
            {
                if (reservedHandle is null)
                {
                    if (_nextSecureDomainId == 0)
                        return KernelResult.Fail(KernelError.CapacityExhausted, "Secure handle identity space is exhausted before effect.");
                    reservedHandle = new(new(_nextSecureDomainId), new(1));
                    _nextSecureDomainId = _nextSecureDomainId == ulong.MaxValue ? 0 : _nextSecureDomainId + 1;
                }
                return commit();
            });
        KernelResult<PlatformAuthorityBridge.SecureDomainBinding> created = PlatformAuthority.CreateSecureDomain(parent, PlatformIdentity(process.Value!), profile, Admit, CommitSource);
        if (!created.IsSuccess) return KernelResult<SecureDomainAuthoritySet>.Fail(created.Error, created.Message!);
        try { BeforeSecureDomainLocalPublication?.Invoke(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            _ = PlatformAuthority.RevokeSecureDomain(created.Value!);
            return KernelResult<SecureDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
                $"Secure-domain local publication failed before a handle was issued: {exception.Message}");
        }

        KernelResult<Func<Func<KernelResult>, KernelResult>> preparedPublication = PlatformAuthority.PrepareSecureDomainPublication(created.Value!, PlatformIdentity(process.Value!));
        if (!preparedPublication.IsSuccess)
            return KernelResult<SecureDomainAuthoritySet>.Fail(preparedPublication.Error, preparedPublication.Message!);
        KernelResult<SecureDomainAuthoritySet>? localFailure = null;
        SecureDomainAuthoritySet? published = null;
        List<CapabilityDescriptorV1> mintObservations = [];
        List<CapabilityId> rollbackObservations = [];
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> current = Processes.Resolve(owner);
            KernelResult publication = PlatformAuthority.ValidateSecureDomainLocalCommit(created.Value!,
                current.IsSuccess ? PlatformIdentity(current.Value!) : PlatformIdentity(process.Value!));
            if (!publication.IsSuccess)
                return KernelResult<SecureDomainAuthoritySet>.Fail(publication.Error, publication.Message!);
            KernelResult effect = current.IsSuccess ? EnsureProcessAcceptsNewEffects(current.Value!) :
                KernelResult.Fail(current.Error, current.Message!);
            if (!effect.IsSuccess)
                localFailure = KernelResult<SecureDomainAuthoritySet>.Fail(effect.Error, effect.Message!);
            else
            {
                SecureDomainHandle handle = reservedHandle!.Value;
                List<CapabilityId> mintedIds = new List<CapabilityId>(4);
                (ResourceKind Kind, string Resource, CapabilityRights Rights)[] specifications = new (ResourceKind Kind, string Resource, CapabilityRights Rights)[]
                {
                    (ResourceKind.SecureCompute, SecureComputeResourceIds.Domain(handle.DomainId), CapabilityRights.Configure),
                    (ResourceKind.SecureCompute, SecureComputeResourceIds.Memory(handle.DomainId), CapabilityRights.Map),
                    (ResourceKind.SecureCompute, SecureComputeResourceIds.Domain(handle.DomainId), CapabilityRights.Execute),
                    (ResourceKind.Evidence, SecureComputeResourceIds.Evidence(handle.DomainId), CapabilityRights.Read),
                };
                foreach ((ResourceKind Kind, string Resource, CapabilityRights Rights) specification in specifications)
                {
                    KernelResult<CapabilityDescriptorV1> minted = MintCapabilityCore(current.Value!.DomainId, owner, specification.Kind,
                        specification.Resource, specification.Rights, mintObservations: mintObservations);
                    if (!minted.IsSuccess)
                    {
                        localFailure = KernelResult<SecureDomainAuthoritySet>.Fail(minted.Error, minted.Message!);
                        break;
                    }
                    mintedIds.Add(minted.Value!.CapabilityId);
                }
                if (localFailure is not null)
                {
                    // These IDs have not been published or bound to consumers.
                    // Revoke permission before any observation can reenter.
                    foreach (CapabilityId mintedId in mintedIds)
                    {
                        if (CapabilityAuthority.Revoke(mintedId).IsSuccess)
                        {
                            current.Value!.RemoveCapability(mintedId);
                            rollbackObservations.Add(mintedId);
                        }
                    }
                }
                else
                {
                    KernelResult committed = preparedPublication.Value!( () => CommitSource(() =>
                    {
                        SecureRecord record = new SecureRecord(handle, owner, created.Value!, _platformMemoryUseGate);
                    record.Capabilities.AddRange(mintedIds);
                    _secureDomainRecords.Add(handle.DomainId, record);
                    published = new(handle, mintedIds[0], mintedIds[1], mintedIds[2], mintedIds[3]);
                    return KernelResult.Ok();
                    }));
                    if (!committed.IsSuccess)
                    {
                        localFailure = KernelResult<SecureDomainAuthoritySet>.Fail(committed.Error, committed.Message!);
                        foreach (CapabilityId mintedId in mintedIds)
                            if (CapabilityAuthority.Revoke(mintedId).IsSuccess)
                            {
                                current.Value!.RemoveCapability(mintedId);
                                rollbackObservations.Add(mintedId);
                            }
                    }
                }
            }
        }

        KernelResult? cleanup = published is null
            ? PlatformAuthority.RevokeSecureDomain(created.Value!) : null;
        foreach (CapabilityDescriptorV1 minted in mintObservations)
            RecordTrace(owner, TraceEventKind.CapabilityMinted, null, "capability",
                minted.CapabilityId.Value.ToString(), "active", "minted");
        foreach (CapabilityId revoked in rollbackObservations)
            RecordTrace(owner, TraceEventKind.CapabilityRevoked, null, "capability",
                revoked.Value.ToString(), "revoked", "revoked");
        if (published is { } authoritySet)
        {
            RecordTrace(owner, TraceEventKind.SecureDomainLifecycle, null, "secure-domain",
                authoritySet.Domain.DomainId.Value.ToString(), SecureDomainState.Created.ToString(), "created");
            return KernelResult<SecureDomainAuthoritySet>.Ok(authoritySet);
        }
        return cleanup!.Value.IsSuccess ? localFailure!.Value : KernelResult<SecureDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
            "Secure-domain local publication failed and exact provider closure is unavailable; parent remains pinned.");
    }

    public KernelResult BindSecureRegion(ProcessHandle owner, SecureDomainHandle domain, CapabilityId memoryCapability, PlatformRegionMapping mapping, PlatformSecureRegionClass regionClass)
    {
        using ComposedUse composedUse = PinComposedDomain(owner, secureDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        KernelResult<SecureRecord> record = ResolveSecure(owner, domain, memoryCapability, SecureComputeResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        SecureDomainState expected = record.Value!.State;
        KernelResult<SecureEffectSource> source = CaptureSecureEffectSource(record.Value);
        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
        KernelResult CommitSource(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> process = Processes.Resolve(owner);
                if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                KernelResult accepts = EnsureProcessAcceptsNewEffects(process.Value!);
                if (!accepts.IsSuccess) return accepts;
                if (!_secureDomainRecords.TryGetValue(domain.DomainId, out SecureRecord? current) ||
                    !ReferenceEquals(current, record.Value) || current.Handle != domain || current.Owner != owner)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure-region source changed.");
                return CommitSecureEffectSource(source.Value!, () => CommitComposedDomainSources(owner, null, () =>
                    CapabilityAuthority.CommitSecureDomainEffect(memoryCapability, process.Value!.DomainId,
                        owner.Generation, CapabilityRights.Map, SecureComputeResourceIds.Memory(domain.DomainId), commit), domain));
            }
        }
        try { BeforeSecureRegionAdmission?.Invoke(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, $"Secure-region admission failed before provider bind: {exception.Message}"); }
        lock (_platformMemoryUseGate)
        {
            KernelResult admission = CommitSource(() =>
            {
            if (record.Value!.State is not (SecureDomainState.Created or SecureDomainState.Configured or SecureDomainState.Parked))
                return KernelResult.Fail(KernelError.InvalidTransition, "Secure regions can only be bound while the domain is not running or draining.");
            if (record.Value.TransitionInFlight || record.Value.ClosureInFlight || record.Value.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain lifecycle or region mutation is in flight.");
            expected = record.Value.State;
            record.Value.RegionMutationInFlight = true;
            return KernelResult.Ok();
            });
            if (!admission.IsSuccess) return admission;
        }
        try
        {
            KernelResult result = PlatformAuthority.BindSecureRegion(record.Value.Binding, mapping, regionClass, CommitSource);
            KernelResult<object> bindingSource = result.IsSuccess ? PlatformAuthority.CaptureOriginalSecureRegionBinding(record.Value.Binding, mapping) : default;
            if (result.IsSuccess && !bindingSource.IsSuccess) return KernelResult.Fail(bindingSource.Error, bindingSource.Message!);
            if (result.IsSuccess) BeforeSecureRegionLocalCommit?.Invoke();
            KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = result.IsSuccess ? PlatformAuthority.PrepareSecureDomainPublication(
                record.Value.Binding, record.Value.Binding.Parent.Subject, mapping) : default;
            lock (_platformMemoryUseGate)
            {
                if (!result.IsSuccess)
                {
                    if (!PlatformAuthority.ValidateSecureDomainLocalCommit(record.Value.Binding,
                        record.Value.Binding.Parent.Subject).IsSuccess)
                        record.Value.State = SecureDomainState.Quarantined;
                    return result;
                }
                KernelResult publication = prepared.IsSuccess ? PlatformAuthority.CommitOriginalSecureRegionBinding(bindingSource.Value!, () => CommitSource(() => prepared.Value!(() =>
                {
                if (record.Value.State != expected)
                {
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Secure-domain state changed before region binding publication.");
                }
                    KernelResult live = PlatformAuthority.ValidateSecureDomainLocalCommit(record.Value.Binding,
                    record.Value.Binding.Parent.Subject);
                if (!live.IsSuccess) return live;
                if (record.Value.ProtectionGeneration == ulong.MaxValue || record.Value.Regions.ContainsKey(mapping.MappingId))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-region publication cannot advance the exact local generation.");
                record.Value.Regions.Add(mapping.MappingId, mapping);
                record.Value.RegionBindingSources.Add(mapping.MappingId, bindingSource.Value!);
                record.Value.State = SecureDomainState.Configured;
                record.Value.ProtectionGeneration = checked(record.Value.ProtectionGeneration + 1);
                return KernelResult.Ok();
                }))) : KernelResult.Fail(prepared.Error, prepared.Message!);
                if (!publication.IsSuccess)
                {
                    // Bridge retains the exact provider binding; refusal does not erase its effect.
                    record.Value.State = SecureDomainState.Quarantined;
                    PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value.Binding);
                }
                return publication;
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
        using ComposedUse composedUse = PinComposedDomain(owner, secureDomain: domain, allowDraining: true);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        KernelResult<SecureRecord> record = ResolveSecure(owner, domain, memoryCapability, SecureComputeResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        lock (_platformMemoryUseGate)
        {
            if (!record.Value!.Regions.TryGetValue(mapping.MappingId, out PlatformRegionMapping exact) || exact != mapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Secure-region mapping is absent or stale.");
            if (record.Value.TransitionInFlight || record.Value.ClosureInFlight || record.Value.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain lifecycle or region mutation is in flight.");
            record.Value.RegionMutationInFlight = true;
        }
        try
        {
            if (!record.Value.RegionBindingSources.TryGetValue(mapping.MappingId, out object? bindingSource))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original kernel secure-region binding source unavailable.");
            KernelResult result = PlatformAuthority.UnbindSecureRegion(record.Value.Binding, mapping,
                commit => CompleteSecureRegionClosure(record.Value, mapping, commit), bindingSource);
            if (!result.IsSuccess) record.Value.State = SecureDomainState.Quarantined;
            return result;
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
        KernelResult<SecureRecord> record = ResolveSecure(owner, domain, evidenceCapability, SecureComputeResourceIds.Evidence(domain.DomainId), CapabilityRights.Read);
        if (!record.IsSuccess) return KernelResult<EvidenceRecord>.Fail(record.Error, record.Message!);
        if (record.Value!.State is SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed)
            return KernelResult<EvidenceRecord>.Fail(KernelError.PlatformFaulted, "Secure-domain evidence context is no longer live.");
        KernelResult<SingProcess> process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<EvidenceRecord>.Fail(process.Error, process.Message!);
        if (parent != record.Value!.Binding.Parent)
            return KernelResult<EvidenceRecord>.Fail(KernelError.WrongPlatformDomain, "Evidence parent does not match the exact secure-domain parent.");
        return PlatformAuthority.ReadEvidence(parent, PlatformIdentity(process.Value!), null, identity, visibility, minimumFreshness,
            new EvidenceDomainIdentity(domain.DomainId.Value, domain.Generation.Value));
    }

    public KernelResult TransitionSecureDomain(ProcessHandle owner, SecureDomainHandle domain, CapabilityId executeCapability, PlatformSecureDomainTransition transition)
    {
        using ComposedUse composedUse = PinComposedDomain(owner, secureDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        KernelResult<SecureRecord> record = ResolveSecure(owner, domain, executeCapability, SecureComputeResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        KernelResult<SecureEffectSource> source = CaptureSecureEffectSource(record.Value!);
        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
        bool localSourceChanged = false;
        KernelResult CommitSource(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> process = Processes.Resolve(owner);
                if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                KernelResult accepts = EnsureProcessAcceptsNewEffects(process.Value!);
                if (!accepts.IsSuccess) return accepts;
                if (!_secureDomainRecords.TryGetValue(domain.DomainId, out SecureRecord? current) ||
                    !ReferenceEquals(current, record.Value) || current.Handle != domain || current.Owner != owner)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure transition source changed.");
                localSourceChanged |= record.Value!.SourceRevisionExhausted || record.Value.SourceRevision != source.Value!.Revision;
                return CommitSecureEffectSource(source.Value!, () => CommitComposedDomainSources(owner, null, () =>
                    CapabilityAuthority.CommitSecureDomainEffect(executeCapability, process.Value!.DomainId,
                        owner.Generation, CapabilityRights.Execute, SecureComputeResourceIds.Domain(domain.DomainId), commit), domain));
            }
        }
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
        KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = default;
        try
        {
            result = PlatformAuthority.TransitionSecureDomain(record.Value.Binding, transition, CommitSource);
            if (result.IsSuccess) BeforeSecureDomainTransitionLocalCommit?.Invoke();
            if (result.IsSuccess)
            {
                prepared = PlatformAuthority.PrepareSecureDomainPublication(record.Value.Binding,
                    record.Value.Binding.Parent.Subject);
                if (!prepared.IsSuccess) result = KernelResult.Fail(prepared.Error, prepared.Message!);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value!.Binding);
            result = KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-domain transition reached a local callback fault after possible provider effect: {exception.Message}");
        }
        SecureDomainState committedState;
        lock (_platformMemoryUseGate)
        {
            record.Value!.TransitionInFlight = false;
            if (!result.IsSuccess)
            {
                record.Value.State = SecureDomainState.Quarantined;
                return result;
            }
            KernelResult publication = CommitSource(() => prepared.Value!(() =>
            {
                if (record.Value.State != expected)
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Secure-domain state changed before transition publication.");
                record.Value.State = transition switch { PlatformSecureDomainTransition.Start => SecureDomainState.Running, PlatformSecureDomainTransition.Park => SecureDomainState.Parked, PlatformSecureDomainTransition.Resume => SecureDomainState.Running, _ => SecureDomainState.Draining };
                return KernelResult.Ok();
            }));
            if (!publication.IsSuccess)
            {
                record.Value.State = SecureDomainState.Quarantined;
                PlatformAuthority.QuarantineSecureDomainLocalTransition(record.Value.Binding, localSourceChanged);
                return publication;
            }
            committedState = record.Value.State;
        }
        RecordTrace(owner, TraceEventKind.SecureDomainLifecycle, null, "secure-domain",
            domain.DomainId.Value.ToString(), committedState.ToString(), transition.ToString());
        return KernelResult.Ok();
    }

    public KernelResult DestroySecureDomain(ProcessHandle owner, SecureDomainHandle domain, CapabilityId configureCapability)
    {
        KernelResult<SecureRecord> record = ResolveSecure(owner, domain, configureCapability, SecureComputeResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
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
            lock (_platformMemoryUseGate)
                if (record.Value!.State is not (SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed))
                    record.Value.State = SecureDomainState.Draining;
            KernelResult secureGuestDrain = CloseSecureGuestRegionsForSecureDomain(owner, domain);
            if (!secureGuestDrain.IsSuccess) return secureGuestDrain;
            KernelResult composedDrain = DrainSecureExecutions(owner, secureDomain: domain);
            if (!composedDrain.IsSuccess) return composedDrain;
            bool originalRevoke = PlatformAuthority.HasOriginalSecureRevokeInvocation(record.Value!.Binding);
            KernelResult<bool> knownDrain = originalRevoke ? KernelResult<bool>.Ok(true) : PlatformAuthority.ConsumeKnownSecureDrainForCleanup(record.Value.Binding);
            if (!knownDrain.IsSuccess) return KernelResult.Fail(knownDrain.Error, knownDrain.Message!);
            if (!originalRevoke && !knownDrain.Value && record.Value.State is SecureDomainState.Quarantined or SecureDomainState.Faulted)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Quarantined secure-domain authority remains pinned.");
            if (!HasOriginalSecureRecord(record.Value)) return KernelResult.Fail(KernelError.PlatformFaulted, "Original local secure cleanup owner changed.");
            record.Value.State = SecureDomainState.Draining;
            if (record.Value.Regions.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure regions must close before secure-domain destruction.");
            KernelResult drain = knownDrain.Value ? KernelResult.Ok() : PlatformAuthority.TransitionSecureDomain(record.Value.Binding, PlatformSecureDomainTransition.BeginDrain);
            if (!drain.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return drain; }
            KernelResult close = PlatformAuthority.RevokeSecureDomain(record.Value.Binding,
                commit => CompleteSecureDomainClosure(record.Value, commit, originalRevoke));
            if (!close.IsSuccess) { record.Value.State = SecureDomainState.Quarantined; return close; }
            foreach (CapabilityId capability in record.Value.Capabilities) _ = RevokeCapability(capability);
            return KernelResult.Ok();
        }
        finally
        {
            lock (_platformMemoryUseGate) record.Value!.ClosureInFlight = false;
        }
    }

    private KernelResult CloseSecureDomainsForProcess(ProcessHandle owner)
    {
        foreach (SecureRecord? record in _secureDomainRecords.Values.Where(record => record.Owner == owner && record.State != SecureDomainState.Closed).OrderBy(record => record.Handle.DomainId.Value).ToArray())
        {
            if (!HasOriginalSecureRecord(record)) return KernelResult.Fail(KernelError.PlatformFaulted, "Original local secure cleanup owner changed.");
            if (record.TransitionInFlight || record.ClosureInFlight || record.RegionMutationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Secure-domain transition or closure must settle before process teardown.");
            KernelResult secureGuestDrain = CloseSecureGuestRegionsForSecureDomain(owner, record.Handle);
            if (!secureGuestDrain.IsSuccess) return secureGuestDrain;
            foreach (PlatformRegionMapping mapping in record.Regions.Values.OrderBy(mapping => mapping.MappingId.Value).ToArray())
            {
                if (!record.RegionBindingSources.TryGetValue(mapping.MappingId, out object? bindingSource))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original kernel secure-region binding source unavailable.");
                KernelResult unbind = PlatformAuthority.UnbindSecureRegion(record.Binding, mapping,
                    commit => CompleteSecureRegionClosure(record, mapping, commit), bindingSource);
                if (!unbind.IsSuccess) { record.State = SecureDomainState.Quarantined; return unbind; }
            }
            bool originalRevoke = PlatformAuthority.HasOriginalSecureRevokeInvocation(record.Binding);
            KernelResult<bool> knownDrain = originalRevoke ? KernelResult<bool>.Ok(true) : PlatformAuthority.ConsumeKnownSecureDrainForCleanup(record.Binding);
            if (!knownDrain.IsSuccess) return KernelResult.Fail(knownDrain.Error, knownDrain.Message!);
            if (!HasOriginalSecureRecord(record)) return KernelResult.Fail(KernelError.PlatformFaulted, "Original local secure cleanup owner changed.");
            record.State = SecureDomainState.Draining;
            KernelResult drain = knownDrain.Value ? KernelResult.Ok() : PlatformAuthority.TransitionSecureDomain(record.Binding, PlatformSecureDomainTransition.BeginDrain);
            if (!drain.IsSuccess) { record.State = SecureDomainState.Quarantined; return drain; }
            KernelResult close = PlatformAuthority.RevokeSecureDomain(record.Binding,
                commit => CompleteSecureDomainClosure(record, commit, originalRevoke));
            if (!close.IsSuccess) { record.State = SecureDomainState.Quarantined; return close; }
            foreach (CapabilityId capability in record.Capabilities) _ = RevokeCapability(capability);
        }
        return KernelResult.Ok();
    }

    private KernelResult CompleteSecureRegionClosure(SecureRecord record, PlatformRegionMapping mapping,
        Func<Func<KernelResult>, KernelResult> bridgeCommit)
    {
        if (!record.RegionBindingSources.TryGetValue(mapping.MappingId, out object? bindingSource))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original kernel binding source unavailable for completion.");
        BeforeSecureRegionLocalCommit?.Invoke();
        KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = PlatformAuthority.PrepareOriginalSecureRegionClosureCompletion(record.Binding, mapping, bindingSource);
        if (!prepared.IsSuccess) return KernelResult.Fail(KernelError.PlatformFaulted, prepared.Message!);
        lock (_platformMemoryUseGate)
            return prepared.Value!(() => bridgeCommit(() =>
            {
                if (!HasOriginalSecureRecord(record) ||
                    !record.Regions.TryGetValue(mapping.MappingId, out PlatformRegionMapping exact) || exact != mapping ||
                    !record.RegionBindingSources.TryGetValue(mapping.MappingId, out object? currentBindingSource) || !ReferenceEquals(currentBindingSource, bindingSource))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Exact secure-region local closure record changed before completion.");
                record.Regions.Remove(mapping.MappingId);
                record.RegionBindingSources.Remove(mapping.MappingId);
                record.AdvanceProtectionAfterClosure();
                return KernelResult.Ok();
            }));
    }

    private KernelResult CompleteSecureDomainClosure(SecureRecord record,
        Func<Func<KernelResult>, KernelResult> bridgeCommit, bool consumingOriginalClosure = false)
    {
        BeforeSecureDomainClosureLocalCommit?.Invoke();
        KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = consumingOriginalClosure ? PlatformAuthority.PrepareOriginalSecureDomainClosureCompletion(record.Binding) :
            PlatformAuthority.PrepareSecureDomainPublication(record.Binding, record.Binding.Parent.Subject);
        if (!prepared.IsSuccess) return KernelResult.Fail(prepared.Error, prepared.Message!);
        lock (_platformMemoryUseGate)
            return prepared.Value!(() => bridgeCommit(() =>
            {
                if (!HasOriginalSecureRecord(record) || !consumingOriginalClosure && record.State is SecureDomainState.Quarantined or SecureDomainState.Faulted)
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Exact secure-domain local closure record changed before completion.");
                record.State = SecureDomainState.Closed;
                _secureDomainRecords.Remove(record.Handle.DomainId);
                return KernelResult.Ok();
            }));
    }

    private void QuarantineSecureDomainsForPlatformBackendReset()
    {
        foreach (SecureRecord record in _secureDomainRecords.Values) record.State = SecureDomainState.Quarantined;
    }

    private KernelResult<SecureRecord> ResolveSecure(ProcessHandle owner, SecureDomainHandle domain, CapabilityId capabilityId, string resource, CapabilityRights rights)
    {
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(owner, capabilityId, rights);
        if (!capability.IsSuccess) return KernelResult<SecureRecord>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind is not (ResourceKind.SecureCompute or ResourceKind.Evidence) || capability.Value.ResourceId != resource)
            return KernelResult<SecureRecord>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize the exact secure-domain resource.");
        if (!_secureDomainRecords.TryGetValue(domain.DomainId, out SecureRecord? record)) return KernelResult<SecureRecord>.Fail(KernelError.PlatformBindingNotFound, "Secure domain was not found.");
        if (!HasOriginalSecureRecord(record) || record.Handle != domain || record.Owner != owner) return KernelResult<SecureRecord>.Fail(KernelError.StaleGeneration, "Secure domain is stale or owned by another process.");
        if (rights != CapabilityRights.Configure && rights != CapabilityRights.Read)
        {
            KernelResult composed = RevalidateComposedDomain(owner, secureDomain: domain);
            if (!composed.IsSuccess) return KernelResult<SecureRecord>.Fail(composed.Error, composed.Message!);
        }
        return KernelResult<SecureRecord>.Ok(record);
    }
}
