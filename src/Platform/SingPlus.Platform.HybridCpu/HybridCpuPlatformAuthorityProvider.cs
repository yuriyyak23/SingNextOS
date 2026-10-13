using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformAuthorityProvider,
    IPlatformFeatureProvider,
    IPlatformDomainExecutionProvider,
    IPlatformChildCreateDispositionProvider,
    IPlatformDomainRevocationNoEffectProvider,
    IPlatformRegionRevocationNoEffectProvider,
    IPlatformOriginalAdmissionResolutionProvider,
    IPlatformOriginalDomainClosureProvider,
    IPlatformOriginalDomainClosureContinuationProvider,
    IPlatformExecutableArtifactReleaseProvider,
    IPlatformOriginalArtifactBindProvider, IPlatformExecutablePrivateDataQuotaProvider, IPlatformExecutionTerminalProvider
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
        public List<ChildCreateFailure> OriginalChildCreates { get; } = [];
        public bool RootCloseInFlight { get; set; }
        public bool RootCloseUncertain { get; set; }
        public NeutralDomainCloseResult? RootCloseObservation { get; set; }
        public NeutralOriginalDomainCloseRequest? RootCloseRequest { get; set; }
        public NeutralOriginalDomainCloseObservation? RootCloseEnvelope { get; set; }
        public NeutralOriginalDomainCloseObservation? RootCloseNoEffectConsumed { get; set; }
        public bool ExternalChildCreateInvoked { get; set; }
        public NeutralExternalParentCloseRequest? ExternalParentCloseRequest { get; set; }
        public NeutralVirtualizationResult<NeutralExternalParentCloseReceipt>? ExternalParentCloseObservation { get; set; }
        public NeutralVirtualizationResult<NeutralExternalParentCloseReceipt>? ExternalParentCloseNoEffectConsumed { get; set; }
        public bool ExternalParentCloseInvoked { get; set; }
        public bool ExternalParentClosureConsumed { get; set; }
        public bool DeviceBindInFlight { get; set; }
        public DeviceBindFailure? FailedDeviceBind { get; set; }
        public List<DeviceBindFailure> OriginalDeviceAdmissions { get; } = [];
        public bool OwnedMapInFlight { get; set; }
        public bool CleanupOnly { get; set; }
        public OwnedMapFailure? OwnedMapAdmissionOwner { get; set; }
        public OwnedMapFailure? FailedOwnedMap { get; set; }
        public bool RootTransitionInFlight { get; set; }
        public bool RootTransitionUncertain { get; set; }
        public RootTransitionFact? RootTransition { get; set; }
    }

    private readonly INeutralDomainRuntime _runtime;
    private readonly Guid? _originalNativeMappingIncarnation;
    private readonly INeutralChildDomainProvider? _childRuntime;
    private readonly INeutralRuntimeFeatureProvider? _childFeatureSource;
    private readonly Dictionary<PlatformProviderDomainLeaseId, DomainRecord> _domains = [];
    private readonly Dictionary<PlatformDomainIdentity, DomainRecord> _activeSubjects = [];
    private sealed class RootBindRecord(PlatformProviderDomainLease lease)
    {
        public PlatformProviderDomainLease Lease { get; } = lease;
        public bool InFlight { get; set; } = true;
        public NeutralDomainBindResult? Observation { get; set; }
        public PlatformDomainBindRequest? Request { get; init; }
        public NeutralOriginalDomainBindRequest? NativeRequest { get; set; }
        public NeutralOriginalDomainBindObservation? NativeObservation { get; set; }
        public bool NoEffectConsumed { get; set; }
        public DomainRecord? OriginalDomain { get; set; }
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
        if (runtime is INeutralOriginalMappingClosureProvider native)
        {
            var incarnation = native.MappingClosureIncarnation;
            if (incarnation == Guid.Empty)
                throw new ArgumentException("Original native runtime incarnation must be non-empty.", nameof(runtime));
            _originalNativeMappingIncarnation = incarnation;
        }
        _childRuntime = childRuntime;
        if (childRuntime is INeutralOriginalVirtualIoProvider originalIo && runtime is INeutralOriginalDeviceLoanProvider deviceLoans)
            originalIo.AttachOriginalDeviceLoanProvider(deviceLoans);
        _childFeatureSource = childRuntime as INeutralRuntimeFeatureProvider;
        ImplementationProfile = runtime.ImplementationProfile;
        var neutralFeatures = runtime.QueryNeutralFeatures();
        Descriptor = new PlatformProviderDescriptor(
            new PlatformProviderId("hybridcpu-neutral"),
            5,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        _featureManifest = TranslateFeatures(ImplementationProfile, neutralFeatures, _childFeatureSource?.QueryNeutralFeatures(),runtime is INeutralOriginalDmaCopyProvider);
        if (runtime is INeutralOriginalDsc1CopyProvider)
            _featureManifest = new PlatformFeatureManifest(_featureManifest.Features.Concat([
                new PlatformFeatureDescriptor(PlatformFeatureFamily.Dsc1BulkCompute,
                    PlatformOriginalDsc1CopyBackingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)]));
    }

    public PlatformProviderDescriptor Descriptor { get; }
    public NeutralRuntimeImplementationProfile ImplementationProfile { get; }

    public PlatformFeatureManifest QueryFeatures() => _featureManifest;

    // Observed outside owner commit locks; equal tuples in another facade cannot
    // continue the original native lifetime captured by this provider.
    private PlatformAuthorityResult ValidateOriginalNativeMappingSource()
    {
        if (_originalNativeMappingIncarnation is not { } original) return PlatformAuthorityResult.Ok();
        try
        {
            return _runtime is INeutralOriginalMappingClosureProvider native && native.MappingClosureIncarnation == original
                ? PlatformAuthorityResult.Ok()
                : PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original native runtime incarnation changed.");
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                $"Original native runtime source is unavailable: {exception.Message}");
        }
    }

    private static PlatformFeatureManifest TranslateFeatures(
        NeutralRuntimeImplementationProfile implementationProfile,
        NeutralRuntimeFeatureManifest neutral,
        NeutralRuntimeFeatureManifest? child,bool originalCopy)
    {
        var translated = new List<PlatformFeatureDescriptor>();
        Add(NeutralRuntimeFeatureFamily.DomainLifecycle, PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.OwnedRegionMapping, PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.DeviceBinding, PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion);
        Add(NeutralRuntimeFeatureFamily.DmaAdmission, PlatformFeatureFamily.DmaMapping, originalCopy ? PlatformDmaCopySubmissionContract.ContractVersion : PlatformDmaGrantContract.ContractVersion);
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
        AddChild(NeutralRuntimeFeatureFamily.NestedDomains, PlatformFeatureFamily.NestedDomains, PlatformNestedDomainContract.ContractVersion);
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
        PlatformDomainIdentity subject) => BindDomainCore(subject, null);

    public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainBindRequest request) => request.InvocationId == Guid.Empty
        ? PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Original Bind key is empty.")
        : BindDomainCore(request.Subject, request);

    private PlatformAuthorityResult<PlatformProviderDomainLease> BindDomainCore(PlatformDomainIdentity subject, PlatformDomainBindRequest? request)
    {
        var subjectValidation = PlatformDomainContract.ValidateSubject(subject);
        if (!subjectValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                subjectValidation.Status,
                subjectValidation.Message!);

        var source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, source.Message!);
        RootBindRecord invocation;
        lock (_childCreateCommitGate)
        {
            if (_activeSubjects.ContainsKey(subject))
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Denied,
                    "The platform subject already has an active HybridCPU binding.");
            if (request is { } intended && _originalRootBinds.Values.Any(x => x.Request?.InvocationId == intended.InvocationId))
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Original Bind key cannot be reused after consumption.");
            if (_originalRootBinds.Values.Any(original => original.Lease.Subject == subject && !original.NoEffectConsumed &&
                (original.OriginalDomain is not { Revoked: true, RootCloseObservation: { Decision: NeutralDomainCloseDecision.Closed or NeutralDomainCloseDecision.Revoked } })))
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original subject binding is live or unresolved; routing loss does not admit a second owner.");
            if (_rootBinds.ContainsKey(subject))
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original root binding is pending or unresolved; repeat effect refused.");
            if (_nextProviderDomainId is 0 or ulong.MaxValue)
                return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Provider root identity capacity exhausted before neutral binding.");
            invocation = new(new(new(_nextProviderDomainId++), new(1), subject)) { Request = request };
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
            NeutralDomainBindResult external;
            if (request is { } intended && _runtime is INeutralOriginalAdmissionProvider fenced &&
                _originalNativeMappingIncarnation is { } incarnation)
            {
                var nativeRequest = new NeutralOriginalDomainBindRequest(intended.InvocationId, incarnation, NeutralDomainProfile.OrdinaryService);
                lock (_childCreateCommitGate) invocation.NativeRequest = nativeRequest;
                var observation = fenced.Bind(nativeRequest);
                lock (_childCreateCommitGate) invocation.NativeObservation = observation;
                external = observation.Request == nativeRequest && observation.SourceFenceMatched
                    ? observation.Observation
                    : new(false, NeutralDomainBindDecision.Faulted, default, "Original native Bind source fence refused or response malformed.");
            }
            else external = _runtime.Bind(NeutralDomainProfile.OrdinaryService);
            DomainRecord? originalOutput;
            lock (_childCreateCommitGate)
            {
                invocation.Observation = external;
                if (external is { IsBound: true, Decision: NeutralDomainBindDecision.Bound } && external.Lease.Handle.Value != 0 && external.Lease.Epoch.Value != 0)
                {
                    invocation.OriginalDomain = new DomainRecord(invocation.Lease, external.Lease) { BindSource = invocation, CleanupOnly = true };
                    invocation.PublishedDomain = invocation.OriginalDomain;
                }
                originalOutput = invocation.OriginalDomain;
            }
            source = ValidateOriginalNativeMappingSource();
            if (!source.IsSuccess) return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(source.Status, source.Message!);
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
                var record = invocation.PublishedDomain!;
                if (!ReferenceEquals(record, originalOutput) || !ReferenceEquals(invocation.OriginalDomain, originalOutput))
                    return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind output owner changed before publication.");
                record.CleanupOnly = false;
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

        var source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(PlatformAuthorityStatus.NotAccepted, source.Message!);
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
            lock (_childCreateCommitGate) record.RootTransition = new(transition, external);
            source = ValidateOriginalNativeMappingSource();
            if (!source.IsSuccess) return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(source.Status, source.Message!);
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
        => RevokeDomainCore(lease, false);

    public PlatformAuthorityResult ResolveDomainClosure(PlatformProviderDomainLease lease)
        => RevokeDomainCore(lease, true);

    public PlatformAuthorityResult ContinueOriginalDomainClosure(PlatformProviderDomainLease lease)
        => RevokeDomainCore(lease, true, true);

    private PlatformAuthorityResult RevokeDomainCore(PlatformProviderDomainLease lease, bool resolutionOnly, bool continuePending = false)
    {
        DomainRecord record;
        lock (_childCreateCommitGate)
        {
        var validation = ValidateDomainIdentity(lease);
        if (!validation.IsSuccess) return validation;

        record = _domains[lease.LeaseId];
        if (resolutionOnly && (record.RootCloseRequest is null || _runtime is not INeutralOriginalDomainCloseProvider))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original native root Close request is unavailable.");
        if (record.Revoked)
        {
            if (resolutionOnly && record.RootCloseRequest is { } consumedRequest &&
                record.RootCloseEnvelope is { SourceFenceMatched: true } consumed && consumed.Request == consumedRequest &&
                consumed.Observation == record.RootCloseObservation &&
                consumed.Observation is { Decision: NeutralDomainCloseDecision.Closed or NeutralDomainCloseDecision.Revoked })
                return PlatformAuthorityResult.Ok(); // Already published exact lifetime; no new effect/admission.
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider domain lease has already been revoked.");
        }

        if (record.ChildCreateInFlight || record.FailedChildCreate is not null ||
            record.OwnedMapInFlight || record.FailedOwnedMap is not null ||
            record.DeviceBindInFlight || record.FailedDeviceBind is not null)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Original child create invocation remains in flight or pinned.");
        if (record.RootCloseInFlight || (record.RootCloseUncertain && record.RootCloseRequest is null) || record.RootTransitionInFlight)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Original root close is in flight or has an unknown outcome.");
        if (_originalProviderChildren.Values.Any(child => child.Lease.ParentDomainLease == lease &&
                (!HasPublishedChildClosure(child) || child.GuestMapInFlight || child.UnclosedGuestMap is not null)) ||
            _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease.ParentDomainLease == lease) ||
            _originalProviderArtifacts.Values.Any(artifact => !artifact.Released && artifact.Receipt.ParentLeaseId == lease.LeaseId &&
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
        if (record.ExternalChildCreateInvoked && _childRuntime is INeutralExternalParentClosureProvider)
            record.ExternalParentCloseRequest ??= new(Guid.NewGuid(), record.HybridCpuLease); // Composed intent before neutral Close effect.
        }
        try
        {
        NeutralDomainCloseResult external;
        var source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess)
        {
            lock (_childCreateCommitGate)
                return PlatformAuthorityResult.Fail(record.RootCloseRequest is null && record.RootCloseObservation is null && !record.RootCloseUncertain
                    ? PlatformAuthorityStatus.NotAccepted : PlatformAuthorityStatus.Faulted, source.Message!);
        }
        try
        {
            if (_runtime is INeutralOriginalDomainCloseProvider originalClose &&
                record.BindSource?.NativeRequest is { } bindRequest && _originalNativeMappingIncarnation is { } incarnation)
            {
                NeutralOriginalDomainCloseRequest request;
                bool resolve;
                lock (_childCreateCommitGate)
                {
                    if (!ValidateDomainIdentity(lease).IsSuccess || !_domains.TryGetValue(lease.LeaseId, out var routed) || !ReferenceEquals(routed, record))
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original root owner changed before Close invocation.");
                    resolve = record.RootCloseRequest is not null;
                    request = record.RootCloseRequest ?? new(Guid.NewGuid(), incarnation, bindRequest.InvocationId, record.HybridCpuLease);
                    record.RootCloseRequest = request; // Intent before possible effect.
                }
                var envelope = record.RootCloseEnvelope ?? (resolve ? originalClose.ResolveDomainClose(request) : originalClose.Close(request));
                lock (_childCreateCommitGate)
                {
                    record.RootCloseEnvelope = envelope; // Raw before any source callback.
                    if (envelope is not { } raw || raw.Request != request || raw.Observation is null)
                    {
                        record.RootCloseUncertain = true;
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original root Close outcome remains unknown.");
                    }
                    if (!raw.SourceFenceMatched && raw.Observation.Decision != NeutralDomainCloseDecision.Faulted)
                    {
                        record.RootCloseUncertain = true;
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Malformed original Close no-effect response.");
                    }
                    if (!raw.SourceFenceMatched)
                    {
                        // Exact no-effect in the addressed realm: no closure publication.
                        record.RootCloseNoEffectConsumed = raw;
                        record.RootCloseRequest = null;
                        record.RootCloseEnvelope = null;
                        record.RootCloseUncertain = false;
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Original root Close source fence refused before effect.");
                    }
                    external = raw.Observation;
                    record.RootCloseUncertain = false;
                }
            }
            else if (resolutionOnly) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original root Close lookup unsupported.");
            else external = record.RootCloseObservation ?? _runtime.Close(record.HybridCpuLease);
        }
        catch
        {
            lock (_childCreateCommitGate) record.RootCloseUncertain = true;
            throw;
        }
        lock (_childCreateCommitGate) record.RootCloseObservation = external;
        source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return source;
        if (external is { Decision: NeutralDomainCloseDecision.Closed or NeutralDomainCloseDecision.Revoked })
        {
            var parentClosure = CloseExternalParentLifetime(record, !resolutionOnly || continuePending);
            if (!parentClosure.IsSuccess) return parentClosure;
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
                    _originalProviderArtifacts.Values.Any(artifact => !artifact.Released && artifact.Receipt.ParentLeaseId == lease.LeaseId &&
                        artifact.Receipt.ParentGeneration == lease.Generation) || HasActiveProviderDeviceLeases(lease) ||
                    HasActiveProviderMappings(lease))
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Root close observation retained while local dependencies changed; publication refused.");
                MarkRevoked(record);
                return PlatformAuthorityResult.Ok();

            case NeutralDomainCloseDecision.ActiveDependents:
                record.RootCloseObservation = null;
                record.RootCloseEnvelope = null;
                record.RootCloseRequest = null;
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
        if (record.CleanupOnly || record.Revoked || record.ExternalRevocationObserved)
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
            source.Lease != record.Lease || !ReferenceEquals(source.PublishedDomain, record) || !ReferenceEquals(source.OriginalDomain, record))
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
