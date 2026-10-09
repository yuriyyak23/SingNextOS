using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDomainBindingId(ulong Value);
public readonly record struct PlatformDomainBindingGeneration(ulong Value);
public readonly record struct PlatformRegionMappingId(ulong Value);
public readonly record struct PlatformRegionMappingGeneration(ulong Value);

public readonly record struct PlatformDomainBinding(
    PlatformDomainBindingId BindingId,
    PlatformDomainBindingGeneration Generation,
    PlatformDomainIdentity Subject);

public readonly record struct PlatformRegionMapping(
    PlatformRegionMappingId MappingId,
    PlatformRegionMappingGeneration Generation,
    PlatformDomainBinding DomainBinding,
    RegionHandle Region,
    PlatformMemoryAccess Access);

public enum PlatformExternalClosureState
{
    Active = 0,
    Draining,
    Closed,
    Faulted
}

public readonly record struct PlatformRegionMappingLifecycle(
    PlatformRegionMapping Mapping,
    bool LocalAuthorizationRevoked,
    PlatformExternalClosureState PlatformClosure,
    bool LocalReservationReleased)
{
    public bool LocalReclaimAllowed =>
        PlatformClosure == PlatformExternalClosureState.Closed && LocalReservationReleased;
}

public sealed partial class PlatformAuthorityBridge
{
    private enum DomainAuthorityState
    {
        Active = 0,
        Quarantined,
        Closed,
    }

    private sealed class DomainRecord(
        PlatformDomainBinding binding,
        PlatformProviderDomainLease providerLease)
    {
        public PlatformDomainBinding Binding { get; } = binding;
        public PlatformProviderDomainLease ProviderLease { get; } = providerLease;
        public PlatformProviderIncarnation ProviderIncarnation { get; init; }
        public PlatformBackendEpoch BackendEpoch { get; init; }
        public bool ParentRevokeInFlight { get; set; }
        public bool ProviderClosureConfirmed { get; set; }
        public PlatformAuthorityResult? RootRevokeObservation { get; set; }
        public DomainAuthorityState AuthorityState { get; set; }
        public bool SecureCreateMayHaveEffect { get; set; }
        public bool DeviceBindMayHaveEffect { get; set; }
        public PlatformAuthorityResult<PlatformProviderDeviceLease>? DeviceBindObservation { get; set; }
        public CapabilityId? DeviceBindCapabilityId { get; set; }
        public PlatformDeviceIdentity? DeviceBindDevice { get; set; }
        public PlatformDeviceRights? DeviceBindRights { get; set; }
        public PlatformProviderIncarnation? DeviceBindIncarnation { get; set; }
        public PlatformBackendEpoch? DeviceBindEpoch { get; set; }
        public DomainRecord? DeviceBindSource { get; set; } // Original cleanup source retained across epoch replacement.
        public int PendingDeviceBinds { get; set; }
        public HashSet<object> MmioSources { get; init; } = new(ReferenceEqualityComparer.Instance); // Parent lifetime references, never permission.
        public HashSet<object> IrqSources { get; init; } = new(ReferenceEqualityComparer.Instance); // Cleanup routes, never event authority.
        public int PendingMappings { get; set; }
        public List<MappingRecord> MappingRecords { get; } = [];
        public bool MappingMayHaveEffect { get; set; }
        public bool ChildCreateMayHaveEffect { get; set; }
        public List<ChildCreateInvocationFact> ChildCreateInvocations { get; init; } = [];
        public int PendingChildCreates { get; set; }
        public List<UnpublishedChildCreateReceipt> UnpublishedChildCreates { get; init; } = [];
        public int PendingSecureCreates { get; set; }
        public List<SecureCreateInvocationFact> SecureCreateInvocations { get; init; } = [];
        public List<UnpublishedSecureCreateReceipt> UnpublishedSecureCreates { get; init; } = [];
        // Lifetime references to the same operation owners; the dictionary is routing.
        public List<Dsc1OperationRecord> Dsc1Operations { get; } = [];
        public bool ParentRevokeMayHaveEffect { get; set; }
        // Admission-only interlock; quarantine/closure remain owned by this record.
        public bool ExecutionTransitionInFlight { get; set; }
        public PlatformExecutionPolicyRegistration? ExecutionPolicy { get; set; }
    }

    private sealed class MappingRecord(
        PlatformRegionMapping mapping,
        PlatformProviderRegionMappingLease providerLease,
        CapabilityId authorityCapabilityId,
        PlatformProviderIncarnation providerIncarnation,
        PlatformBackendEpoch backendEpoch)
    {
        public DomainRecord? OriginalDomain { get; set; }
        public PlatformRegionMapping Mapping { get; } = mapping;
        public PlatformProviderRegionMappingLease ProviderLease { get; } = providerLease;
        public CapabilityId AuthorityCapabilityId { get; } = authorityCapabilityId;
        public PlatformProviderIncarnation ProviderIncarnation { get; } = providerIncarnation;
        public PlatformBackendEpoch BackendEpoch { get; } = backendEpoch;
        public bool LocalAuthorizationRevoked { get; set; }
        public PlatformExternalClosureState ClosureState { get; set; } =
            PlatformExternalClosureState.Active;
        public PlatformOperationIdentity? ClosureOperation { get; set; }
        public PlatformAuthorityResult<PlatformRegionRevocationTicket>? RevocationBeginObservation { get; set; }
        public bool LegacyRevokeMayHaveEffect { get; set; }
        public PlatformAuthorityResult? LegacyRevokeObservation { get; set; }
        public PlatformCompletionReceipt? KnownClosureReceipt { get; set; }
        public bool LocalReservationReleased { get; set; }
        // A reference to the existing quantitative owner record, not another ledger.
        public bool KernelBudgetBindingKnown { get; set; }
        public bool ExpectsBudgetReservation { get; set; }
        public object? OriginalBudgetReservationOwner { get; set; }
        public PlatformAuthorityResult<PlatformRegionAcquireResult>? AcquireObservation { get; set; }
        public bool AcquireInFlight { get; set; }
        public bool AcquireUncertain { get; set; }
        public PlatformRegionAcquireRequest? AcquireRequest { get; set; }
        public int PendingGuestMaps { get; set; }
        public List<UnpublishedGuestMapReceipt> UnpublishedGuestMaps { get; } = [];
        public bool ClosureTransitionInFlight { get; set; } // Callback interlock, never closure evidence.
        public int PendingChildDrains { get; set; } // Admission interlock, not closure evidence.

        public PlatformRegionMappingLifecycle Lifecycle => new(
            Mapping,
            LocalAuthorizationRevoked,
            ClosureState,
            LocalReservationReleased);
    }

    private readonly IPlatformAuthorityProvider? _provider;
    private PlatformFeatureManifest _featureManifest;
    private readonly Dictionary<PlatformDomainBindingId, DomainRecord> _domains = [];
    private readonly object _secureDomainLifecycleGate = new();
    private readonly Dictionary<PlatformRegionMappingId, MappingRecord> _mappings = [];
    // Existing subject index retains the original owner, independently of current domain routing.
    private readonly Dictionary<PlatformDomainIdentity, DomainRecord> _activeSubjects = [];
    private readonly HashSet<PlatformDomainIdentity> _pendingDomainBinds = [];
    private readonly HashSet<PlatformDomainIdentity> _unresolvedDomainBinds = [];
    private ulong _nextDomainBindingId = 1;
    private ulong _nextMappingId = 1;

    internal PlatformAuthorityBridge(IPlatformAuthorityProvider? provider)
    {
        _provider = provider;
        _featureManifest = provider switch
        {
            null => PlatformFeatureManifest.Empty,
            IPlatformFeatureProvider featureProvider =>
                featureProvider.QueryFeatures() ?? PlatformFeatureManifest.Empty,
            _ => PlatformFeatureManifest.FromLegacy(provider.Descriptor.Features)
        };
    }

    public PlatformProviderDescriptor? ProviderDescriptor => _provider?.Descriptor;

    public PlatformFeatureManifest FeatureManifest => _featureManifest;

    internal PlatformFeatureManifest RefreshFeatureManifest()
    {
        _featureManifest = _provider is IPlatformFeatureProvider featureProvider
            ? featureProvider.QueryFeatures() ?? PlatformFeatureManifest.Empty
            : _provider is null
                ? PlatformFeatureManifest.Empty
                : PlatformFeatureManifest.FromLegacy(_provider.Descriptor.Features);
        return _featureManifest;
    }

    public bool IsAvailable => _provider is not null;

    internal KernelResult<PlatformDomainBinding> BindDomain(PlatformDomainIdentity subject,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<PlatformDomainBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<KernelResult>? invocationConsumer = null)
    {
        if (_backendEpochExhausted)
            return KernelResult<PlatformDomainBinding>.Fail(KernelError.CapacityExhausted,
                "Platform backend generation space is exhausted.");
        if (_provider is null)
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformUnavailable,
                "No platform authority provider is configured.");

        var subjectValidation = PlatformDomainContract.ValidateSubject(subject);
        if (!subjectValidation.IsSuccess)
            return FromProviderFailure<PlatformDomainBinding>(
                subjectValidation.Status,
                subjectValidation.Message);

