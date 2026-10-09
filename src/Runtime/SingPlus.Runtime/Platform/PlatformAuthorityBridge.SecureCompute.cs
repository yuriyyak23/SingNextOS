using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class SecureCreateInvocationFact(DomainRecord source, PlatformSecureDomainRequest request)
    {
        public DomainRecord Source { get; } = source;
        public PlatformSecureDomainRequest Request { get; } = request;
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease>? Observation { get; set; }
    }
    internal readonly record struct SecureDomainBindingId(ulong Value);
    internal readonly record struct SecureDomainBindingGeneration(ulong Value);
    internal readonly record struct SecureDomainBinding(SecureDomainBindingId BindingId, SecureDomainBindingGeneration Generation, PlatformDomainBinding Parent);
    // Receipt retention is cleanup evidence only; it cannot authorize mapping use or reclaim.
    private sealed record UnpublishedSecureRegionReceipt(PlatformRegionMapping Mapping,
        PlatformProviderRegionMappingLease AdmittedMapping, PlatformSecureRegionClass RequestedClass,
        PlatformBackendEpoch AdmissionEpoch, PlatformProviderSecureRegionBinding Receipt);
    private sealed record SecureDomainRecord(SecureDomainBinding Binding, PlatformProviderSecureDomainLease ProviderLease)
    {
        // Immutable admitted evidence projection; never permission or a replacement receipt.
        public string ProvenPropertiesKey { get; init; } = "";
        public Dictionary<PlatformRegionMappingId, SecureRegionBindInvocationFact> RegionBindInvocations { get; } = [];
        public Dictionary<PlatformRegionMappingId, PlatformAuthorityResult<PlatformProviderSecureRegionBinding>> RegionBindObservations { get; } = [];
        public Dictionary<PlatformRegionMappingId, MappingRecord> RegionMappingSources { get; } = [];
        public Dictionary<PlatformRegionMappingId, PlatformProviderSecureRegionBinding> Regions { get; } = [];
        public Dictionary<PlatformRegionMappingId, UnpublishedSecureRegionReceipt> UnpublishedRegionReceipts { get; } = [];
        public HashSet<PlatformRegionMappingId> QuarantinedRegions { get; } = [];
        public bool TerminalQuarantined { get; set; }
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease>? CreateObservation { get; set; }
        public DomainRecord? CreateSource { get; init; }
        public bool TransitionInFlight { get; set; }
        public SecureTransitionInvocationFact? LastTransitionInvocation { get; set; }
        public bool RevokeInFlight { get; set; }
        public bool ClosureConsumed { get; set; }
        public SecureDomainRevokeInvocationFact? RevokeInvocation { get; set; }
        public HashSet<PlatformRegionMappingId> RegionMutationsInFlight { get; } = [];
        public SecureDomainClosureReceiptFact? LastClosureReceipt { get; set; }
        public Dictionary<PlatformRegionMappingId, SecureRegionClosureReceiptFact> RegionClosureReceipts { get; } = [];
        public List<SecureRegionClosureInvocationFact> RegionClosureInvocations { get; } = [];
        public bool Quarantined => TerminalQuarantined || QuarantinedRegions.Count != 0;
    }
    private sealed class SecureRegionBindInvocationFact(SecureDomainRecord source, PlatformRegionMapping mapping,
        PlatformProviderRegionMappingLease admittedMapping, PlatformSecureRegionClass regionClass, PlatformBackendEpoch epoch)
    {
        public SecureDomainRecord Source { get; } = source;
        public PlatformRegionMapping Mapping { get; } = mapping;
        public PlatformProviderRegionMappingLease AdmittedMapping { get; } = admittedMapping;
        public PlatformSecureRegionClass RegionClass { get; } = regionClass;
        public PlatformBackendEpoch Epoch { get; } = epoch;
        public PlatformAuthorityResult<PlatformProviderSecureRegionBinding>? Observation { get; set; }
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>? ReturnedObjectCleanup { get; set; }
    }
    // Original invocation correlation; neither a permission nor closure proof.
    private sealed class SecureTransitionInvocationFact(SecureDomainRecord source,
        PlatformSecureDomainTransition transition, PlatformBackendEpoch epoch)
    {
        public SecureDomainRecord Source { get; } = source;
        public PlatformProviderSecureDomainLease AdmittedLease { get; } = source.ProviderLease;
        public PlatformSecureDomainTransition Transition { get; } = transition;
        public PlatformBackendEpoch Epoch { get; } = epoch;
        public PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>? Observation { get; set; }
    }
    // Cleanup correlation only; raw replies do not prove exact closure or continuity.
    private sealed record SecureDomainClosureReceiptFact(PlatformProviderSecureDomainLease AdmittedLease,
        PlatformBackendEpoch AdmissionEpoch, PlatformSecureDomainClosureReceipt Receipt);
    private sealed class SecureDomainRevokeInvocationFact(SecureDomainRecord source, PlatformBackendEpoch epoch)
    {
        public SecureDomainRecord Source { get; } = source;
        public PlatformProviderSecureDomainLease AdmittedLease { get; } = source.ProviderLease;
        public DomainRecord? ParentSource { get; } = source.CreateSource;
        public PlatformBackendEpoch Epoch { get; } = epoch;
        public PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>? Observation { get; set; }
    }
    private sealed record SecureRegionClosureReceiptFact(PlatformProviderSecureRegionBinding AdmittedBinding,
        PlatformBackendEpoch AdmissionEpoch, PlatformSecureRegionClosureReceipt Receipt);
    // Preserve original cleanup attempts across V1 retries; raw responses are not closure authority.
    private sealed class SecureRegionClosureInvocationFact(SecureDomainRecord source, MappingRecord mappingSource,
        PlatformProviderSecureRegionBinding admittedBinding, PlatformBackendEpoch epoch)
    {
        public SecureDomainRecord Source { get; } = source;
        public MappingRecord MappingSource { get; } = mappingSource;
        public DomainRecord? ParentSource { get; } = source.CreateSource;
        public PlatformProviderSecureRegionBinding AdmittedBinding { get; } = admittedBinding;
        public PlatformBackendEpoch Epoch { get; } = epoch;
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>? Observation { get; set; }
    }
    private readonly Dictionary<SecureDomainBindingId, SecureDomainRecord> _secureDomains = [];
    // Canonical lifetime index references the same authoritative record as the route.
    private readonly Dictionary<SecureDomainBindingId, SecureDomainRecord> _originalSecureOwners = [];
    private ulong _nextSecureDomainBindingId = 1;

    // Per returned invocation, cleanup correlation only; no local child binding or permission.
    private sealed record UnpublishedSecureCreateReceipt(PlatformDomainBinding Parent,
        PlatformProviderDomainLease AdmittedParent, PlatformBackendEpoch AdmissionEpoch,
        PlatformProviderSecureDomainLease Receipt);
    internal KernelResult<SecureDomainBinding> CreateSecureDomain(PlatformDomainBinding parent, PlatformDomainIdentity subject, SecureDomainProfile profile,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null)
    {
        if (_provider is not IPlatformSecureComputeProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.SecureDomains, PlatformSecureComputeContract.ContractVersion, PlatformFeatureAvailability.ProductionSecure))
            return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformUnsupported, "Every requested secure property requires a ProductionSecure provider claim.");
        if (profile.MaximumMemoryBytes <= 0 || profile.RequiredProperties is null || profile.RequiredProperties.Count == 0 ||
            profile.RequiredProperties.Any(property => !Enum.IsDefined(property)) || profile.RequiredProperties.Distinct().Count() != profile.RequiredProperties.Count)
            return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformDenied, "Secure-domain profile is malformed.");
        PlatformProviderDomainLease parentLease;
        DomainRecord originalParent;
        PlatformBackendEpoch backendEpoch;
        SecureDomainBinding reservedBinding;
        SecureCreateInvocationFact? invocation = null;
        lock (_secureDomainLifecycleGate)
        {
            var validParent = ValidateDomain(parent, subject);
            if (!validParent.IsSuccess) return KernelResult<SecureDomainBinding>.Fail(validParent.Error, validParent.Message!);
            var record = _domains[parent.BindingId];
            originalParent = record;
            if (record.ParentRevokeMayHaveEffect)
                return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                    "Parent-domain revoke is in flight or has ambiguous effect.");
            if (_nextSecureDomainBindingId == 0 || record.PendingSecureCreates == int.MaxValue ||
                _secureDomains.Count >= int.MaxValue - record.PendingSecureCreates ||
                _originalSecureOwners.Count >= int.MaxValue - record.PendingSecureCreates)
                return KernelResult<SecureDomainBinding>.Fail(KernelError.CapacityExhausted,
                    "Secure-domain identity or pending capacity is exhausted before provider invocation.");
            _secureDomains.EnsureCapacity(_secureDomains.Count + record.PendingSecureCreates + 1);
            _originalSecureOwners.EnsureCapacity(_originalSecureOwners.Count + record.PendingSecureCreates + 1);
            reservedBinding = new(new(_nextSecureDomainBindingId), new(1), parent);
            _nextSecureDomainBindingId = _nextSecureDomainBindingId == ulong.MaxValue ? 0 : _nextSecureDomainBindingId + 1;
            parentLease = record.ProviderLease;
            backendEpoch = BackendEpoch;
            record.PendingSecureCreates = checked(record.PendingSecureCreates + 1);
        }
        try
        {
            bool SourceCurrent()
            {
                var source = CurrentProviderIncarnation();
                return source.Value != 0 && source == originalParent.ProviderIncarnation &&
                    BackendEpoch == backendEpoch && !_backendEpochExhausted;
            }
            PlatformAuthorityResult<PlatformProviderSecureDomainLease> result;
            try
            {
                if (!SourceCurrent())
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted, "Secure-create source changed before invocation.");
                KernelResult FinalAdmission()
                {
                    lock (_secureDomainLifecycleGate)
                    {
                    if (!TryGetCanonicalRootRecord(parent.BindingId, out var exactParent) || !ReferenceEquals(exactParent, originalParent) ||
                        originalParent.AuthorityState != DomainAuthorityState.Active || BackendEpoch != backendEpoch)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original secure-create parent changed before effect.");
                    KernelResult Commit()
                    {
                        invocation = new(originalParent, new(parentLease, profile));
                        originalParent.SecureCreateInvocations.Add(invocation);
                        return KernelResult.Ok();
                    }
                    return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
                    }
                }
                var admission = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
                if (!admission.IsSuccess) return KernelResult<SecureDomainBinding>.Fail(admission.Error, admission.Message!);
                result = provider.CreateSecureDomain(invocation!.Request);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Source observation can fail before the effect-capable invocation.
                if (invocation is not null) PinUnresolvedSecureCreate(originalParent);
                return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                    invocation is null ? $"Secure-create source observation failed before invocation: {exception.Message}" :
                    $"Secure-domain creation may have taken effect without a lease: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                invocation!.Observation = result;
            }
            bool sourceCurrent;
            try { sourceCurrent = SourceCurrent(); }
            catch (Exception exception) when (exception is not StackOverflowException) { sourceCurrent = false; }
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || BackendEpoch != backendEpoch || !TryGetCanonicalRootRecord(parent.BindingId, out var currentParent) ||
                    !ReferenceEquals(currentParent, originalParent) || originalParent.AuthorityState != DomainAuthorityState.Active)
                {
                    if (result.IsSuccess)
                        originalParent.UnpublishedSecureCreates.Add(new(parent, parentLease, backendEpoch, result.Value));
                    PinUnresolvedSecureCreate(originalParent);
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                        "Backend reset during secure-domain creation leaves the parent pinned.");
                }
                if (!result.IsSuccess)
                {
                    PinUnresolvedSecureCreate(originalParent);
                    return FromProviderFailure<SecureDomainBinding>(result.Status, result.Message);
                }
            }
            var lease = result.Value;
            bool malformed;
            string provenPropertiesKey;
            try
            {
                // Provider collections can execute callbacks. Materialize and validate
                // the same evidence outside owner locks, before fresh publication checks.
                var provenProperties = lease.ProvenProperties?.ToArray();
                malformed = lease.DomainId.Value == 0 || lease.Generation.Value == 0 || lease.Parent != parentLease ||
                    provenProperties is null || provenProperties.Any(property => !Enum.IsDefined(property)) ||
                    profile.RequiredProperties.Any(property => !provenProperties.Contains(property));
                provenPropertiesKey = malformed ? "" : string.Join(",", provenProperties!.OrderBy(property => property));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate)
                {
                    originalParent.UnpublishedSecureCreates.Add(new(parent, parentLease, backendEpoch, lease));
                    PinUnresolvedSecureCreate(originalParent);
                }
                return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                    $"Returned secure-create lease validation failed; known receipt remains pinned: {exception.Message}");
            }
            if (malformed)
            {
                // Returned-object compensation cannot close the original malformed invocation.
                PinUnresolvedSecureCreate(originalParent);
                var cleanupClosed = false;
                try
                {
                    var cleanupSource = SourceCurrent();
                    lock (_secureDomainLifecycleGate)
                        cleanupSource &= TryGetCanonicalRootRecord(parent.BindingId, out var cleanupOwner) &&
                            ReferenceEquals(cleanupOwner, originalParent) && !originalParent.ParentRevokeMayHaveEffect;
                    if (!cleanupSource)
                        return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted, "Malformed returned secure lease retained; cleanup source changed.");
                    var cleanup = provider.RevokeSecureDomain(lease);
                    cleanupClosed = IsExactClosure(cleanup, lease);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // A failed cleanup cannot prove that the malformed child was closed.
                }
                lock (_secureDomainLifecycleGate)
                {
                    var exactlyClosed = cleanupClosed && BackendEpoch == backendEpoch &&
                        TryGetCanonicalRootRecord(parent.BindingId, out var cleanupParent) && ReferenceEquals(cleanupParent, originalParent) &&
                        originalParent.AuthorityState == DomainAuthorityState.Active;
                    if (!exactlyClosed)
                    {
                        var quarantined = reservedBinding;
                        var retained = new SecureDomainRecord(quarantined, lease) { TerminalQuarantined = true, CreateSource = originalParent };
                        _originalSecureOwners.Add(quarantined.BindingId, retained);
                        _secureDomains.Add(quarantined.BindingId, retained);
                    }
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                        exactlyClosed ? "Provider did not prove every requested secure property." : "Malformed secure-domain admission could not be closed and remains quarantined.");
                }
            }
            try { sourceCurrent = SourceCurrent(); }
            catch (Exception exception) when (exception is not StackOverflowException) { sourceCurrent = false; }
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || BackendEpoch != backendEpoch || !TryGetCanonicalRootRecord(parent.BindingId, out var publicationParent) ||
                    !ReferenceEquals(publicationParent, originalParent) || originalParent.AuthorityState != DomainAuthorityState.Active)
                {
                    originalParent.UnpublishedSecureCreates.Add(new(parent, parentLease, backendEpoch, lease));
                    PinUnresolvedSecureCreate(originalParent);
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                        "Parent or backend changed before secure-domain publication; possible effect remains pinned.");
                }
                var binding = reservedBinding;
                var published = new SecureDomainRecord(binding, lease) { CreateObservation = invocation!.Observation,
                    CreateSource = originalParent, ProvenPropertiesKey = provenPropertiesKey };
                _originalSecureOwners.Add(binding.BindingId, published);
                _secureDomains.Add(binding.BindingId, published);
                originalParent.SecureCreateInvocations.Remove(invocation);
                return KernelResult<SecureDomainBinding>.Ok(binding);
            }
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
                originalParent.PendingSecureCreates--;
        }
    }

    private void PinUnresolvedSecureCreate(DomainRecord record)
    {
        lock (_secureDomainLifecycleGate)
        {
            record.SecureCreateMayHaveEffect = true;
            QuarantineDomain(record);
        }
    }

    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareSecureDomainPublication(
        SecureDomainBinding binding, PlatformDomainIdentity subject, PlatformRegionMapping? regionMapping = null,
        bool closingRegion = false)
    {
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            QuarantineSecureDomainLocalTransition(binding);
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformFaulted,
                "Secure publication source unavailable; original lease remains pinned.");
        }
        lock (_secureDomainLifecycleGate)
        {
            KernelResult ValidateLocal()
            {
                if (!closingRegion) return ValidateSecureDomainLocalCommit(binding, subject);
                var resolved = ResolveSecure(binding);
                if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
                var parent = ValidateDomain(binding.Parent, subject);
                if (!parent.IsSuccess) return parent;
                return regionMapping is { } closing && !resolved.Value!.TerminalQuarantined &&
                    !resolved.Value.QuarantinedRegions.Any(id => id != closing.MappingId)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.PlatformFaulted, "Secure cleanup source is quarantined.");
            }
            var valid = ValidateLocal();
            if (!valid.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(valid.Error, valid.Message!);
            var secure = _secureDomains[binding.BindingId];
            var original = _domains[binding.Parent.BindingId];
            var epoch = BackendEpoch;
            MappingRecord? originalMapping = null;
            bool RegionCurrent()
            {
                if (regionMapping is not { } mapping) return true;
                return secure.RegionMappingSources.TryGetValue(mapping.MappingId, out var admitted) &&
                    ReferenceEquals(admitted, originalMapping) &&
                    _mappings.TryGetValue(mapping.MappingId, out var current) && ReferenceEquals(current, admitted) &&
                    current.Mapping == mapping && current.ProviderIncarnation == source && current.BackendEpoch == epoch &&
                    (closingRegion || (!current.LocalAuthorizationRevoked && current.ClosureState == PlatformExternalClosureState.Active)) &&
                    secure.Regions.TryGetValue(mapping.MappingId, out var region) && region.Mapping == current.ProviderLease;
            }
            if (regionMapping is { } requested) secure.RegionMappingSources.TryGetValue(requested.MappingId, out originalMapping);
            if (!ReferenceEquals(secure.CreateSource, original) || secure.ProviderLease.Parent != original.ProviderLease ||
                source.Value == 0 || source != original.ProviderIncarnation || epoch != original.BackendEpoch || _backendEpochExhausted || !RegionCurrent())
            {
                secure.TerminalQuarantined = true;
                return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.PlatformFaulted,
                    "Secure publication source changed; original lease remains pinned.");
            }
            KernelResult Commit(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (BackendEpoch != epoch || _backendEpochExhausted ||
                        !_secureDomains.TryGetValue(binding.BindingId, out var current) || (!IsOriginalSecureOwner(secure) || !ReferenceEquals(current, secure)) ||
                        !TryGetCanonicalRootRecord(binding.Parent.BindingId, out var parent) || !ReferenceEquals(parent, original) ||
                        !ValidateLocal().IsSuccess || !RegionCurrent())
                    {
                        secure.TerminalQuarantined = true;
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original secure publication owner changed before commit.");
                    }
                    return commit(); // Trusted local publication only; no provider or observation callbacks.
                }
            }
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit);
        }
    }

    internal KernelResult ValidateSecureDomainLocalCommit(SecureDomainBinding binding, PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            var parent = ValidateDomain(binding.Parent, subject);
            if (!parent.IsSuccess || secure.Value!.Quarantined)
            {
                secure.Value!.TerminalQuarantined = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Secure-domain authority changed before local commit and remains pinned.");
            }
            return KernelResult.Ok();
        }
    }

    internal void QuarantineSecureDomainLocalTransition(SecureDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (_secureDomains.TryGetValue(binding.BindingId, out var record) && record.Binding == binding)
                record.TerminalQuarantined = true;
        }
    }

    internal KernelResult BindSecureRegion(SecureDomainBinding binding, PlatformRegionMapping mapping, PlatformSecureRegionClass regionClass,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null)
    {
        if (!Enum.IsDefined(regionClass)) return KernelResult.Fail(KernelError.PlatformDenied, "Secure region class is invalid.");
        SecureDomainRecord record;
        PlatformProviderRegionMappingLease mappingLease;
        PlatformBackendEpoch backendEpoch;
        SecureRegionBindInvocationFact invocation;
        MappingRecord originalMapping;
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            record = secure.Value!;
            if (record.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (record.TransitionInFlight || record.RevokeInFlight || record.RegionMutationsInFlight.Contains(mapping.MappingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition, revoke, or exact mapping mutation is in flight.");
            if (record.Regions.ContainsKey(mapping.MappingId)) return KernelResult.Fail(KernelError.PlatformDenied, "Mapping is already bound to this secure domain.");
            if (!_mappings.TryGetValue(mapping.MappingId, out var mapped) || mapped.Mapping != mapping ||
                mapped.LocalAuthorizationRevoked || mapped.ClosureState != PlatformExternalClosureState.Active)
                return KernelResult.Fail(KernelError.StaleGeneration, "Secure region mapping is absent, stale, or closing.");
            if (mapping.DomainBinding != binding.Parent)
                return KernelResult.Fail(KernelError.WrongPlatformDomain, "Secure region belongs to another parent domain.");
            mappingLease = mapped.ProviderLease;
            originalMapping = mapped;
            backendEpoch = BackendEpoch;
            record.RegionMutationsInFlight.Add(mapping.MappingId);
            invocation = new(record, mapping, mappingLease, regionClass, backendEpoch);
            record.RegionBindInvocations.Add(mapping.MappingId, invocation);
        }
        try
        {
            bool SourceCurrent()
            {
                try
                {
                    var source = CurrentProviderIncarnation();
                    return source.Value != 0 && source == originalMapping.ProviderIncarnation &&
                        source == record.CreateSource?.ProviderIncarnation;
                }
                catch (Exception exception) when (exception is not StackOverflowException) { return false; }
            }
            bool OriginalOwnersCurrent() => BackendEpoch == backendEpoch && !_backendEpochExhausted &&
                _secureDomains.TryGetValue(binding.BindingId, out var secureOwner) && IsOriginalSecureOwner(record) && ReferenceEquals(secureOwner, record) &&
                _mappings.TryGetValue(mapping.MappingId, out var mappingOwner) && ReferenceEquals(mappingOwner, originalMapping) &&
                !originalMapping.LocalAuthorizationRevoked && originalMapping.ClosureState == PlatformExternalClosureState.Active &&
                originalMapping.BackendEpoch == backendEpoch && record.CreateSource is { } parent &&
                TryGetCanonicalRootRecord(binding.Parent.BindingId, out var currentParent) && ReferenceEquals(currentParent, parent) &&
                parent.BackendEpoch == backendEpoch && parent.AuthorityState == DomainAuthorityState.Active && !parent.ParentRevokeMayHaveEffect;
            var sourceCurrent = SourceCurrent();
            KernelResult Admit()
            {
                lock (_secureDomainLifecycleGate)
                {
                if (!sourceCurrent || !OriginalOwnersCurrent() || record.Quarantined)
                {
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Secure bind source or original owner changed before provider effect.");
                }
                return KernelResult.Ok();
                }
            }
            var admitted = sourceCommitConsumer is null ? Admit() : sourceCommitConsumer(Admit);
            if (!admitted.IsSuccess)
            {
                lock (_secureDomainLifecycleGate) record.RegionBindInvocations.Remove(mapping.MappingId);
                return admitted;
            }
            PlatformAuthorityResult<PlatformProviderSecureRegionBinding> result;
            try { result = ((IPlatformSecureComputeProvider)_provider!).BindSecureRegion(record.ProviderLease, mappingLease, regionClass); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    $"Secure-region bind may have taken effect without a lease: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                invocation.Observation = result;
                if (result.IsSuccess)
                    record.UnpublishedRegionReceipts.Add(mapping.MappingId,
                        new(mapping, mappingLease, regionClass, backendEpoch, result.Value));
            }
            sourceCurrent = SourceCurrent();
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || !OriginalOwnersCurrent() || record.TerminalQuarantined)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Backend reset during secure-region bind leaves the mapping pinned.");
                }
                if (!result.IsSuccess)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return FromProviderFailure(result.Status, result.Message);
                }
            }
            var providerBinding = result.Value;
            if (providerBinding.BindingId.Value == 0 || providerBinding.Generation.Value == 0 || providerBinding.Domain != record.ProviderLease ||
                providerBinding.Mapping != mappingLease || providerBinding.RegionClass != regionClass)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
                var cleanupClosed = false;
                try
                {
                    sourceCurrent = SourceCurrent();
                    lock (_secureDomainLifecycleGate)
                        if (!sourceCurrent || !OriginalOwnersCurrent())
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Malformed secure binding retained; original cleanup source changed.");
                    var cleanup = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding);
                    lock (_secureDomainLifecycleGate) invocation.ReturnedObjectCleanup = cleanup;
                    cleanupClosed = cleanup.IsSuccess && cleanup.Value.Binding == providerBinding && cleanup.Value.Closed;
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // The malformed provider binding may still exist after cleanup fault.
                }
                lock (_secureDomainLifecycleGate)
                {
                    var exactlyClosed = cleanupClosed && BackendEpoch == backendEpoch && !record.TerminalQuarantined;
                    if (!exactlyClosed)
                    {
                        record.TerminalQuarantined = true;
                        record.QuarantinedRegions.Add(mapping.MappingId);
                    }
                    if (exactlyClosed) record.UnpublishedRegionReceipts.Remove(mapping.MappingId);
                    return KernelResult.Fail(exactlyClosed ? KernelError.PlatformBindingRevoked : KernelError.PlatformFaulted, exactlyClosed
                        ? "Provider secure-region binding was malformed and exactly compensated."
                        : "Provider secure-region binding was malformed; exact compensation failed and the secure domain is quarantined.");
                }
            }
            sourceCurrent = SourceCurrent();
            KernelResult Publish()
            {
                lock (_secureDomainLifecycleGate)
                {
                if (!sourceCurrent || !OriginalOwnersCurrent() || record.TerminalQuarantined)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Secure-region bind lost backend continuity before binding publication.");
                }
                record.Regions.Add(mapping.MappingId, providerBinding);
                record.RegionMappingSources[mapping.MappingId] = originalMapping;
                record.RegionBindObservations[mapping.MappingId] = result;
                record.RegionBindInvocations.Remove(mapping.MappingId);
                record.UnpublishedRegionReceipts.Remove(mapping.MappingId);
                return KernelResult.Ok();
                }
            }
            var published = sourceCommitConsumer is null ? Publish() : sourceCommitConsumer(Publish);
            if (!published.IsSuccess)
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
            return published;
        }
        finally
        {
            lock (_secureDomainLifecycleGate) record.RegionMutationsInFlight.Remove(mapping.MappingId);
        }
    }

    internal KernelResult UnbindSecureRegion(SecureDomainBinding binding, PlatformRegionMapping mapping,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        SecureDomainRecord record;
        PlatformProviderSecureRegionBinding providerBinding;
        PlatformBackendEpoch backendEpoch;
        MappingRecord originalMapping;
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            record = secure.Value!;
            if (!record.Regions.TryGetValue(mapping.MappingId, out providerBinding))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Secure-region binding was not found.");
            if (!_mappings.TryGetValue(mapping.MappingId, out var mapped) || mapped.Mapping != mapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure-region mapping is absent or stale.");
            originalMapping = mapped;
            if (record.TerminalQuarantined)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (record.TransitionInFlight || record.RevokeInFlight || record.RegionMutationsInFlight.Contains(mapping.MappingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition, revoke, or exact mapping mutation is in flight.");
            backendEpoch = BackendEpoch;
            record.RegionMutationsInFlight.Add(mapping.MappingId);
        }
        try
        {
            bool SourceCurrent()
            {
                try
                {
                    var source = CurrentProviderIncarnation();
                    return source.Value != 0 && source == originalMapping.ProviderIncarnation &&
                        source == record.CreateSource?.ProviderIncarnation;
                }
                catch (Exception exception) when (exception is not StackOverflowException) { return false; }
            }
            bool OriginalOwnersCurrent() => BackendEpoch == backendEpoch && !_backendEpochExhausted &&
                _secureDomains.TryGetValue(binding.BindingId, out var currentSecure) && IsOriginalSecureOwner(record) && ReferenceEquals(currentSecure, record) &&
                _mappings.TryGetValue(mapping.MappingId, out var currentMapping) && ReferenceEquals(currentMapping, originalMapping) &&
                originalMapping.BackendEpoch == backendEpoch && record.CreateSource is { } parent &&
                TryGetCanonicalRootRecord(binding.Parent.BindingId, out var currentParent) && ReferenceEquals(currentParent, parent) &&
                parent.BackendEpoch == backendEpoch && !parent.ParentRevokeMayHaveEffect;
            var sourceCurrent = SourceCurrent();
            SecureRegionClosureInvocationFact invocation;
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || !OriginalOwnersCurrent())
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-region cleanup source changed before provider invocation.");
                invocation = new(record, originalMapping, providerBinding, backendEpoch);
                record.RegionClosureInvocations.Add(invocation);
            }
            PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> result;
            try { result = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    $"Secure-region unbind may have taken effect without closure evidence: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                invocation.Observation = result;
                if (result.IsSuccess)
                    record.RegionClosureReceipts[mapping.MappingId] = new(providerBinding, backendEpoch, result.Value);
            }
            sourceCurrent = SourceCurrent();
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || !OriginalOwnersCurrent() || record.TerminalQuarantined)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Backend reset during secure-region unbind leaves closure uncertain.");
                }
                if (!result.IsSuccess)
                {
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return FromProviderFailure(result.Status, result.Message);
                }
                if (result.Value.Binding != providerBinding || !result.Value.Closed)
                {
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-region closure receipt is malformed.");
                }
            }
            sourceCurrent = SourceCurrent();
            var committed = false;
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!sourceCurrent || !OriginalOwnersCurrent() || record.TerminalQuarantined ||
                        !_secureDomains.TryGetValue(binding.BindingId, out var current) || (!IsOriginalSecureOwner(record) || !ReferenceEquals(current, record)) ||
                        !record.Regions.TryGetValue(mapping.MappingId, out var region) || region != providerBinding ||
                        !_mappings.TryGetValue(mapping.MappingId, out var mapped) || mapped.Mapping != mapping ||
                        (completionConsumer is not null && (!ValidateDomain(binding.Parent, binding.Parent.Subject).IsSuccess ||
                            record.QuarantinedRegions.Any(id => id != mapping.MappingId))))
                    {
                        record.TerminalQuarantined = true;
                        record.QuarantinedRegions.Add(mapping.MappingId);
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact secure-region closure changed before local completion.");
                    }
                    var completed = commit();
                    if (!completed.IsSuccess)
                    {
                        record.TerminalQuarantined = true;
                        record.QuarantinedRegions.Add(mapping.MappingId);
                        return completed;
                    }
                    record.Regions.Remove(mapping.MappingId);
                    record.RegionMappingSources.Remove(mapping.MappingId);
                    record.QuarantinedRegions.Remove(mapping.MappingId);
                    committed = true;
                    return KernelResult.Ok();
                }
            }
            try
            {
                var completed = completionConsumer is null ? CommitCurrent(KernelResult.Ok) : completionConsumer(CommitCurrent);
                lock (_secureDomainLifecycleGate)
                {
                    if (!completed.IsSuccess || !committed)
                    {
                        record.TerminalQuarantined = true;
                        record.QuarantinedRegions.Add(mapping.MappingId);
                        return completed.IsSuccess
                            ? KernelResult.Fail(KernelError.PlatformFaulted, "Secure-region local completion was not committed.") : completed;
                    }
                }
                return completed;
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
                return KernelResult.Fail(KernelError.PlatformFaulted, $"Known secure-region closure could not complete locally: {exception.Message}");
            }
        }
        finally
        {
            lock (_secureDomainLifecycleGate) record.RegionMutationsInFlight.Remove(mapping.MappingId);
        }
    }

    private bool HasActiveSecureRegion(PlatformRegionMappingId mappingId)
    {
        lock (_secureDomainLifecycleGate)
            return SecureDependencyRecordsLocked().Any(record => !record.ClosureConsumed && (record.Regions.ContainsKey(mappingId) ||
                record.QuarantinedRegions.Contains(mappingId) || record.RegionMutationsInFlight.Contains(mappingId)));
    }

    internal KernelResult TransitionSecureDomain(SecureDomainBinding binding, PlatformSecureDomainTransition transition,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null)
    {
        SecureDomainRecord record;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            if (secure.Value!.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (!Enum.IsDefined(transition)) return KernelResult.Fail(KernelError.PlatformDenied, "Secure-domain transition is invalid.");
            if (secure.Value.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition is already in flight.");
            if (secure.Value.RevokeInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain revoke is already in flight.");
            if (secure.Value.RegionMutationsInFlight.Count != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-region mutation must settle before domain transition.");
            record = secure.Value;
            backendEpoch = BackendEpoch;
            record.TransitionInFlight = true;
        }

        var originalParent = record.CreateSource;
        SecureTransitionInvocationFact? invocation = null;
        bool SourceCurrent()
        {
            try
            {
                var source = CurrentProviderIncarnation();
                return source.Value != 0 && source == originalParent?.ProviderIncarnation;
            }
            catch (Exception exception) when (exception is not StackOverflowException) { return false; }
        }
        bool OriginalOwnersCurrent() => BackendEpoch == backendEpoch && !_backendEpochExhausted &&
            _secureDomains.TryGetValue(binding.BindingId, out var owner) && IsOriginalSecureOwner(record) && ReferenceEquals(owner, record) &&
            originalParent is not null && TryGetCanonicalRootRecord(binding.Parent.BindingId, out var parent) &&
            ReferenceEquals(parent, originalParent) && parent.BackendEpoch == backendEpoch &&
            parent.ProviderLease == record.ProviderLease.Parent &&
            ValidateDomain(binding.Parent, binding.Parent.Subject).IsSuccess && !record.Quarantined;
        try
        {
            var sourceCurrent = SourceCurrent();
            KernelResult Admit()
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!sourceCurrent || !OriginalOwnersCurrent())
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-domain transition source changed before provider invocation.");
                    invocation = new(record, transition, backendEpoch);
                    record.LastTransitionInvocation = invocation;
                    return KernelResult.Ok();
                }
            }
            var admitted = sourceCommitConsumer is null ? Admit() : sourceCommitConsumer(Admit);
            if (!admitted.IsSuccess) return admitted;
            var result = ((IPlatformSecureComputeProvider)_provider!).TransitionSecureDomain(record.ProviderLease, transition);
            lock (_secureDomainLifecycleGate) invocation!.Observation = result;
            sourceCurrent = SourceCurrent();
            KernelResult Publish()
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!sourceCurrent || !OriginalOwnersCurrent())
                    {
                        record.TerminalQuarantined = true;
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-domain transition source changed after possible effect.");
                    }
                    if (!result.IsSuccess) { record.TerminalQuarantined = true; return FromProviderFailure(result.Status, result.Message); }
                    if (result.Value.Domain != record.ProviderLease || result.Value.Transition != transition || !result.Value.Accepted)
                    { record.TerminalQuarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-domain transition receipt is malformed."); }
                    return KernelResult.Ok();
                }
            }
            var published = sourceCommitConsumer is null ? Publish() : sourceCommitConsumer(Publish);
            if (!published.IsSuccess) lock (_secureDomainLifecycleGate) record.TerminalQuarantined = true;
            return published;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            if (invocation is not null) lock (_secureDomainLifecycleGate) record.TerminalQuarantined = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-domain transition failed; original invocation remains pinned if admitted: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate) record.TransitionInFlight = false;
        }
    }

    internal KernelResult RevokeSecureDomain(SecureDomainBinding binding,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        KernelResult<SecureDomainRecord> secure;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            if (secure.Value!.Regions.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure regions must close before secure-domain authority.");
            if (secure.Value.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (secure.Value.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition must settle before revoke.");
            if (secure.Value.RevokeInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain revoke is already in flight.");
            if (secure.Value.RegionMutationsInFlight.Count != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-region mutation must settle before domain revoke.");
            secure.Value.RevokeInFlight = true;
            backendEpoch = BackendEpoch;
        }
        try
        {
        var originalParent = secure.Value!.CreateSource;
        bool SourceCurrent()
        {
            try
            {
                var source = CurrentProviderIncarnation();
                return source.Value != 0 && source == originalParent?.ProviderIncarnation;
            }
            catch (Exception exception) when (exception is not StackOverflowException) { return false; }
        }
        bool OriginalOwnersCurrent() => BackendEpoch == backendEpoch && !_backendEpochExhausted &&
            _secureDomains.TryGetValue(binding.BindingId, out var owner) && IsOriginalSecureOwner(secure.Value) && ReferenceEquals(owner, secure.Value) &&
            originalParent is not null && TryGetCanonicalRootRecord(binding.Parent.BindingId, out var parent) &&
            ReferenceEquals(parent, originalParent) && parent.BackendEpoch == backendEpoch &&
            parent.ProviderLease == secure.Value.ProviderLease.Parent && !parent.ParentRevokeMayHaveEffect;
        var sourceCurrent = SourceCurrent();
        lock (_secureDomainLifecycleGate)
        {
            if (!sourceCurrent || !OriginalOwnersCurrent())
                return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-domain revoke source changed before provider invocation.");
            secure.Value.RevokeInvocation = new(secure.Value, backendEpoch);
        }
        PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> result;
        try { result = ((IPlatformSecureComputeProvider)_provider!).RevokeSecureDomain(secure.Value.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                secure.Value.TerminalQuarantined = true;
                secure.Value.RevokeInFlight = false;
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-domain revoke may have taken effect without closure evidence: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
            secure.Value.RevokeInvocation!.Observation = result;
            if (result.IsSuccess)
                secure.Value.LastClosureReceipt = new(secure.Value.ProviderLease, backendEpoch, result.Value);
        }
        sourceCurrent = SourceCurrent();
        lock (_secureDomainLifecycleGate)
        {
            if (!sourceCurrent || !OriginalOwnersCurrent() || secure.Value.Quarantined)
            {
                secure.Value.TerminalQuarantined = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Backend reset during secure-domain revoke leaves closure uncertain.");
            }
            if (!result.IsSuccess) { secure.Value.TerminalQuarantined = true; return FromProviderFailure(result.Status, result.Message); }
            if (!IsExactClosure(result, secure.Value.ProviderLease))
            { secure.Value.TerminalQuarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-domain closure receipt is malformed."); }
            // Keep the admission interlock through required local completion.
            secure.Value.RevokeInFlight = true;
        }
        sourceCurrent = SourceCurrent();
        var committed = false;
        KernelResult CommitCurrent(Func<KernelResult> commit)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || !OriginalOwnersCurrent() || secure.Value!.Quarantined ||
                    !_secureDomains.TryGetValue(binding.BindingId, out var current) ||
                    (!IsOriginalSecureOwner(secure.Value) || !ReferenceEquals(current, secure.Value)))
                {
                    secure.Value.TerminalQuarantined = true;
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Secure-domain closure changed before local completion.");
                }
                var completed = commit();
                if (!completed.IsSuccess) { secure.Value.TerminalQuarantined = true; return completed; }
                secure.Value.ClosureConsumed = true;
                _secureDomains.Remove(binding.BindingId);
                committed = true;
                return KernelResult.Ok();
            }
        }
        try
        {
            var completed = completionConsumer is null ? CommitCurrent(KernelResult.Ok) : completionConsumer(CommitCurrent);
            lock (_secureDomainLifecycleGate)
            {
                if (!completed.IsSuccess || !committed)
                {
                    secure.Value!.TerminalQuarantined = true;
                    return completed.IsSuccess
                        ? KernelResult.Fail(KernelError.PlatformFaulted, "Secure-domain local completion was not committed.")
                        : completed;
                }
            }
            return completed;
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            lock (_secureDomainLifecycleGate) secure.Value!.TerminalQuarantined = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Known secure-domain closure could not complete locally: {exception.Message}");
        }
        }
        finally
        {
            lock (_secureDomainLifecycleGate) secure.Value!.RevokeInFlight = false;
        }
    }

    private static bool IsExactClosure(
        PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> result,
        PlatformProviderSecureDomainLease lease) =>
        result.IsSuccess && result.Value.Domain == lease && result.Value.Closed;

    private bool IsOriginalSecureOwner(SecureDomainRecord record) =>
        _originalSecureOwners.TryGetValue(record.Binding.BindingId, out var original) && ReferenceEquals(original, record);

    private IEnumerable<SecureDomainRecord> SecureDependencyRecordsLocked() =>
        _originalSecureOwners.Values.Concat(_secureDomains.Values).Distinct<SecureDomainRecord>(ReferenceEqualityComparer.Instance);

    private KernelResult<SecureDomainRecord> ResolveSecure(SecureDomainBinding binding)
    {
        if (!_secureDomains.TryGetValue(binding.BindingId, out var record))
            return KernelResult<SecureDomainRecord>.Fail(KernelError.PlatformBindingNotFound, "Secure-domain binding was not found.");
        if (record.Binding != binding || record.ClosureConsumed ||
            !_originalSecureOwners.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
            return KernelResult<SecureDomainRecord>.Fail(KernelError.StaleGeneration, "Secure-domain binding is stale or belongs to another parent.");
        return KernelResult<SecureDomainRecord>.Ok(record);
    }
}
