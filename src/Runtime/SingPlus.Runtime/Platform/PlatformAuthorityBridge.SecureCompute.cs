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
    private sealed class SecureDomainRecord
    {
        private readonly object _sourceGate;
        public SecureDomainRecord(SecureDomainBinding binding, PlatformProviderSecureDomainLease providerLease, object sourceGate)
        { Binding = binding; ProviderLease = providerLease; OriginalOwner = this; _sourceGate = sourceGate; }
        public SecureDomainBinding Binding { get; }
        public PlatformProviderSecureDomainLease ProviderLease { get; }
        public SecureDomainRecord OriginalOwner { get; }
        // Immutable admitted evidence projection; never permission or a replacement receipt.
        public string ProvenPropertiesKey { get; init; } = "";
        public Dictionary<PlatformRegionMappingId, SecureRegionBindInvocationFact> RegionBindInvocations { get; } = [];
        public Dictionary<PlatformRegionMappingId, SecureRegionBindInvocationFact> RegionBindingOwners { get; } = [];
        public Dictionary<PlatformRegionMappingId, PlatformAuthorityResult<PlatformProviderSecureRegionBinding>> RegionBindObservations { get; } = [];
        public Dictionary<PlatformRegionMappingId, MappingRecord> RegionMappingSources { get; } = [];
        public Dictionary<PlatformRegionMappingId, PlatformProviderSecureRegionBinding> Regions { get; } = [];
        public Dictionary<PlatformRegionMappingId, UnpublishedSecureRegionReceipt> UnpublishedRegionReceipts { get; } = [];
        public HashSet<PlatformRegionMappingId> QuarantinedRegions { get; } = [];
        private bool _terminalQuarantined;
        public long TerminalRevision { get; private set; }
        public bool TerminalRevisionExhausted { get; private set; }
        public bool TerminalQuarantined
        {
            get { lock (_sourceGate) return _terminalQuarantined; }
            set
            {
                lock (_sourceGate)
                {
                if (TerminalRevision == long.MaxValue) TerminalRevisionExhausted = true;
                else TerminalRevision++;
                _terminalQuarantined = value;
                }
            }
        }
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease>? CreateObservation { get; set; }
        public DomainRecord? CreateSource { get; init; }
        public bool TransitionInFlight { get; set; }
        public SecureTransitionInvocationFact? LastTransitionInvocation { get; set; }
        public bool RevokeInFlight { get; set; }
        public bool RevokeInvoked { get; set; }
        public SecureClosureInvocationFact? ClosureQuarantineOwner { get; set; }
        public HashSet<SecureRegionBindInvocationFact> RegionCloseInvoked { get; } = [];
        public bool ClosureConsumed { get; set; }
        public bool CleanupOnly { get; set; }
        public SecureDomainRevokeInvocationFact? RevokeInvocation { get; set; }
        public HashSet<PlatformRegionMappingId> RegionMutationsInFlight { get; } = [];
        public SecureDomainClosureReceiptFact? LastClosureReceipt { get; set; }
        public Dictionary<PlatformRegionMappingId, SecureRegionClosureReceiptFact> RegionClosureReceipts { get; } = [];
        public List<SecureRegionClosureInvocationFact> RegionClosureInvocations { get; } = [];
        public bool Quarantined => TerminalQuarantined || QuarantinedRegions.Count != 0;
    }
    private sealed class SecureRegionBindInvocationFact
    {
        public SecureRegionBindInvocationFact(SecureDomainRecord source, PlatformRegionMapping mapping,
            PlatformProviderRegionMappingLease admittedMapping, PlatformSecureRegionClass regionClass, PlatformBackendEpoch epoch)
        { OriginalOwner = this; Source = source; Mapping = mapping; AdmittedMapping = admittedMapping; RegionClass = regionClass; Epoch = epoch; }
        public SecureRegionBindInvocationFact OriginalOwner { get; }
        public SecureDomainRecord Source { get; }
        public PlatformRegionMapping Mapping { get; }
        public PlatformProviderRegionMappingLease AdmittedMapping { get; }
        public PlatformSecureRegionClass RegionClass { get; }
        public PlatformBackendEpoch Epoch { get; }
        public PlatformAuthorityResult<PlatformProviderSecureRegionBinding>? Observation { get; set; }
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>? ReturnedObjectCleanup { get; set; }
    }
    // Original invocation correlation; neither a permission nor closure proof.
    private sealed class SecureTransitionInvocationFact
    {
        public SecureTransitionInvocationFact(SecureDomainRecord source, PlatformSecureDomainTransition transition, PlatformBackendEpoch epoch)
        { Source = source; AdmittedLease = source.ProviderLease; Transition = transition; Epoch = epoch; OriginalOwner = this; }
        public SecureTransitionInvocationFact OriginalOwner { get; }
        public SecureDomainRecord Source { get; }
        public PlatformProviderSecureDomainLease AdmittedLease { get; }
        public PlatformSecureDomainTransition Transition { get; }
        public PlatformBackendEpoch Epoch { get; }
        public PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>? Observation { get; set; }
        public bool Published { get; set; }
        public long? CleanupQuarantineRevision { get; set; }
        public bool CleanupConsumed { get; set; }
    }
    // Cleanup correlation only; raw replies do not prove exact closure or continuity.
    private sealed record SecureDomainClosureReceiptFact(PlatformProviderSecureDomainLease AdmittedLease,
        PlatformBackendEpoch AdmissionEpoch, PlatformSecureDomainClosureReceipt Receipt);
    private abstract class SecureClosureInvocationFact
    {
        protected SecureClosureInvocationFact(SecureDomainRecord source, PlatformBackendEpoch epoch)
        { OriginalOwner = this; Source = source; ParentSource = source.CreateSource; Epoch = epoch; }
        public SecureClosureInvocationFact OriginalOwner { get; }
        public SecureDomainRecord Source { get; }
        public DomainRecord? ParentSource { get; }
        public PlatformBackendEpoch Epoch { get; }
        public bool ValidatedClosed { get; set; }
        public long? OwnQuarantineRevision { get; set; }
        public bool Consumed { get; set; }
    }
    private sealed class SecureDomainRevokeInvocationFact : SecureClosureInvocationFact
    {
        public SecureDomainRevokeInvocationFact(SecureDomainRecord source, PlatformBackendEpoch epoch) : base(source, epoch)
        { AdmittedLease = source.ProviderLease; }
        public PlatformProviderSecureDomainLease AdmittedLease { get; }
        public PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>? Observation { get; set; }
    }
    private sealed record SecureRegionClosureReceiptFact(PlatformProviderSecureRegionBinding AdmittedBinding,
        PlatformBackendEpoch AdmissionEpoch, PlatformSecureRegionClosureReceipt Receipt);
    // Preserve original cleanup attempts across V1 retries; raw responses are not closure authority.
    private sealed class SecureRegionClosureInvocationFact : SecureClosureInvocationFact
    {
        public SecureRegionClosureInvocationFact(SecureDomainRecord source, MappingRecord mappingSource,
            PlatformProviderSecureRegionBinding admittedBinding, PlatformBackendEpoch epoch, SecureRegionBindInvocationFact bindingSource) : base(source, epoch)
        { MappingSource = mappingSource; AdmittedBinding = admittedBinding; BindingSource = bindingSource; }
        public SecureRegionBindInvocationFact BindingSource { get; }
        public MappingRecord MappingSource { get; }
        public PlatformProviderSecureRegionBinding AdmittedBinding { get; }
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
            KernelResult validParent = ValidateDomain(parent, subject);
            if (!validParent.IsSuccess) return KernelResult<SecureDomainBinding>.Fail(validParent.Error, validParent.Message!);
            DomainRecord record = _domains[parent.BindingId];
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
                PlatformProviderIncarnation source = CurrentProviderIncarnation();
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
                    if (!TryGetCanonicalRootRecord(parent.BindingId, out DomainRecord? exactParent) || !ReferenceEquals(exactParent, originalParent) ||
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
                KernelResult admission = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
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
                if (!sourceCurrent || BackendEpoch != backendEpoch || !TryGetCanonicalRootRecord(parent.BindingId, out DomainRecord? currentParent) ||
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
            PlatformProviderSecureDomainLease lease = result.Value;
            bool malformed;
            string provenPropertiesKey;
            try
            {
                // Provider collections can execute callbacks. Materialize and validate
                // the same evidence outside owner locks, before fresh publication checks.
                SecureProperty[]? provenProperties = lease.ProvenProperties?.ToArray();
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
                bool cleanupClosed = false;
                try
                {
                    bool cleanupSource = SourceCurrent();
                    lock (_secureDomainLifecycleGate)
                        cleanupSource &= TryGetCanonicalRootRecord(parent.BindingId, out DomainRecord? cleanupOwner) &&
                            ReferenceEquals(cleanupOwner, originalParent) && !originalParent.ParentRevokeMayHaveEffect;
                    if (!cleanupSource)
                        return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted, "Malformed returned secure lease retained; cleanup source changed.");
                    PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> cleanup = provider.RevokeSecureDomain(lease);
                    cleanupClosed = IsExactClosure(cleanup, lease);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // A failed cleanup cannot prove that the malformed child was closed.
                }
                lock (_secureDomainLifecycleGate)
                {
                    bool exactlyClosed = cleanupClosed && BackendEpoch == backendEpoch &&
                        TryGetCanonicalRootRecord(parent.BindingId, out DomainRecord? cleanupParent) && ReferenceEquals(cleanupParent, originalParent) &&
                        originalParent.AuthorityState == DomainAuthorityState.Active;
                    if (!exactlyClosed)
                    {
                        SecureDomainBinding quarantined = reservedBinding;
                        SecureDomainRecord retained = new SecureDomainRecord(quarantined, lease, _secureDomainLifecycleGate) { TerminalQuarantined = true, CreateSource = originalParent };
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
                if (!sourceCurrent || BackendEpoch != backendEpoch || !TryGetCanonicalRootRecord(parent.BindingId, out DomainRecord? publicationParent) ||
                    !ReferenceEquals(publicationParent, originalParent) || originalParent.AuthorityState != DomainAuthorityState.Active)
                {
                    originalParent.UnpublishedSecureCreates.Add(new(parent, parentLease, backendEpoch, lease));
                    PinUnresolvedSecureCreate(originalParent);
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                        "Parent or backend changed before secure-domain publication; possible effect remains pinned.");
                }
                SecureDomainBinding binding = reservedBinding;
                SecureDomainRecord published = new SecureDomainRecord(binding, lease, _secureDomainLifecycleGate) { CreateObservation = invocation!.Observation,
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
                KernelResult<SecureDomainRecord> resolved = ResolveSecure(binding);
                if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
                KernelResult parent = ValidateDomain(binding.Parent, subject);
                if (!parent.IsSuccess) return parent;
                return regionMapping is { } closing && !resolved.Value!.TerminalQuarantined &&
                    !resolved.Value.QuarantinedRegions.Any(id => id != closing.MappingId)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.PlatformFaulted, "Secure cleanup source is quarantined.");
            }
            KernelResult valid = ValidateLocal();
            if (!valid.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(valid.Error, valid.Message!);
            SecureDomainRecord secure = _secureDomains[binding.BindingId];
            DomainRecord original = _domains[binding.Parent.BindingId];
            PlatformBackendEpoch epoch = BackendEpoch;
            MappingRecord? originalMapping = null;
            bool RegionCurrent()
            {
                if (regionMapping is not { } mapping) return true;
                return secure.RegionMappingSources.TryGetValue(mapping.MappingId, out MappingRecord? admitted) &&
                    ReferenceEquals(admitted, originalMapping) &&
                    _mappings.TryGetValue(mapping.MappingId, out MappingRecord? current) && ReferenceEquals(current, admitted) &&
                    current.Mapping == mapping && current.ProviderIncarnation == source && current.BackendEpoch == epoch &&
                    (closingRegion || (!current.LocalAuthorizationRevoked && current.ClosureState == PlatformExternalClosureState.Active)) &&
                    secure.Regions.TryGetValue(mapping.MappingId, out PlatformProviderSecureRegionBinding region) && region.Mapping == current.ProviderLease;
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
                        !_secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? current) || (!IsOriginalSecureOwner(secure) || !ReferenceEquals(current, secure)) ||
                        !TryGetCanonicalRootRecord(binding.Parent.BindingId, out DomainRecord? parent) || !ReferenceEquals(parent, original) ||
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
            KernelResult<SecureDomainRecord> secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            KernelResult parent = ValidateDomain(binding.Parent, subject);
            if (!parent.IsSuccess || secure.Value!.Quarantined)
            {
                secure.Value!.TerminalQuarantined = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Secure-domain authority changed before local commit and remains pinned.");
            }
            return KernelResult.Ok();
        }
    }

    internal void QuarantineSecureDomainLocalTransition(SecureDomainBinding binding, bool knownLocalSourceRefusal = false)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (_originalSecureOwners.TryGetValue(binding.BindingId, out SecureDomainRecord? record) && record.Binding == binding)
            {
                bool eligible = knownLocalSourceRefusal && !record.Quarantined &&
                    record.LastTransitionInvocation is { Published: true, Transition: PlatformSecureDomainTransition.BeginDrain } fact &&
                    ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record);
                record.TerminalQuarantined = true;
                if (eligible && !record.TerminalRevisionExhausted)
                    record.LastTransitionInvocation!.CleanupQuarantineRevision = record.TerminalRevision;
            }
        }
    }

    // Pure consumption of a previously published original acknowledgement. Revoke still supplies closure.
    private static bool HasConsumedSecureDrainContinuity(SecureDomainRecord record) => !record.CleanupOnly ||
        (record.LastTransitionInvocation is { Published: true, CleanupConsumed: true, Observation: { } observation } fact &&
         ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record) &&
         !record.TerminalRevisionExhausted && fact.CleanupQuarantineRevision == record.TerminalRevision &&
         fact.Transition == PlatformSecureDomainTransition.BeginDrain && fact.AdmittedLease == record.ProviderLease &&
         observation.IsSuccess && observation.Value.Domain == fact.AdmittedLease &&
         observation.Value.Transition == fact.Transition && observation.Value.Accepted);

    internal KernelResult<bool> ConsumeKnownSecureDrainForCleanup(SecureDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
            KernelResult<SecureDomainRecord> initial = ResolveSecure(binding);
            if (!initial.IsSuccess) return KernelResult<bool>.Fail(initial.Error, initial.Message!);
            // Ordinary teardown retains its existing fresh admission path and query boundaries.
            if (!initial.Value!.Quarantined && !initial.Value.CleanupOnly)
                return KernelResult<bool>.Ok(false);
        }
        PlatformProviderIncarnation incarnation;
        try { incarnation = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Secure cleanup source is unavailable."); }
        lock (_secureDomainLifecycleGate)
        {
            KernelResult<SecureDomainRecord> resolved = ResolveSecure(binding);
            if (!resolved.IsSuccess) return KernelResult<bool>.Fail(resolved.Error, resolved.Message!);
            SecureDomainRecord record = resolved.Value!;
            SecureTransitionInvocationFact? fact = record.LastTransitionInvocation;
            if (record.TransitionInFlight || record.RevokeInFlight || record.RegionMutationsInFlight.Count != 0)
                return KernelResult<bool>.Fail(KernelError.PlatformBindingActive, "Secure cleanup must wait for original invocations.");
            if (record.CreateSource is not { } source || incarnation.Value == 0 || incarnation != source.ProviderIncarnation ||
                _backendEpochExhausted || source.BackendEpoch != BackendEpoch ||
                !TryGetCanonicalRootRecord(binding.Parent.BindingId, out DomainRecord? parent) || !ReferenceEquals(parent, source) ||
                parent.ProviderLease != record.ProviderLease.Parent || !ValidateDomain(binding.Parent, binding.Parent.Subject).IsSuccess)
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original secure cleanup source changed.");
            bool known = fact is { Published: true, Observation: { } observation } &&
                ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record) && fact.Epoch == BackendEpoch && fact.AdmittedLease == record.ProviderLease &&
                observation.IsSuccess && observation.Value.Domain == fact.AdmittedLease &&
                observation.Value.Transition == fact.Transition && observation.Value.Accepted;
            if (record.CleanupOnly && (!known || fact is not { CleanupConsumed: true } ||
                record.TerminalRevisionExhausted || fact.CleanupQuarantineRevision != record.TerminalRevision))
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Consumed original drain continuity changed; no transition may repeat.");
            if (record.Quarantined)
            {
                if (!known || record.QuarantinedRegions.Count != 0 || record.RevokeInvocation is not null ||
                    record.TerminalRevisionExhausted || fact!.CleanupQuarantineRevision != record.TerminalRevision)
                    return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Independent or unknown secure outcome remains pinned.");
                record.TerminalQuarantined = false;
                fact.CleanupConsumed = true;
                fact.CleanupQuarantineRevision = record.TerminalRevision;
                record.CleanupOnly = true;
            }
            return KernelResult<bool>.Ok(known && fact!.Transition == PlatformSecureDomainTransition.BeginDrain);
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
            KernelResult<SecureDomainRecord> secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            record = secure.Value!;
            if (record.CleanupOnly) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain permits original cleanup only.");
            if (record.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (record.TransitionInFlight || record.RevokeInFlight || record.RegionMutationsInFlight.Contains(mapping.MappingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition, revoke, or exact mapping mutation is in flight.");
            if (record.Regions.ContainsKey(mapping.MappingId)) return KernelResult.Fail(KernelError.PlatformDenied, "Mapping is already bound to this secure domain.");
            if (!_mappings.TryGetValue(mapping.MappingId, out MappingRecord? mapped) || mapped.Mapping != mapping ||
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
                    PlatformProviderIncarnation source = CurrentProviderIncarnation();
                    return source.Value != 0 && source == originalMapping.ProviderIncarnation &&
                        source == record.CreateSource?.ProviderIncarnation;
                }
                catch (Exception exception) when (exception is not StackOverflowException) { return false; }
            }
            bool OriginalOwnersCurrent() => BackendEpoch == backendEpoch && !_backendEpochExhausted &&
                _secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? secureOwner) && IsOriginalSecureOwner(record) && ReferenceEquals(secureOwner, record) &&
                _mappings.TryGetValue(mapping.MappingId, out MappingRecord? mappingOwner) && ReferenceEquals(mappingOwner, originalMapping) &&
                !originalMapping.LocalAuthorizationRevoked && originalMapping.ClosureState == PlatformExternalClosureState.Active &&
                originalMapping.BackendEpoch == backendEpoch && record.CreateSource is { } parent &&
                TryGetCanonicalRootRecord(binding.Parent.BindingId, out DomainRecord? currentParent) && ReferenceEquals(currentParent, parent) &&
                parent.BackendEpoch == backendEpoch && parent.AuthorityState == DomainAuthorityState.Active && !parent.ParentRevokeMayHaveEffect;
            bool sourceCurrent = SourceCurrent();
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
            KernelResult admitted = sourceCommitConsumer is null ? Admit() : sourceCommitConsumer(Admit);
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
            PlatformProviderSecureRegionBinding providerBinding = result.Value;
            if (providerBinding.BindingId.Value == 0 || providerBinding.Generation.Value == 0 || providerBinding.Domain != record.ProviderLease ||
                providerBinding.Mapping != mappingLease || providerBinding.RegionClass != regionClass)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
                bool cleanupClosed = false;
                try
                {
                    sourceCurrent = SourceCurrent();
                    lock (_secureDomainLifecycleGate)
                        if (!sourceCurrent || !OriginalOwnersCurrent())
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Malformed secure binding retained; original cleanup source changed.");
                    PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> cleanup = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding);
                    lock (_secureDomainLifecycleGate) invocation.ReturnedObjectCleanup = cleanup;
                    cleanupClosed = cleanup.IsSuccess && cleanup.Value.Binding == providerBinding && cleanup.Value.Closed;
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // The malformed provider binding may still exist after cleanup fault.
                }
                lock (_secureDomainLifecycleGate)
                {
                    bool exactlyClosed = cleanupClosed && BackendEpoch == backendEpoch && !record.TerminalQuarantined;
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
                record.RegionBindingOwners.Add(mapping.MappingId, invocation);
                record.RegionMappingSources[mapping.MappingId] = originalMapping;
                record.RegionBindObservations[mapping.MappingId] = result;
                record.RegionBindInvocations.Remove(mapping.MappingId);
                record.UnpublishedRegionReceipts.Remove(mapping.MappingId);
                return KernelResult.Ok();
                }
            }
            KernelResult published = sourceCommitConsumer is null ? Publish() : sourceCommitConsumer(Publish);
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
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null, object? originalBindingSource = null)
    {
        KernelResult<bool> retained = ConsumeOriginalSecureRegionClosure(binding, mapping, completionConsumer, originalBindingSource);
        if (!retained.IsSuccess) return KernelResult.Fail(retained.Error, retained.Message!);
        if (retained.Value) return KernelResult.Ok();
        SecureDomainRecord record;
        PlatformProviderSecureRegionBinding providerBinding;
        PlatformBackendEpoch backendEpoch;
        MappingRecord originalMapping;
        SecureRegionBindInvocationFact originalBinding;
        lock (_secureDomainLifecycleGate)
        {
            KernelResult<SecureDomainRecord> secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            record = secure.Value!;
            if (!record.RegionBindingOwners.TryGetValue(mapping.MappingId, out SecureRegionBindInvocationFact? bindingOwner) ||
                !OriginalSecureRegionBindingCurrent(record, bindingOwner) ||
                originalBindingSource is not null && !ReferenceEquals(originalBindingSource, bindingOwner))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original secure-region binding source changed.");
            if (!record.Regions.TryGetValue(mapping.MappingId, out providerBinding))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Secure-region binding was not found.");
            if (!_mappings.TryGetValue(mapping.MappingId, out MappingRecord? mapped) || mapped.Mapping != mapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure-region mapping is absent or stale.");
            originalMapping = mapped;
            originalBinding = bindingOwner;
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
                    PlatformProviderIncarnation source = CurrentProviderIncarnation();
                    return source.Value != 0 && source == originalMapping.ProviderIncarnation &&
                        source == record.CreateSource?.ProviderIncarnation;
                }
                catch (Exception exception) when (exception is not StackOverflowException) { return false; }
            }
            bool OriginalOwnersCurrent() => OriginalSecureRegionBindingCurrent(record, originalBinding) && BackendEpoch == backendEpoch && !_backendEpochExhausted &&
                _secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? currentSecure) && IsOriginalSecureOwner(record) && ReferenceEquals(currentSecure, record) &&
                _mappings.TryGetValue(mapping.MappingId, out MappingRecord? currentMapping) && ReferenceEquals(currentMapping, originalMapping) &&
                originalMapping.BackendEpoch == backendEpoch && record.CreateSource is { } parent &&
                TryGetCanonicalRootRecord(binding.Parent.BindingId, out DomainRecord? currentParent) && ReferenceEquals(currentParent, parent) &&
                parent.BackendEpoch == backendEpoch && !parent.ParentRevokeMayHaveEffect;
            bool sourceCurrent = SourceCurrent();
            SecureRegionClosureInvocationFact invocation;
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || !OriginalOwnersCurrent())
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-region cleanup source changed before provider invocation.");
                invocation = new(record, originalMapping, providerBinding, backendEpoch, originalBinding);
                record.RegionClosureInvocations.Add(invocation);
                record.RegionCloseInvoked.Add(originalBinding);
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
                invocation.ValidatedClosed = true;
            }
            sourceCurrent = SourceCurrent();
            bool committed = false;
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!sourceCurrent || !OriginalOwnersCurrent() || record.TerminalQuarantined ||
                        !_secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? current) || (!IsOriginalSecureOwner(record) || !ReferenceEquals(current, record)) ||
                        !record.Regions.TryGetValue(mapping.MappingId, out PlatformProviderSecureRegionBinding region) || region != providerBinding ||
                        !_mappings.TryGetValue(mapping.MappingId, out MappingRecord? mapped) || mapped.Mapping != mapping ||
                        (completionConsumer is not null && (!ValidateDomain(binding.Parent, binding.Parent.Subject).IsSuccess ||
                            record.QuarantinedRegions.Any(id => id != mapping.MappingId))))
                    {
                        QuarantineSecureClosure(record, invocation);
                        record.QuarantinedRegions.Add(mapping.MappingId);
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact secure-region closure changed before local completion.");
                    }
                    KernelResult completed = commit();
                    if (!completed.IsSuccess)
                    {
                        QuarantineSecureClosure(record, invocation);
                        record.QuarantinedRegions.Add(mapping.MappingId);
                        return completed;
                    }
                    record.Regions.Remove(mapping.MappingId);
                    record.RegionBindingOwners.Remove(mapping.MappingId);
                    record.RegionMappingSources.Remove(mapping.MappingId);
                    record.QuarantinedRegions.Remove(mapping.MappingId);
                    invocation.Consumed = true;
                    committed = true;
                    return KernelResult.Ok();
                }
            }
            try
            {
                KernelResult completed = completionConsumer is null ? CommitCurrent(KernelResult.Ok) : completionConsumer(CommitCurrent);
                lock (_secureDomainLifecycleGate)
                {
                    if (!completed.IsSuccess || !committed)
                    {
                        QuarantineSecureClosure(record, invocation);
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
                    QuarantineSecureClosure(record, invocation);
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
            KernelResult<SecureDomainRecord> secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            if (secure.Value!.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (secure.Value.CleanupOnly) return KernelResult.Fail(KernelError.PlatformFaulted, "Consumed original drain cannot repeat or authorize active transitions.");
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

        DomainRecord? originalParent = record.CreateSource;
        SecureTransitionInvocationFact? invocation = null;
        bool SourceCurrent()
        {
            try
            {
                PlatformProviderIncarnation source = CurrentProviderIncarnation();
                return source.Value != 0 && source == originalParent?.ProviderIncarnation;
            }
            catch (Exception exception) when (exception is not StackOverflowException) { return false; }
        }
        bool OriginalOwnersCurrent() => BackendEpoch == backendEpoch && !_backendEpochExhausted &&
            _secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? owner) && IsOriginalSecureOwner(record) && ReferenceEquals(owner, record) &&
            originalParent is not null && TryGetCanonicalRootRecord(binding.Parent.BindingId, out DomainRecord? parent) &&
            ReferenceEquals(parent, originalParent) && parent.BackendEpoch == backendEpoch &&
            parent.ProviderLease == record.ProviderLease.Parent &&
            ValidateDomain(binding.Parent, binding.Parent.Subject).IsSuccess && !record.Quarantined;
        try
        {
            bool sourceCurrent = SourceCurrent();
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
            KernelResult admitted = sourceCommitConsumer is null ? Admit() : sourceCommitConsumer(Admit);
            if (!admitted.IsSuccess) return admitted;
            PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> result = ((IPlatformSecureComputeProvider)_provider!).TransitionSecureDomain(record.ProviderLease, transition);
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
                    invocation!.Published = true;
                    return KernelResult.Ok();
                }
            }
            KernelResult published = sourceCommitConsumer is null ? Publish() : sourceCommitConsumer(Publish);
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
        KernelResult<bool> retained = ConsumeOriginalSecureDomainClosure(binding, completionConsumer);
        if (!retained.IsSuccess) return KernelResult.Fail(retained.Error, retained.Message!);
        if (retained.Value) return KernelResult.Ok();
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
            DomainRecord? originalParent = secure.Value!.CreateSource;
        bool SourceCurrent()
        {
            try
            {
                    PlatformProviderIncarnation source = CurrentProviderIncarnation();
                return source.Value != 0 && source == originalParent?.ProviderIncarnation;
            }
            catch (Exception exception) when (exception is not StackOverflowException) { return false; }
        }
        bool OriginalOwnersCurrent() => BackendEpoch == backendEpoch && !_backendEpochExhausted &&
            _secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? owner) && IsOriginalSecureOwner(secure.Value) && ReferenceEquals(owner, secure.Value) &&
            HasConsumedSecureDrainContinuity(secure.Value) &&
            originalParent is not null && TryGetCanonicalRootRecord(binding.Parent.BindingId, out DomainRecord? parent) &&
            ReferenceEquals(parent, originalParent) && parent.BackendEpoch == backendEpoch &&
            parent.ProviderLease == secure.Value.ProviderLease.Parent && !parent.ParentRevokeMayHaveEffect;
            bool sourceCurrent = SourceCurrent();
        lock (_secureDomainLifecycleGate)
        {
            if (!sourceCurrent || !OriginalOwnersCurrent())
                return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-domain revoke source changed before provider invocation.");
            secure.Value.RevokeInvocation = new(secure.Value, backendEpoch);
            secure.Value.RevokeInvoked = true;
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
            secure.Value.RevokeInvocation!.ValidatedClosed = true;
            // Keep the admission interlock through required local completion.
            secure.Value.RevokeInFlight = true;
        }
        sourceCurrent = SourceCurrent();
            bool committed = false;
        KernelResult CommitCurrent(Func<KernelResult> commit)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!sourceCurrent || !OriginalOwnersCurrent() || secure.Value!.Quarantined ||
                    !_secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? current) ||
                    (!IsOriginalSecureOwner(secure.Value) || !ReferenceEquals(current, secure.Value)))
                {
                    QuarantineSecureClosure(secure.Value, secure.Value.RevokeInvocation!);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Secure-domain closure changed before local completion.");
                }
                    KernelResult completed = commit();
                if (!completed.IsSuccess) { QuarantineSecureClosure(secure.Value, secure.Value.RevokeInvocation!); return completed; }
                secure.Value.ClosureConsumed = true;
                secure.Value.RevokeInvocation!.Consumed = true;
                _secureDomains.Remove(binding.BindingId);
                committed = true;
                return KernelResult.Ok();
            }
        }
        try
        {
                KernelResult completed = completionConsumer is null ? CommitCurrent(KernelResult.Ok) : completionConsumer(CommitCurrent);
            lock (_secureDomainLifecycleGate)
            {
                if (!completed.IsSuccess || !committed)
                {
                    QuarantineSecureClosure(secure.Value!, secure.Value.RevokeInvocation!);
                    return completed.IsSuccess
                        ? KernelResult.Fail(KernelError.PlatformFaulted, "Secure-domain local completion was not committed.")
                        : completed;
                }
            }
            return completed;
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            lock (_secureDomainLifecycleGate) QuarantineSecureClosure(secure.Value!, secure.Value.RevokeInvocation!);
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
        ReferenceEquals(record.OriginalOwner, record) && _originalSecureOwners.TryGetValue(record.Binding.BindingId, out SecureDomainRecord? original) && ReferenceEquals(original, record);

    private IEnumerable<SecureDomainRecord> SecureDependencyRecordsLocked() =>
        _originalSecureOwners.Values.Concat(_secureDomains.Values).Distinct<SecureDomainRecord>(ReferenceEqualityComparer.Instance);

    private KernelResult<SecureDomainRecord> ResolveSecure(SecureDomainBinding binding)
    {
        if (!_secureDomains.TryGetValue(binding.BindingId, out SecureDomainRecord? record))
            return KernelResult<SecureDomainRecord>.Fail(KernelError.PlatformBindingNotFound, "Secure-domain binding was not found.");
        if (record.Binding != binding || record.ClosureConsumed || !IsOriginalSecureOwner(record) ||
            !_originalSecureOwners.TryGetValue(binding.BindingId, out SecureDomainRecord? original) || !ReferenceEquals(original, record))
            return KernelResult<SecureDomainRecord>.Fail(KernelError.StaleGeneration, "Secure-domain binding is stale or belongs to another parent.");
        return KernelResult<SecureDomainRecord>.Ok(record);
    }
}
