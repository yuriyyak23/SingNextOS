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
    IPlatformChildExecutionProvider
{
    private sealed record GuestMapFailure(PlatformProviderGuestRegionMappingLeaseId InvocationId,
        NeutralGuestMappingRequest Request, NeutralVirtualizationResult<NeutralGuestMappingLease>? Observation);
    private sealed record ChildCloseFailure(NeutralChildDomainLease Request,
        NeutralVirtualizationResult<NeutralChildDomainCloseReceipt>? Observation);
    private sealed record ChildCreateFailure(PlatformProviderChildDomainLeaseId InvocationId,
        NeutralDomainBindingLease Parent, NeutralChildDomainIntent Intent,
        NeutralVirtualizationResult<NeutralChildDomainLease>? Observation);
    private sealed record ChildTransitionFailure(NeutralChildDomainLease Lease,
        NeutralChildDomainTransition Transition, NeutralVirtualizationResult? Observation);
    private sealed record ChildClosureSource(PlatformProviderChildDomainLease Lease, NeutralChildDomainLease Neutral);
    private sealed record GuestClosureSource(PlatformProviderGuestRegionMappingLease Lease, NeutralGuestMappingLease Neutral);
    private sealed record ChildRecord(PlatformProviderChildDomainLease Lease, NeutralChildDomainLease Neutral)
    {
        public DomainRecord? CreateParent { get; init; }
        public GuestMapFailure? UnclosedGuestMap { get; set; }
        public bool GuestMapInFlight { get; set; }
        public bool CloseInFlight { get; set; }
        public NeutralChildDomainCloseReceipt? ClosureReceipt { get; set; }
        public ChildClosureSource? ClosureSource { get; set; }
        public bool ClosurePublished { get; set; }
        public ChildCloseFailure? FailedClose { get; set; }
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
        public NeutralGuestMappingCloseReceipt? ClosureReceipt { get; set; }
        public GuestClosureSource? ClosureSource { get; set; }
        public bool ClosurePublished { get; set; }
        public bool ArtifactBindInFlight { get; set; }
        public ArtifactBindFailure? FailedArtifactBind { get; set; }
    }
    private sealed record ArtifactBindFailure(PlatformExecutableArtifactId InvocationId,
        NeutralExecutableArtifactRequest Request, string ContentDigest,
        NeutralVirtualizationResult<NeutralExecutableArtifactReceipt>? Observation);
    private sealed record GuestUnmapFailure(NeutralGuestMappingLease Request,
        NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt>? Observation);
    private bool HasGuestUnmapInterlock(PlatformProviderChildDomainLease child) =>
        _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease == child &&
            (mapping.UnmapInFlight || mapping.FailedUnmap is not null));
    private sealed record IoRecord(PlatformProviderVirtualIoLease Lease, NeutralVirtualIoLease Neutral);
    private sealed record ArtifactRecord(PlatformExecutableArtifactReceipt Receipt, NeutralExecutableArtifactReceipt Neutral)
    {
        public bool AdmissionPublished { get; init; } = true;
        public GuestRecord? BindMapping { get; init; }
        public bool ExecutionInFlight { get; set; }
        public ExecutionAttempt? OriginalExecution { get; set; }
        public bool ExecutionPublished { get; set; }
    }
    private sealed record ExecutionAttempt(NeutralChildExecutionStartRequest Request,
        NeutralVirtualizationResult<NeutralChildExecutionReceipt>? Observation);
    private bool HasArtifactBindInterlock(PlatformProviderChildDomainLease child, GuestRecord? binding = null, ArtifactRecord? execution = null) =>
        _originalProviderChildren.Values.Any(record => record.Lease == child &&
            (record.TransitionInFlight || record.FailedTransition is not null)) ||
        _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease == child &&
            ((mapping.ArtifactBindInFlight && !ReferenceEquals(mapping, binding)) || mapping.FailedArtifactBind is not null)) ||
        _originalProviderArtifacts.Values.Any(artifact => (!artifact.AdmissionPublished || (artifact.ExecutionInFlight && !ReferenceEquals(artifact, execution)) ||
                (artifact.OriginalExecution is not null && !artifact.ExecutionPublished && !ReferenceEquals(artifact, execution))) &&
            artifact.Receipt.ChildLeaseId == child.LeaseId && artifact.Receipt.ChildGeneration == child.Generation);
    private readonly Dictionary<PlatformProviderChildDomainLeaseId, ChildRecord> _providerChildren = [];
    private readonly Dictionary<PlatformProviderGuestRegionMappingLeaseId, GuestRecord> _providerGuestMappings = [];
    private readonly Dictionary<PlatformProviderChildDomainLeaseId, ChildRecord> _originalProviderChildren = [];
    private readonly Dictionary<PlatformProviderGuestRegionMappingLeaseId, GuestRecord> _originalProviderGuestMappings = [];
    private readonly Dictionary<PlatformProviderVirtualIoLeaseId, IoRecord> _providerVirtualIo = [];
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
    {
        refusedBeforeEffect = false;
        var validation = PlatformChildDomainContract.ValidateCreateRequest(parentLease, intent);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(
                validation.Status,
                validation.Message ?? "The child-domain request is invalid.");
        }

        if (_childRuntime is null)
            return ChildContourBlocked<PlatformProviderChildDomainLease>("create child domain");
        DomainRecord parent;
        PlatformProviderChildDomainLeaseId reservedId;
        var neutralIntent = ToNeutral(intent);
        lock (_childCreateCommitGate)
        {
            if (!_domains.TryGetValue(parentLease.LeaseId, out parent!))
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Parent provider lease was not found.");
            var liveParent = ValidateDomain(parentLease);
            if (!liveParent.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(liveParent.Status, liveParent.Message!);
            if (parent.ChildCreateInFlight || parent.FailedChildCreate is not null)
            {
            refusedBeforeEffect = true;
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted,
            "Original child create is in flight or pinned.");
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
        }
        try
        {
            NeutralVirtualizationResult<NeutralChildDomainLease> external;
            try { external = _childRuntime.CreateChildDomain(parent.HybridCpuLease, neutralIntent); }
            catch
            {
                lock (_childCreateCommitGate)
                parent.FailedChildCreate ??= new(reservedId, parent.HybridCpuLease, neutralIntent, null);
                throw;
            }
            lock (_childCreateCommitGate)
            {
                if (!external.IsSuccess && external.Status != NeutralVirtualizationStatus.NotInvoked)
                parent.FailedChildCreate ??= new(reservedId, parent.HybridCpuLease, neutralIntent, external);
                if (!external.IsSuccess)
                {
                    refusedBeforeEffect = external.Status == NeutralVirtualizationStatus.NotInvoked;
                    return FromNeutral<PlatformProviderChildDomainLease>(external.Status, external.Reason);
                }
                var exact = NeutralChildDomainContract.ValidateLease(parent.HybridCpuLease, neutralIntent, external.Value);
                if (!exact.IsSuccess || !ValidateDomain(parentLease).IsSuccess ||
                !_domains.TryGetValue(parentLease.LeaseId, out var currentParent) || !ReferenceEquals(currentParent, parent))
                {
                    parent.FailedChildCreate ??= new(reservedId, parent.HybridCpuLease, neutralIntent, external);
                    return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original child result or final parent continuity failed; raw outcome retained without publication.");
                }
                var lease = new PlatformProviderChildDomainLease(reservedId, new(1), parentLease, intent);
                parent.FailedChildCreate = new(reservedId, parent.HybridCpuLease, neutralIntent, external);
                var originalRecord = new ChildRecord(lease, external.Value) { CreateParent = parent };
                _originalProviderChildren.Add(lease.LeaseId, originalRecord);
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
    {
        var validation = PlatformChildDomainContract.ValidateTransition(lease, transition);
        if (!validation.IsSuccess) return validation;
        if (_childRuntime is null) return ChildContourBlocked("transition child domain");
        ChildRecord child;
        lock (_childCreateCommitGate)
        {
        if (!_providerChildren.TryGetValue(lease.LeaseId, out child!) || !IsOriginalChildOwner(child) || child.Lease != lease)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Child provider lease is absent or stale.");
        if (child.TransitionInFlight)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Child transition is in flight.");
        if (child.FailedTransition is not null || child.UnclosedGuestMap is not null || child.GuestMapInFlight || child.CloseInFlight ||
            child.FailedClose is not null || child.ClosureReceipt is not null || HasGuestUnmapInterlock(child.Lease) ||
            HasArtifactBindInterlock(child.Lease))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original guest mapping invocation is unclosed.");
        var parent = ValidateDomain(lease.ParentDomainLease);
        if (!parent.IsSuccess) return parent;
        if (!ReferenceEquals(child.CreateParent, _domains[lease.ParentDomainLease.LeaseId]))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original child create parent changed before transition.");
        child.TransitionInFlight = true;
        }
        var neutralTransition = transition switch
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
        try { external = _childRuntime.TransitionChildDomain(child.Neutral, neutralTransition); }
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
                !_domains.TryGetValue(lease.ParentDomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, child.CreateParent) ||
                !_providerChildren.TryGetValue(lease.LeaseId, out var current) || !ReferenceEquals(current, child)))
        {
            child.FailedTransition ??= new(child.Neutral, neutralTransition, external);
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Transition inputs changed; original result retained without publication.");
        }
        return external.IsSuccess ? PlatformAuthorityResult.Ok() : FromNeutral(external.Status, external.Reason);
        }
        }
        finally { lock (_childCreateCommitGate) child.TransitionInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(
        PlatformProviderChildDomainLease lease)
    {
        var validation = PlatformChildDomainContract.ValidateLease(
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
        ChildClosureSource closureSource;
        lock (_childCreateCommitGate)
        {
        if (!_providerChildren.TryGetValue(lease.LeaseId, out child!) || !IsOriginalChildOwner(child) || child.Lease != lease)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Child provider lease is absent or stale.");
        if (child.GuestMapInFlight || child.TransitionInFlight)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Guest map or child transition admission is in flight; child close was not invoked.");
        if (child.UnclosedGuestMap is not null || child.FailedTransition is not null)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest map or child transition is unclosed; child closure is not inferred.");
        if (_originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease == child.Lease && mapping.FailedUnmap is not null))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest unmap is unclosed; child closure is not inferred.");
        if (_originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease == child.Lease))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Exact guest mappings must finish cleanup before child close; neutral close was not invoked.");
        if (HasArtifactBindInterlock(child.Lease))
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original artifact binding is in flight, unknown, or retained without release evidence.");
        if (child.CloseInFlight)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Child close admission is in flight.");
        if (child.FailedClose is not null)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original child close invocation lacks exact closure; repeating the neutral invocation is prohibited.");
        invoke = child.ClosureReceipt is null;
        closureSource = new ChildClosureSource(child.Lease, child.Neutral);
        if (invoke)
        {
            var parent = ValidateDomain(lease.ParentDomainLease);
            if (!parent.IsSuccess || !ReferenceEquals(child.CreateParent, _domains[lease.ParentDomainLease.LeaseId]))
                return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original child create parent is not current before close.");
            child.CloseInFlight = true;
        }
        }
        try
        {
        if (invoke)
        {
            try
            {
                var external = _childRuntime.CloseChildDomain(closureSource.Neutral);
                var closure = NeutralChildDomainContract.ValidateClose(closureSource.Neutral, external);
                lock (_childCreateCommitGate)
                {
                if (!closure.IsSuccess)
                {
                    if (external.Status != NeutralVirtualizationStatus.NotInvoked)
                        child.FailedClose ??= new(closureSource.Neutral, external);
                    return FromNeutral<PlatformChildDomainClosureReceipt>(closure.Status, closure.Reason);
                }
                child.ClosureReceipt = external.Value;
                child.ClosureSource = closureSource;
                }
            }
            catch
            {
                lock (_childCreateCommitGate) child.FailedClose ??= new(closureSource.Neutral, null);
                throw;
            }
        }
        lock (_childCreateCommitGate)
        {
        if (!IsOriginalChildOwner(child) || !_providerChildren.TryGetValue(lease.LeaseId, out var currentChild) || !ReferenceEquals(currentChild, child) ||
            child.ClosureSource != new ChildClosureSource(child.Lease, child.Neutral) ||
            !NeutralChildDomainContract.ValidateClose(child.Neutral,
                new(NeutralVirtualizationStatus.Success, child.ClosureReceipt!.Value, string.Empty)).IsSuccess ||
            (!child.ClosurePublished && (!ValidateDomain(lease.ParentDomainLease).IsSuccess ||
                !_domains.TryGetValue(lease.ParentDomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, child.CreateParent))) || child.GuestMapInFlight || child.TransitionInFlight ||
            child.UnclosedGuestMap is not null || child.FailedTransition is not null ||
            _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapping => mapping.Lease.ChildLease == lease) || HasArtifactBindInterlock(lease))
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
        child.ClosurePublished && IsOriginalChildOwner(child) &&
        child.ClosureSource == new ChildClosureSource(child.Lease, child.Neutral) && child.ClosureReceipt is { } receipt &&
        NeutralChildDomainContract.ValidateClose(child.Neutral,
            new(NeutralVirtualizationStatus.Success, receipt, string.Empty)).IsSuccess;

    private bool IsOriginalChildOwner(ChildRecord child) =>
        _originalProviderChildren.TryGetValue(child.Lease.LeaseId, out var original) && ReferenceEquals(original, child);

    private bool IsOriginalGuestOwner(GuestRecord mapping) =>
        _originalProviderGuestMappings.TryGetValue(mapping.Lease.LeaseId, out var original) && ReferenceEquals(original, mapping);

    public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(
        PlatformGuestRegionMappingRequest request)
    {
        var validation = PlatformGuestMemoryContract.ValidateRequest(request);
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
        lock (_childCreateCommitGate)
        {
        if (!_providerChildren.TryGetValue(request.ChildLease.LeaseId, out child!) || !IsOriginalChildOwner(child) ||
            !_providerMappings.TryGetValue(request.ParentMapping.Lease.MappingId, out parentMapping!))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Exact child or parent mapping was not found.");
        if (child.Lease != request.ChildLease)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Stale,
                "Child provider lease does not match the admitted tuple.");
        if (child.TransitionInFlight || child.GuestMapInFlight || parentMapping.GuestMapInFlight)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Original guest map or child transition admission is in flight.");
        if (child.FailedTransition is not null || parentMapping.FailedGuestMap is not null ||
            _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(mapped => !mapped.AdmissionPublished && mapped.Lease.ChildLease == child.Lease) ||
            child.UnclosedGuestMap is not null || child.CloseInFlight || child.FailedClose is not null ||
            child.ClosureReceipt is not null || HasGuestUnmapInterlock(child.Lease) || HasArtifactBindInterlock(child.Lease))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest mapping invocation is unclosed; retry refused.");
        var domain = ValidateDomain(request.ChildLease.ParentDomainLease);
        if (!domain.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(domain.Status, domain.Message!);
        parent = _domains[request.ChildLease.ParentDomainLease.LeaseId];
        if (!ReferenceEquals(parent, child.CreateParent))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted,
                "Original child create parent changed before guest map.");
        var mapping = ValidateProviderMapping(request.ParentMapping.Lease, request.ParentMapping.Slice);
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
        child.GuestMapInFlight = parentMapping.GuestMapInFlight = true;
        }
        void Retain(NeutralVirtualizationResult<NeutralGuestMappingLease>? observation)
        {
            var failure = new GuestMapFailure(reservedId, neutralRequest, observation);
            child.UnclosedGuestMap ??= failure;
            parentMapping.FailedGuestMap ??= failure;
        }
        try
        {
        NeutralVirtualizationResult<NeutralGuestMappingLease> external;
        try { external = guest.MapGuestRegion(neutralRequest); }
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
            return FromNeutral<PlatformProviderGuestRegionMappingLease>(external.Status, external.Reason);
        }
        var exact = NeutralGuestMemoryContract.ValidateLease(neutralRequest, external.Value);
        if (!exact.IsSuccess)
        {
            Retain(external);
            return FromNeutral<PlatformProviderGuestRegionMappingLease>(exact.Status, exact.Reason);
        }
        var publish = child.UnclosedGuestMap is null && child.FailedTransition is null &&
            _providerChildren.TryGetValue(request.ChildLease.LeaseId, out var currentChild) && ReferenceEquals(currentChild, child) &&
            _providerMappings.TryGetValue(request.ParentMapping.Lease.MappingId, out var currentMapping) && ReferenceEquals(currentMapping, parentMapping) &&
            _domains.TryGetValue(request.ChildLease.ParentDomainLease.LeaseId, out var currentParent) && ReferenceEquals(currentParent, parent) &&
            ValidateDomain(request.ChildLease.ParentDomainLease).IsSuccess &&
            ValidateProviderMapping(request.ParentMapping.Lease, request.ParentMapping.Slice).IsSuccess;
        var lease = new PlatformProviderGuestRegionMappingLease(reservedId, new(1), request.ChildLease,
            request.ParentMapping.Lease, request.GuestRange, request.Access);
        Retain(external);
        var originalRecord = new GuestRecord(lease, external.Value)
            { AdmissionPublished = publish, MapChild = child, MapSource = parentMapping, MapParent = parent };
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
            lock (_childCreateCommitGate) child.GuestMapInFlight = parentMapping.GuestMapInFlight = false;
        }
    }

    // Pending and original outcome dependencies belong to the existing mapping/guest records.
    private bool HasActiveProviderGuestMappings(PlatformProviderRegionMappingLease mapping) =>
        (_originalProviderMappings.TryGetValue(mapping.MappingId, out var source) && (source.GuestMapInFlight || source.FailedGuestMap is not null)) ||
        _originalProviderGuestMappings.Values.Where(mapping => !mapping.ClosurePublished).Any(guest => guest.Lease.ParentMappingLease == mapping);

    // Cleanup validates source identity, not renewed mapping authorization or live mapping use.
    private bool HasCurrentGuestMapSources(GuestRecord mapping) => IsOriginalGuestOwner(mapping) && !mapping.ClosurePublished &&
        _providerChildren.TryGetValue(mapping.Lease.ChildLease.LeaseId, out var child) && ReferenceEquals(child, mapping.MapChild) && IsOriginalChildOwner(child) &&
        _providerMappings.TryGetValue(mapping.Lease.ParentMappingLease.MappingId, out var source) && ReferenceEquals(source, mapping.MapSource) &&
        _domains.TryGetValue(mapping.Lease.ChildLease.ParentDomainLease.LeaseId, out var parent) && ReferenceEquals(parent, mapping.MapParent) &&
        ReferenceEquals(child.CreateParent, parent) && ReferenceEquals(source.MapParent, parent) &&
        child.Lease == mapping.Lease.ChildLease && child.Neutral == mapping.Neutral.ChildLease &&
        source.Mapping.Lease == mapping.Lease.ParentMappingLease && source.HybridCpuLease == mapping.Neutral.ParentMapping;

    public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(
        PlatformProviderGuestRegionMappingLease lease)
    {
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
        if (mapping.FailedArtifactBind is not null || _originalProviderArtifacts.Values.Any(artifact =>
                artifact.Receipt.MappingLeaseId == lease.LeaseId && artifact.Receipt.MappingGeneration == lease.Generation))
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original artifact binding remains pinned without exact release evidence.");
        if (mapping.UnmapInFlight)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Guest unmap admission is in flight.");
        if (mapping.FailedUnmap is not null)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest unmap invocation lacks exact closure; repeating the neutral invocation is prohibited.");
        invoke = mapping.ClosureReceipt is null;
        closureSource = new GuestClosureSource(mapping.Lease, mapping.Neutral);
        mapping.UnmapInFlight = true;
        }
        try
        {
        if (invoke)
        {
        try
        {
            var external = guest.UnmapGuestRegion(closureSource.Neutral);
            var closure = NeutralGuestMemoryContract.ValidateClose(closureSource.Neutral, external);
            lock (_childCreateCommitGate)
            {
            if (!closure.IsSuccess)
            {
                if (external.Status != NeutralVirtualizationStatus.NotInvoked)
                    mapping.FailedUnmap ??= new(closureSource.Neutral, external);
                return FromNeutral<PlatformGuestRegionMappingClosureReceipt>(closure.Status, closure.Reason);
            }
            mapping.ClosureReceipt = external.Value;
            mapping.ClosureSource = closureSource;
            }
        }
        catch
        {
            lock (_childCreateCommitGate) mapping.FailedUnmap ??= new(closureSource.Neutral, null);
            throw;
        }
        }
        lock (_childCreateCommitGate)
        {
        if (!_providerGuestMappings.TryGetValue(lease.LeaseId, out var currentMapping) || !ReferenceEquals(currentMapping, mapping) ||
            !HasCurrentGuestMapSources(mapping) ||
            mapping.ClosureSource != new GuestClosureSource(mapping.Lease, mapping.Neutral) ||
            !NeutralGuestMemoryContract.ValidateClose(mapping.Neutral,
                new(NeutralVirtualizationStatus.Success, mapping.ClosureReceipt!.Value, string.Empty)).IsSuccess ||
            !_providerChildren.TryGetValue(lease.ChildLease.LeaseId, out var currentChild) || !ReferenceEquals(currentChild, transitioning) ||
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
    {
        var validation = PlatformVirtualEventContract.ValidateRequest(request);
        if (!validation.IsSuccess)
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(
                validation.Status,
                validation.Message ?? "The virtual-event request is invalid.");
        return ChildContourBlocked<PlatformVirtualEventReceipt>("inject virtual event");
    }

    public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(
        PlatformVirtualIoRequest request)
    {
        var validation = PlatformVirtualIoContract.ValidateRequest(request);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(
                validation.Status,
                validation.Message ?? "The virtual-I/O request is invalid.");
        }

        if (_childRuntime is not INeutralVirtualIoProvider io)
            return ChildContourBlocked<PlatformProviderVirtualIoLease>("bind bounded virtual I/O");
        if (!_providerChildren.TryGetValue(request.ChildLease.LeaseId, out var child) ||
            !_deviceLeases.TryGetValue(request.ParentDeviceLease.LeaseId, out var device))
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Exact child or parent device was not found.");
        var neutral = new NeutralVirtualIoRequest(child.Neutral, device.HybridCpuLease,
            new(ToNeutralDeviceRights(request.Profile.Rights), request.Profile.MaximumDmaTransferBytes));
        if (child.UnclosedGuestMap is not null || child.GuestMapInFlight || child.CloseInFlight ||
            child.FailedClose is not null || child.ClosureReceipt is not null || HasGuestUnmapInterlock(child.Lease) ||
            HasArtifactBindInterlock(child.Lease))
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest mapping invocation is unclosed.");
        var external = io.BindVirtualIo(neutral);
        if (!external.IsSuccess) return FromNeutral<PlatformProviderVirtualIoLease>(external.Status, external.Reason);
        var lease = new PlatformProviderVirtualIoLease(new(_nextProviderIoId++), new(1), request.ChildLease,
            request.ParentDeviceLease, request.Profile);
        _providerVirtualIo.Add(lease.LeaseId, new(lease, external.Value));
        return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(lease);
    }

    public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(
        PlatformProviderVirtualIoLease lease)
    {
        if (_childRuntime is not INeutralVirtualIoProvider io)
            return ChildContourBlocked<PlatformVirtualIoClosureReceipt>("revoke bounded virtual I/O");
        if (!_providerVirtualIo.TryGetValue(lease.LeaseId, out var binding) || binding.Lease != lease)
            return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Virtual-I/O binding is absent or stale.");
        var external = io.CloseVirtualIo(binding.Neutral);
        if (!external.IsSuccess || !external.Value.IsTerminal)
            return FromNeutral<PlatformVirtualIoClosureReceipt>(external.Status, external.Reason);
        _providerVirtualIo.Remove(lease.LeaseId);
        return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
            lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
            lease.ChildLease.ParentDomainLease.Generation, true));
    }

    public PlatformAuthorityResult<PlatformExecutableArtifactReceipt> BindExecutableArtifact(PlatformExecutableArtifactRequest request)
    {
        var valid = PlatformChildExecutionContract.ValidateRequest(request);
        if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(valid.Status, valid.Message!);
        ChildRecord child;
        GuestRecord mapping;
        INeutralChildExecutionProvider execution;
        PlatformExecutableArtifactId reservedId;
        string digest;
        NeutralExecutableArtifactRequest neutralRequest;
        lock (_childCreateCommitGate)
        {
        if (_childRuntime is not INeutralChildExecutionProvider resolved ||
            !_providerChildren.TryGetValue(request.ChildLease.LeaseId, out child!) || !IsOriginalChildOwner(child) ||
            !_providerGuestMappings.TryGetValue(request.GuestMapping.LeaseId, out mapping!))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Unsupported, "Executable child adapter or exact mapping is unavailable.");
        execution = resolved;
        if (child.Lease != request.ChildLease || mapping.Lease != request.GuestMapping || !HasCurrentGuestMapSources(mapping))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Stale,
                "Executable child or guest mapping differs from the exact admitted tuple.");
        var liveParent = ValidateDomain(request.ChildLease.ParentDomainLease);
        if (!liveParent.IsSuccess)
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(liveParent.Status, liveParent.Message!);
        var liveSource = ValidateProviderMapping(mapping.Lease.ParentMappingLease, null);
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
        var package = request.ImmutablePackage.ToArray();
        digest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(package));
        neutralRequest = new NeutralExecutableArtifactRequest(child.Neutral, mapping.Neutral, package, request.MaximumExecutionSteps);
        _originalProviderArtifacts.EnsureCapacity(_originalProviderArtifacts.Count + 1);
        _providerArtifacts.EnsureCapacity(_originalProviderArtifacts.Count + 1);
        mapping.ArtifactBindInFlight = true;
        }
        try
        {
        NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> external;
        try { external = execution.BindExecutableArtifact(neutralRequest); }
        catch
        {
            lock (_childCreateCommitGate) mapping.FailedArtifactBind ??= new(reservedId, neutralRequest, digest, null);
            throw;
        }
        lock (_childCreateCommitGate)
        {
        if (!external.IsSuccess)
        {
            if (external.Status != NeutralVirtualizationStatus.NotInvoked)
                mapping.FailedArtifactBind ??= new(reservedId, neutralRequest, digest, external);
            return FromNeutral<PlatformExecutableArtifactReceipt>(external.Status, external.Reason);
        }
        var exact = external.Value.ContentDigest is null
            ? new NeutralVirtualizationResult(NeutralVirtualizationStatus.Faulted, "Artifact digest is absent.")
            : NeutralChildExecutionContract.ValidateAdmissionReceipt(neutralRequest, external.Value);
        if (!exact.IsSuccess || external.Value.ContentDigest != digest)
        {
            mapping.FailedArtifactBind ??= new(reservedId, neutralRequest, digest, external);
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Artifact receipt does not match the original admission tuple and captured package digest.");
        }
        bool publish = !HasArtifactBindInterlock(child.Lease, binding: mapping) && HasCurrentGuestMapSources(mapping) && child.FailedClose is null &&
            child.ClosureReceipt is null && child.UnclosedGuestMap is null && !child.CloseInFlight && !child.GuestMapInFlight &&
            _providerChildren.TryGetValue(child.Lease.LeaseId, out var currentChild) && ReferenceEquals(currentChild, child) &&
            ValidateDomain(request.ChildLease.ParentDomainLease).IsSuccess &&
            ValidateProviderMapping(mapping.Lease.ParentMappingLease, null).IsSuccess &&
            _providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out var current) && ReferenceEquals(current, mapping) &&
            !HasGuestUnmapInterlock(child.Lease);
        mapping.FailedArtifactBind = new(reservedId, neutralRequest, digest, external);
        var receipt = new PlatformExecutableArtifactReceipt(reservedId, new(1),
            request.ChildLease.LeaseId, request.ChildLease.Generation, request.GuestMapping.LeaseId,
            request.GuestMapping.Generation, request.ChildLease.ParentDomainLease.LeaseId,
            request.ChildLease.ParentDomainLease.Generation, external.Value.ContentDigest, request.MaximumExecutionSteps);
        var originalRecord = new ArtifactRecord(receipt, external.Value) { AdmissionPublished = publish, BindMapping = mapping };
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
            lock (_childCreateCommitGate) mapping.ArtifactBindInFlight = false;
        }
    }

    public PlatformAuthorityResult<PlatformChildExecutionReceipt> StartExecutableArtifact(
        PlatformChildExecutionStartRequest request)
    {
        var admission = PlatformChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(admission.Status, admission.Message!);
        ChildRecord child;
        GuestRecord mapping;
        ArtifactRecord found;
        INeutralChildExecutionProvider execution;
        NeutralChildExecutionStartRequest neutralRequest;
        lock (_childCreateCommitGate)
        {
        if (_childRuntime is not INeutralChildExecutionProvider resolved ||
            !_providerChildren.TryGetValue(request.ChildLease.LeaseId, out child!) || !IsOriginalChildOwner(child) ||
            !_providerArtifacts.TryGetValue(request.Artifact.ArtifactId, out found!) || found.Receipt != request.Artifact ||
            child.Lease != request.ChildLease)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Stale, "Executable artifact binding is absent or stale.");
        execution = resolved;
        if (!_originalProviderArtifacts.TryGetValue(request.Artifact.ArtifactId, out var originalArtifact) ||
            !ReferenceEquals(originalArtifact, found))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original executable artifact owner is not current before Start.");
        if (!_providerGuestMappings.TryGetValue(found.Receipt.MappingLeaseId, out mapping!) ||
            mapping.Lease.Generation != found.Receipt.MappingGeneration || mapping.Lease.ChildLease != request.ChildLease ||
            !mapping.AdmissionPublished || !ReferenceEquals(mapping, found.BindMapping) || !HasCurrentGuestMapSources(mapping))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Stale,
                "Executable artifact guest mapping is absent, stale, or unpublished.");
        if (_originalProviderArtifacts.Values.Any(artifact => artifact.ExecutionInFlight &&
                artifact.Receipt.ChildLeaseId == child.Lease.LeaseId && artifact.Receipt.ChildGeneration == child.Lease.Generation))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.NotAccepted,
                "Child execution admission is in flight.");
        if (found.OriginalExecution is not null)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Denied,
                "Artifact already has an original execution attempt; repeat effect refused.");
        if (child.UnclosedGuestMap is not null || child.GuestMapInFlight || child.CloseInFlight ||
            child.FailedClose is not null || child.ClosureReceipt is not null || HasGuestUnmapInterlock(child.Lease) ||
            HasArtifactBindInterlock(child.Lease) || !found.AdmissionPublished)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Original guest mapping invocation is unclosed.");
        neutralRequest = new NeutralChildExecutionStartRequest(child.Neutral, found.Neutral,
            new(request.OperationId.Value), new(request.OperationGeneration.Value));
        var parent = ValidateDomain(request.ChildLease.ParentDomainLease);
        var source = ValidateProviderMapping(mapping.Lease.ParentMappingLease, null);
        if (!parent.IsSuccess || !source.IsSuccess)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Stale,
                "Executable source is no longer live.");
        found.ExecutionInFlight = true;
        }
        try
        {
        NeutralVirtualizationResult<NeutralChildExecutionReceipt> external;
        try { external = execution.StartExecutableArtifact(neutralRequest); }
        catch
        {
            lock (_childCreateCommitGate) found.OriginalExecution ??= new(neutralRequest, null);
            throw;
        }
        lock (_childCreateCommitGate)
        {
        if (external.Status != NeutralVirtualizationStatus.NotInvoked)
            found.OriginalExecution ??= new(neutralRequest, external);
        if (!external.IsSuccess) return FromNeutral<PlatformChildExecutionReceipt>(external.Status, external.Reason);
        var original = NeutralChildExecutionContract.ValidateExecutionReceipt(neutralRequest, external.Value);
        if (!original.IsSuccess)
            return FromNeutral<PlatformChildExecutionReceipt>(original.Status, original.Reason);
        if (!HasCurrentGuestMapSources(mapping) || !ReferenceEquals(found.BindMapping, mapping) ||
            !_providerArtifacts.TryGetValue(found.Receipt.ArtifactId, out var liveArtifact) || !ReferenceEquals(liveArtifact, found) ||
            !_originalProviderArtifacts.TryGetValue(found.Receipt.ArtifactId, out var originalArtifact) || !ReferenceEquals(originalArtifact, found) ||
            HasArtifactBindInterlock(child.Lease, execution: found) ||
            child.Lease != request.ChildLease || child.FailedClose is not null || child.ClosureReceipt is not null ||
            child.CloseInFlight || child.GuestMapInFlight || child.UnclosedGuestMap is not null ||
            HasGuestUnmapInterlock(child.Lease) || !ValidateDomain(request.ChildLease.ParentDomainLease).IsSuccess ||
            !ValidateProviderMapping(mapping.Lease.ParentMappingLease, null).IsSuccess ||
            !_providerChildren.TryGetValue(child.Lease.LeaseId, out var liveChild) || !ReferenceEquals(liveChild, child) ||
            !_providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out var liveMapping) || !ReferenceEquals(liveMapping, mapping))
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                "Execution inputs changed; exact original response retained without publication or release evidence.");
        var value = external.Value;
        var artifact = request.Artifact;
        var receipt = new PlatformChildExecutionReceipt(artifact.ArtifactId, artifact.ArtifactGeneration,
            artifact.ChildLeaseId, artifact.ChildGeneration, artifact.MappingLeaseId, artifact.MappingGeneration,
            artifact.ParentLeaseId, artifact.ParentGeneration, artifact.ContentDigest,
            request.OperationId, request.OperationGeneration,
            new(value.ExecutionGeneration.Value), value.RetiredWorkUnits, value.RetiredSequence,
            value.LastRetiredCodeOffset, value.IsTerminal);
        var exact = PlatformChildExecutionContract.ValidateExecution(request, receipt);
        if (exact.IsSuccess) found.ExecutionPublished = true;
        return exact.IsSuccess ? PlatformAuthorityResult<PlatformChildExecutionReceipt>.Ok(receipt) :
            PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(exact.Status, exact.Message!);
        }
        }
        finally
        {
            lock (_childCreateCommitGate) found.ExecutionInFlight = false;
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