        var neutralDomainFeature = _featureManifest.Resolve(
            PlatformFeatureFamily.NeutralDomains);
        if (!Supports(PlatformAuthorityFeatures.NeutralDomainBinding) ||
            neutralDomainFeature.ContractVersion < PlatformDomainContract.ContractVersion ||
            neutralDomainFeature.Availability is not
                (PlatformFeatureAvailability.RuntimeAdmission or
                 PlatformFeatureAvailability.Executable))
        {
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformUnsupported,
                $"The platform provider does not admit neutral domain contract v{PlatformDomainContract.ContractVersion} authority.");
        }

        PlatformBackendEpoch backendEpoch = default;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            if (_activeSubjects.ContainsKey(subject))
                return KernelResult.Fail(KernelError.PlatformDenied,
                    "The local subject already has an active platform binding.");
            if (HasActiveAuthority(subject))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "The original subject still owns a live platform fact; a missing canonical route cannot admit a replacement root.");
            if (_pendingDomainBinds.Contains(subject))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "The local subject already has platform binding admission in flight.");
            if (_unresolvedDomainBinds.Contains(subject))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "The local subject has an unresolved possible provider domain binding.");
            if (_backendEpochExhausted) return KernelResult.Fail(KernelError.CapacityExhausted, "Backend epoch exhausted before root admission.");
            KernelResult CommitAdmission()
            {
                _pendingDomainBinds.Add(subject);
                backendEpoch = BackendEpoch;
                return KernelResult.Ok();
            }
            return sourceCommitConsumer is null ? CommitAdmission() : sourceCommitConsumer(CommitAdmission);
        }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformDomainBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
        PlatformProviderIncarnation providerIncarnation;
        try { providerIncarnation = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformDomainBinding>.Fail(KernelError.PlatformFaulted, "Root source unavailable before provider invocation."); }
        lock (_secureDomainLifecycleGate)
        {
            if (providerIncarnation.Value == 0 || BackendEpoch != backendEpoch || _backendEpochExhausted)
                return KernelResult<PlatformDomainBinding>.Fail(KernelError.PlatformFaulted, "Root source changed before provider invocation.");
        }
        var invocation = invocationConsumer?.Invoke() ?? KernelResult.Ok();
        if (!invocation.IsSuccess) return KernelResult<PlatformDomainBinding>.Fail(invocation.Error, invocation.Message!);
        PlatformAuthorityResult<PlatformProviderDomainLease> providerResult;
        try { providerResult = _provider.BindDomain(subject); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) _unresolvedDomainBinds.Add(subject);
            return KernelResult<PlatformDomainBinding>.Fail(KernelError.PlatformFaulted,
                "Root binding may have taken effect without an exact lease; original subject remains pinned.");
        }
        if (!providerResult.IsSuccess)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || providerResult.Status != PlatformAuthorityStatus.NotAccepted)
                    _unresolvedDomainBinds.Add(subject);
            }
            return FromProviderFailure<PlatformDomainBinding>(providerResult.Status, providerResult.Message);
        }
        var providerLease = providerResult.Value!;
        var leaseValidation = PlatformDomainContract.ValidateLease(subject, providerLease);
        if (!leaseValidation.IsSuccess)
        {
            var leaseMessage = leaseValidation.Message ??
                "The platform provider returned malformed domain authority.";
            // Malformed returned authority cannot prove original subject closure.
            lock (_secureDomainLifecycleGate) _unresolvedDomainBinds.Add(subject);
            PlatformAuthorityResult cleanup;
            try { cleanup = _provider.RevokeDomain(providerLease); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                cleanup = PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                    $"Malformed domain cleanup receipt was lost: {exception.Message}");
            }
            if (!cleanup.IsSuccess)
            {
                lock (_secureDomainLifecycleGate)
                    _ = AddDomainRecord(subject, providerLease, DomainAuthorityState.Quarantined, providerIncarnation, backendEpoch);
            }

            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformFaulted,
                cleanup.IsSuccess
                    ? leaseMessage
                    : $"{leaseMessage} Cleanup returned {cleanup.Status}; the provider lease remains quarantined for teardown.");
        }

        PlatformDomainBinding published;
        DomainRecord publishedOwner;
        lock (_secureDomainLifecycleGate)
        {
            // Retain the valid original lease/source before any late query or
            // publication refusal. Quarantined owner is cleanup truth only.
            published = AddDomainRecord(subject, providerLease, DomainAuthorityState.Quarantined,
                providerIncarnation, backendEpoch);
            publishedOwner = _domains[published.BindingId];
        }
        PlatformProviderIncarnation currentSource;
        try { currentSource = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformDomainBinding>.Fail(KernelError.PlatformFaulted, "Known root lease source unavailable; original cleanup owner retained."); }
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || _backendEpochExhausted || currentSource != providerIncarnation ||
                currentSource.Value == 0 || _unresolvedDomainBinds.Contains(subject) ||
                !_domains.TryGetValue(published.BindingId, out var routed) || !ReferenceEquals(routed, publishedOwner) ||
                !_activeSubjects.TryGetValue(subject, out var original) || !ReferenceEquals(original, publishedOwner))
                return KernelResult<PlatformDomainBinding>.Fail(KernelError.PlatformFaulted,
                    "Root source changed before publication; exact original cleanup owner remains quarantined.");
            publishedOwner.AuthorityState = DomainAuthorityState.Active;
        }
        KernelResult CommitAttachment(Func<KernelResult> commit)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
                    !_domains.TryGetValue(published.BindingId, out var routed) || !ReferenceEquals(routed, publishedOwner) ||
                    !_activeSubjects.TryGetValue(subject, out var original) || !ReferenceEquals(original, publishedOwner))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original root binding owner/backend changed before local attachment.");
                var current = ValidateDomain(published, subject);
                return current.IsSuccess ? commit() : current;
            }
        }
        if (completionConsumer is null) return KernelResult<PlatformDomainBinding>.Ok(published);
        KernelResult completed;
        try { completed = completionConsumer(published, CommitAttachment); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            completed = KernelResult.Fail(KernelError.PlatformFaulted,
                $"Known provider binding could not be attached locally: {exception.Message}");
        }
        if (!completed.IsSuccess)
        {
            lock (_secureDomainLifecycleGate)
            {
                QuarantineDomain(publishedOwner);
                var routed = CurrentOriginalRootOwnerLocked(publishedOwner);
                if (routed is not null) QuarantineDomain(routed);
            }
            return KernelResult<PlatformDomainBinding>.Fail(completed.Error, completed.Message!);
        }
        return KernelResult<PlatformDomainBinding>.Ok(published);
        }
        finally { lock (_secureDomainLifecycleGate) _pendingDomainBinds.Remove(subject); }
    }

    // Called outside kernel/capability/owner commit locks after consumer callbacks.
    internal KernelResult ValidateRootAttachmentSource(PlatformDomainBinding binding)
    {
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Root attachment source unavailable; original cleanup fact remains pinned.");
        }
        lock (_secureDomainLifecycleGate)
        {
            if (!_activeSubjects.TryGetValue(binding.Subject, out var original))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original root attachment owner is absent.");
            return ValidateRootCleanupSourceLocked(original, binding, binding.Subject, original.BackendEpoch, source);
        }
    }

    // Trusted pure local consumer only; root state remains owned by DomainRecord.
    internal KernelResult CommitActiveDomainConsumer(
        PlatformDomainBinding binding, PlatformDomainIdentity subject, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            var current = ValidateDomain(binding, subject);
            return current.IsSuccess ? commit() : current;
        }
    }

    internal bool HasUnresolvedDomainBindEffect(PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
            return _pendingDomainBinds.Contains(subject) || _unresolvedDomainBinds.Contains(subject);
    }

    // A cleanup route to the existing authoritative owner, never an execution attachment.
    internal bool TryGetOriginalDomainCleanupBinding(PlatformDomainIdentity subject, out PlatformDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (_activeSubjects.TryGetValue(subject, out var original) && original.AuthorityState != DomainAuthorityState.Closed)
            {
                binding = original.Binding;
                return true;
            }
            binding = default;
            return false;
        }
    }

    internal bool TryGetQuarantinedDomainBinding(
        PlatformDomainIdentity subject,
        out PlatformDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
        if (_activeSubjects.TryGetValue(subject, out var original))
        {
            // A reset projection can expose only a stale diagnostic binding. It
            // cannot receive original outcomes or mutate the original interlock.
            var record = original;
            if (_domains.TryGetValue(original.Binding.BindingId, out var projection) &&
                projection.Binding.Subject == original.Binding.Subject && projection.ProviderLease == original.ProviderLease &&
                projection.ProviderIncarnation == original.ProviderIncarnation && projection.BackendEpoch == original.BackendEpoch)
                record = projection;
            if (record.AuthorityState == DomainAuthorityState.Quarantined)
            {
                binding = record.Binding;
                return true;
            }
            if (original.AuthorityState == DomainAuthorityState.Quarantined)
            {
                binding = original.Binding;
                return true;
            }
        }

        binding = default;
        return false;
        }
    }

    internal KernelResult RevokeDomain(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        DomainRecord record;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            var validation = ValidateDomainIdentity(binding, expectedSubject);
            if (!validation.IsSuccess) return validation;

            var executionRecord = _domains[binding.BindingId];
            if (executionRecord.ExecutionTransitionInFlight)
            {
                QuarantineDomain(executionRecord);
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Pending execution transition may still take effect; exact domain closure must wait.");
            }

            if (HasActiveDsc1Operations(binding))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "DSC1 operations must close and release local reservations before the platform domain binding.");
            if (_domains[binding.BindingId].MappingMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "A mapping may exist without an exact provider lease or closure receipt.");
            if (OriginalMappingRecordsLocked().Any(m =>
                    !m.LocalReservationReleased && m.Mapping.DomainBinding.BindingId == binding.BindingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Platform region mappings must reach verified closure and release their local reservation before the domain binding.");
            if (DeviceDependencyRecordsLocked().Any(device =>
                    !device.PlatformClosed && device.Lease.DomainBinding.BindingId == binding.BindingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Platform device leases must reach exact closure before the domain binding.");

            record = _domains[binding.BindingId];
            var originalOwnerCurrent = _activeSubjects.TryGetValue(expectedSubject, out var original)
                ? ReferenceEquals(original, record) : record.AuthorityState == DomainAuthorityState.Closed;
            if (record.PendingSecureCreates != 0 || record.PendingChildCreates != 0 ||
                record.PendingMappings != 0 ||
                record.PendingDeviceBinds != 0 || record.MmioSources.Count != 0 || record.IrqSources.Count != 0 ||
                record.SecureCreateMayHaveEffect || record.SecureCreateInvocations.Count != 0 ||
                record.DeviceBindMayHaveEffect || record.ChildCreateMayHaveEffect || record.ChildCreateInvocations.Count != 0 ||
                _childBindings.Values.Concat(_originalChildOwners.Values).Any(child =>
                    child.Binding.ParentBinding.BindingId == binding.BindingId &&
                    (child.State != PlatformChildDomainState.Closed || !HasValidatedChildClosureFact(child))) ||
                SecureDependencyRecordsLocked().Any(secure => !secure.ClosureConsumed && secure.Binding.Parent.BindingId == binding.BindingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Child creation and authority must have exact closure before the parent platform domain.");
            if (record.ParentRevokeInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Root revoke is already in flight.");
            if (record.ParentRevokeMayHaveEffect && !record.ProviderClosureConfirmed)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Parent-domain revoke may have taken effect without exact closure evidence.");
            if (record.AuthorityState == DomainAuthorityState.Closed)
            {
                if (!record.ProviderClosureConfirmed || record.RootRevokeObservation is not { IsSuccess: true })
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Closed root projection lacks the original provider closure fact.");
                if (!originalOwnerCurrent && original!.Binding.BindingId == record.Binding.BindingId)
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "A foreign closed root cannot discharge the original subject owner.");
                ReleaseActiveSubject(record);
                return KernelResult.Ok();
            }
            if (record.MappingMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "A mapping may exist without an exact provider lease or closure receipt.");
            if (!originalOwnerCurrent)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Root cleanup requires the original subject owner; replacement routing cannot authorize revoke.");
            record.ParentRevokeInFlight = true;
            QuarantineDomain(record);
            backendEpoch = record.BackendEpoch;
        }
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Root cleanup source unavailable; original owner retained."); }
            bool invokeRevoke;
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateRootCleanupSourceLocked(record, binding, expectedSubject, backendEpoch, source);
                if (!exact.IsSuccess) return exact;
                invokeRevoke = !record.ProviderClosureConfirmed;
                // Reset replacement must observe the intent admitted against this exact owner.
                if (invokeRevoke) record.ParentRevokeMayHaveEffect = true;
            }
            if (invokeRevoke)
            {
                PlatformAuthorityResult result;
                try { result = _provider!.RevokeDomain(record.ProviderLease); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { return KernelResult.Fail(KernelError.PlatformFaulted, "Root revoke outcome unknown; original owner remains pinned."); }
                lock (_secureDomainLifecycleGate)
                {
                    record.RootRevokeObservation = result;
                    var routedOwner = CurrentOriginalRootOwnerLocked(record);
                    if (routedOwner is not null) routedOwner.RootRevokeObservation = result;
                    if (result.IsSuccess)
                    {
                        // Preserve the original cleanup fact even when current routing is unavailable.
                        record.ProviderClosureConfirmed = true;
                        var owner = CurrentOriginalRootOwnerLocked(record);
                        if (owner is not null) owner.ProviderClosureConfirmed = true;
                    }
                    else
                    {
                        // A failed response cannot prove no effect or permit another original invocation.
                        return FromProviderFailure(result.Status, result.Message);
                    }
                }
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Known root closure source unavailable; original success retained for retry."); }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateRootCleanupSourceLocked(record, binding, expectedSubject, backendEpoch, source);
                if (!exact.IsSuccess) return exact;
                CloseDomain(record);
                return KernelResult.Ok();
            }
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                record.ParentRevokeInFlight = false;
                var owner = CurrentOriginalRootOwnerLocked(record);
                if (owner is not null) owner.ParentRevokeInFlight = false;
            }
        }
    }

    // Only the same original owner can receive an outcome or interlock mutation.
    // Equal tuples and reset projections are never an outcome-transfer target.
    private DomainRecord? CurrentOriginalRootOwnerLocked(DomainRecord original)
    {
        if (_domains.TryGetValue(original.Binding.BindingId, out var current) && ReferenceEquals(current, original))
            return current;
        return null;
    }
    // Identity continuity only. Permission, source generation and lifecycle checks remain independent.
    private bool TryGetCanonicalRootRecord(PlatformDomainBindingId id, out DomainRecord record)
    {
        lock (_secureDomainLifecycleGate)
            return _domains.TryGetValue(id, out record!) &&
                _activeSubjects.TryGetValue(record.Binding.Subject, out var original) && ReferenceEquals(original, record) &&
                HasOriginalRootSourceContinuityLocked(record);
    }
    // Existing effect owners retain their original source. Equal tuples in both indexes cannot replace it.
    private bool HasOriginalRootSourceContinuityLocked(DomainRecord candidate)
    {
        var id = candidate.Binding.BindingId;
        return !_originalChildOwners.Values.Concat(_childBindings.Values).Any(child =>
                child.Binding.ParentBinding.BindingId == id && !ReferenceEquals(child.CreateRoot, candidate)) &&
            !SecureDependencyRecordsLocked().Any(secure =>
                secure.Binding.Parent.BindingId == id && !ReferenceEquals(secure.CreateSource, candidate)) &&
            !DeviceDependencyRecordsLocked().Any(device =>
                device.Lease.DomainBinding.BindingId == id && !ReferenceEquals(device.BindParentSource, candidate)) &&
            !OriginalMappingRecordsLocked().Any(mapping =>
                mapping.Mapping.DomainBinding.BindingId == id && !ReferenceEquals(mapping.OriginalDomain, candidate));
    }
    private KernelResult ValidateRootCleanupSourceLocked(DomainRecord record, PlatformDomainBinding binding,
        PlatformDomainIdentity subject, PlatformBackendEpoch epoch, PlatformProviderIncarnation source)
    {
        if (_backendEpochExhausted || BackendEpoch != epoch || source.Value == 0 || source != record.ProviderIncarnation ||
            !_domains.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) || record.Binding != binding ||
            !_activeSubjects.TryGetValue(subject, out var original) || !ReferenceEquals(original, record) ||
            !HasOriginalRootSourceContinuityLocked(record))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original root cleanup source/owner changed; local closure remains pinned.");
        return ValidateDomainIdentity(binding, subject);
    }
    private KernelResult<DomainRecord> CaptureDomainAdmissionRecord(
        PlatformDomainBinding binding, PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateDomain(binding, subject);
            return valid.IsSuccess ? KernelResult<DomainRecord>.Ok(_domains[binding.BindingId])
                : KernelResult<DomainRecord>.Fail(valid.Error, valid.Message!);
        }
    }

    internal KernelResult ValidateDomain(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var identityValidation = ValidateDomainIdentity(binding, expectedSubject);
            if (!identityValidation.IsSuccess) return identityValidation;

            var record = _domains[binding.BindingId];
            if (record.ParentRevokeInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Root cleanup source/provider stage is in flight.");
            if (record.AuthorityState != DomainAuthorityState.Active)
            {
                return record.AuthorityState == DomainAuthorityState.Closed
                    ? KernelResult.Fail(
                        KernelError.PlatformBindingRevoked,
                        "The platform domain binding has reached externally confirmed closure.")
                    : KernelResult.Fail(
                        KernelError.PlatformFaulted,
                        "The platform domain binding is quarantined without external closure proof.");
            }

            if (record.ParentRevokeMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "The parent domain is already entering provider revocation.");

            if (!TryGetCanonicalRootRecord(binding.BindingId, out var original) || !ReferenceEquals(original, record))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Active root permission requires the canonical original subject owner; routing alone is insufficient.");
            return KernelResult.Ok();
        }
    }

    private KernelResult ValidateDomainIdentity(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_domains.TryGetValue(binding.BindingId, out var record))
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform domain binding does not exist.");

        if (record.Binding.Generation != binding.Generation)
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform domain binding generation is stale.");

        if (record.Binding.Subject != binding.Subject || record.Binding.Subject != expectedSubject)
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The platform domain binding does not belong to the expected local subject.");

        return KernelResult.Ok();
    }

    internal KernelResult TransitionDomainExecution(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformDomainExecutionTransition transition,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        lock (_secureDomainLifecycleGate)
        {
            var bindingValidation = ValidateDomain(binding, expectedSubject);
            if (!bindingValidation.IsSuccess) return bindingValidation;
        }
        var transitionValidation = PlatformDomainExecutionContract.ValidateTransition(transition);
        if (!transitionValidation.IsSuccess)
            return FromProviderFailure(transitionValidation.Status, transitionValidation.Message);
        if (!_featureManifest.Supports(PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.Executable))
            return KernelResult.Fail(KernelError.PlatformUnsupported,
                "The bound platform provider does not classify neutral execution transitions as executable.");
        if (_provider is not IPlatformDomainExecutionProvider executionProvider)
            return KernelResult.Fail(KernelError.PlatformUnsupported,
                "The bound platform provider does not expose neutral execution lifecycle transitions.");
        DomainRecord record = null!;
        PlatformBackendEpoch backendEpoch = default;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                var validation = ValidateDomain(binding, expectedSubject);
                if (!validation.IsSuccess) return validation;
                record = _domains[binding.BindingId];
                if (record.ExecutionTransitionInFlight)
                    return KernelResult.Fail(KernelError.PlatformBindingActive,
                        "An execution transition is already pending for this exact domain.");
                record.ExecutionTransitionInFlight = true;
                backendEpoch = BackendEpoch;
                return KernelResult.Ok();
            }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return admitted;
        try
        {
            // Provider callback runs outside the record owner gate. Its returned
            // failure/exception never establishes no-effect or closure.
            var providerResult = executionProvider.TransitionDomainExecution(record.ProviderLease, transition);
            lock (_secureDomainLifecycleGate)
            {
                if (!_domains.TryGetValue(binding.BindingId, out var current) || current != record ||
                    current.Binding != binding || BackendEpoch != backendEpoch)
                {
                    QuarantineDomain(record);
                    if (current is not null) QuarantineDomain(current);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Execution callback lost exact domain/backend continuity; closure remains uncertain.");
                }
                if (!providerResult.IsSuccess)
                {
                    QuarantineDomain(record);
                    return FromProviderFailure(providerResult.Status, providerResult.Message);
                }
                var resultValidation = PlatformDomainExecutionContract.ValidateResult(record.ProviderLease,
                    transition, providerResult.Value!);
                if (!resultValidation.IsSuccess)
                {
                    QuarantineDomain(record);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        resultValidation.Message ?? "Malformed execution transition evidence.");
                }
                var currentValidation = ValidateDomain(binding, expectedSubject);
                if (!currentValidation.IsSuccess) return currentValidation;
            }
            KernelResult CommitLocal(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!_domains.TryGetValue(binding.BindingId, out var current) || current != record ||
                        current.Binding != binding || BackendEpoch != backendEpoch)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Execution receipt lost exact owner continuity before local commit.");
                    var validation = ValidateDomain(binding, expectedSubject);
                    if (!validation.IsSuccess) return validation;
                    return commit();
                }
            }
            if (completionConsumer is null) return KernelResult.Ok();
            var completed = completionConsumer(CommitLocal);
            if (!completed.IsSuccess)
            {
                lock (_secureDomainLifecycleGate) QuarantineDomain(record);
            }
            return completed;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) QuarantineDomain(record);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Execution callback may have taken effect without an exact receipt: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                record.ExecutionTransitionInFlight = false;
                // Balance only the original admission owner. A reset projection
                // remains quarantined and cannot receive an interlock mutation.
                if (CurrentOriginalRootOwnerLocked(record) is { } current)
                    current.ExecutionTransitionInFlight = false;
            }
        }
    }
    internal KernelResult<PlatformRegionMapping> MapOwnedRegion(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access) =>
        MapOwnedRegion(binding, expectedSubject, authorityCapabilityId, region, access, out _);

    internal KernelResult<PlatformRegionMapping> MapOwnedRegion(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access,
        out bool retainReservation,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null)
    {
        retainReservation = false;
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);

        if (_provider is null)
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformUnavailable,
                "No platform authority provider is configured.");

        if (!Supports(PlatformAuthorityFeatures.DirectOwnedRegionMapping))
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not support direct owned-region mapping.");

        if (!IsValidAccess(access))
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformDenied,
                "The requested platform memory access is invalid.");

        var capturedDomain = CaptureDomainAdmissionRecord(binding, expectedSubject);
        if (!capturedDomain.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(capturedDomain.Error, capturedDomain.Message!);
        var domainRecord = capturedDomain.Value!;
        PlatformProviderIncarnation providerIncarnation;
        try { providerIncarnation = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<PlatformRegionMapping>.Fail(KernelError.PlatformFaulted,
                "The mapping admission source is unavailable; no provider Map was invoked.");
        }
        var backendEpoch = BackendEpoch;
        if (providerIncarnation.Value == 0)
            return KernelResult<PlatformRegionMapping>.Fail(KernelError.PlatformFaulted,
                "The mapping provider returned an invalid zero runtime incarnation.");
        PlatformRegionMappingId mappingIdentity = default;
        KernelResult Begin()
        {
          lock (_secureDomainLifecycleGate)
          {
            var originalSource = ValidateRootCleanupSourceLocked(domainRecord, binding, expectedSubject,
                domainRecord.BackendEpoch, providerIncarnation);
            if (!originalSource.IsSuccess) return originalSource;
            var admission = ValidateDomain(binding, expectedSubject);
            if (!admission.IsSuccess)
                return KernelResult.Fail(admission.Error, admission.Message!);
            KernelResult CommitAdmission()
            {
                if (domainRecord.PendingMappings == int.MaxValue || domainRecord.MappingRecords.Count >= int.MaxValue - domainRecord.PendingMappings || _nextMappingId is 0 or ulong.MaxValue ||
                    _mappings.ContainsKey(new PlatformRegionMappingId(_nextMappingId)))
                    return KernelResult.Fail(KernelError.CapacityExhausted,
                        "The root mapping admission identity or interlock is exhausted.");
                mappingIdentity = new PlatformRegionMappingId(_nextMappingId++);
                domainRecord.MappingRecords.EnsureCapacity(domainRecord.MappingRecords.Count + domainRecord.PendingMappings + 1);
                domainRecord.PendingMappings++;
                return KernelResult.Ok();
            }
            return sourceCommitConsumer is null ? CommitAdmission() : sourceCommitConsumer(CommitAdmission);
          }
        }
        var sourceAdmission = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!sourceAdmission.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(sourceAdmission.Error, sourceAdmission.Message!);
        try
        {
        PlatformAuthorityResult<PlatformProviderRegionMappingLease> providerResult;
        try { providerResult = _provider.MapOwnedRegion(domainRecord.ProviderLease, region, access); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                // Correlate the unreceipted effect with the existing mapping owner so
                // a later capability cascade cannot mistake an empty snapshot for closure.
                var unresolved = new PlatformRegionMapping(
                    mappingIdentity,
                    new PlatformRegionMappingGeneration(1), binding, region.Handle, access);
                AddOriginalMappingRecordLocked(domainRecord, unresolved.MappingId,
                    new MappingRecord(unresolved, default, authorityCapabilityId,
                        providerIncarnation, backendEpoch)
                    {
                        ClosureState = PlatformExternalClosureState.Faulted,
                    });
                domainRecord.MappingMayHaveEffect = true;
                QuarantineDomain(domainRecord);
            }
            retainReservation = true;
            return KernelResult<PlatformRegionMapping>.Fail(KernelError.PlatformFaulted,
                $"Owned-region mapping may have taken effect without a provider receipt: {exception.Message}");
        }
        if (!providerResult.IsSuccess)
        {
            var refusalSource = providerIncarnation;
            var refusalSourceAvailable = true;
            if (providerResult.Status == PlatformAuthorityStatus.NotAccepted)
            {
                // Consume an original no-effect disposition only against its fresh source.
                try { refusalSource = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { refusalSourceAvailable = false; }
            }
            lock (_secureDomainLifecycleGate)
            {
            var admissionLostContinuity = !refusalSourceAvailable || refusalSource != providerIncarnation ||
                BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !_domains.TryGetValue(binding.BindingId, out var routed) || !ReferenceEquals(routed, domainRecord) ||
                !ValidateDomain(binding, expectedSubject).IsSuccess;
            if (providerResult.Status != PlatformAuthorityStatus.NotAccepted || admissionLostContinuity)
            {
                var quarantinedMapping = new PlatformRegionMapping(
                    mappingIdentity,
                    new PlatformRegionMappingGeneration(1), binding, region.Handle, access);
                AddOriginalMappingRecordLocked(domainRecord, quarantinedMapping.MappingId,
                    new MappingRecord(quarantinedMapping, default,
                        authorityCapabilityId, providerIncarnation, backendEpoch)
                    {
                        ClosureState = PlatformExternalClosureState.Faulted,
                    });
                QuarantineDomain(domainRecord);
                retainReservation = true;
            }

            if (admissionLostContinuity)
                return KernelResult<PlatformRegionMapping>.Fail(KernelError.PlatformFaulted,
                    "Mapping admission crossed a backend generation or parent-domain transition; local reservation remains pinned.");

            return FromProviderFailure<PlatformRegionMapping>(providerResult.Status, providerResult.Message);
            }
        }

        var providerLease = providerResult.Value!;
        if (providerLease.DomainLease != domainRecord.ProviderLease ||
            providerLease.Region != region || providerLease.Access != access)
        {
            // Exact requested tuple failed: retain original reservation before cleanup.
                lock (_secureDomainLifecycleGate)
                {
                var quarantinedMapping = new PlatformRegionMapping(
                    mappingIdentity,
                    new PlatformRegionMappingGeneration(1), binding, region.Handle, access);
                AddOriginalMappingRecordLocked(domainRecord, quarantinedMapping.MappingId,
                    new MappingRecord(quarantinedMapping, providerLease,
                        authorityCapabilityId, providerIncarnation, backendEpoch)
                    {
                        ClosureState = PlatformExternalClosureState.Faulted,
                    });
                QuarantineDomain(domainRecord);
                retainReservation = true;
                }

            _ = TryCloseMalformedMappingLease(providerLease, providerIncarnation, backendEpoch);
            return KernelResult<PlatformRegionMapping>.Fail(KernelError.PlatformFaulted,
                "The platform provider returned a mapping identity that does not match the request.");
        }

        PlatformRegionMapping mapping;
        MappingRecord mappingRecord;
        lock (_secureDomainLifecycleGate)
        {
            mapping = new PlatformRegionMapping(
                mappingIdentity,
                new PlatformRegionMappingGeneration(1), binding, region.Handle, access);
            mappingRecord = new MappingRecord(mapping, providerLease, authorityCapabilityId,
                providerIncarnation, backendEpoch)
            {
                LocalAuthorizationRevoked = true, // Known cleanup fact, not published permission.
            };
            if (!_domains.TryGetValue(binding.BindingId, out var routed) || !ReferenceEquals(routed, domainRecord) ||
                BackendEpoch != backendEpoch || !ValidateDomain(binding, expectedSubject).IsSuccess)
                mappingRecord.ClosureState = PlatformExternalClosureState.Faulted;
            AddOriginalMappingRecordLocked(domainRecord, mapping.MappingId, mappingRecord);
        }

        // Retain the original exact lease before querying a mutable external source.
        PlatformProviderIncarnation publicationIncarnation = default;
        var sourceAvailable = false;
        try { publicationIncarnation = CurrentProviderIncarnation(); sourceAvailable = true; }
        catch (Exception exception) when (exception is not StackOverflowException) { }
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !_domains.TryGetValue(binding.BindingId, out var routed) || !ReferenceEquals(routed, domainRecord) ||
                !ValidateDomain(binding, expectedSubject).IsSuccess ||
                (sourceAvailable && publicationIncarnation != providerIncarnation))
                mappingRecord.ClosureState = PlatformExternalClosureState.Faulted;
            if (sourceAvailable && mappingRecord.ClosureState == PlatformExternalClosureState.Active)
                mappingRecord.LocalAuthorizationRevoked = false;
            else
            {
                if (mappingRecord.ClosureState == PlatformExternalClosureState.Faulted)
                    mappingRecord.LocalAuthorizationRevoked = false; // Preserve existing fault error semantics.
                // Existing V1 faulted success returns its cleanup handle; keep its out-flag semantics.
                retainReservation = !sourceAvailable && mappingRecord.ClosureState == PlatformExternalClosureState.Active;
            }
        }
        return KernelResult<PlatformRegionMapping>.Ok(mapping);
        }
        finally
        {
            lock (_secureDomainLifecycleGate) domainRecord.PendingMappings--;
        }
    }

    private KernelResult ValidateMappingCleanupOwnerLocked(MappingRecord record,
        PlatformRegionMapping mapping, PlatformDomainIdentity subject)
    {
        if (!record.LocalReservationReleased && (_backendEpochExhausted || BackendEpoch != record.BackendEpoch))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping cleanup backend continuity was lost.");
        var identity = ValidateMappingClosureIdentityLocked(mapping, subject);
        if (!identity.IsSuccess) return identity;
        if (!_mappings.TryGetValue(mapping.MappingId, out var current) || !ReferenceEquals(current, record) ||
            !record.ClosureTransitionInFlight)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping cleanup owner or pending admission changed.");
        return KernelResult.Ok();
    }
    private KernelResult ValidateMappingClosureIdentityLocked(PlatformRegionMapping mapping, PlatformDomainIdentity subject)
    {
        var active = ValidateMappingIdentity(mapping, subject);
        return active.IsSuccess || !_mappings.TryGetValue(mapping.MappingId, out var record) || !record.LocalReservationReleased
            ? active : ValidateMappingIdentity(mapping, subject, requireActiveDomain: false);
    }
    private KernelResult<PlatformRegionMappingLifecycle> RunMappingClosureTransition(
        PlatformRegionMapping mapping, PlatformDomainIdentity subject,
        Func<MappingRecord, KernelResult<PlatformRegionMappingLifecycle>> transition)
    {
        MappingRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var exact = ValidateMappingClosureIdentityLocked(mapping, subject);
            if (!exact.IsSuccess) return KernelResult<PlatformRegionMappingLifecycle>.Fail(exact.Error, exact.Message!);
            record = _mappings[mapping.MappingId];
            if (record.ClosureTransitionInFlight)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformBindingDraining,
                    "Exact mapping closure transition is already in flight.");
            record.ClosureTransitionInFlight = true;
        }
        try { return transition(record); }
        finally { lock (_secureDomainLifecycleGate) record.ClosureTransitionInFlight = false; }
    }

    internal KernelResult<PlatformRegionMappingLifecycle> BeginRegionMappingRevocation(
        PlatformRegionMapping mapping, PlatformDomainIdentity expectedSubject, PlatformRegionRevocationPolicy policy)
        => RunMappingClosureTransition(mapping, expectedSubject,
            record => BeginRegionMappingRevocationCore(mapping, expectedSubject, policy, record));

    internal KernelResult<PlatformRegionMappingLifecycle> ObserveRegionMappingRevocation(
        PlatformRegionMapping mapping, PlatformDomainIdentity expectedSubject)
        => RunMappingClosureTransition(mapping, expectedSubject,
            record => ObserveRegionMappingRevocationCore(mapping, expectedSubject, record));
    private KernelResult<PlatformRegionMappingLifecycle> BeginRegionMappingRevocationCore(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformRegionRevocationPolicy policy, MappingRecord record)
    {
        if (HasActiveSecureRegion(mapping.MappingId))
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformBindingActive, "Secure-region binding must close before mapping revocation.");
        KernelResult validation;
        lock (_secureDomainLifecycleGate) validation = ValidateMappingCleanupOwnerLocked(record, mapping, expectedSubject);
        if (!validation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                validation.Error,
                validation.Message!);

        if (Dsc1OperationsForMapping(mapping).Length != 0)
        {
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformBindingActive,
                "DSC1 operations must close and release local reservations before their owned-region mapping.");
        }

        lock (_secureDomainLifecycleGate)
        {
            if (record.LocalReservationReleased ||
                record.ClosureState == PlatformExternalClosureState.Closed)
            {
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    KernelError.PlatformBindingRevoked,
                    "The platform region mapping has already reached verified closure.");
            }

            if (record.ClosureState == PlatformExternalClosureState.Faulted)
            {
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    KernelError.PlatformFaulted,
                    "The platform region mapping closure is faulted and remains non-reclaimable.");
            }

        }
        var generation = ValidateMappingClosureGeneration(record);
        if (!generation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                generation.Error, generation.Message!);

        if (record.ClosureState == PlatformExternalClosureState.Draining &&
            record.ClosureOperation is not null)
        {
            return ObserveRegionMappingRevocationCore(mapping, expectedSubject, record);
        }

        lock (_secureDomainLifecycleGate)
        {
            var exactOwner = ValidateMappingCleanupOwnerLocked(record, mapping, expectedSubject);
            if (!exactOwner.IsSuccess) return KernelResult<PlatformRegionMappingLifecycle>.Fail(exactOwner.Error, exactOwner.Message!);
            if (_backendEpochExhausted || BackendEpoch != record.BackendEpoch || record.LocalReservationReleased ||
                record.ClosureState == PlatformExternalClosureState.Closed ||
                record.ClosureState == PlatformExternalClosureState.Faulted)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted,
                    "Mapping source continuity was lost before revocation admission; local reclaim remains pinned.");
            if (HasActiveSecureRegion(mapping.MappingId))
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformBindingActive,
                    "Secure-region bind or unbind must settle before mapping revocation.");
            if (HasUnclosedGuestDependency(record))
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformBindingActive,
                    "Guest mapping admission or closure must settle before parent mapping revocation.");
            record.ClosureState = PlatformExternalClosureState.Draining;
        }

        if (_provider is not IPlatformRegionRevocationProvider revocationProvider)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (record.LegacyRevokeMayHaveEffect)
                    return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformUnsupported,
                        "The original legacy revoke may have taken effect; no completion contract permits repetition or reclaim.");
                var exactOwner = ValidateMappingCleanupOwnerLocked(record, mapping, expectedSubject);
                if (!exactOwner.IsSuccess)
                    return KernelResult<PlatformRegionMappingLifecycle>.Fail(exactOwner.Error, exactOwner.Message!);
                record.LegacyRevokeMayHaveEffect = true;
            }
            PlatformAuthorityResult legacyResult;
            try { legacyResult = _provider!.RevokeRegionMapping(record.ProviderLease, policy); }
            catch (Exception exception)
            {
                lock (_secureDomainLifecycleGate) record.ClosureState = PlatformExternalClosureState.Faulted;
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted,
                    $"The mapping provider threw during revoke; local reservation remains pinned: {exception.Message}");
            }
            // Retain the original outcome before any mutable source callback can refuse publication.
            lock (_secureDomainLifecycleGate) record.LegacyRevokeObservation = legacyResult;
            generation = ValidateMappingClosureGeneration(record);
            if (!generation.IsSuccess)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    generation.Error, generation.Message!);
            if (!legacyResult.IsSuccess)
            {
                if (legacyResult.Status == PlatformAuthorityStatus.Faulted)
                    lock (_secureDomainLifecycleGate) record.ClosureState = PlatformExternalClosureState.Faulted;

                return FromProviderFailure<PlatformRegionMappingLifecycle>(
                    legacyResult.Status,
                    legacyResult.Message);
            }

            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformUnsupported,
                "The provider does not expose a completion-backed region-revocation contract; local reclaim remains pinned despite synchronous provider revocation.");
        }

        PlatformAuthorityResult<PlatformRegionRevocationTicket> beginResult;
        try
        {
            beginResult = revocationProvider.BeginRegionMappingRevocation(
                record.ProviderLease, policy);
        }
        catch (Exception exception)
        {
            lock (_secureDomainLifecycleGate) record.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted,
                $"The mapping provider threw during revocation begin; local reservation remains pinned: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate) record.RevocationBeginObservation = beginResult;
        if (!beginResult.IsSuccess)
        {
            // Generic terminal status does not prove no effect for the original invocation.
            lock (_secureDomainLifecycleGate) record.ClosureState = PlatformExternalClosureState.Faulted;

            return FromProviderFailure<PlatformRegionMappingLifecycle>(
                beginResult.Status,
                beginResult.Message);
        }

        var ticket = beginResult.Value!;
        var ticketValidation = PlatformRegionRevocationContract.ValidateTicket(
            record.ProviderLease,
            ticket);
        if (!ticketValidation.IsSuccess)
        {
            record.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformFaulted,
                ticketValidation.Message ?? "The provider returned a malformed region-revocation ticket.");
        }

        record.ClosureOperation = ticket.Operation;
        generation = ValidateMappingClosureGeneration(record);
        if (!generation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(generation.Error, generation.Message!);
        return ObserveRegionMappingRevocationCore(mapping, expectedSubject, record);
    }

    private KernelResult<PlatformRegionMappingLifecycle> ObserveRegionMappingRevocationCore(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject, MappingRecord record)
    {
        lock (_secureDomainLifecycleGate)
        {
            var validation = ValidateMappingCleanupOwnerLocked(record, mapping, expectedSubject);
            if (!validation.IsSuccess)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    validation.Error,
                    validation.Message!);

            if (record.ClosureState == PlatformExternalClosureState.Active)
            {
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    KernelError.PlatformDenied,
                    "Platform region mapping revocation has not started.");
            }

            if (record.ClosureState == PlatformExternalClosureState.Faulted)
            {
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    KernelError.PlatformFaulted,
                    "The platform region mapping closure is faulted and remains non-reclaimable.");
            }

            if (record.LocalReservationReleased)
                return KernelResult<PlatformRegionMappingLifecycle>.Ok(record.Lifecycle);
            if (HasUnclosedGuestDependency(record))
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformBindingActive,
                    "Original guest dependency must close before parent mapping observation.");
            if (record.ClosureState == PlatformExternalClosureState.Closed)
                return KernelResult<PlatformRegionMappingLifecycle>.Ok(record.Lifecycle);

        }
        var generation = ValidateMappingClosureGeneration(record);
        if (!generation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                generation.Error, generation.Message!);

        PlatformOperationIdentity operation;
        bool knownReceipt;
        lock (_secureDomainLifecycleGate)
        {
            var exactOwner = ValidateMappingCleanupOwnerLocked(record, mapping, expectedSubject);
            if (!exactOwner.IsSuccess) return KernelResult<PlatformRegionMappingLifecycle>.Fail(exactOwner.Error, exactOwner.Message!);
            if (HasUnclosedGuestDependency(record) || record.ClosureState != PlatformExternalClosureState.Draining || record.LocalReservationReleased ||
                _backendEpochExhausted || BackendEpoch != record.BackendEpoch)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted, "Mapping observation admission changed during source callback.");
            if (record.ClosureOperation is not { } capturedOperation)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformBindingDraining,
                    "The platform region mapping is draining without a completion-capable operation; local reclaim remains pinned.");
            operation = capturedOperation;
            knownReceipt = record.KnownClosureReceipt is not null;
        }
        if (knownReceipt) return CommitMappingClosureObservation(record, PlatformExternalClosureState.Closed);
        if (_provider is not IPlatformCompletionProvider completionProvider)
        {
            record.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                KernelError.PlatformFaulted,
                "The provider returned a revocation operation but cannot observe its completion.");
        }

        PlatformAuthorityResult<PlatformCompletionReceipt> observed;
        try { observed = completionProvider.ObserveCompletion(operation); }
        catch (Exception exception)
        {
            record.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted,
                $"The mapping provider threw during completion observation; local reservation remains pinned: {exception.Message}");
        }
        if (!observed.IsSuccess)
        {
            if (observed.Status == PlatformAuthorityStatus.Faulted)
                record.ClosureState = PlatformExternalClosureState.Faulted;

            return FromProviderFailure<PlatformRegionMappingLifecycle>(
                observed.Status,
                observed.Message);
        }

        var receipt = observed.Value!;
        var receiptValidation = PlatformCompletionContract.ValidateReceiptIdentity(operation, receipt);
        if (!receiptValidation.IsSuccess)
        {
            if (receiptValidation.Status == PlatformAuthorityStatus.Faulted)
                record.ClosureState = PlatformExternalClosureState.Faulted;

            return FromProviderFailure<PlatformRegionMappingLifecycle>(
                receiptValidation.Status,
                receiptValidation.Message);
        }

        // Exact cleanup evidence survives a later unavailable source read; it is not
        // local reclaim authority until fresh source continuity is established.
        if (receipt.State == PlatformCompletionState.Closed)
            record.KnownClosureReceipt = receipt;
        else if (receipt.State == PlatformCompletionState.Faulted)
            record.ClosureState = PlatformExternalClosureState.Faulted;
        generation = ValidateMappingClosureGeneration(record);
        if (!generation.IsSuccess)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(generation.Error, generation.Message!);

        switch (receipt.State)
        {
            case PlatformCompletionState.Closed:
                return CommitMappingClosureObservation(record, PlatformExternalClosureState.Closed);
            case PlatformCompletionState.Faulted:
                record.ClosureState = PlatformExternalClosureState.Faulted;
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                    KernelError.PlatformFaulted,
                    "The provider reported a faulted region-revocation operation; local reclaim remains pinned.");
            default:
                return CommitMappingClosureObservation(record, PlatformExternalClosureState.Draining);
        }
    }

    private KernelResult<PlatformRegionMappingLifecycle> CommitMappingClosureObservation(
        MappingRecord record, PlatformExternalClosureState state)
    {
        // Source callbacks precede this local commit. Reset shares this gate and
        // cannot have its fault pin overwritten by an older observation.
        lock (_secureDomainLifecycleGate)
        {
            if (_backendEpochExhausted || BackendEpoch != record.BackendEpoch ||
                record.ClosureState == PlatformExternalClosureState.Faulted ||
                !_mappings.TryGetValue(record.Mapping.MappingId, out var current) ||
                !ReferenceEquals(current, record))
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted,
                    "Exact mapping closure owner or source changed before local commit; reclaim remains pinned.");
            var exact = ValidateMappingIdentity(record.Mapping, record.Mapping.DomainBinding.Subject);
            if (!exact.IsSuccess)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(exact.Error, exact.Message!);
            if (HasUnclosedGuestDependency(record))
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformBindingActive,
                    "Original guest dependency changed before parent mapping closure commit.");
            if (state == PlatformExternalClosureState.Closed && !HasValidatedMappingClosureFact(record))
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted,
                    "Original mapping closure fact changed before closure commit; reclaim remains pinned.");
            record.ClosureState = state;
            return KernelResult<PlatformRegionMappingLifecycle>.Ok(record.Lifecycle);
        }
    }

    internal KernelResult<PlatformRegionMappingLifecycle> QueryRegionMappingLifecycle(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_secureDomainLifecycleGate)
        {
        var validation = ValidateMappingIdentity(mapping, expectedSubject);
        if (!validation.IsSuccess)
        {
            // An already committed local release remains cleanup truth after
            // domain admission quarantine. Exact tuple is still mandatory;
            // ordinary ValidateMapping continues to require active permission.
            var cleanup = ValidateMappingIdentity(mapping, expectedSubject, requireActiveDomain: false);
            if (!cleanup.IsSuccess || !_mappings[mapping.MappingId].LocalReservationReleased)
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(
                validation.Error,
                validation.Message!);
        }

        return KernelResult<PlatformRegionMappingLifecycle>.Ok(
            _mappings[mapping.MappingId].Lifecycle);
        }
    }

    // This is a fresh source observation at the actual reclaim consumer, not
    // a permission receipt. Final typed local release still checks its owners.
    internal KernelResult ValidateRegionMappingReleaseSource(
        PlatformRegionMapping mapping, PlatformDomainIdentity subject)
    {
        MappingRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var exact = ValidateMappingIdentity(mapping, subject);
            if (!exact.IsSuccess)
            {
                var cleanup = ValidateMappingIdentity(mapping, subject, requireActiveDomain: false);
                if (cleanup.IsSuccess && _mappings[mapping.MappingId].LocalReservationReleased)
                    return KernelResult.Ok();
                return exact;
            }
            record = _mappings[mapping.MappingId];
            if (record.LocalReservationReleased) return KernelResult.Ok();
            if (HasUnclosedGuestDependency(record))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original guest dependency still pins mapping reclaim.");
            if (record.ClosureState == PlatformExternalClosureState.Faulted)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Mapping reclaim remains fault-pinned.");
            if (record.ClosureState != PlatformExternalClosureState.Closed)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Mapping closure must precede reclaim.");
        }

        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Mapping release source is unavailable; original closure fact and reservation are retained for retry.");
        }

        lock (_secureDomainLifecycleGate)
        {
            if (!_mappings.TryGetValue(mapping.MappingId, out var current) || !ReferenceEquals(current, record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original mapping release owner changed.");
            if (record.LocalReservationReleased) return KernelResult.Ok();
            if (_backendEpochExhausted || BackendEpoch != record.BackendEpoch ||
                source.Value == 0 || source != record.ProviderIncarnation ||
                record.ClosureState == PlatformExternalClosureState.Faulted)
            {
                record.ClosureState = PlatformExternalClosureState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Mapping release source changed; original cleanup receipt is retained and reclaim remains pinned.");
            }
            var exact = ValidateMappingIdentity(mapping, subject);
            if (!exact.IsSuccess) return exact;
            if (HasUnclosedGuestDependency(record))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original guest dependency changed during release source observation.");
            return record.ClosureState == PlatformExternalClosureState.Closed
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformBindingDraining, "Mapping closure changed before reclaim.");
        }
    }
    private KernelResult ValidateMappingClosureGeneration(MappingRecord record)
    {
        try
        {
            var current = CurrentProviderIncarnation();
            if (current.Value != 0 && current == record.ProviderIncarnation &&
                BackendEpoch == record.BackendEpoch)
                return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            if (record.ClosureOperation is not null && BackendEpoch == record.BackendEpoch)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Mapping provider generation is unavailable; exact cleanup facts are retained and local reclaim remains pinned.");
        }

        record.ClosureState = PlatformExternalClosureState.Faulted;
        return KernelResult.Fail(KernelError.PlatformFaulted,
            "The mapping provider or local backend generation changed; local reservation remains pinned.");
    }

    private bool TryCloseMalformedMappingLease(
        PlatformProviderRegionMappingLease lease,
        PlatformProviderIncarnation providerIncarnation,
        PlatformBackendEpoch backendEpoch)
    {
        try
        {
            var cleanup = _provider!.RevokeRegionMapping(lease,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke);
            return cleanup.IsSuccess && lease.MappingId.Value != 0 &&
                lease.Generation.Value != 0 &&
                CurrentProviderIncarnation() == providerIncarnation &&
                BackendEpoch == backendEpoch;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return false;
        }
    }

    // Kernel holds the exact Region gate before this bridge gate. This typed
    // local composition invokes no provider or delegated callback.
    internal KernelResult CommitRegionReservationRelease(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject,
        RegionAuthority regions)
    {
        lock (_secureDomainLifecycleGate)
        {
        var validation = ValidateMappingIdentity(mapping, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _mappings[mapping.MappingId];
        if (_backendEpochExhausted || BackendEpoch != record.BackendEpoch ||
            record.ClosureState == PlatformExternalClosureState.Faulted)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Exact mapping source changed before local release; Region reservation remains pinned.");
        if (record.ClosureState != PlatformExternalClosureState.Closed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "Local region reservation cannot be released before verified platform closure.");
        }

        if (record.LocalReservationReleased) return KernelResult.Ok();
        if (HasUnclosedGuestDependency(record))
            return KernelResult.Fail(KernelError.PlatformBindingActive, "Original guest dependency changed before Region reservation release.");
        if (record.AcquireInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "Admitted mapping acquire must settle before local reservation release.");
        var released = regions.ReleasePlatformMappingReservationLocked(mapping.Region,
            new RegionOwner(expectedSubject.DomainId, expectedSubject.ProcessGeneration));
        if (!released.IsSuccess) return released;
        record.LocalReservationReleased = true;
        return KernelResult.Ok();
        }
    }

    internal KernelResult RevokeRegionMapping(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformRegionRevocationPolicy policy)
    {
        var lifecycle = BeginRegionMappingRevocation(mapping, expectedSubject, policy);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        return lifecycle.Value!.PlatformClosure switch
        {
            PlatformExternalClosureState.Closed => KernelResult.Ok(),
            PlatformExternalClosureState.Faulted => KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform region mapping closure faulted."),
            _ => KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "The platform region mapping is still draining.")
        };
    }

    internal KernelResult ValidateMapping(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var validation = ValidateMappingIdentity(mapping, expectedSubject);
            if (!validation.IsSuccess) return validation;

            var record = _mappings[mapping.MappingId];
            if (record.PendingChildDrains != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Mapping child drain admission is in flight.");
            if (record.LocalAuthorizationRevoked)
            {
                return KernelResult.Fail(
                    KernelError.PlatformBindingRevoked,
                    "The local authorization backing this platform mapping has been revoked.");
            }

            return record.ClosureState switch
            {
                PlatformExternalClosureState.Active => KernelResult.Ok(),
                PlatformExternalClosureState.Draining => KernelResult.Fail(
                    KernelError.PlatformBindingDraining,
                    "The platform region mapping is draining and cannot authorize new effects."),
                PlatformExternalClosureState.Faulted => KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "The platform region mapping closure faulted and cannot authorize new effects."),
                _ => KernelResult.Fail(
                    KernelError.PlatformBindingRevoked,
                    "The platform region mapping has reached verified closure.")
            };
        }
    }

    internal IReadOnlyList<PlatformRegionMapping> BeginCapabilityRevocation(CapabilityId capabilityId, Func<CapabilityId, bool> dependsOnRevokedCapability)
    {
        lock (_secureDomainLifecycleGate)
        {
            var affected = OriginalMappingRecordsLocked()
                .Where(m => dependsOnRevokedCapability(m.AuthorityCapabilityId) && !m.LocalReservationReleased)
                .OrderBy(static m => m.Mapping.MappingId.Value)
                .ToArray();
            foreach (var record in affected)
                record.LocalAuthorizationRevoked = true;
            return affected.Select(static m => m.Mapping).ToArray();
        }
    }
    internal KernelResult RevokeExactMappingLocalAuthorization(
        PlatformRegionMapping mapping, PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var validation = ValidateMappingIdentity(mapping, subject);
            if (!validation.IsSuccess) return validation;
            _mappings[mapping.MappingId].LocalAuthorizationRevoked = true;
            return KernelResult.Ok();
        }
    }

    private static bool HasRetainedRootInvocation(DomainRecord record) =>
        record.ChildCreateMayHaveEffect || record.ChildCreateInvocations.Count != 0 ||
        record.DeviceBindMayHaveEffect || record.SecureCreateMayHaveEffect ||
        record.SecureCreateInvocations.Count != 0 || record.MappingMayHaveEffect ||
        record.ParentRevokeInFlight || record.ParentRevokeMayHaveEffect && !record.ProviderClosureConfirmed;

    internal bool HasActiveAuthority(PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
            return (_activeSubjects.TryGetValue(subject, out var original) &&
                    (original.AuthorityState != DomainAuthorityState.Closed || !original.ProviderClosureConfirmed ||
                     HasRetainedRootInvocation(original))) ||
                _domains.Values.Any(domain => domain.Binding.Subject == subject &&
                    (domain.AuthorityState != DomainAuthorityState.Closed || !domain.ProviderClosureConfirmed ||
                     HasRetainedRootInvocation(domain))) ||
                _originalChildOwners.Values.Concat(_childBindings.Values).Any(child => child.Binding.ParentBinding.Subject == subject &&
                    (child.State != PlatformChildDomainState.Closed || !HasValidatedChildClosureFact(child))) ||
                SecureDependencyRecordsLocked().Any(secure => !secure.ClosureConsumed && secure.Binding.Parent.Subject == subject) ||
                DeviceDependencyRecordsLocked().Any(device => !device.PlatformClosed && device.Lease.DomainBinding.Subject == subject) ||
                _originalGuestOwners.Values.Concat(_guestBindings.Values).Any(guest => guest.Mapping.Child.ParentBinding.Subject == subject &&
                    (guest.Closure != PlatformExternalClosureState.Closed || !HasValidatedGuestClosureFact(guest))) ||
                OriginalMappingRecordsLocked().Any(m => !m.LocalReservationReleased && m.Mapping.DomainBinding.Subject == subject);
    }

    internal bool HasActiveMapping(RegionHandle region)
    {
        lock (_secureDomainLifecycleGate)
            return OriginalMappingRecordsLocked().Any(
                m => !m.LocalReservationReleased && m.Mapping.Region == region);
    }

    private static bool HasValidatedMappingClosureFact(MappingRecord record) =>
        record.ClosureOperation is { } operation &&
        record.RevocationBeginObservation is { IsSuccess: true } begin &&
        PlatformRegionRevocationContract.ValidateTicket(record.ProviderLease, begin.Value!).IsSuccess &&
        begin.Value!.Operation == operation &&
        record.KnownClosureReceipt is { State: PlatformCompletionState.Closed } receipt &&
        PlatformCompletionContract.ValidateReceiptIdentity(operation, receipt).IsSuccess;

    private void AddOriginalMappingRecordLocked(DomainRecord original, PlatformRegionMappingId id, MappingRecord record)
    {
        record.OriginalDomain = original;
        original.MappingRecords.Add(record);
        _mappings.Add(id, record);
    }

    internal KernelResult<object> BindOriginalMappingBudgetOwner(
        PlatformRegionMapping mapping, bool expectsReservation, object? originalOwner)
    {
        lock (_secureDomainLifecycleGate)
        {
            // Retain a known receipt's original cleanup correlation even when
            // mutable root routes now refuse admission. This grants no usability.
            if (!_mappings.TryGetValue(mapping.MappingId, out var record) || record.Mapping != mapping ||
                record.OriginalDomain is not { } original ||
                !original.MappingRecords.Any(owned => ReferenceEquals(owned, record)))
                return KernelResult<object>.Fail(KernelError.PlatformFaulted, "Original mapping owner was lost before charge binding.");
            if (record.KernelBudgetBindingKnown)
                return record.ExpectsBudgetReservation == expectsReservation && ReferenceEquals(record.OriginalBudgetReservationOwner, originalOwner)
                    ? KernelResult<object>.Ok(record)
                    : KernelResult<object>.Fail(KernelError.PlatformFaulted, "Original mapping charge binding cannot be replaced.");
            record.KernelBudgetBindingKnown = true;
            record.ExpectsBudgetReservation = expectsReservation;
            record.OriginalBudgetReservationOwner = originalOwner;
            return KernelResult<object>.Ok(record);
        }
    }

    internal KernelResult<(bool Known, bool Expected, object? Owner, object MappingOwner)> QueryOriginalMappingBudgetOwner(PlatformRegionMapping mapping)
    {
        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateMappingIdentity(mapping, mapping.DomainBinding.Subject, requireActiveDomain: false);
            if (!identity.IsSuccess) return KernelResult<(bool, bool, object?, object)>.Fail(identity.Error, identity.Message!);
            var record = _mappings[mapping.MappingId];
            return KernelResult<(bool, bool, object?, object)>.Ok((record.KernelBudgetBindingKnown,
                record.ExpectsBudgetReservation || record.OriginalBudgetReservationOwner is not null,
                record.OriginalBudgetReservationOwner, record));
        }
    }

    private IEnumerable<MappingRecord> OriginalMappingRecordsLocked() =>
        _activeSubjects.Values.Concat(_domains.Values)
            .Concat(_mappings.Values.Where(record => record.OriginalDomain is not null).Select(record => record.OriginalDomain!))
            .Distinct<DomainRecord>(ReferenceEqualityComparer.Instance)
            .SelectMany(original => original.MappingRecords)
            .Distinct<MappingRecord>(ReferenceEqualityComparer.Instance);
    private KernelResult ValidateMappingIdentity(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject,
        bool requireActiveDomain = true)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (!_mappings.TryGetValue(mapping.MappingId, out var record))
                return KernelResult.Fail(
                    KernelError.PlatformBindingNotFound,
                    "The platform region mapping does not exist.");

            if (record.Mapping.Generation != mapping.Generation)
                return KernelResult.Fail(
                    KernelError.StaleGeneration,
                    "The platform region mapping generation is stale.");

            if (record.Mapping.DomainBinding.BindingId != mapping.DomainBinding.BindingId)
                return KernelResult.Fail(
                    KernelError.WrongPlatformDomain,
                    "The platform region mapping refers to a different local platform binding.");

            if (record.Mapping.DomainBinding.Generation != mapping.DomainBinding.Generation)
                return KernelResult.Fail(
                    KernelError.StaleGeneration,
                    "The platform region mapping domain-binding generation is stale.");

            if (record.Mapping.DomainBinding.Subject != mapping.DomainBinding.Subject)
                return KernelResult.Fail(
                    KernelError.WrongPlatformDomain,
                    "The platform region mapping domain subject does not match the active mapping.");

            if (record.Mapping.Region.RegionId != mapping.Region.RegionId)
                return KernelResult.Fail(
                    KernelError.WrongPlatformDomain,
                    "The platform region mapping refers to a different local region.");

            if (record.Mapping.Region.Generation != mapping.Region.Generation)
                return KernelResult.Fail(
                    KernelError.StaleGeneration,
                    "The platform region mapping region generation is stale.");

            if (record.Mapping.Access != mapping.Access)
                return KernelResult.Fail(
                    KernelError.PlatformDenied,
                    "The platform region mapping access does not match the active mapping.");

            if (!requireActiveDomain && record.LocalReservationReleased)
            {
                // Consumed lifetime truth needs its original owner/fact, not fresh
                // permission from a later parent routing generation.
                var consumedOriginal = record.OriginalDomain;
                var retained = consumedOriginal is not null && consumedOriginal.Binding == record.Mapping.DomainBinding &&
                    record.Mapping.DomainBinding.Subject == expectedSubject &&
                    consumedOriginal.MappingRecords.Any(owned => ReferenceEquals(owned, record)) &&
                    ((_domains.TryGetValue(mapping.DomainBinding.BindingId, out var consumedParent) && ReferenceEquals(consumedParent, consumedOriginal)) ||
                     (_activeSubjects.TryGetValue(expectedSubject, out var consumedCanonical) && ReferenceEquals(consumedCanonical, consumedOriginal)));
                return retained && record.ClosureState == PlatformExternalClosureState.Closed && HasValidatedMappingClosureFact(record)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.PlatformFaulted, "Consumed mapping lost its original owner or exact closure fact.");
            }
            var domain = requireActiveDomain ? ValidateDomain(record.Mapping.DomainBinding, expectedSubject)
                : ValidateDomainIdentity(record.Mapping.DomainBinding, expectedSubject);
            if (!domain.IsSuccess) return domain;
            if (record.OriginalDomain is not { } original ||
                !original.MappingRecords.Any(owned => ReferenceEquals(owned, record)) ||
                !_domains.TryGetValue(mapping.DomainBinding.BindingId, out var parent) ||
                !ReferenceEquals(parent, original) ||
                (!record.LocalReservationReleased &&
                    (!_activeSubjects.TryGetValue(expectedSubject, out var canonical) || !ReferenceEquals(canonical, original))))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Original mapping owner continuity was lost; closure and reclaim remain pinned.");
            if (record.ClosureState == PlatformExternalClosureState.Closed && !HasValidatedMappingClosureFact(record))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Closed mapping projection lacks the original validated closure fact; reclaim remains pinned.");
            return KernelResult.Ok();
        }
    }

    private PlatformDomainBinding AddDomainRecord(
        PlatformDomainIdentity subject,
        PlatformProviderDomainLease providerLease,
        DomainAuthorityState authorityState, PlatformProviderIncarnation providerIncarnation, PlatformBackendEpoch backendEpoch)
    {
        var binding = new PlatformDomainBinding(
            new PlatformDomainBindingId(_nextDomainBindingId++),
            new PlatformDomainBindingGeneration(1),
            subject);
        var record = new DomainRecord(binding, providerLease)
        {
            AuthorityState = authorityState,
            ProviderIncarnation = providerIncarnation,
            BackendEpoch = backendEpoch,
        };
        _domains.Add(binding.BindingId, record);
        _activeSubjects.Add(subject, record);
        return binding;
    }

    private static void QuarantineDomain(DomainRecord record)
    {
        if (record.AuthorityState != DomainAuthorityState.Closed)
            record.AuthorityState = DomainAuthorityState.Quarantined;
    }

    private static bool RequiresDomainQuarantine(PlatformAuthorityStatus status) =>
        status is PlatformAuthorityStatus.Stale or
            PlatformAuthorityStatus.Revoked or
            PlatformAuthorityStatus.WrongDomain or
            PlatformAuthorityStatus.Faulted;

    private void CloseDomain(DomainRecord record)
    {
        record.AuthorityState = DomainAuthorityState.Closed;
        ReleaseActiveSubject(record);
    }

    private void ReleaseActiveSubject(DomainRecord record)
    {
        if (_activeSubjects.TryGetValue(record.Binding.Subject, out var original) &&
            ReferenceEquals(original, record))
        {
            _activeSubjects.Remove(record.Binding.Subject);
        }
    }

    private bool Supports(PlatformAuthorityFeatures feature) =>
        _provider is not null &&
        (_provider.Descriptor.Features & feature) == feature;

    private static bool IsValidAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;

    private static KernelResult FromProviderFailure(
        PlatformAuthorityStatus status,
        string? message) =>
        KernelResult.Fail(ToKernelError(status), message ?? $"Platform provider returned {status}.");

    private static KernelResult<T> FromProviderFailure<T>(
        PlatformAuthorityStatus status,
        string? message) =>
        KernelResult<T>.Fail(ToKernelError(status), message ?? $"Platform provider returned {status}.");

    private static KernelError ToKernelError(PlatformAuthorityStatus status) =>
        status switch
        {
            PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
            PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
            PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
            PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
            PlatformAuthorityStatus.Revoked => KernelError.PlatformBindingRevoked,
            PlatformAuthorityStatus.WrongDomain => KernelError.WrongPlatformDomain,
            PlatformAuthorityStatus.Faulted => KernelError.PlatformFaulted,
            _ => KernelError.PlatformFaulted
        };
}


