using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformAuthorityProvider,
    IPlatformFeatureProvider,
    IPlatformDomainExecutionProvider,
    IPlatformChildCreateDispositionProvider
{
    private sealed record RootTransitionFact(PlatformDomainExecutionTransition Transition, NeutralExecutionTransitionResult? Observation);
    private sealed class DomainRecord(
        PlatformProviderDomainLease lease,
        NeutralDomainBindingLease hybridCpuLease)
    {
        public PlatformProviderDomainLease Lease { get; } = lease;
        public NeutralDomainBindingLease HybridCpuLease { get; } = hybridCpuLease;
        public RootBindRecord? BindSource { get; init; }
        public bool ExternalRevocationObserved { get; set; }
        public bool Revoked { get; set; }
        public bool ChildCreateInFlight { get; set; }
        public ChildCreateFailure? FailedChildCreate { get; set; }
        public bool RootCloseInFlight { get; set; }
        public bool RootCloseUncertain { get; set; }
        public NeutralDomainCloseResult? RootCloseObservation { get; set; }
        public bool DeviceBindInFlight { get; set; }
        public DeviceBindFailure? FailedDeviceBind { get; set; }
        public bool OwnedMapInFlight { get; set; }
        public OwnedMapFailure? FailedOwnedMap { get; set; }
        public bool RootTransitionInFlight { get; set; }
        public bool RootTransitionUncertain { get; set; }
        public RootTransitionFact? RootTransition { get; set; }
    }

    private readonly INeutralDomainRuntime _runtime;
    private readonly INeutralChildDomainProvider? _childRuntime;
    private readonly INeutralRuntimeFeatureProvider? _childFeatureSource;
    private readonly Dictionary<PlatformProviderDomainLeaseId, DomainRecord> _domains = [];
    private readonly Dictionary<PlatformDomainIdentity, DomainRecord> _activeSubjects = [];
    private sealed class RootBindRecord(PlatformProviderDomainLease lease)
    {
        public PlatformProviderDomainLease Lease { get; } = lease;
        public bool InFlight { get; set; } = true;
        public NeutralDomainBindResult? Observation { get; set; }
        public DomainRecord? PublishedDomain { get; set; }
    }
    private readonly Dictionary<PlatformDomainIdentity, RootBindRecord> _rootBinds = [];
    // Original invocation and published owner references, not a second binding ledger.
    private readonly Dictionary<PlatformProviderDomainLeaseId, RootBindRecord> _originalRootBinds = [];
    private readonly PlatformFeatureManifest _featureManifest;
    private ulong _nextProviderDomainId = 1;

    public HybridCpuPlatformAuthorityProvider()
        : this(new NeutralDomainRuntimeFacade(), null)
    {
    }

    public HybridCpuPlatformAuthorityProvider(INeutralDomainRuntime runtime)
        : this(runtime, null)
    {
    }

    public HybridCpuPlatformAuthorityProvider(
        INeutralDomainRuntime runtime,
        INeutralChildDomainProvider? childRuntime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _childRuntime = childRuntime;
        _childFeatureSource = childRuntime as INeutralRuntimeFeatureProvider;
        ImplementationProfile = runtime.ImplementationProfile;
        var neutralFeatures = runtime.QueryNeutralFeatures();
        Descriptor = new PlatformProviderDescriptor(
            new PlatformProviderId("hybridcpu-neutral"),
            5,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        _featureManifest = TranslateFeatures(ImplementationProfile, neutralFeatures, _childFeatureSource?.QueryNeutralFeatures());
    }

    public PlatformProviderDescriptor Descriptor { get; }
    public NeutralRuntimeImplementationProfile ImplementationProfile { get; }

    public PlatformFeatureManifest QueryFeatures() => _featureManifest;

    private static PlatformFeatureManifest TranslateFeatures(
        NeutralRuntimeImplementationProfile implementationProfile,
        NeutralRuntimeFeatureManifest neutral,
        NeutralRuntimeFeatureManifest? child)
    {
        var translated = new List<PlatformFeatureDescriptor>();
        Add(NeutralRuntimeFeatureFamily.DomainLifecycle, PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.OwnedRegionMapping, PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.DeviceBinding, PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.DmaAdmission, PlatformFeatureFamily.DmaMapping, PlatformDmaGrantContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.MmioMapping, PlatformFeatureFamily.MmioMapping, PlatformMmioLeaseContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.InterruptBinding, PlatformFeatureFamily.IrqBinding, PlatformIrqBindingContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.ExplicitMemoryVisibility, PlatformFeatureFamily.ExplicitMemoryVisibility, PlatformRegionAcquireContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildGuestMemory, PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildGuestMemory, PlatformFeatureFamily.GuestMemory, PlatformGuestMemoryContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildEventDelivery, PlatformFeatureFamily.ChildEventDelivery, PlatformVirtualEventContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildEventDelivery, PlatformFeatureFamily.VirtualEvents, PlatformVirtualEventContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildTrapDelivery, PlatformFeatureFamily.ChildTrapDelivery, PlatformVirtualTrapContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildTrapDelivery, PlatformFeatureFamily.VirtualTraps, PlatformVirtualTrapContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.BoundedVirtualIo, PlatformFeatureFamily.BoundedVirtualIo, PlatformVirtualIoContract.ContractVersion);
        AddChild(NeutralRuntimeFeatureFamily.ChildExecutableArtifact, PlatformFeatureFamily.ChildExecutableArtifact, PlatformChildExecutionContract.ContractVersion);
        if (child?.Resolve(NeutralRuntimeFeatureFamily.ChildExecutableArtifact).Availability == NeutralRuntimeFeatureAvailability.Executable &&
            child.Resolve(NeutralRuntimeFeatureFamily.BoundedVirtualIo).Availability == NeutralRuntimeFeatureAvailability.Executable)
            translated.Add(new(PlatformFeatureFamily.VirtualizationDomains, PlatformChildExecutionContract.ContractVersion, PlatformFeatureAvailability.Executable));
        return new PlatformFeatureManifest(translated);

        void Add(NeutralRuntimeFeatureFamily neutralFamily, PlatformFeatureFamily platformFamily, uint platformVersion)
        {
            var feature = neutral.Resolve(neutralFamily);
            if (feature.ContractVersion < NeutralRuntimeFeatureContracts.VersionFor(neutralFamily) ||
                feature.Availability == NeutralRuntimeFeatureAvailability.Unavailable)
                return;

            var availability = feature.Availability switch
            {
                NeutralRuntimeFeatureAvailability.RuntimeAdmission => PlatformFeatureAvailability.RuntimeAdmission,
                NeutralRuntimeFeatureAvailability.Executable when neutralFamily == NeutralRuntimeFeatureFamily.DmaAdmission => PlatformFeatureAvailability.RuntimeAdmission,
                NeutralRuntimeFeatureAvailability.Executable when implementationProfile == NeutralRuntimeImplementationProfile.ExecutableAdapter => PlatformFeatureAvailability.Executable,
                NeutralRuntimeFeatureAvailability.Executable => PlatformFeatureAvailability.RuntimeAdmission,
                _ => throw new InvalidOperationException("The neutral feature manifest contains an unsupported availability."),
            };
            translated.Add(new PlatformFeatureDescriptor(platformFamily, platformVersion, availability));
        }

        void AddChild(NeutralRuntimeFeatureFamily neutralFamily, PlatformFeatureFamily platformFamily, uint platformVersion)
        {
            if (child is null) return;
            var feature = child.Resolve(neutralFamily);
            if (feature.ContractVersion < NeutralRuntimeFeatureContracts.VersionFor(neutralFamily) ||
                feature.Availability == NeutralRuntimeFeatureAvailability.Unavailable) return;
            translated.Add(new(platformFamily, platformVersion,
                feature.Availability == NeutralRuntimeFeatureAvailability.Executable
                    ? PlatformFeatureAvailability.Executable
                    : PlatformFeatureAvailability.RuntimeAdmission));
        }
    }

    public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
        PlatformDomainIdentity subject)
    {
        var subjectValidation = PlatformDomainContract.ValidateSubject(subject);
        if (!subjectValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                subjectValidation.Status,
                subjectValidation.Message!);

        RootBindRecord invocation;
        lock (_childCreateCommitGate)
        {
            if (_activeSubjects.ContainsKey(subject))
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Denied,
                    "The platform subject already has an active HybridCPU binding.");
            if (_originalRootBinds.Values.Any(original => original.Lease.Subject == subject &&
                (original.PublishedDomain is not { Revoked: true, RootCloseObservation: { Decision: NeutralDomainCloseDecision.Closed or NeutralDomainCloseDecision.Revoked } })))
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original subject binding is live or unresolved; routing loss does not admit a second owner.");
            if (_rootBinds.ContainsKey(subject))
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original root binding is pending or unresolved; repeat effect refused.");
            if (_nextProviderDomainId is 0 or ulong.MaxValue)
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Provider root identity capacity exhausted before neutral binding.");
            invocation = new(new(new(_nextProviderDomainId++), new(1), subject));
            var capacity = checked(_originalRootBinds.Count + 1);
            _originalRootBinds.EnsureCapacity(capacity);
            _rootBinds.EnsureCapacity(checked(_rootBinds.Count + 1));
            _domains.EnsureCapacity(capacity);
            _activeSubjects.EnsureCapacity(capacity);
            _originalRootBinds.Add(invocation.Lease.LeaseId, invocation);
            _rootBinds.Add(subject, invocation);
        }
        try
        {
            var external = _runtime.Bind(NeutralDomainProfile.OrdinaryService);
            lock (_childCreateCommitGate)
            {
                invocation.Observation = external;
                if (!external.IsBound && external.Decision == NeutralDomainBindDecision.UnsupportedProfile && external.Lease == default)
                {
                    if (!_originalRootBinds.TryGetValue(invocation.Lease.LeaseId, out var original) || !ReferenceEquals(original, invocation))
                        return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                            "Original no-effect root binding owner changed; consumption refused.");
                    _originalRootBinds.Remove(invocation.Lease.LeaseId);
                    if (_rootBinds.TryGetValue(subject, out var pending) && ReferenceEquals(pending, invocation)) _rootBinds.Remove(subject);
                    return FromBindFailure(external);
                }
                if (!external.IsBound || external.Decision != NeutralDomainBindDecision.Bound || external.Lease.Handle.Value == 0 ||
                    external.Lease.Epoch.Value == 0 || !_originalRootBinds.TryGetValue(invocation.Lease.LeaseId, out var originalOwner) || !ReferenceEquals(originalOwner, invocation) ||
                    !_rootBinds.TryGetValue(subject, out var current) || !ReferenceEquals(current, invocation) ||
                    _activeSubjects.ContainsKey(subject) || _domains.ContainsKey(invocation.Lease.LeaseId))
                    return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original root outcome retained without exact binding publication or closure.");
                var record = new DomainRecord(invocation.Lease, external.Lease) { BindSource = invocation };
                invocation.PublishedDomain = record;
                _domains.Add(invocation.Lease.LeaseId, record);
                _activeSubjects.Add(subject, record);
                _rootBinds.Remove(subject);
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(invocation.Lease);
            }
        }
        finally { lock (_childCreateCommitGate) invocation.InFlight = false; }
    }

    public PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> TransitionDomainExecution(
        PlatformProviderDomainLease domainLease,
        PlatformDomainExecutionTransition transition)
    {
        var transitionValidation = PlatformDomainExecutionContract.ValidateTransition(transition);
        if (!transitionValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                transitionValidation.Status,
                transitionValidation.Message ?? "The platform execution transition is invalid.");
        }

        DomainRecord record;
        var neutralTransition = ToNeutralTransition(transition);
        lock (_childCreateCommitGate)
        {
            var validation = ValidateDomain(domainLease, allowRetainedTransition: true);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(validation.Status, validation.Message!);
            record = _domains[domainLease.LeaseId];
            if (record.RootTransitionInFlight || record.RootTransitionUncertain ||
                record.RootTransition is { } original && original.Transition != transition)
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original root transition is pending, unknown or belongs to a different request.");
            record.RootTransition ??= new(transition, null);
            record.RootTransitionInFlight = true;
        }
        try
        {
            NeutralExecutionTransitionResult external;
            try { external = record.RootTransition!.Observation ?? _runtime.TransitionExecution(record.HybridCpuLease, neutralTransition); }
            catch { lock (_childCreateCommitGate) record.RootTransitionUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.RootTransition = new(transition, external);
                var exactTuple = external.Lease == record.HybridCpuLease && external.Transition == neutralTransition;
                var success = exactTuple && external.IsTransitioned && external.Decision == NeutralExecutionTransitionDecision.Transitioned &&
                    external.State == ToNeutralState(PlatformDomainExecutionContract.ExpectedState(transition));
                var denied = exactTuple && !external.IsTransitioned && external.Decision == NeutralExecutionTransitionDecision.InvalidTransition && external.State == default;
                if (!success && !denied)
                {
                    record.RootTransitionUncertain = true;
                    if (!external.IsTransitioned && external.Decision == NeutralExecutionTransitionDecision.Revoked)
                        record.ExternalRevocationObserved = true;
                    return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                        external.Decision == NeutralExecutionTransitionDecision.Revoked ? PlatformAuthorityStatus.Revoked : PlatformAuthorityStatus.Faulted,
                        "Original root transition outcome retained without publication or closure.");
                }
                if (!_domains.TryGetValue(domainLease.LeaseId, out var current) || !ReferenceEquals(current, record) ||
                    current.Lease != domainLease || !ValidateDomain(domainLease, allowRetainedTransition: true).IsSuccess)
                    return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original transition retained without current owner publication.");
                record.RootTransition = null;
                if (denied)
                    return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(PlatformAuthorityStatus.Denied, external.Reason);
                return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Ok(new(record.Lease, transition, FromNeutralState(external.State)));
            }
        }
        finally { lock (_childCreateCommitGate) record.RootTransitionInFlight = false; }
    }

    public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
    {
        DomainRecord record;
        lock (_childCreateCommitGate)
        {
        var validation = ValidateDomainIdentity(lease);
        if (!validation.IsSuccess) return validation;

        record = _domains[lease.LeaseId];
        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider domain lease has already been revoked.");
        }

        if (record.ChildCreateInFlight || record.FailedChildCreate is not null ||
            record.OwnedMapInFlight || record.FailedOwnedMap is not null ||
            record.DeviceBindInFlight || record.FailedDeviceBind is not null)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Original child create invocation remains in flight or pinned.");
        if (record.RootCloseInFlight || record.RootCloseUncertain || record.RootTransitionInFlight)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Original root close is in flight or has an unknown outcome.");
        if (_originalProviderChildren.Values.Any(child => child.Lease.ParentDomainLease == lease &&
                (!HasPublishedChildClosure(child) || child.GuestMapInFlight || child.UnclosedGuestMap is not null)) ||
            _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease.ParentDomainLease == lease) ||
            _originalProviderArtifacts.Values.Any(artifact => artifact.Receipt.ParentLeaseId == lease.LeaseId &&
                artifact.Receipt.ParentGeneration == lease.Generation))
        {
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Original child guest mapping invocations or exact mapping facts must close before the provider domain lease.");
        }

        if (HasActiveProviderDeviceLeases(lease))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU device leases must close before the provider domain lease.");
        }

        if (HasActiveProviderMappings(lease))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU owned-region mappings must close before the provider domain lease.");
        }

        // A transition-time Revoked observation never substitutes for this exact
        // close call. The neutral runtime must still confirm Closed or that this
        // exact lease was already Revoked before provider closure is published.
        record.RootCloseInFlight = true;
        }
        try
        {
        NeutralDomainCloseResult external;
        try { external = record.RootCloseObservation ?? _runtime.Close(record.HybridCpuLease); }
        catch
        {
            lock (_childCreateCommitGate) record.RootCloseUncertain = true;
            throw;
        }
        lock (_childCreateCommitGate)
        {
        if (external is null)
        {
            record.RootCloseUncertain = true;
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Original root close returned no observation; dependencies remain pinned.");
        }
        record.RootCloseObservation = external;
        if (!_domains.TryGetValue(lease.LeaseId, out var current) || !ReferenceEquals(current, record) ||
            current.Lease != lease || !ValidateDomainIdentity(lease).IsSuccess || record.ChildCreateInFlight || record.FailedChildCreate is not null ||
            record.OwnedMapInFlight || record.FailedOwnedMap is not null ||
            record.DeviceBindInFlight || record.FailedDeviceBind is not null || record.RootTransitionInFlight)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Original root close observation retained without current owner continuity.");
        switch (external.Decision)
        {
            case NeutralDomainCloseDecision.Closed:
            case NeutralDomainCloseDecision.Revoked:
                if (_originalProviderChildren.Values.Any(child => child.Lease.ParentDomainLease == lease && !HasPublishedChildClosure(child)) ||
                    _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease.ParentDomainLease == lease) ||
                    _originalProviderArtifacts.Values.Any(artifact => artifact.Receipt.ParentLeaseId == lease.LeaseId &&
                        artifact.Receipt.ParentGeneration == lease.Generation) || HasActiveProviderDeviceLeases(lease) ||
                    HasActiveProviderMappings(lease))
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Root close observation retained while local dependencies changed; publication refused.");
                MarkRevoked(record);
                return PlatformAuthorityResult.Ok();

            case NeutralDomainCloseDecision.ActiveDependents:
                record.RootCloseObservation = null;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    external.Reason);

            case NeutralDomainCloseDecision.Stale:
                record.RootCloseUncertain = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU rejected the provider-owned domain lease as stale; closure is not proven.");

            case NeutralDomainCloseDecision.NotFound:
                record.RootCloseUncertain = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU no longer recognizes the provider-owned domain lease; closure is not proven.");

            case NeutralDomainCloseDecision.Faulted:
            default:
                record.RootCloseUncertain = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
        }
        }
        finally { lock (_childCreateCommitGate) record.RootCloseInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
        PlatformProviderDomainLease domainLease,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access)
    {
        var mapped = MapOwnedRegionSlice(
            domainLease,
            new PlatformRegionSlice(region, 0, region.ByteLength, access));
        return mapped.IsSuccess
            ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
            : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                mapped.Status,
                mapped.Message ?? "HybridCPU whole-region compatibility mapping failed.");
    }

    public PlatformAuthorityResult RevokeRegionMapping(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy)
    {
        if (!Enum.IsDefined(policy) || policy != PlatformRegionRevocationPolicy.DrainBeforeRevoke)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Only drain-before-revoke is supported for HybridCPU region mappings.");
        }

        return CloseProviderRegionMapping(mapping);
    }

    private PlatformAuthorityResult ValidateDomain(PlatformProviderDomainLease lease, bool allowRetainedTransition = false)
    {
        var identityValidation = ValidateDomainIdentity(lease);
        if (!identityValidation.IsSuccess) return identityValidation;

        var record = _domains[lease.LeaseId];
        if (record.Revoked || record.ExternalRevocationObserved)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider domain lease has already been revoked.");
        }

        if (record.RootCloseInFlight || record.RootCloseUncertain || record.RootCloseObservation is not null)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Original root close is in flight or retained; new provider admission is refused.");

        if (!allowRetainedTransition && (record.RootTransitionInFlight || record.RootTransitionUncertain || record.RootTransition is not null))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Original root transition is pending or retained; new provider admission is refused.");

        return PlatformAuthorityResult.Ok();
    }

    private PlatformAuthorityResult ValidateDomainIdentity(PlatformProviderDomainLease lease)
    {
        if (!_domains.TryGetValue(lease.LeaseId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider domain lease does not exist.");
        }

        if (record.Lease.Generation != lease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider domain lease generation is stale.");
        }

        if (record.Lease.Subject != lease.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider domain lease belongs to a different Sing platform subject.");
        }
        if (!_originalRootBinds.TryGetValue(lease.LeaseId, out var source) || !ReferenceEquals(record.BindSource, source) ||
            source.Lease != record.Lease || !ReferenceEquals(source.PublishedDomain, record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Provider domain route is not the captured original binding owner.");

        if (!record.Revoked && (!_activeSubjects.TryGetValue(lease.Subject, out var original) || !ReferenceEquals(original, record)))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Active subject index does not identify the original domain owner.");

        return PlatformAuthorityResult.Ok();
    }

    private static PlatformAuthorityResult<PlatformProviderDomainLease> FromBindFailure(
        NeutralDomainBindResult result)
    {
        var status = result.Decision switch
        {
            NeutralDomainBindDecision.UnsupportedProfile => PlatformAuthorityStatus.Unsupported,
            NeutralDomainBindDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
            status,
            result.Reason);
    }

    private void MarkRevoked(DomainRecord record)
    {
        record.Revoked = true;
        _activeSubjects.Remove(record.Lease.Subject);
    }

    private static NeutralExecutionTransition ToNeutralTransition(
        PlatformDomainExecutionTransition transition) =>
        transition switch
        {
            PlatformDomainExecutionTransition.Start => NeutralExecutionTransition.Start,
            PlatformDomainExecutionTransition.Park => NeutralExecutionTransition.Park,
            PlatformDomainExecutionTransition.Resume => NeutralExecutionTransition.Resume,
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    private static NeutralExecutionState ToNeutralState(PlatformDomainExecutionState state) =>
        state switch
        {
            PlatformDomainExecutionState.Ready => NeutralExecutionState.Ready,
            PlatformDomainExecutionState.Running => NeutralExecutionState.Running,
            PlatformDomainExecutionState.Parked => NeutralExecutionState.Parked,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static PlatformDomainExecutionState FromNeutralState(NeutralExecutionState state) =>
        state switch
        {
            NeutralExecutionState.Ready => PlatformDomainExecutionState.Ready,
            NeutralExecutionState.Running => PlatformDomainExecutionState.Running,
            NeutralExecutionState.Parked => PlatformDomainExecutionState.Parked,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

}
