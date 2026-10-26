using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public enum HybridCpuChildDomainAdapterReadiness
{
    ExternalBlocked = 0,
    Ready,
}

/// <summary>
/// Non-authoritative integration readiness evidence for the external child-domain ABI.
/// Feature admission remains exclusively in <see cref="PlatformFeatureManifest"/>.
/// </summary>
public sealed record HybridCpuChildDomainAdapterStatus(
    HybridCpuChildDomainAdapterReadiness Readiness,
    string ExternalRequirement,
    uint PlatformContractVersion,
    bool HasVersionedExternalChildContract,
    bool HasParentChildSubset,
    bool HasGuestMemory,
    bool HasVirtualEvents,
    bool HasVirtualTraps,
    bool HasVirtualIo,
    bool HasDefinitiveClose);

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformChildDomainProvider,
    IPlatformGuestMemoryProvider,
    IPlatformVirtualEventProvider,
    IPlatformVirtualIoProvider,
    IPlatformChildExecutionProvider,
    IPlatformOriginalChildCloseProvider
{
    private sealed class GuestMapFailure
    {
        public GuestMapFailure(PlatformProviderGuestRegionMappingLeaseId identity, NeutralGuestMappingRequest request, NeutralVirtualizationResult<NeutralGuestMappingLease>? observation)
        { OriginalOwner = this; InvocationId = identity; Request = request; Observation = observation; }
        public GuestMapFailure OriginalOwner { get; }
        public PlatformProviderGuestRegionMappingLeaseId InvocationId { get; }
        public NeutralGuestMappingRequest Request { get; }
        public NeutralVirtualizationResult<NeutralGuestMappingLease>? Observation { get; set; }
        public NeutralVirtualizationResult<NeutralGuestMappingLease>? Resolution { get; set; }
        public PlatformOriginalGuestMapRequest? OriginalRequest { get; init; }
        public NeutralOriginalGuestMapRequest? NativeRequest { get; init; }
        public ChildRecord? Child { get; init; }
        public ProviderMappingRecord? ParentMapping { get; init; }
        public DomainRecord? Parent { get; init; }
        public GuestRecord? Mapping { get; set; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
    }
    private sealed class ChildCloseFailure
    {
        public ChildCloseFailure(NeutralChildDomainLease request, NeutralVirtualizationResult<NeutralChildDomainCloseReceipt>? observation)
        { OriginalOwner = this; Request = request; Observation = observation; }
        public ChildCloseFailure OriginalOwner { get; }
        public NeutralChildDomainLease Request { get; }
        public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt>? Observation { get; set; }
        public bool NoEffectConsumed { get; set; }
    }
    private sealed class ChildCreateFailure
    {
        public ChildCreateFailure(PlatformProviderChildDomainLeaseId invocationId, NeutralDomainBindingLease parent, NeutralChildDomainIntent intent,
            NeutralVirtualizationResult<NeutralChildDomainLease>? observation)
        { OriginalOwner = this; InvocationId = invocationId; Parent = parent; Intent = intent; Observation = observation; }
        public ChildCreateFailure OriginalOwner { get; }
        public PlatformProviderChildDomainLeaseId InvocationId { get; }
        public NeutralDomainBindingLease Parent { get; }
        public NeutralChildDomainIntent Intent { get; }
        public NeutralVirtualizationResult<NeutralChildDomainLease>? Observation { get; set; }
        public NeutralVirtualizationResult<NeutralChildDomainLease>? Resolution { get; set; }
        public PlatformOriginalChildCreateRequest? OriginalRequest { get; init; }
        public NeutralOriginalChildCreateRequest? NativeRequest { get; init; }
        public DomainRecord? Source { get; init; }
        public ChildRecord? Child { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool PreviousExternalParentEffect { get; init; }
    }
    private sealed record ChildTransitionFailure(NeutralChildDomainLease Lease,
        NeutralChildDomainTransition Transition, NeutralVirtualizationResult? Observation);
    private sealed record ChildClosureSource(PlatformProviderChildDomainLease Lease, NeutralChildDomainLease Neutral);
    private sealed record GuestClosureSource(PlatformProviderGuestRegionMappingLease Lease, NeutralGuestMappingLease Neutral);
    private sealed record ChildRecord(PlatformProviderChildDomainLease Lease, NeutralChildDomainLease Neutral)
    {
        public DomainRecord? CreateParent { get; init; }
        public bool OriginalCreateRequired { get; init; }
        public bool OriginalNestedRequired { get; init; }
        public ChildRecord? NestedParent { get; init; }
        public ProviderNestedFact? NestedFactory { get; init; }
        public List<ProviderNestedFact> OriginalNested { get; } = [];
        public ulong NestedIssued { get; set; }
        public ulong IoAdmissionsIssued { get; set; }
        public ulong IoAdmissionsConsumed { get; set; }
        public List<ProviderEventFact> OriginalEvents { get; } = [];
        public ulong EventsIssued { get; set; }
        public GuestMapFailure? UnclosedGuestMap { get; set; }
        public List<GuestMapFailure> OriginalGuestMaps { get; } = [];
        public ulong OriginalGuestMapsIssued { get; set; }
        public bool GuestMapInFlight { get; set; }
        public bool CloseInFlight { get; set; }
        public NeutralChildDomainCloseReceipt? ClosureReceipt { get; set; }
        public ChildClosureSource? ClosureSource { get; set; }
        public bool ClosurePublished { get; set; }
        public ChildCloseFailure? FailedClose { get; set; }
        public ChildCloseFailure? OriginalCloseInvocation { get; set; }
        public bool TransitionInFlight { get; set; }
        public ChildTransitionFailure? FailedTransition { get; set; }
    }
    private sealed record GuestRecord(PlatformProviderGuestRegionMappingLease Lease, NeutralGuestMappingLease Neutral)
    {
        public ChildRecord? MapChild { get; init; }
        public ProviderMappingRecord? MapSource { get; init; }
        public DomainRecord? MapParent { get; init; }
        public bool AdmissionPublished { get; init; } = true;
        public bool UnmapInFlight { get; set; }
        public GuestUnmapFailure? FailedUnmap { get; set; }
        public GuestUnmapFailure? OriginalUnmapInvocation { get; set; }
        public NeutralGuestMappingCloseReceipt? ClosureReceipt { get; set; }
        public GuestClosureSource? ClosureSource { get; set; }
        public bool ClosurePublished { get; set; }
        public bool ArtifactBindInFlight { get; set; }
        public ArtifactBindFailure? FailedArtifactBind { get; set; }
        public ArtifactBindFailure? OriginalArtifactBindInvocation { get; set; }
        public PlatformExecutableArtifactRequest? OriginalArtifactBindRequest { get; set; }
        public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt>? ArtifactBindResolutionObservation { get; set; }
    }
    private sealed class ArtifactBindFailure(PlatformExecutableArtifactId invocationId,
        NeutralExecutableArtifactRequest request, string contentDigest)
    {
        public ArtifactBindFailure? OwnerSelf { get; private set; }
        public void EstablishSelf() => OwnerSelf = this;
        public bool NoEffectConsumed { get; set; }
        public PlatformExecutableArtifactId InvocationId { get; } = invocationId;
        public NeutralExecutableArtifactRequest Request { get; } = request;
        public string ContentDigest { get; } = contentDigest;
        public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt>? Observation { get; set; }
    }
    private sealed class GuestUnmapFailure
    {
        public GuestUnmapFailure(NeutralGuestMappingLease request, NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt>? observation)
        { OriginalOwner = this; Request = request; Observation = observation; }
        public GuestUnmapFailure OriginalOwner { get; }
        public NeutralGuestMappingLease Request { get; }
        public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt>? Observation { get; set; }
        public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt>? Resolution { get; set; }
        public bool Invoked { get; set; }
    }
    private bool HasGuestUnmapInterlock(PlatformProviderChildDomainLease child) =>
        _originalProviderGuestMappings.Values.Where(mapping => !HasPublishedGuestClosure(mapping)).Any(mapping => mapping.Lease.ChildLease == child &&
            (mapping.UnmapInFlight || mapping.FailedUnmap is not null));
    private sealed class IoRecord(PlatformProviderVirtualIoLease lease, PlatformVirtualIoRequest request, ChildRecord child, DeviceLeaseRecord device)
    {
        public PlatformProviderVirtualIoLease Lease { get; } = lease;
        public PlatformVirtualIoRequest Request { get; } = request;
        public ChildRecord Child { get; } = child;
        public DeviceLeaseRecord Device { get; } = device;
        public DomainRecord? Parent { get; } = child.CreateParent;
        public RootBindRecord? ParentFactory { get; } = child.CreateParent?.BindSource;
        public DeviceBindFailure? DeviceFactory { get; } = device.AdmissionSource;
        public NeutralVirtualIoLease Neutral { get; set; }
        public NeutralOriginalVirtualIoRequest? NativeOriginalRequest { get; set; }
        public PlatformOriginalVirtualIoBindRequest? BindAdmission { get; set; }
        public NeutralVirtualizationResult<NeutralOriginalVirtualIoBindCleanupClosure>? BindCleanupRaw { get; set; }
        public NeutralOriginalVirtualIoBindCleanupClosure? ValidatedBindCleanup { get; set; }
        public PlatformOriginalVirtualIoBindCleanupClosure? BindCleanup { get; set; }
        public PlatformOriginalVirtualIoBindCleanupClosure? OriginalBindCleanup { get; set; }
        public NeutralVirtualizationResult<NeutralVirtualIoLease>? Admission { get; set; }
        public IoCloseObservation? Completion { get; set; }
        public IoCloseObservation? OriginalCompletion { get; set; }
        public PlatformOriginalVirtualIoCloseRequest? CloseAdmission { get; set; }
        public NeutralOriginalVirtualIoCloseRequest? NativeCloseAdmission { get; set; }
        public List<ProviderIoTransferFact> Transfers { get; } = [];
        public ulong TransfersIssued { get; set; }
        public bool TransferInFlight { get; set; }
        public bool BindInFlight { get; set; }
        public bool Published { get; set; }
        public bool CloseInvoked { get; set; }
        public bool CloseInFlight { get; set; }
        public bool Consumed { get; set; }
        public bool SourceFaulted { get; set; }
        public void EstablishSelf() => _ownerSelf = this;
        private IoRecord? _ownerSelf;
        public bool IsOriginal => ReferenceEquals(_ownerSelf, this);
    }
    // Custody belongs to the existing IO owner. The value-shaped V1 receipt is
    // evidence only inside this exact observation, retained before callbacks.
    private sealed class IoCloseObservation
    {
        private readonly IoCloseObservation _self;
        public IoCloseObservation(IoRecord source, PlatformOriginalVirtualIoCloseRequest? admission,
            NeutralOriginalVirtualIoCloseRequest? nativeAdmission, NeutralVirtualIoLease lease,
            NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> result)
        {
            _self = this; Source = source; Admission = admission; NativeAdmission = nativeAdmission;
            Lease = lease; Result = result;
        }
        public IoRecord Source { get; }
        public PlatformOriginalVirtualIoCloseRequest? Admission { get; }
        public NeutralOriginalVirtualIoCloseRequest? NativeAdmission { get; }
        public NeutralVirtualIoLease Lease { get; }
        public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> Result { get; }
        public bool IsOriginal => ReferenceEquals(_self, this);
    }
    private sealed record ArtifactRecord(PlatformExecutableArtifactReceipt Receipt, NeutralExecutableArtifactReceipt Neutral)
    {
        public bool AdmissionPublished { get; init; } = true;
        public GuestRecord? BindMapping { get; init; }
        public bool ExecutionInFlight { get; set; }
        public ExecutionAttempt? OriginalExecution { get; set; }
        public List<ExecutionAttempt> ExecutionHistory { get; } = [];
        public ulong ExecutionsIssued { get; set; }
        public bool ExecutionPublished { get; set; }
        public PlatformArtifactReleaseRequest? ReleaseRequest { get; set; }
        public NeutralArtifactReleaseRequest? NativeReleaseRequest { get; set; }
        public NeutralVirtualizationResult<NeutralArtifactReleaseReceipt>? ReleaseObservation { get; set; }
        public NeutralVirtualizationResult<NeutralArtifactReleaseReceipt>? ReleaseResolutionObservation { get; set; }
        public (PlatformArtifactReleaseRequest Request, NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> Observation)? OriginalRelease { get; set; }
        public bool ReleaseInFlight { get; set; }
        public bool ReleaseInvoked { get; set; }
        public bool Released { get; set; }
    }
    private sealed class ExecutionAttempt(NeutralChildExecutionStartRequest request,ArtifactRecord source,ChildRecord child,GuestRecord mapping)
    {
        public ExecutionAttempt? OwnerSelf { get; private set; }
        public void EstablishSelf() => OwnerSelf = this;
        public NeutralChildExecutionStartRequest Request { get; } = request;
        public ArtifactRecord Source { get; } = source;
        public ChildRecord Child { get; } = child;
        public GuestRecord Mapping { get; } = mapping;
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public NeutralVirtualizationResult<NeutralChildExecutionReceipt>? Observation { get; set; }
        public NeutralVirtualizationResult<NeutralExecutionTerminalObservation>? TerminalObservation { get; set; }
    }
    private static bool OriginalExecutionHistoryIntact(ArtifactRecord artifact) =>
        (ulong)artifact.ExecutionHistory.Count == artifact.ExecutionsIssued &&
        artifact.ExecutionHistory.All(attempt => ReferenceEquals(attempt.OwnerSelf,attempt) && ReferenceEquals(attempt.Source,artifact)) &&
        (artifact.OriginalExecution is null ? artifact.ExecutionsIssued == 0 :
            artifact.ExecutionHistory.Count > 0 && ReferenceEquals(artifact.ExecutionHistory[^1],artifact.OriginalExecution));
    private bool HasArtifactBindInterlock(PlatformProviderChildDomainLease child, GuestRecord? binding = null, ArtifactRecord? execution = null, ChildRecord? transition = null) =>
        _originalProviderChildren.Values.Any(record => record.Lease == child &&
            (record.TransitionInFlight && !ReferenceEquals(record, transition) || record.FailedTransition is not null)) ||
        _originalProviderGuestMappings.Values.Where(mapping => !HasPublishedGuestClosure(mapping)).Any(mapping => mapping.Lease.ChildLease == child &&
            ((mapping.ArtifactBindInFlight && !ReferenceEquals(mapping, binding)) || mapping.FailedArtifactBind is not null)) ||
        _originalProviderArtifacts.Values.Any(artifact => !HasRetainedArtifactRelease(artifact) && (artifact.ReleaseInFlight || artifact.ReleaseInvoked || artifact.ReleaseRequest is not null || artifact.NativeReleaseRequest is not null || !artifact.AdmissionPublished || (artifact.ExecutionInFlight && !ReferenceEquals(artifact, execution)) ||
                (!OriginalExecutionHistoryIntact(artifact) || artifact.OriginalExecution is { NoEffectConsumed: false } && !artifact.ExecutionPublished && !ReferenceEquals(artifact, execution))) &&
            artifact.Receipt.ChildLeaseId == child.LeaseId && artifact.Receipt.ChildGeneration == child.Generation);
    private readonly Dictionary<PlatformProviderChildDomainLeaseId, ChildRecord> _providerChildren = [];
    private readonly Dictionary<PlatformProviderGuestRegionMappingLeaseId, GuestRecord> _providerGuestMappings = [];
    private readonly Dictionary<PlatformProviderChildDomainLeaseId, ChildRecord> _originalProviderChildren = [];
    private readonly Dictionary<PlatformProviderGuestRegionMappingLeaseId, GuestRecord> _originalProviderGuestMappings = [];
    private readonly Dictionary<PlatformProviderVirtualIoLeaseId, IoRecord> _providerVirtualIo = [];
    private readonly Dictionary<PlatformProviderVirtualIoLeaseId, IoRecord> _originalProviderVirtualIo = [];
    private readonly Dictionary<PlatformExecutableArtifactId, ArtifactRecord> _providerArtifacts = [];
    private readonly Dictionary<PlatformExecutableArtifactId, ArtifactRecord> _originalProviderArtifacts = [];
    private readonly object _childCreateCommitGate = new();
    private ulong _nextProviderChildId = 1;
    private ulong _nextProviderGuestId = 1;
    private ulong _nextProviderIoId = 1;
    private ulong _nextProviderArtifactId = 1;
    private static readonly HybridCpuChildDomainAdapterStatus BlockedChildDomainStatus = new(
        HybridCpuChildDomainAdapterReadiness.ExternalBlocked,
        "EXT-HCPU-006",
        PlatformChildDomainContract.ContractVersion,
        HasVersionedExternalChildContract: true,
        HasParentChildSubset: true,
        HasGuestMemory: true,
        HasVirtualEvents: true,
        HasVirtualTraps: true,
        HasVirtualIo: false,
        HasDefinitiveClose: true);

    public HybridCpuChildDomainAdapterStatus QueryChildDomainAdapterStatus() =>
        _childRuntime is null ? BlockedChildDomainStatus : BlockedChildDomainStatus with
        {
            Readiness = HybridCpuChildDomainAdapterReadiness.Ready,
            ExternalRequirement = string.Empty,
            HasVirtualIo = _childRuntime is INeutralVirtualIoProvider &&
                _childRuntime is INeutralRuntimeFeatureProvider features &&
                features.QueryNeutralFeatures().Resolve(NeutralRuntimeFeatureFamily.BoundedVirtualIo).Availability !=
                    NeutralRuntimeFeatureAvailability.Unavailable,
        };

    public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
        PlatformProviderDomainLease parentLease,
        PlatformChildDomainIntent intent)
        => CreateChildDomain(parentLease, intent, out _);

    public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
        PlatformProviderDomainLease parentLease, PlatformChildDomainIntent intent,
        out bool refusedBeforeEffect)
        => SupportsOriginalChildCreateResolution ? CreateOriginalChild(new(parentLease, intent), out refusedBeforeEffect) :
            CreateChildDomainCore(parentLease, intent, null, out refusedBeforeEffect);
    private PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomainCore(
        PlatformProviderDomainLease parentLease, PlatformChildDomainIntent intent, PlatformOriginalChildCreateRequest? originalRequest,
        out bool refusedBeforeEffect)
    {
        refusedBeforeEffect = false;
        PlatformAuthorityResult validation = PlatformChildDomainContract.ValidateCreateRequest(parentLease, intent);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(
                validation.Status,
                validation.Message ?? "The child-domain request is invalid.");
        }

        if (_childRuntime is null)
            return ChildContourBlocked<PlatformProviderChildDomainLease>("create child domain");
        DomainRecord parent;
        ChildCreateFailure? original = null;
        bool previousExternalParentEffect;
        PlatformProviderChildDomainLeaseId reservedId;
        NeutralChildDomainIntent neutralIntent = ToNeutral(intent);
        lock (_childCreateCommitGate)
        {
            if (!_domains.TryGetValue(parentLease.LeaseId, out parent!))
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Parent provider lease was not found.");
            PlatformAuthorityResult liveParent = ValidateDomain(parentLease);
            if (!liveParent.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(liveParent.Status, liveParent.Message!);
            if (parent.ChildCreateInFlight || parent.FailedChildCreate is not null ||
                parent.OriginalChildCreates.Any(fact => !fact.NoEffectConsumed && fact.Child is null))
            {
            refusedBeforeEffect = true;
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted,
            "Original child create is in flight or pinned.");
            }
            if (!HasOriginalChildCreateHistory(parent) || originalRequest is not null &&
                (parent.OriginalChildCreatesIssued == ulong.MaxValue || parent.OriginalChildCreates.Count == int.MaxValue))
            {
                refusedBeforeEffect = true;
                return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted,
                    "Original child create history is unavailable or exhausted before invocation.");
            }
            if (_nextProviderChildId == 0 || _nextProviderChildId == ulong.MaxValue)
            {
            refusedBeforeEffect = true;
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted,
            "Child identity capacity is exhausted before provider invocation.");
            }
            reservedId = new PlatformProviderChildDomainLeaseId(_nextProviderChildId++);
            _originalProviderChildren.EnsureCapacity(_originalProviderChildren.Count + 1);
            _providerChildren.EnsureCapacity(_originalProviderChildren.Count + 1);
            parent.ChildCreateInFlight = true;
            previousExternalParentEffect = parent.ExternalChildCreateInvoked;
            if (originalRequest is not null)
            {
                parent.OriginalChildCreates.EnsureCapacity(parent.OriginalChildCreates.Count + 1);
                original = new(reservedId, parent.HybridCpuLease, neutralIntent, null)
                    { OriginalRequest = originalRequest, NativeRequest = new(parent.HybridCpuLease, neutralIntent), Source = parent, PreviousExternalParentEffect = previousExternalParentEffect };
                parent.OriginalChildCreates.Add(original); parent.OriginalChildCreatesIssued++; parent.FailedChildCreate = original;
            }
        }
        try
        {
            NeutralVirtualizationResult<NeutralChildDomainLease> external;
            lock (_childCreateCommitGate) if (_childRuntime is INeutralExternalParentClosureProvider) parent.ExternalChildCreateInvoked = true;
            try { external = original is null ? _childRuntime.CreateChildDomain(parent.HybridCpuLease, neutralIntent) :
                ((INeutralOriginalChildCreateProvider)_childRuntime).CreateOriginalChild(original.NativeRequest!); }
            catch
            {
                lock (_childCreateCommitGate)
                parent.FailedChildCreate ??= new(reservedId, parent.HybridCpuLease, neutralIntent, null);
                throw;
            }
            lock (_childCreateCommitGate)
            {
                if (!external.IsSuccess && external.Status != NeutralVirtualizationStatus.NotInvoked)
                {
                if (original is not null) original.Observation = external;
                parent.FailedChildCreate ??= new(reservedId, parent.HybridCpuLease, neutralIntent, external);
                }
                if (original is not null) original.Observation = external;
                if (!external.IsSuccess)
                {
                    refusedBeforeEffect = external.Status == NeutralVirtualizationStatus.NotInvoked;
                    if (refusedBeforeEffect) parent.ExternalChildCreateInvoked = previousExternalParentEffect;
                    if (refusedBeforeEffect && original is not null) { original.NoEffectConsumed = true; if (ReferenceEquals(parent.FailedChildCreate, original)) parent.FailedChildCreate = null; }
                    return FromNeutral<PlatformProviderChildDomainLease>(external.Status, external.Reason);
                }
                NeutralVirtualizationResult exact = NeutralChildDomainContract.ValidateLease(parent.HybridCpuLease, neutralIntent, external.Value);
                if (!exact.IsSuccess || !ValidateDomain(parentLease).IsSuccess ||
                original is not null && !OriginalChildCreateSources(original) ||
                !_domains.TryGetValue(parentLease.LeaseId, out DomainRecord? currentParent) || !ReferenceEquals(currentParent, parent))
                {
                    parent.FailedChildCreate ??= new(reservedId, parent.HybridCpuLease, neutralIntent, external);
                    return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original child result or final parent continuity failed; raw outcome retained without publication.");
                }
                PlatformProviderChildDomainLease lease = new PlatformProviderChildDomainLease(reservedId, new(1), parentLease, intent);
                parent.FailedChildCreate = original ?? new(reservedId, parent.HybridCpuLease, neutralIntent, external);
                ChildRecord originalRecord = new ChildRecord(lease, external.Value) { CreateParent = parent, OriginalCreateRequired = original is not null };
                _originalProviderChildren.Add(lease.LeaseId, originalRecord);
                if (original is not null) original.Child = originalRecord;
                _providerChildren.Add(lease.LeaseId, originalRecord);
                parent.FailedChildCreate = null;
                return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(lease);
            }
        }
        finally
        {
            lock (_childCreateCommitGate) parent.ChildCreateInFlight = false;
        }
    }

    public PlatformAuthorityResult TransitionChildDomain(
        PlatformProviderChildDomainLease lease,
        PlatformChildDomainTransition transition)
        => TransitionChildDomainCore(lease, transition, null);

    private PlatformAuthorityResult TransitionChildDomainCore(PlatformProviderChildDomainLease lease,
        PlatformChildDomainTransition transition, OriginalDrainFact? originalDrain)
    {
        PlatformAuthorityResult validation = PlatformChildDomainContract.ValidateTransition(lease, transition);
        if (!validation.IsSuccess) return validation;
        if (_childRuntime is null) return ChildContourBlocked("transition child domain");
        ChildRecord child;
        lock (_childCreateCommitGate)
        {
        if (!_providerChildren.TryGetValue(lease.LeaseId, out child!) || !IsOriginalChildOwner(child) || child.Lease != lease)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Child provider lease is absent or stale.");
        if ((child.TransitionInFlight && (originalDrain is null || !ReferenceEquals(originalDrain.Child, child))) || _pendingNestedSources.Contains(child))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Child transition is in flight.");
        if (transition == PlatformChildDomainTransition.BeginDrain && HasPublishedChildClosure(child))
            return PlatformAuthorityResult.Ok(); // Cleanup-only adoption of retained exact closure; no new transition.
        if (originalDrain is null && _originalChildDrains.Values.Any(f => ReferenceEquals(f.Child, child) && f.Invoked &&
                f.Result is not { Status: PlatformAuthorityStatus.NotAccepted }))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Original drain requires same-original lookup; no new transition effect.");
        if (!ProviderGuestMapHistory(child) || child.FailedTransition is not null || child.UnclosedGuestMap is not null || child.GuestMapInFlight || child.CloseInFlight ||
            child.FailedClose is not null || child.ClosureReceipt is not null || HasProviderIoPins(child.Lease, true) || HasGuestUnmapInterlock(child.Lease) ||
            HasArtifactBindInterlock(child.Lease, transition: originalDrain?.Child))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original guest mapping invocation is unclosed.");
        if (HasProviderEventPins(child))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Original event custody remains pinned; child transition was not invoked.");
        if (HasPlatformSoftwareMemoryPins(child))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Original software memory custody remains pinned; child transition was not invoked.");
        if (child.OriginalNestedRequired && (child.NestedParent is null || !ProviderNestedIdentity(child)))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,
                "Original nested lineage refused before child transition; no provider transition was invoked.");
            PlatformAuthorityResult parent = ValidateDomain(lease.ParentDomainLease);
        if (!parent.IsSuccess) return parent;
        if (!ReferenceEquals(child.CreateParent, _domains[lease.ParentDomainLease.LeaseId]))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original child create parent changed before transition.");
        child.TransitionInFlight = true;
        }
        NeutralChildDomainTransition neutralTransition = transition switch
        {
            PlatformChildDomainTransition.Start => NeutralChildDomainTransition.Start,
            PlatformChildDomainTransition.Park => NeutralChildDomainTransition.Park,
            PlatformChildDomainTransition.Resume => NeutralChildDomainTransition.Resume,
            PlatformChildDomainTransition.BeginDrain => NeutralChildDomainTransition.BeginDrain,
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };
        try
        {
        NeutralVirtualizationResult external;
        try
        {
            if (originalDrain is null) external = _childRuntime.TransitionChildDomain(child.Neutral, neutralTransition);
            else
            {
                lock (_childCreateCommitGate) originalDrain.Invoked = true;
                var producer = (INeutralOriginalChildDrainProvider)_childRuntime;
                NeutralOriginalChildDrainObservation? returned = producer.BeginOriginalChildDrain(originalDrain.NativeRequest);
                lock (_childCreateCommitGate) originalDrain.Reply = returned;
                NeutralOriginalChildDrainObservation? canonical = producer.ResolveOriginalChildDrain(originalDrain.NativeRequest);
                lock (_childCreateCommitGate)
                {
                    if (ValidDrainRaw(originalDrain, canonical))
                    { originalDrain.Raw = originalDrain.OriginalRaw = canonical; originalDrain.CanonicalObserved = true; }
                    external = ReferenceEquals(returned, canonical) && ValidDrainRaw(originalDrain, canonical)
                        ? canonical!.Result : new(NeutralVirtualizationStatus.Ambiguous, "Original drain reply unavailable or foreign; pure continuation required.");
                }
            }
        }
        catch
        {
            lock (_childCreateCommitGate) child.FailedTransition ??= new(child.Neutral, neutralTransition, null);
            throw;
        }
        lock (_childCreateCommitGate)
        {
        if (!external.IsSuccess && external.Status != NeutralVirtualizationStatus.NotInvoked)
            child.FailedTransition ??= new(child.Neutral, neutralTransition, external);
        if (external.IsSuccess && (!ValidateDomain(lease.ParentDomainLease).IsSuccess ||
                child.OriginalNestedRequired && (child.NestedParent is null || !ProviderNestedIdentity(child)) ||
                !_domains.TryGetValue(lease.ParentDomainLease.LeaseId, out DomainRecord? parent) || !ReferenceEquals(parent, child.CreateParent) ||
                !_providerChildren.TryGetValue(lease.LeaseId, out ChildRecord? current) || !ReferenceEquals(current, child)))
        {
            child.FailedTransition ??= new(child.Neutral, neutralTransition, external);
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Transition inputs changed; original result retained without publication.");
        }
        return external.IsSuccess ? PlatformAuthorityResult.Ok() : FromNeutral(external.Status, external.Reason);
        }
        }
        finally { lock (_childCreateCommitGate) if (originalDrain is null) child.TransitionInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(
        PlatformProviderChildDomainLease lease)
        => CloseChildDomainCore(lease, false, out _);

    public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> ResolveOriginalChildClose(PlatformProviderChildDomainLease originalChild)
    {
        PlatformAuthorityResult<PlatformChildDomainClosureReceipt> result = CloseChildDomainCore(originalChild, true, out bool exactNoEffect);
        return result.Status == PlatformAuthorityStatus.NotAccepted && !exactNoEffect
            ? PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Pending/refused lookup is not original no-effect.") : result;
    }

    public bool SupportsOriginalChildCloseResolution => _childRuntime is INeutralOriginalChildCloseProvider { SupportsOriginalChildCloseResolution: true };

    private PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomainCore(PlatformProviderChildDomainLease lease, bool resolutionOnly, out bool exactNoEffect)
    {
        exactNoEffect = false;
        PlatformAuthorityResult validation = PlatformChildDomainContract.ValidateLease(
            lease.ParentDomainLease,
            lease.Intent,
            lease);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(
                validation.Status,
                validation.Message ?? "The child-domain lease is invalid.");
        }

        if (_childRuntime is null) return ChildContourBlocked<PlatformChildDomainClosureReceipt>("close child domain with definitive receipt");
        ChildRecord child;
        bool invoke;
        bool resolve;
        ChildCloseFailure? invocation;
        ChildClosureSource closureSource;
        lock (_childCreateCommitGate)
        {
        if (!_providerChildren.TryGetValue(lease.LeaseId, out child!) || !IsOriginalChildOwner(child) || child.Lease != lease)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Child provider lease is absent or stale.");
        if (_pendingNestedSources.Contains(child))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Original ancestor admission is pending; Close was not invoked.");
        if (!ProviderGuestMapHistory(child) || HasProviderNestedPins(child) || HasProviderIoPins(child.Lease) || HasProviderEventPins(child) || HasSoftwareSecurePins(child))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original virtual-I/O lifetime remains active or unknown.");
        if (resolutionOnly && child.OriginalCloseInvocation is { NoEffectConsumed: true } consumed &&
            ReferenceEquals(consumed.OriginalOwner, consumed) && consumed.Request == child.Neutral && consumed.Observation is { Status: NeutralVirtualizationStatus.NotInvoked })
        {
            exactNoEffect = true;
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original no-effect already consumed; no closure.");
        }
        if (child.OriginalNestedRequired && (child.NestedParent is null || !ProviderNestedIdentity(child)))
        {
            if (!resolutionOnly && (child.OriginalCloseInvocation is null || child.OriginalCloseInvocation.NoEffectConsumed) &&
                child.FailedClose is null && child.ClosureReceipt is null)
            {
                child.OriginalCloseInvocation = new(child.Neutral, new(NeutralVirtualizationStatus.NotInvoked, default,
                    "Original nested lineage refused before neutral Close.")) { NoEffectConsumed = true };
                return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                    "Original nested lineage refused before neutral Close.");
            }
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original nested lineage is unavailable; retained Close cannot be repeated or published.");
        }
        if (child.GuestMapInFlight || child.TransitionInFlight)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Guest map or child transition admission is in flight; child close was not invoked.");
        if (child.UnclosedGuestMap is not null || child.FailedTransition is not null)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest map or child transition is unclosed; child closure is not inferred.");
        if (_originalProviderGuestMappings.Values.Where(mapping => !HasPublishedGuestClosure(mapping)).Any(mapping => mapping.Lease.ChildLease == child.Lease && mapping.FailedUnmap is not null))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest unmap is unclosed; child closure is not inferred.");
        if (_originalProviderGuestMappings.Values.Where(mapping => !HasPublishedGuestClosure(mapping)).Any(mapping => mapping.Lease.ChildLease == child.Lease))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Exact guest mappings must finish cleanup before child close; neutral close was not invoked.");
        if (HasArtifactBindInterlock(child.Lease))
        {
            if(!resolutionOnly && (child.OriginalCloseInvocation is null || child.OriginalCloseInvocation.NoEffectConsumed) && child.FailedClose is null && child.ClosureReceipt is null)
            {
                // Producing owner records this component's refusal before returning; lookup never infers it from absence.
                child.OriginalCloseInvocation = new(child.Neutral,new(NeutralVirtualizationStatus.NotInvoked,default,"Artifact dependency refused before neutral Close.")) {NoEffectConsumed=true};
                return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,"Original artifact dependency refused before neutral Close.");
            }
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original artifact binding is in flight, unknown, or retained without release evidence.");
        }
        if (child.CloseInFlight)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Child close admission is in flight.");
        resolve = child.FailedClose is not null || resolutionOnly;
        if (resolve && child.ClosureReceipt is null &&
            (_childRuntime is not INeutralOriginalChildCloseProvider || child.FailedClose is not { } failed ||
             !ReferenceEquals(failed.OriginalOwner, failed) || !ReferenceEquals(failed, child.OriginalCloseInvocation) || failed.Request != child.Neutral))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original child close invocation lacks exact closure; repeating the neutral invocation is prohibited.");
        invoke = child.ClosureReceipt is null;
        invocation = child.OriginalCloseInvocation;
        closureSource = new ChildClosureSource(child.Lease, child.Neutral);
        if (invoke)
        {
                PlatformAuthorityResult parent = ValidateDomain(lease.ParentDomainLease);
            if (!parent.IsSuccess || !ReferenceEquals(child.CreateParent, _domains[lease.ParentDomainLease.LeaseId]))
                return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original child create parent is not current before close.");
            child.CloseInFlight = true;
            if (!resolve)
            {
                invocation = new(closureSource.Neutral, null);
                child.OriginalCloseInvocation = invocation;
            }
        }
        }
        try
        {
        if (invoke)
        {
            try
            {
                    NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> external = resolve ? ((INeutralOriginalChildCloseProvider)_childRuntime).ResolveOriginalChildClose(closureSource.Neutral)
                    : _childRuntime.CloseChildDomain(closureSource.Neutral);
                lock (_childCreateCommitGate) invocation!.Observation = external; // Original raw before validation/publication.
                    NeutralVirtualizationResult closure = NeutralChildDomainContract.ValidateClose(closureSource.Neutral, external);
                lock (_childCreateCommitGate)
                {
                if (!closure.IsSuccess)
                {
                    if (external.Status != NeutralVirtualizationStatus.NotInvoked)
                        child.FailedClose ??= invocation;
                    else
                    {
                        invocation!.NoEffectConsumed = true; exactNoEffect = true;
                        child.FailedClose = null; // Exact original no-effect consumed; no closure.
                    }
                    return FromNeutral<PlatformChildDomainClosureReceipt>(closure.Status, closure.Reason);
                }
                child.ClosureReceipt = external.Value;
                child.ClosureSource = closureSource;
                child.FailedClose = null;
                }
            }
            catch
            {
                lock (_childCreateCommitGate) child.FailedClose ??= invocation;
                throw;
            }
        }
        lock (_childCreateCommitGate)
        {
        if (!IsOriginalChildOwner(child) || !_providerChildren.TryGetValue(lease.LeaseId, out ChildRecord? currentChild) || !ReferenceEquals(currentChild, child) ||
            !HasRetainedChildClosure(child) || !ReferenceEquals(child.OriginalCloseInvocation, invocation) ||
            (!child.ClosurePublished && (!ValidateDomain(lease.ParentDomainLease).IsSuccess ||
                !_domains.TryGetValue(lease.ParentDomainLease.LeaseId, out DomainRecord? parent) || !ReferenceEquals(parent, child.CreateParent))) || child.GuestMapInFlight || child.TransitionInFlight ||
            child.UnclosedGuestMap is not null || child.FailedTransition is not null ||
            _originalProviderGuestMappings.Values.Where(mapping => !HasPublishedGuestClosure(mapping)).Any(mapping => mapping.Lease.ChildLease == lease) || HasArtifactBindInterlock(lease))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Child closure inputs changed; exact original receipt retained without publication.");
        child.ClosurePublished = true;
        return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
            lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, PlatformChildDomainClosureDisposition.Closed));
        }
        }
        finally
        {
            lock (_childCreateCommitGate)
            {
                if (invoke) child.CloseInFlight = false;

            }
        }
    }

    private bool HasPublishedChildClosure(ChildRecord child) =>
        child.ClosurePublished && !child.CloseInFlight && HasRetainedChildClosure(child);

    private bool HasRetainedChildClosure(ChildRecord child) =>
        IsOriginalChildOwner(child) && child.FailedClose is null &&
        (!child.OriginalNestedRequired || child.NestedParent is not null && ProviderNestedIdentity(child)) &&
        child.ClosureSource == new ChildClosureSource(child.Lease, child.Neutral) && child.ClosureReceipt is { } receipt &&
        child.OriginalCloseInvocation is { NoEffectConsumed: false, Observation: { } observation } invocation &&
        ReferenceEquals(invocation.OriginalOwner, invocation) && invocation.Request == child.Neutral &&
        observation.Value == receipt && NeutralChildDomainContract.ValidateClose(child.Neutral, observation).IsSuccess;

    private bool IsOriginalChildOwner(ChildRecord child) =>
        _originalProviderChildren.TryGetValue(child.Lease.LeaseId, out ChildRecord? original) && ReferenceEquals(original, child) &&
        _providerChildren.TryGetValue(child.Lease.LeaseId, out ChildRecord? routed) && ReferenceEquals(routed, child) &&
        (!child.OriginalCreateRequired || child.CreateParent is { } parent && HasOriginalChildCreateHistory(parent) &&
            parent.OriginalChildCreates.Any(fact => ReferenceEquals(fact.Child, child)));

    private bool HasPublishedGuestClosure(GuestRecord mapping) =>
        mapping.ClosurePublished && !mapping.UnmapInFlight && HasRetainedGuestClosure(mapping);

    private bool HasRetainedGuestClosure(GuestRecord mapping) =>
        IsOriginalGuestOwner(mapping) &&
        mapping.FailedUnmap is null && mapping.ClosureSource == new GuestClosureSource(mapping.Lease, mapping.Neutral) &&
        mapping.ClosureReceipt is { } receipt && mapping.OriginalUnmapInvocation is { Invoked: true } invocation &&
        ReferenceEquals(invocation.OriginalOwner, invocation) && invocation.Request == mapping.Neutral &&
        (HasGuestCloseObservation(invocation.Observation, mapping.Neutral, receipt) ||
         HasGuestCloseObservation(invocation.Resolution, mapping.Neutral, receipt));

    private static bool HasGuestCloseObservation(NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt>? raw,
        NeutralGuestMappingLease request, NeutralGuestMappingCloseReceipt receipt) =>
        raw is { } observation && observation.Value == receipt && NeutralGuestMemoryContract.ValidateClose(request, observation).IsSuccess;
    private bool IsOriginalGuestOwner(GuestRecord mapping) =>
        _originalProviderGuestMappings.TryGetValue(mapping.Lease.LeaseId, out GuestRecord? original) && ReferenceEquals(original, mapping);

    public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(
        PlatformGuestRegionMappingRequest request)
        => SupportsOriginalGuestMapResolution ? MapOriginalGuest(new(request)) : MapGuestRegionCore(request, null);

    private PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegionCore(
        PlatformGuestRegionMappingRequest request, PlatformOriginalGuestMapRequest? original)
    {
        PlatformAuthorityResult validation = PlatformGuestMemoryContract.ValidateRequest(request);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(
                validation.Status,
                validation.Message ?? "The guest-memory request is invalid.");
        }

        if (_childRuntime is not INeutralGuestMemoryProvider guest)
            return ChildContourBlocked<PlatformProviderGuestRegionMappingLease>("map guest memory");
        ChildRecord child;
        ProviderMappingRecord parentMapping;
        DomainRecord parent;
        PlatformProviderGuestRegionMappingLeaseId reservedId;
        NeutralGuestMappingRequest neutralRequest;
        GuestMapFailure invocation;
        ChildRecord[] guestLineage;
        lock (_childCreateCommitGate)
        {
        if (!_providerChildren.TryGetValue(request.ChildLease.LeaseId, out child!) || !IsOriginalChildOwner(child) ||
            !_providerMappings.TryGetValue(request.ParentMapping.Lease.MappingId, out parentMapping!))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Exact child or parent mapping was not found.");
        if (child.Lease != request.ChildLease)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Stale,
                "Child provider lease does not match the admitted tuple.");
        if (child.NestedParent is not null && !ProviderNestedIdentity(child))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original nested guest factory/history unavailable.");
        guestLineage = NestedProviderLineage(child).ToArray();
        if (guestLineage.Any(x => _pendingNestedSources.Contains(x) || x.CloseInFlight || x.TransitionInFlight ||
            x.FailedClose is not null || x.FailedTransition is not null || x.ClosureReceipt is not null ||
            !ReferenceEquals(x, child) && (x.GuestMapInFlight || x.UnclosedGuestMap is not null)))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Nested guest ancestor effect is pending or unclosed.");
        if (child.TransitionInFlight || child.GuestMapInFlight || parentMapping.GuestMapInFlight)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Original guest map or child transition admission is in flight.");
        if (child.FailedTransition is not null || parentMapping.FailedGuestMap is not null ||
            _originalProviderGuestMappings.Values.Where(mapping => !HasPublishedGuestClosure(mapping)).Any(mapped => !mapped.AdmissionPublished && mapped.Lease.ChildLease == child.Lease) ||
            child.UnclosedGuestMap is not null || child.CloseInFlight || child.FailedClose is not null ||
            child.ClosureReceipt is not null || HasGuestUnmapInterlock(child.Lease) || HasArtifactBindInterlock(child.Lease))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest mapping invocation is unclosed; retry refused.");
            PlatformAuthorityResult domain = ValidateDomain(request.ChildLease.ParentDomainLease);
        if (!domain.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(domain.Status, domain.Message!);
        parent = _domains[request.ChildLease.ParentDomainLease.LeaseId];
        if (!ReferenceEquals(parent, child.CreateParent))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted,
                "Original child create parent changed before guest map.");
            PlatformAuthorityResult mapping = ValidateProviderMapping(request.ParentMapping.Lease, request.ParentMapping.Slice);
        if (!mapping.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(mapping.Status, mapping.Message!);
        if (_nextProviderGuestId == 0 || _nextProviderGuestId == ulong.MaxValue)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Guest mapping identity capacity is exhausted before provider invocation.");
        if (child.GuestMapInFlight)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Guest mapping admission is in flight.");
        reservedId = new PlatformProviderGuestRegionMappingLeaseId(_nextProviderGuestId++);
        neutralRequest = new NeutralGuestMappingRequest(child.Neutral, parentMapping.HybridCpuLease,
            new(request.GuestRange.GuestAddress, request.GuestRange.ByteLength),
            (NeutralGuestMemoryAccess)(int)request.Access);
        _originalProviderGuestMappings.EnsureCapacity(_originalProviderGuestMappings.Count + 1);
        _providerGuestMappings.EnsureCapacity(_originalProviderGuestMappings.Count + 1);
        if (!ProviderGuestMapHistory(child) || child.OriginalGuestMapsIssued == ulong.MaxValue ||
            original is not null && child.OriginalGuestMaps.Any(f => ReferenceEquals(f.OriginalRequest, original)))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original guest Map request cannot be repeated.");
        invocation = new(reservedId, neutralRequest, null)
        {
            OriginalRequest = original, NativeRequest = original is null ? null : new(neutralRequest),
            Child = child, ParentMapping = parentMapping, Parent = parent
        };
        child.OriginalGuestMaps.Add(invocation);
        child.OriginalGuestMapsIssued++;
        child.GuestMapInFlight = parentMapping.GuestMapInFlight = true;
        foreach (ChildRecord source in guestLineage) _pendingNestedSources.Add(source);
        }
        void Retain(NeutralVirtualizationResult<NeutralGuestMappingLease>? observation)
        {
            invocation.Observation = observation;
            child.UnclosedGuestMap ??= invocation;
            parentMapping.FailedGuestMap ??= invocation;
        }
        try
        {
        NeutralVirtualizationResult<NeutralGuestMappingLease> external;
        try
        {
            lock (_childCreateCommitGate) invocation.Invoked = true;
            external = invocation.NativeRequest is { } nativeRequest && guest is INeutralOriginalGuestMapProvider { SupportsOriginalGuestMapResolution: true } originalGuest
                ? originalGuest.MapOriginalGuest(nativeRequest) : guest.MapGuestRegion(neutralRequest);
            lock (_childCreateCommitGate) invocation.Observation = external;
        }
        catch
        {
            lock (_childCreateCommitGate) Retain(null);
            throw;
        }
        lock (_childCreateCommitGate)
        {
        if (!external.IsSuccess)
        {
            if (external.Status != NeutralVirtualizationStatus.NotInvoked)
                Retain(external);
            else invocation.NoEffectConsumed = true;
            return FromNeutral<PlatformProviderGuestRegionMappingLease>(external.Status, external.Reason);
        }
                NeutralVirtualizationResult exact = NeutralGuestMemoryContract.ValidateLease(neutralRequest, external.Value);
        if (!exact.IsSuccess)
        {
            Retain(external);
            return FromNeutral<PlatformProviderGuestRegionMappingLease>(exact.Status, exact.Reason);
        }
                bool publish = child.UnclosedGuestMap is null && child.FailedTransition is null &&
            (child.NestedParent is null || ProviderNestedIdentity(child)) &&
            _providerChildren.TryGetValue(request.ChildLease.LeaseId, out ChildRecord? currentChild) && ReferenceEquals(currentChild, child) &&
            _providerMappings.TryGetValue(request.ParentMapping.Lease.MappingId, out ProviderMappingRecord? currentMapping) && ReferenceEquals(currentMapping, parentMapping) &&
            _domains.TryGetValue(request.ChildLease.ParentDomainLease.LeaseId, out DomainRecord? currentParent) && ReferenceEquals(currentParent, parent) &&
            ValidateDomain(request.ChildLease.ParentDomainLease).IsSuccess &&
            ValidateProviderMapping(request.ParentMapping.Lease, request.ParentMapping.Slice).IsSuccess;
                PlatformProviderGuestRegionMappingLease lease = new PlatformProviderGuestRegionMappingLease(reservedId, new(1), request.ChildLease,
            request.ParentMapping.Lease, request.GuestRange, request.Access);
        Retain(external);
                GuestRecord originalRecord = new GuestRecord(lease, external.Value)
            { AdmissionPublished = publish, MapChild = child, MapSource = parentMapping, MapParent = parent };
        invocation.Mapping = originalRecord;
        _originalProviderGuestMappings.Add(lease.LeaseId, originalRecord);
        _providerGuestMappings.Add(lease.LeaseId, originalRecord);
        child.UnclosedGuestMap = parentMapping.FailedGuestMap = null;
        if (!publish)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted,
                "Guest mapping inputs changed; exact mapping retained for cleanup, publication refused.");
        return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(lease);
        }
        }
        finally
        {
            lock (_childCreateCommitGate)
            {
                child.GuestMapInFlight = parentMapping.GuestMapInFlight = false;
                foreach (ChildRecord source in guestLineage) _pendingNestedSources.Remove(source);
            }
        }
    }

    // Pending and original outcome dependencies belong to the existing mapping/guest records.
    private bool HasActiveProviderGuestMappings(PlatformProviderRegionMappingLease mapping) =>
        (_originalProviderMappings.TryGetValue(mapping.MappingId, out ProviderMappingRecord? source) && (source.GuestMapInFlight || source.FailedGuestMap is not null)) ||
        _originalProviderGuestMappings.Values.Where(mapping => !HasPublishedGuestClosure(mapping)).Any(guest => guest.Lease.ParentMappingLease == mapping);

    // Cleanup validates source identity, not renewed mapping authorization or live mapping use.
    private bool HasCurrentGuestMapSources(GuestRecord mapping) => IsOriginalGuestOwner(mapping) && !mapping.ClosurePublished &&
        _providerChildren.TryGetValue(mapping.Lease.ChildLease.LeaseId, out ChildRecord? child) && ReferenceEquals(child, mapping.MapChild) && IsOriginalChildOwner(child) &&
        (child.NestedParent is null || ProviderNestedIdentity(child)) &&
        _providerMappings.TryGetValue(mapping.Lease.ParentMappingLease.MappingId, out ProviderMappingRecord? source) && ReferenceEquals(source, mapping.MapSource) &&
        _domains.TryGetValue(mapping.Lease.ChildLease.ParentDomainLease.LeaseId, out DomainRecord? parent) && ReferenceEquals(parent, mapping.MapParent) &&
        ReferenceEquals(child.CreateParent, parent) && ReferenceEquals(source.MapParent, parent) &&
        child.Lease == mapping.Lease.ChildLease && child.Neutral == mapping.Neutral.ChildLease &&
        source.Mapping.Lease == mapping.Lease.ParentMappingLease && source.HybridCpuLease == mapping.Neutral.ParentMapping;

    public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(
        PlatformProviderGuestRegionMappingLease lease)
        => UnmapGuestRegionCore(lease, false, out _);

    private PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegionCore(
        PlatformProviderGuestRegionMappingLease lease, bool resolve, out bool exactNoEffect)
    {
        exactNoEffect = false;
        if (_childRuntime is not INeutralGuestMemoryProvider guest)
            return ChildContourBlocked<PlatformGuestRegionMappingClosureReceipt>("unmap guest memory");
        GuestRecord mapping;
        ChildRecord transitioning;
        bool invoke;
        GuestClosureSource closureSource;
        lock (_childCreateCommitGate)
        {
        if (!_providerGuestMappings.TryGetValue(lease.LeaseId, out mapping!) || mapping.Lease != lease)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Guest mapping is absent or stale.");
        if (!HasCurrentGuestMapSources(mapping) || !ValidateDomain(lease.ChildLease.ParentDomainLease).IsSuccess)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest mapping child owner is absent or changed; cleanup inputs are pinned.");
        transitioning = mapping.MapChild!;
        if (HasPlatformSoftwareMemoryPins(transitioning))
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Original software memory source custody remains pinned.");
        if (transitioning.TransitionInFlight || transitioning.FailedTransition is not null)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(
                transitioning.TransitionInFlight ? PlatformAuthorityStatus.NotAccepted : PlatformAuthorityStatus.Faulted,
                "Original child transition is in flight or pinned.");
        if (mapping.ArtifactBindInFlight)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Artifact bind admission is in flight.");
        if (_originalProviderArtifacts.Values.Any(artifact => artifact.ExecutionInFlight &&
                artifact.Receipt.MappingLeaseId == lease.LeaseId && artifact.Receipt.MappingGeneration == lease.Generation))
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Artifact execution admission is in flight.");
        if (mapping.FailedArtifactBind is not null || _originalProviderArtifacts.Values.Any(artifact => !HasRetainedArtifactRelease(artifact) &&
                artifact.Receipt.MappingLeaseId == lease.LeaseId && artifact.Receipt.MappingGeneration == lease.Generation))
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original artifact binding remains pinned without exact release evidence.");
        if (mapping.UnmapInFlight)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Guest unmap admission is in flight.");
        if (mapping.FailedUnmap is not null && _childRuntime is not INeutralOriginalGuestUnmapProvider { SupportsOriginalGuestUnmapResolution: true })
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest unmap invocation lacks exact closure; repeating the neutral invocation is prohibited.");
        invoke = mapping.ClosureReceipt is null;
        closureSource = new GuestClosureSource(mapping.Lease, mapping.Neutral);
        mapping.OriginalUnmapInvocation ??= new(mapping.Neutral, null);
        if (!ReferenceEquals(mapping.OriginalUnmapInvocation.OriginalOwner, mapping.OriginalUnmapInvocation) || mapping.OriginalUnmapInvocation.Request != mapping.Neutral)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original guest invocation changed.");
        mapping.UnmapInFlight = true;
        }
        try
        {
        if (invoke)
        {
        try
        {
                    GuestUnmapFailure invocation = mapping.OriginalUnmapInvocation!;
            bool pure = resolve || invocation.Invoked;
            if (!pure) lock (_childCreateCommitGate) invocation.Invoked = true;
                    NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> external = pure
                ? _childRuntime is INeutralOriginalGuestUnmapProvider { SupportsOriginalGuestUnmapResolution: true } original
                    ? original.ResolveOriginalGuestUnmap(closureSource.Neutral)
                    : new NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, default, "Original guest resolution unavailable.")
                : guest.UnmapGuestRegion(closureSource.Neutral);
            lock (_childCreateCommitGate)
            {
                if (pure) invocation.Resolution = external;
                else invocation.Observation = external;
            }
                    NeutralVirtualizationResult closure = NeutralGuestMemoryContract.ValidateClose(closureSource.Neutral, external);
            lock (_childCreateCommitGate)
            {
            if (!closure.IsSuccess)
            {
                if (external.Status != NeutralVirtualizationStatus.NotInvoked)
                    mapping.FailedUnmap ??= invocation;
                else { exactNoEffect = true; mapping.FailedUnmap = null; mapping.OriginalUnmapInvocation = null; }
                return FromNeutral<PlatformGuestRegionMappingClosureReceipt>(closure.Status, closure.Reason);
            }
            mapping.ClosureReceipt = external.Value;
            mapping.ClosureSource = closureSource;
            mapping.FailedUnmap = null;
            }
        }
        catch
        {
            lock (_childCreateCommitGate) mapping.FailedUnmap ??= mapping.OriginalUnmapInvocation;
            throw;
        }
        }
        lock (_childCreateCommitGate)
        {
        if (!_providerGuestMappings.TryGetValue(lease.LeaseId, out GuestRecord? currentMapping) || !ReferenceEquals(currentMapping, mapping) ||
            !HasCurrentGuestMapSources(mapping) ||
            !HasRetainedGuestClosure(mapping) ||
            !_providerChildren.TryGetValue(lease.ChildLease.LeaseId, out ChildRecord? currentChild) || !ReferenceEquals(currentChild, transitioning) ||
            !ValidateDomain(lease.ChildLease.ParentDomainLease).IsSuccess || transitioning.TransitionInFlight ||
            transitioning.FailedTransition is not null || mapping.ArtifactBindInFlight || mapping.FailedArtifactBind is not null)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Guest closure inputs changed; exact original receipt retained without removal/publication.");
        mapping.ClosurePublished = true;
        _providerGuestMappings.Remove(lease.LeaseId);
        return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
            lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
            lease.ChildLease.ParentDomainLease.Generation, true));
        }
        }
        finally
        {
            lock (_childCreateCommitGate)
            {
                mapping.UnmapInFlight = false;

            }
        }
    }

    public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(
        PlatformVirtualEventRequest request)
        => InjectOriginalVirtualEvent(new(request));

    public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(PlatformVirtualIoRequest request)
        => BindOriginalProviderVirtualIo(request);

    public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(PlatformProviderVirtualIoLease lease)
        => CloseOriginalProviderVirtualIo(lease);
    public PlatformAuthorityResult<PlatformExecutableArtifactReceipt> BindExecutableArtifact(PlatformExecutableArtifactRequest request)
    {
        if (request.MaximumPrivateDataBytes != 0 && _childRuntime is not INeutralExecutablePrivateDataQuotaProvider)
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Private data quota adapter unavailable before Bind.");
        PlatformAuthorityResult valid = PlatformChildExecutionContract.ValidateRequest(request);
        if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(valid.Status, valid.Message!);
        ChildRecord child;
        GuestRecord mapping;
        INeutralChildExecutionProvider execution;
        PlatformExecutableArtifactId reservedId;
        string digest;
        NeutralExecutableArtifactRequest neutralRequest;
        ArtifactBindFailure invocation;
        ChildRecord[] artifactLineage;
        lock (_childCreateCommitGate)
        {
        if (_childRuntime is not INeutralChildExecutionProvider resolved ||
            !_providerChildren.TryGetValue(request.ChildLease.LeaseId, out child!) || !IsOriginalChildOwner(child) ||
            !_providerGuestMappings.TryGetValue(request.GuestMapping.LeaseId, out mapping!))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Unsupported, "Executable child adapter or exact mapping is unavailable.");
        execution = resolved;
        if (child.NestedParent is not null && !ProviderNestedIdentity(child))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original artifact ancestor factory/history unavailable.");
        artifactLineage = NestedProviderLineage(child).ToArray();
        if (artifactLineage.Any(source => _pendingNestedSources.Contains(source) || source.CloseInFlight || source.TransitionInFlight ||
            source.FailedClose is not null || source.FailedTransition is not null || source.ClosureReceipt is not null ||
            source.GuestMapInFlight || source.UnclosedGuestMap is not null))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Artifact ancestor effect is pending or unclosed before Bind.");
        if (child.Lease != request.ChildLease || mapping.Lease != request.GuestMapping || !HasCurrentGuestMapSources(mapping))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Stale,
                "Executable child or guest mapping differs from the exact admitted tuple.");
            PlatformAuthorityResult liveParent = ValidateDomain(request.ChildLease.ParentDomainLease);
        if (!liveParent.IsSuccess)
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(liveParent.Status, liveParent.Message!);
            PlatformAuthorityResult liveSource = ValidateProviderMapping(mapping.Lease.ParentMappingLease, null);
        if (!liveSource.IsSuccess)
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(liveSource.Status, liveSource.Message!);
        if (child.UnclosedGuestMap is not null || child.GuestMapInFlight || child.CloseInFlight ||
            child.FailedClose is not null || child.ClosureReceipt is not null || !mapping.AdmissionPublished ||
            HasGuestUnmapInterlock(child.Lease))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Guest mapping is quarantined or retained only for cleanup.");
        if (_nextProviderArtifactId == 0 || _nextProviderArtifactId == ulong.MaxValue)
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Artifact identity capacity is exhausted before provider invocation.");
        if (HasArtifactBindInterlock(child.Lease))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Artifact binding is in flight or quarantined.");
        reservedId = new PlatformExecutableArtifactId(_nextProviderArtifactId++);
            byte[] package = request.ImmutablePackage.ToArray();
        digest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(package));
        neutralRequest = new NeutralExecutableArtifactRequest(child.Neutral, mapping.Neutral, package, request.MaximumExecutionSteps) { MaximumPrivateDataBytes = request.MaximumPrivateDataBytes };
        _originalProviderArtifacts.EnsureCapacity(_originalProviderArtifacts.Count + 1);
        _providerArtifacts.EnsureCapacity(_originalProviderArtifacts.Count + 1);
        mapping.ArtifactBindInFlight = true;
        mapping.OriginalArtifactBindRequest = request;
        invocation = new(reservedId, neutralRequest, digest);
        invocation.EstablishSelf();
        mapping.OriginalArtifactBindInvocation = invocation; // Identity/request/digest before possible effect.
        foreach (ChildRecord source in artifactLineage) _pendingNestedSources.Add(source);
        }
        try
        {
        NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> external;
        try { external = execution.BindExecutableArtifact(neutralRequest); }
        catch
        {
            lock (_childCreateCommitGate) mapping.FailedArtifactBind ??= invocation;
            throw;
        }
        lock (_childCreateCommitGate)
        {
        invocation.Observation = external; // Captured original raw before validation/publication.
        if (!external.IsSuccess)
        {
            if (external.Status != NeutralVirtualizationStatus.NotInvoked)
                mapping.FailedArtifactBind ??= invocation;
            return FromNeutral<PlatformExecutableArtifactReceipt>(external.Status, external.Reason);
        }
                NeutralVirtualizationResult exact = external.Value.ContentDigest is null
            ? new NeutralVirtualizationResult(NeutralVirtualizationStatus.Faulted, "Artifact digest is absent.")
            : NeutralChildExecutionContract.ValidateAdmissionReceipt(neutralRequest, external.Value);
        if (!exact.IsSuccess || external.Value.ContentDigest != digest)
        {
            mapping.FailedArtifactBind ??= invocation;
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Artifact receipt does not match the original admission tuple and captured package digest.");
        }
        bool publish = (child.NestedParent is null || ProviderNestedIdentity(child)) && ReferenceEquals(mapping.OriginalArtifactBindInvocation, invocation) && !HasArtifactBindInterlock(child.Lease, binding: mapping) && HasCurrentGuestMapSources(mapping) && child.FailedClose is null &&
            child.ClosureReceipt is null && child.UnclosedGuestMap is null && !child.CloseInFlight && !child.GuestMapInFlight &&
            _providerChildren.TryGetValue(child.Lease.LeaseId, out ChildRecord? currentChild) && ReferenceEquals(currentChild, child) &&
            ValidateDomain(request.ChildLease.ParentDomainLease).IsSuccess &&
            ValidateProviderMapping(mapping.Lease.ParentMappingLease, null).IsSuccess &&
            _providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out GuestRecord? current) && ReferenceEquals(current, mapping) &&
            !HasGuestUnmapInterlock(child.Lease);
        mapping.FailedArtifactBind = invocation;
                PlatformExecutableArtifactReceipt receipt = new PlatformExecutableArtifactReceipt(reservedId, new(1),
            request.ChildLease.LeaseId, request.ChildLease.Generation, request.GuestMapping.LeaseId,
            request.GuestMapping.Generation, request.ChildLease.ParentDomainLease.LeaseId,
            request.ChildLease.ParentDomainLease.Generation, external.Value.ContentDigest, request.MaximumExecutionSteps) { MaximumPrivateDataBytes = request.MaximumPrivateDataBytes };
                ArtifactRecord originalRecord = new ArtifactRecord(receipt, external.Value) { AdmissionPublished = publish, BindMapping = mapping };
        _originalProviderArtifacts.Add(receipt.ArtifactId, originalRecord);
        _providerArtifacts.Add(receipt.ArtifactId, originalRecord);
        mapping.FailedArtifactBind = null;
        if (!publish)
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Artifact inputs changed; exact binding retained without admission publication or release evidence.");
        return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Ok(receipt);
        }
        }
        finally
        {
            lock (_childCreateCommitGate)
            {
                mapping.ArtifactBindInFlight = false;
                foreach (ChildRecord source in artifactLineage) _pendingNestedSources.Remove(source);
            }
        }
    }

    public PlatformAuthorityResult<PlatformChildExecutionReceipt> StartExecutableArtifact(
        PlatformChildExecutionStartRequest request)
    {
        PlatformAuthorityResult admission = PlatformChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(admission.Status, admission.Message!);
        ChildRecord child;
        GuestRecord mapping;
        ArtifactRecord found;
        INeutralChildExecutionProvider execution;
        NeutralChildExecutionStartRequest neutralRequest;
        ExecutionAttempt attempt;
        ChildRecord[] startLineage;
        lock (_childCreateCommitGate)
        {
        if (_childRuntime is not INeutralChildExecutionProvider resolved ||
            !_providerChildren.TryGetValue(request.ChildLease.LeaseId, out child!) || !IsOriginalChildOwner(child) ||
            !_providerArtifacts.TryGetValue(request.Artifact.ArtifactId, out found!) || found.Receipt != request.Artifact ||
            child.Lease != request.ChildLease)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Stale, "Executable artifact binding is absent or stale.");
        execution = resolved;
        if (child.NestedParent is not null && !ProviderNestedIdentity(child))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Start ancestor identity/history unavailable.");
        startLineage = child.NestedParent is null ? [] : NestedProviderLineage(child).ToArray();
        if (startLineage.Any(source => _pendingNestedSources.Contains(source) || source.CloseInFlight || source.TransitionInFlight ||
            source.FailedClose is not null || source.FailedTransition is not null || source.ClosureReceipt is not null ||
            source.GuestMapInFlight || source.UnclosedGuestMap is not null || HasArtifactBindInterlock(source.Lease)))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Original Start ancestor admission is unavailable or pending.");
        if (!_originalProviderArtifacts.TryGetValue(request.Artifact.ArtifactId, out ArtifactRecord? originalArtifact) ||
            !ReferenceEquals(originalArtifact, found))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original executable artifact owner is not current before Start.");
        if (found.ReleaseRequest is not null || found.Released)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Denied, "Artifact release permanently refuses further Start.");
        if (!_providerGuestMappings.TryGetValue(found.Receipt.MappingLeaseId, out mapping!) ||
            mapping.Lease.Generation != found.Receipt.MappingGeneration || mapping.Lease.ChildLease != request.ChildLease ||
            !mapping.AdmissionPublished || !ReferenceEquals(mapping, found.BindMapping) || !HasCurrentGuestMapSources(mapping))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Stale,
                "Executable artifact guest mapping is absent, stale, or unpublished.");
        if (_originalProviderArtifacts.Values.Any(artifact => artifact.ExecutionInFlight &&
                artifact.Receipt.ChildLeaseId == child.Lease.LeaseId && artifact.Receipt.ChildGeneration == child.Lease.Generation))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Child execution admission is in flight.");
        if (!OriginalExecutionHistoryIntact(found) || found.ExecutionHistory.Any(previous => previous.Invoked && !previous.NoEffectConsumed))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Denied,
                "Artifact already has an original execution attempt; repeat effect refused.");
        if (child.UnclosedGuestMap is not null || child.GuestMapInFlight || child.CloseInFlight ||
            child.FailedClose is not null || child.ClosureReceipt is not null || HasProviderIoPins(child.Lease, true) || HasGuestUnmapInterlock(child.Lease) ||
            HasArtifactBindInterlock(child.Lease) || !found.AdmissionPublished)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest mapping invocation is unclosed.");
        neutralRequest = new NeutralChildExecutionStartRequest(child.Neutral, found.Neutral,
            new(request.OperationId.Value), new(request.OperationGeneration.Value));
            PlatformAuthorityResult parent = ValidateDomain(request.ChildLease.ParentDomainLease);
            PlatformAuthorityResult source = ValidateProviderMapping(mapping.Lease.ParentMappingLease, null);
        if (!parent.IsSuccess || !source.IsSuccess)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Stale,
                "Executable source is no longer live.");
        if (found.ExecutionsIssued == ulong.MaxValue)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,"Original execution history exhausted before effect.");
        found.ExecutionHistory.EnsureCapacity(found.ExecutionHistory.Count + 1);
        attempt = new(neutralRequest,found,child,mapping); attempt.EstablishSelf();
        found.ExecutionHistory.Add(attempt); found.ExecutionsIssued++; found.OriginalExecution = attempt;
        foreach (ChildRecord sourceOwner in startLineage) _pendingNestedSources.Add(sourceOwner);
        found.ExecutionInFlight = true; attempt.Invoked = true; // Authoritative original request before the effect-capable call.
        }
        try
        {
            NeutralVirtualizationResult<NeutralChildExecutionReceipt> external = execution.StartExecutableArtifact(neutralRequest); // Exception leaves the same pre-effect attempt unresolved.
        lock (_childCreateCommitGate)
        {
        attempt.Observation = external; // Raw on original fact before validation/source callbacks, even if routing/history changed.
        if (!OriginalExecutionHistoryIntact(found) || !ReferenceEquals(found.OriginalExecution,attempt))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted,"Original execution attempt history changed; raw retained, no repeat.");
        if (external.Status == NeutralVirtualizationStatus.NotInvoked) attempt.NoEffectConsumed = true;
        if (!external.IsSuccess) return FromNeutral<PlatformChildExecutionReceipt>(external.Status, external.Reason);
                NeutralVirtualizationResult original = NeutralChildExecutionContract.ValidateExecutionReceipt(neutralRequest, external.Value);
        if (!original.IsSuccess)
            return FromNeutral<PlatformChildExecutionReceipt>(original.Status, original.Reason);
        if (child.NestedParent is not null && !ProviderNestedIdentity(child) || !HasCurrentGuestMapSources(mapping) || !ReferenceEquals(found.BindMapping, mapping) ||
            !_providerArtifacts.TryGetValue(found.Receipt.ArtifactId, out ArtifactRecord? liveArtifact) || !ReferenceEquals(liveArtifact, found) ||
            !_originalProviderArtifacts.TryGetValue(found.Receipt.ArtifactId, out ArtifactRecord? originalArtifact) || !ReferenceEquals(originalArtifact, found) ||
            HasArtifactBindInterlock(child.Lease, execution: found) ||
            child.Lease != request.ChildLease || child.FailedClose is not null || child.ClosureReceipt is not null ||
            child.CloseInFlight || child.GuestMapInFlight || child.UnclosedGuestMap is not null ||
            HasGuestUnmapInterlock(child.Lease) || !ValidateDomain(request.ChildLease.ParentDomainLease).IsSuccess ||
            !ValidateProviderMapping(mapping.Lease.ParentMappingLease, null).IsSuccess ||
            !_providerChildren.TryGetValue(child.Lease.LeaseId, out ChildRecord? liveChild) || !ReferenceEquals(liveChild, child) ||
            !_providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out GuestRecord? liveMapping) || !ReferenceEquals(liveMapping, mapping))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Execution inputs changed; exact original response retained without publication or release evidence.");
                NeutralChildExecutionReceipt value = external.Value;
                PlatformExecutableArtifactReceipt artifact = request.Artifact;
                PlatformChildExecutionReceipt receipt = new PlatformChildExecutionReceipt(artifact.ArtifactId, artifact.ArtifactGeneration,
            artifact.ChildLeaseId, artifact.ChildGeneration, artifact.MappingLeaseId, artifact.MappingGeneration,
            artifact.ParentLeaseId, artifact.ParentGeneration, artifact.ContentDigest,
            request.OperationId, request.OperationGeneration,
            new(value.ExecutionGeneration.Value), value.RetiredWorkUnits, value.RetiredSequence,
            value.LastRetiredCodeOffset, value.IsTerminal);
                PlatformAuthorityResult exact = PlatformChildExecutionContract.ValidateExecution(request, receipt);
        if (exact.IsSuccess) found.ExecutionPublished = true;
        return exact.IsSuccess ? PlatformAuthorityResult<PlatformChildExecutionReceipt>.Ok(receipt) :
            PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(exact.Status, exact.Message!);
        }
        }
        finally
        {
            lock (_childCreateCommitGate)
            {
                found.ExecutionInFlight = false;
                foreach (ChildRecord sourceOwner in startLineage) _pendingNestedSources.Remove(sourceOwner);
            }
        }
    }

    private static NeutralChildDomainIntent ToNeutral(PlatformChildDomainIntent intent) => new(
        new(intent.Profile.VirtualProcessorCount, intent.Profile.MaximumGuestMemoryBytes),
        new((NeutralChildAuthorityClass)(int)intent.Authority.ParentAuthority,
            (NeutralChildAuthorityClass)(int)intent.Authority.ChildAuthority));

    private static PlatformAuthorityStatus ToPlatform(NeutralVirtualizationStatus status) => status switch
    {
        NeutralVirtualizationStatus.Unsupported or NeutralVirtualizationStatus.Unavailable => PlatformAuthorityStatus.Unsupported,
        NeutralVirtualizationStatus.Denied => PlatformAuthorityStatus.Denied,
        // Explicit pre-invocation disposition only; generic Denied is not no-effect evidence.
        NeutralVirtualizationStatus.NotInvoked => PlatformAuthorityStatus.NotAccepted,
        NeutralVirtualizationStatus.Stale => PlatformAuthorityStatus.Stale,
        NeutralVirtualizationStatus.Revoked => PlatformAuthorityStatus.Revoked,
        NeutralVirtualizationStatus.WrongParent or NeutralVirtualizationStatus.WrongOwner => PlatformAuthorityStatus.WrongDomain,
        NeutralVirtualizationStatus.Faulted or NeutralVirtualizationStatus.Ambiguous => PlatformAuthorityStatus.Faulted,
        _ => PlatformAuthorityStatus.Faulted,
    };

    private static PlatformAuthorityResult FromNeutral(NeutralVirtualizationStatus status, string reason) =>
        PlatformAuthorityResult.Fail(ToPlatform(status), reason);
    private static PlatformAuthorityResult<T> FromNeutral<T>(NeutralVirtualizationStatus status, string reason) =>
        PlatformAuthorityResult<T>.Fail(ToPlatform(status), reason);

    private static PlatformAuthorityResult ChildContourBlocked(string operation) =>
        PlatformAuthorityResult.Fail(
            PlatformAuthorityStatus.Unsupported,
            $"EXT-HCPU-006 ExternalBlocked: HybridCPU cannot {operation} because no executable child adapter was composed.");

    private static PlatformAuthorityResult<T> ChildContourBlocked<T>(string operation) =>
        PlatformAuthorityResult<T>.Fail(
            PlatformAuthorityStatus.Unsupported,
            $"EXT-HCPU-006 ExternalBlocked: HybridCPU cannot {operation} because no executable child adapter was composed.");
}






