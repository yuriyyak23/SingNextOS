using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformOwnedRegionMappingProvider,
    IPlatformRegionVisibilityProvider,
    IPlatformRegionRevocationResolutionProvider
{
    private sealed class OwnedMapFailure(PlatformProviderRegionMappingId invocationId,
        PlatformProviderDomainLease domain, NeutralDomainBindingLease neutralDomain,
        PlatformRegionSlice slice, NeutralOwnedRegionSlice neutralSlice, NeutralOwnedRegionMapResult? observation)
    {
        public PlatformProviderRegionMappingId InvocationId { get; } = invocationId;
        public PlatformProviderDomainLease Domain { get; } = domain;
        public NeutralDomainBindingLease NeutralDomain { get; } = neutralDomain;
        public PlatformRegionSlice Slice { get; } = slice;
        public NeutralOwnedRegionSlice NeutralSlice { get; } = neutralSlice;
        public NeutralOwnedRegionMapResult? Observation { get; set; } = observation;
        public PlatformOwnedRegionMapRequest? Request { get; init; }
        public NeutralOriginalOwnedRegionMapRequest? NativeRequest { get; set; }
        public NeutralOriginalOwnedRegionMapObservation? NativeObservation { get; set; }
        public NeutralOriginalOwnedRegionMapObservation? NativeResolutionObservation { get; set; }
        public ProviderMappingRecord? OriginalMapping { get; set; }
        public ProviderMappingRecord? CleanupMapping { get; set; }
    }
    private sealed class ProviderMappingRecord(
        PlatformProviderOwnedRegionMapping mapping,
        NeutralOwnedRegionMappingLease hybridCpuLease)
    {
        public PlatformProviderOwnedRegionMapping Mapping { get; } = mapping;
        public NeutralOwnedRegionMappingLease HybridCpuLease { get; } = hybridCpuLease;
        public DomainRecord? MapParent { get; init; }
        public bool CleanupOnly { get; set; }
        public PlatformOwnedRegionMapRequest? AdmissionRequest { get; init; }
        public OwnedMapFailure? MapAdmissionSource { get; init; }
        public bool GuestMapInFlight { get; set; }
        public GuestMapFailure? FailedGuestMap { get; set; }
        public bool Revoked { get; set; }
        public bool DmaBindInFlight { get; set; }
        public DmaBindFailure? FailedDmaBind { get; set; }
        public bool VisibilityInFlight { get; set; }
        public bool VisibilityUncertain { get; set; }
        public PlatformRegionVisibilityRequest? VisibilityRequest { get; set; }
        public DomainRecord? VisibilityParent { get; set; }
        public NeutralOwnedRegionVisibilityResult? VisibilityObservation { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralOwnedRegionCloseResult? CloseObservation { get; set; }
        public DomainRecord? CloseParent { get; set; }
        public NeutralOwnedRegionAcquireResult? AcquireObservation { get; set; }
        public ProviderOperationRecord? CloseOperation { get; set; }
        public PlatformRegionRevocationRequest? RevocationRequest { get; set; }
        public ProviderOperationRecord? PreparedCloseOperation { get; set; }
        public NeutralOriginalMappingCloseRequest? OriginalNeutralCloseRequest { get; set; }
        public NeutralOriginalMappingCloseResolution? NeutralCloseResolutionObservation { get; set; }
        public bool CloseResolutionInFlight { get; set; }
        public List<Dsc1BackingFact> Dsc1Copies {get;}=[];
        public ulong Dsc1CopiesIssued {get;set;}
    }

    private sealed class ProviderOperationRecord(
        PlatformOperationIdentity identity,
        PlatformCompletionReceipt receipt,
        ProviderMappingRecord mappingSource)
    {
        public PlatformOperationIdentity Identity { get; } = identity;
        public PlatformCompletionReceipt Receipt { get; } = receipt;
        public ProviderMappingRecord MappingSource { get; } = mappingSource;
    }

    private readonly Dictionary<PlatformProviderRegionMappingId, ProviderMappingRecord>
        _providerMappings = [];
    // Same authoritative mapping records; the routing projection may never replace them.
    private readonly Dictionary<PlatformProviderRegionMappingId, ProviderMappingRecord>
        _originalProviderMappings = [];
    private readonly Dictionary<PlatformOperationId, ProviderOperationRecord>
        _providerOperations = [];
    private ulong _nextProviderMappingId = 1;
    private ulong _nextProviderOperationId = 1;

    public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
        PlatformProviderDomainLease domainLease,
        PlatformRegionSlice slice) => MapOwnedRegionSliceCore(domainLease, slice, null);

    public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(PlatformOwnedRegionMapRequest request) => request.InvocationId == Guid.Empty
        ? PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.NotAccepted, "Original Map key is empty.")
        : MapOwnedRegionSliceCore(request.Domain, request.Slice, request);

    private PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSliceCore(
        PlatformProviderDomainLease domainLease, PlatformRegionSlice slice, PlatformOwnedRegionMapRequest? request)
    {
        var source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.NotAccepted, source.Message!);
        var domainValidation = ValidateDomain(domainLease);
        if (!domainValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                domainValidation.Status,
                domainValidation.Message ?? "The provider domain lease is not live.");
        }

        var sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
        if (!sliceValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                sliceValidation.Status,
                sliceValidation.Message ?? "The exact region slice is invalid.");
        }

        var expectedOwner = new SingPlus.Contracts.RegionOwner(
            domainLease.Subject.DomainId,
            domainLease.Subject.ProcessGeneration);
        if (slice.Region.Owner != expectedOwner)
        {
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The exact region slice is owned by a different Sing subject.");
        }

        DomainRecord domainRecord;
        PlatformProviderRegionMappingId reservedId;
        OwnedMapFailure originalMap;
        var neutralSlice = new NeutralOwnedRegionSlice(
            slice.Offset,
            slice.Length,
            ToNeutralAccess(slice.Access));
        lock (_childCreateCommitGate)
        {
            var current = ValidateDomain(domainLease);
            if (!current.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(current.Status, current.Message!);
            domainRecord = _domains[domainLease.LeaseId];
            if (request is { } intended && (_originalProviderMappings.Values.Any(x => x.AdmissionRequest?.InvocationId == intended.InvocationId) ||
                _domains.Values.Any(x => x.OwnedMapAdmissionOwner?.Request?.InvocationId == intended.InvocationId)))
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.NotAccepted, "Original Map key cannot be reused after consumption.");
            if (domainRecord.OwnedMapInFlight || domainRecord.FailedOwnedMap is not null)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.NotAccepted,
                    "Original owned mapping invocation is in flight or pinned.");
            if (_nextProviderMappingId == 0 || _nextProviderMappingId == ulong.MaxValue)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.NotAccepted,
                    "Owned mapping identity capacity is exhausted before neutral invocation.");
            _providerMappings.EnsureCapacity(checked(_providerMappings.Count + 1));
            _originalProviderMappings.EnsureCapacity(checked(_originalProviderMappings.Count + 1));
            reservedId = new(_nextProviderMappingId++);
            originalMap = new(reservedId, domainLease, domainRecord.HybridCpuLease, slice, neutralSlice, null) { Request = request };
            domainRecord.OwnedMapAdmissionOwner = originalMap;
            domainRecord.FailedOwnedMap = originalMap;
            domainRecord.OwnedMapInFlight = true;
        }
        try
        {
        NeutralOwnedRegionMapResult external;
        try
        {
            if (request is { } intended && _runtime is INeutralOriginalAdmissionProvider fenced &&
                domainRecord.BindSource is { NativeRequest: { } parentRequest,
                    NativeObservation: { SourceFenceMatched: true } parentObservation } &&
                parentObservation.Request == parentRequest && _originalNativeMappingIncarnation is { } incarnation)
            {
                var nativeRequest = new NeutralOriginalOwnedRegionMapRequest(intended.InvocationId, incarnation,
                    parentRequest.InvocationId, domainRecord.HybridCpuLease, neutralSlice);
                lock (_childCreateCommitGate) originalMap.NativeRequest = nativeRequest;
                var observation = fenced.MapOwnedRegion(nativeRequest);
                lock (_childCreateCommitGate) originalMap.NativeObservation = observation;
                external = observation.Request == nativeRequest && observation.SourceFenceMatched
                    ? observation.Observation
                    : new() { Decision = NeutralOwnedRegionMapDecision.Faulted, Reason = "Original native Map source fence refused or response malformed." };
            }
            else external = _runtime.MapOwnedRegion(domainRecord.HybridCpuLease, neutralSlice);
        }
        catch
        {
            lock (_childCreateCommitGate)
                domainRecord.FailedOwnedMap ??= new(reservedId, domainLease, domainRecord.HybridCpuLease, slice, neutralSlice, null);
            throw;
        }
        ProviderMappingRecord? originalOutput;
        lock (_childCreateCommitGate)
        {
            originalMap.Observation = external;
            if (external is { IsMapped: true, Decision: NeutralOwnedRegionMapDecision.Mapped } && external.Lease.Handle.Value != 0 &&
                external.Lease.Epoch.Value != 0 && external.Lease.DomainLease == domainRecord.HybridCpuLease && external.Lease.Slice == neutralSlice &&
                external.Lease.Coherence == NeutralMemoryCoherenceModel.NonCoherent)
            {
                var originalLease = new PlatformProviderRegionMappingLease(reservedId, new(1), domainLease, slice.Region, slice.Access);
                originalMap.OriginalMapping = new(new(originalLease, slice), external.Lease) { MapParent = domainRecord, CleanupOnly = true, AdmissionRequest = request, MapAdmissionSource = originalMap };
                originalMap.CleanupMapping = originalMap.OriginalMapping;
            }
            originalOutput = originalMap.OriginalMapping;
        }
        source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(source.Status, source.Message!);
        lock (_childCreateCommitGate)
        {
        if (external is null)
        {
            domainRecord.FailedOwnedMap ??= new(reservedId, domainLease, domainRecord.HybridCpuLease, slice, neutralSlice, null);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.Faulted,
                "Original owned mapping outcome is absent; source remains pinned.");
        }
        if (!external.IsMapped)
        {
            if (external.Lease != default ||
                external.Decision is not (NeutralOwnedRegionMapDecision.InvalidRange or NeutralOwnedRegionMapDecision.InvalidAccess))
                domainRecord.FailedOwnedMap ??= new(reservedId, domainLease, domainRecord.HybridCpuLease, slice, neutralSlice, external);
            var status = external.Decision switch
            {
                NeutralOwnedRegionMapDecision.InvalidRange => PlatformAuthorityStatus.Denied,
                NeutralOwnedRegionMapDecision.InvalidAccess => PlatformAuthorityStatus.Denied,
                NeutralOwnedRegionMapDecision.Revoked => PlatformAuthorityStatus.Revoked,
                NeutralOwnedRegionMapDecision.Stale => PlatformAuthorityStatus.Faulted,
                NeutralOwnedRegionMapDecision.NotFound => PlatformAuthorityStatus.Faulted,
                _ => PlatformAuthorityStatus.Faulted,
            };
            if (external.Lease != default) status = PlatformAuthorityStatus.Faulted;
            if (external.Lease == default && external.Decision is NeutralOwnedRegionMapDecision.InvalidRange or NeutralOwnedRegionMapDecision.InvalidAccess &&
                ReferenceEquals(domainRecord.FailedOwnedMap, originalMap))
            { domainRecord.FailedOwnedMap = null; domainRecord.OwnedMapAdmissionOwner = null; }
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                status,
                external.Reason);
        }

        if (external.Decision != NeutralOwnedRegionMapDecision.Mapped || external.Lease.Handle.Value == 0 ||
            external.Lease.Epoch.Value == 0 || external.Lease.DomainLease != domainRecord.HybridCpuLease ||
            external.Lease.Slice != neutralSlice ||
            external.Lease.Coherence != NeutralMemoryCoherenceModel.NonCoherent)
        {
            domainRecord.FailedOwnedMap ??= new(reservedId, domainLease, domainRecord.HybridCpuLease, slice, neutralSlice, external);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned mapping evidence that does not match the exact provider request.");
        }

        if (!ValidateDomain(domainLease).IsSuccess ||
            !_domains.TryGetValue(domainLease.LeaseId, out var finalDomain) || !ReferenceEquals(finalDomain, domainRecord))
        {
            domainRecord.FailedOwnedMap ??= new(reservedId, domainLease, domainRecord.HybridCpuLease, slice, neutralSlice, external);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.Faulted,
                "Original exact owned mapping retained without current owner publication.");
        }

        var lease = new PlatformProviderRegionMappingLease(
            reservedId,
            new PlatformProviderLeaseGeneration(1),
            domainRecord.Lease,
            slice.Region,
            slice.Access);
        var mapping = new PlatformProviderOwnedRegionMapping(lease, slice);
        if (!ReferenceEquals(domainRecord.FailedOwnedMap, originalMap) || !ReferenceEquals(domainRecord.OwnedMapAdmissionOwner, originalMap))
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.Faulted, "Original Map admission owner changed.");
        var originalMapping = originalMap.CleanupMapping!;
        if (!ReferenceEquals(originalMapping, originalOutput) || !ReferenceEquals(originalMap.OriginalMapping, originalOutput))
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.Faulted, "Original Map output owner changed before publication.");
        originalMapping.CleanupOnly = false;
        _originalProviderMappings.Add(lease.MappingId, originalMapping);
        _providerMappings.Add(lease.MappingId, originalMapping);
        if (ReferenceEquals(domainRecord.FailedOwnedMap, originalMap))
        { domainRecord.FailedOwnedMap = null; domainRecord.OwnedMapAdmissionOwner = null; }
        return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapping);
        }
        }
        finally { lock (_childCreateCommitGate) domainRecord.OwnedMapInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformRegionVisibilityResult> PrepareRegionMappingForConsumer(
        PlatformRegionVisibilityRequest request)
    {
        var source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(source.Status, source.Message!);
        var requestValidation = PlatformRegionVisibilityContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                requestValidation.Status,
                requestValidation.Message ?? "The region-visibility request is invalid.");
        }

        ProviderMappingRecord record;
        DomainRecord parent;
        lock (_childCreateCommitGate)
        {
            var validation = ValidateProviderMapping(request.Mapping, request.Slice);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(validation.Status, validation.Message!);
            record = _providerMappings[request.Mapping.MappingId];
            var domainValidation = ValidateDomain(request.Mapping.DomainLease);
            if (!domainValidation.IsSuccess)
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(domainValidation.Status, domainValidation.Message!);
            parent = _domains[request.Mapping.DomainLease.LeaseId];
            if (record.VisibilityInFlight || record.VisibilityUncertain ||
                (record.VisibilityRequest is { } previous && (previous != request || !ReferenceEquals(record.VisibilityParent, parent))))
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original visibility invocation is pending, unknown or belongs to a different source.");
            if (request.Consumer != PlatformMemoryConsumerClass.ExternalExecutionDomain)
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                    VisibilityResult(request, PlatformMemoryVisibilityOutcome.Unsupported));
            record.VisibilityRequest ??= request;
            record.VisibilityParent ??= parent;
            record.VisibilityInFlight = true;
        }
        try
        {
            NeutralOwnedRegionVisibilityResult external;
            try { external = record.VisibilityObservation ?? _runtime.PrepareOwnedRegionVisibility(
                record.HybridCpuLease, ToNeutralVisibilityRequirement(request.Requirement)); }
            catch
            {
                lock (_childCreateCommitGate) record.VisibilityUncertain = true;
                throw;
            }
            lock (_childCreateCommitGate) record.VisibilityObservation = external;
            source = ValidateOriginalNativeMappingSource();
            if (!source.IsSuccess) return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(source.Status, source.Message!);
            lock (_childCreateCommitGate)
            {
                if (external is null || !Enum.IsDefined(external.Decision) || !Enum.IsDefined(external.Outcome) ||
                    external.Lease != record.HybridCpuLease ||
                    external.Requirement != ToNeutralVisibilityRequirement(request.Requirement))
                {
                    record.VisibilityUncertain = true;
                    return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original visibility observation is absent, unknown or mismatches its invocation.");
                }
                var expectedOutcome = request.Requirement switch
                {
                    PlatformMemoryVisibilityRequirement.CoherentAccess => NeutralMemoryVisibilityOutcome.Coherent,
                    PlatformMemoryVisibilityRequirement.PublicationFence => NeutralMemoryVisibilityOutcome.PublicationFenceSatisfied,
                    PlatformMemoryVisibilityRequirement.CacheMaintenance => NeutralMemoryVisibilityOutcome.CacheMaintenanceSatisfied,
                    _ => NeutralMemoryVisibilityOutcome.Unsupported
                };
                if ((external.Decision == NeutralOwnedRegionVisibilityDecision.Satisfied &&
                        (external.Outcome != expectedOutcome ||
                         (external.Outcome == NeutralMemoryVisibilityOutcome.Coherent &&
                          record.HybridCpuLease.Coherence != NeutralMemoryCoherenceModel.Coherent))) ||
                    (external.Decision == NeutralOwnedRegionVisibilityDecision.Unsupported &&
                        external.Outcome != NeutralMemoryVisibilityOutcome.Unsupported))
                {
                    record.VisibilityUncertain = true;
                    return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(PlatformAuthorityStatus.Faulted,
                        "Visibility outcome does not refine the requested semantics.");
                }
                if (!_providerMappings.TryGetValue(request.Mapping.MappingId, out var current) || !ReferenceEquals(current, record) ||
                    !_domains.TryGetValue(parent.Lease.LeaseId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
                    !ValidateDomain(request.Mapping.DomainLease).IsSuccess ||
                    !ValidateProviderMapping(request.Mapping, request.Slice).IsSuccess)
                    return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original visibility observation retained without current source publication.");
                switch (external.Decision)
                {
                    case NeutralOwnedRegionVisibilityDecision.Satisfied:
                    case NeutralOwnedRegionVisibilityDecision.Unsupported:
                        var result = VisibilityResult(request, external.Decision == NeutralOwnedRegionVisibilityDecision.Unsupported
                            ? PlatformMemoryVisibilityOutcome.Unsupported : FromNeutralVisibilityOutcome(external.Outcome));
                        var resultValidation = PlatformRegionVisibilityContract.ValidateResult(request, result);
                        if (!resultValidation.IsSuccess)
                        {
                            record.VisibilityUncertain = true;
                            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(PlatformAuthorityStatus.Faulted,
                                resultValidation.Message!);
                        }
                        record.VisibilityObservation = null;
                        record.VisibilityRequest = null;
                        record.VisibilityParent = null;
                        return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(result);
                    default:
                        record.VisibilityUncertain = true;
                        return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(PlatformAuthorityStatus.Faulted,
                            "Visibility response does not establish mapping closure: " + external.Reason);
                }
            }
        }
        finally { lock (_childCreateCommitGate) record.VisibilityInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy)
        => BeginRegionMappingRevocationCore(mapping, policy, null);

    public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
        PlatformRegionRevocationRequest request)
    {
        if (request.InvocationId == Guid.Empty)
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Denied, "Original revocation request key is required.");
        return BeginRegionMappingRevocationCore(request.Mapping, request.Policy, request);
    }

    private PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocationCore(
        PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy,
        PlatformRegionRevocationRequest? request)
    {
        if (!Enum.IsDefined(policy) || policy != PlatformRegionRevocationPolicy.DrainBeforeRevoke)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Only drain-before-revoke is supported for neutral owned-region mappings.");
        }

        PlatformOperationIdentity operation;
        ProviderMappingRecord original;
        ProviderOperationRecord fact;
        lock (_childCreateCommitGate)
        {
            if (_originalProviderMappings.TryGetValue(mapping.MappingId, out var prior) &&
                (prior.RevocationRequest is not null || prior.CloseInFlight || prior.CloseUncertain))
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original mapping invocation already exists; no new no-effect disposition can replace it.");
        }
        var source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.NotAccepted, source.Message!);
        lock (_childCreateCommitGate)
        {
        var validation = ValidateProviderMapping(mapping, expectedSlice: null, allowClose: true);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                validation.Status,
                validation.Message ?? "The provider mapping is not live.");
        }

        original = _providerMappings[mapping.MappingId];
        if (original.RevocationRequest is not null || original.CloseInFlight || original.CloseUncertain || original.VisibilityInFlight || HasActiveProviderGuestMappings(mapping))
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Faulted,
                "Original mapping close is pending or unknown; repeat refused before operation reservation.");
        if (_nextProviderOperationId is 0 or ulong.MaxValue)
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Faulted,
                "Provider operation identity capacity is exhausted before mapping close.");
        operation = new PlatformOperationIdentity(new PlatformOperationId(_nextProviderOperationId++),
            new PlatformOperationGeneration(1), mapping.DomainLease);
        _providerOperations.EnsureCapacity(checked(_providerOperations.Count + 1));
        // Immutable intended output, unpublished until exact original Close succeeds.
        fact = new(operation, new(operation.OperationId, operation.Generation,
            operation.DomainLease, PlatformCompletionState.Closed), original);
        if (request is { } intended)
        {
            original.RevocationRequest = intended;
            original.PreparedCloseOperation = fact;
        }
        }

        var close = CloseProviderRegionMapping(mapping, original);
        if (!close.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                close.Status,
                close.Message ?? "The provider mapping could not be closed.");
        }

        lock (_childCreateCommitGate)
        {
            // Retain the completed fact before routing publication; neither a lost
            // index nor an allocation failure may erase a legally completed Close.
            original.CloseOperation = fact;
            if (!_originalProviderMappings.TryGetValue(mapping.MappingId, out var current) || !ReferenceEquals(current, original) ||
                !original.Revoked || original.CloseObservation?.Decision is not (NeutralOwnedRegionCloseDecision.Closed or NeutralOwnedRegionCloseDecision.Revoked))
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original completed mapping operation retained without owner publication.");
            if (_providerOperations.TryGetValue(operation.OperationId, out var existing) && !ReferenceEquals(existing, fact))
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Faulted, "Original operation route is foreign.");
            _providerOperations.TryAdd(operation.OperationId, fact);
        }

        return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
            new PlatformRegionRevocationTicket(
                mapping.MappingId,
                mapping.Generation,
                operation));
    }

    public PlatformAuthorityResult<PlatformRegionRevocationResolution> ResolveRegionMappingRevocation(
        PlatformRegionRevocationRequest request)
    {
        var underlying = ResolveOriginalNeutralMappingClose(request);
        if (!underlying.IsSuccess)
            return PlatformAuthorityResult<PlatformRegionRevocationResolution>.Fail(underlying.Status, underlying.Message!);
        lock (_childCreateCommitGate)
        {
            if (request.InvocationId == Guid.Empty || request.Policy != PlatformRegionRevocationPolicy.DrainBeforeRevoke ||
                !_originalProviderMappings.TryGetValue(request.Mapping.MappingId, out var original) ||
                original.Mapping.Lease != request.Mapping || original.RevocationRequest != request ||
                original.PreparedCloseOperation is not { } operation || !ReferenceEquals(operation.MappingSource, original) ||
                (_providerMappings.TryGetValue(request.Mapping.MappingId, out var route) && !ReferenceEquals(route, original)))
                return PlatformAuthorityResult<PlatformRegionRevocationResolution>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original revocation request/owner is absent or foreign; no effect repeated.");
            if (original.CloseInFlight || original.CloseUncertain || original.VisibilityInFlight ||
                original.CloseObservation?.Decision is not (NeutralOwnedRegionCloseDecision.Closed or NeutralOwnedRegionCloseDecision.Revoked) ||
                original.CloseParent is not { } parent || !ReferenceEquals(parent, original.MapParent) ||
                !_domains.TryGetValue(request.Mapping.DomainLease.LeaseId, out var currentParent) || !ReferenceEquals(parent, currentParent) ||
                parent.Lease != request.Mapping.DomainLease || !ValidateDomainIdentity(parent.Lease).IsSuccess || parent.RootCloseInFlight ||
                original.HybridCpuLease.DomainLease != parent.HybridCpuLease || HasActiveProviderDmaGrants(request.Mapping) ||
                HasActiveProviderGuestMappings(request.Mapping) ||
                (_providerOperations.TryGetValue(operation.Identity.OperationId, out var routedOperation) && !ReferenceEquals(routedOperation, operation)))
                return PlatformAuthorityResult<PlatformRegionRevocationResolution>.Fail(PlatformAuthorityStatus.Faulted,
                    "Exact original closure/source is unavailable; request remains pinned.");
            original.Revoked = true;
            original.CloseOperation = operation;
            _providerOperations.TryAdd(operation.Identity.OperationId, operation);
            return PlatformAuthorityResult<PlatformRegionRevocationResolution>.Ok(new(request,
                new(request.Mapping.MappingId, request.Mapping.Generation, operation.Identity)));
        }
    }

    private PlatformAuthorityResult ResolveOriginalNeutralMappingClose(PlatformRegionRevocationRequest request)
    {
        ProviderMappingRecord original;
        NeutralOriginalMappingCloseRequest nativeRequest;
        INeutralOriginalMappingClosureProvider native;
        NeutralOriginalMappingCloseResolution? retained;
        NeutralOwnedRegionCloseResult? knownClose;
        lock (_childCreateCommitGate)
        {
            if (!_originalProviderMappings.TryGetValue(request.Mapping.MappingId, out original!) ||
                original.Mapping.Lease != request.Mapping || original.RevocationRequest != request ||
                (_providerMappings.TryGetValue(request.Mapping.MappingId, out var route) && !ReferenceEquals(route, original)))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original native Close owner/request is absent or foreign.");
            knownClose = !original.CloseUncertain &&
                original.CloseObservation?.Decision is NeutralOwnedRegionCloseDecision.Closed or NeutralOwnedRegionCloseDecision.Revoked
                ? original.CloseObservation : null;
            if (knownClose is not null && (original.Revoked || original.OriginalNeutralCloseRequest is null))
                return PlatformAuthorityResult.Ok();
            if (_runtime is not INeutralOriginalMappingClosureProvider capable || original.OriginalNeutralCloseRequest is not { } intended ||
                original.CloseInFlight || original.CloseResolutionInFlight)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Exact native Close resolution unavailable or pending; original pins retained.");
            native = capable;
            nativeRequest = intended;
            retained = original.NeutralCloseResolutionObservation;
            original.CloseResolutionInFlight = true;
        }
        try
        {
            if (native.MappingClosureIncarnation != nativeRequest.RuntimeIncarnation)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original native Close incarnation changed.");
            // A direct original result is already known. Revalidate its source,
            // without attributing another query or effect to that invocation.
            var resolved = knownClose is not null
                ? new NeutralOriginalMappingCloseResolution(nativeRequest, knownClose)
                : retained ?? native.ResolveOwnedRegionMappingClose(nativeRequest);
            if (knownClose is null)
                lock (_childCreateCommitGate) original.NeutralCloseResolutionObservation = resolved;
            if (resolved is not { } observed || observed.Request != nativeRequest ||
                observed.Observation.Decision is not (NeutralOwnedRegionCloseDecision.Closed or NeutralOwnedRegionCloseDecision.Revoked))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original native Close has no exact closure observation.");
            var source = native.MappingClosureIncarnation; // Callback outside provider owner gate.
            lock (_childCreateCommitGate)
            {
                if (source != nativeRequest.RuntimeIncarnation || original.OriginalNeutralCloseRequest != nativeRequest ||
                    original.RevocationRequest != request || !_originalProviderMappings.TryGetValue(request.Mapping.MappingId, out var owner) || !ReferenceEquals(owner, original) ||
                    (_providerMappings.TryGetValue(request.Mapping.MappingId, out var route) && !ReferenceEquals(route, original)) ||
                    original.CloseParent is not { } parent || !ReferenceEquals(parent, original.MapParent) ||
                    !_domains.TryGetValue(request.Mapping.DomainLease.LeaseId, out var currentParent) || !ReferenceEquals(parent, currentParent) ||
                    !ValidateDomainIdentity(parent.Lease).IsSuccess || parent.RootCloseInFlight ||
                    HasActiveProviderDmaGrants(request.Mapping) || HasActiveProviderGuestMappings(request.Mapping))
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Native original closure retained without final owner/source continuity.");
                original.CloseObservation = observed.Observation;
                original.CloseUncertain = false;
                return PlatformAuthorityResult.Ok();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, $"Original native Close resolution unavailable: {exception.Message}");
        }
        finally { lock (_childCreateCommitGate) original.CloseResolutionInFlight = false; }
    }

    private NeutralOwnedRegionCloseResult InvokeOriginalMappingClose(ProviderMappingRecord record, PlatformProviderRegionMappingLease mapping,
        DomainRecord parent)
    {
        if (record.RevocationRequest is not { } request || _runtime is not INeutralOriginalMappingClosureProvider native)
            return _runtime.CloseOwnedRegionMapping(record.HybridCpuLease);
        var incarnation = _originalNativeMappingIncarnation ?? Guid.Empty;
        lock (_childCreateCommitGate)
        {
            if (incarnation == Guid.Empty || !record.CloseInFlight || record.RevocationRequest != request ||
                !_originalProviderMappings.TryGetValue(mapping.MappingId, out var original) || !ReferenceEquals(original, record) ||
                !_providerMappings.TryGetValue(mapping.MappingId, out var routed) || !ReferenceEquals(routed, record) ||
                !_domains.TryGetValue(mapping.DomainLease.LeaseId, out var root) || !ReferenceEquals(root, parent) ||
                !ValidateDomainIdentity(parent.Lease).IsSuccess || parent.RootCloseInFlight)
                throw new InvalidOperationException("Original native Close source/owner lost before effect.");
            record.OriginalNeutralCloseRequest = new(request.InvocationId, incarnation, record.HybridCpuLease);
        }
        return native.CloseOwnedRegionMapping(record.OriginalNeutralCloseRequest.Value);
    }

    public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
        PlatformOperationIdentity operation)
    {
        lock (_childCreateCommitGate)
        {
        ProviderOperationRecord? record;
        if (_providerOperations.TryGetValue(operation.OperationId, out var routed))
        {
            if (routed.Identity.OperationId != operation.OperationId ||
                !_originalProviderMappings.TryGetValue(routed.MappingSource.Mapping.Lease.MappingId, out var owner) ||
                !ReferenceEquals(owner, routed.MappingSource) || !ReferenceEquals(owner.CloseOperation, routed))
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(PlatformAuthorityStatus.Faulted,
                    "Provider completion route is not the original completed fact.");
            record = routed;
        }
        else
        {
            var source = _originalProviderMappings.Values.FirstOrDefault(mapping => mapping.CloseOperation?.Identity.OperationId == operation.OperationId);
            record = source?.CloseOperation;
            if (record is not null && !ReferenceEquals(record.MappingSource, source)) record = null;
        }
        if (record is null)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider operation does not exist.");
        }

        if (record.Identity.Generation != operation.Generation)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider operation generation is stale.");
        }

        if (record.Identity.DomainLease.LeaseId != operation.DomainLease.LeaseId)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider operation belongs to a different domain lease.");
        }

        if (record.Identity.DomainLease.Generation != operation.DomainLease.Generation)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider operation domain generation is stale.");
        }

        if (record.Identity.DomainLease.Subject != operation.DomainLease.Subject)
        {
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider operation belongs to a different Sing subject.");
        }

        return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(record.Receipt);
        }
    }

    private bool HasActiveProviderMappings(PlatformProviderDomainLease domainLease) =>
        _originalProviderMappings.Values.Any(record =>
            !record.Revoked && record.Mapping.Lease.DomainLease == domainLease);

    private PlatformAuthorityResult CloseProviderRegionMapping(
        PlatformProviderRegionMappingLease mapping, ProviderMappingRecord? expectedOriginal = null)
    {
        ProviderMappingRecord record;
        DomainRecord parent;
        lock (_childCreateCommitGate)
        {
        var validation = ValidateProviderMapping(mapping, expectedSlice: null, allowClose: true);
        if (!validation.IsSuccess) return validation;
        record = _providerMappings[mapping.MappingId];
        if (expectedOriginal is not null && !ReferenceEquals(record, expectedOriginal))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Mapping owner changed after operation preparation.");
        if (record.CloseInFlight || record.CloseUncertain || record.VisibilityInFlight || HasActiveProviderGuestMappings(mapping))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Original mapping close is in flight or has an unknown outcome; repeat refused.");
        if (!_domains.TryGetValue(mapping.DomainLease.LeaseId, out parent!) || parent.Lease != mapping.DomainLease ||
            parent.RootCloseInFlight)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Original mapping parent is absent, changed or closing.");

        if (HasActiveProviderDmaGrants(mapping))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU DMA grants must close before the provider region mapping.");
        }

        if (record.CloseParent is not null && !ReferenceEquals(record.CloseParent, parent))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original mapping close parent owner has changed.");
        record.CloseParent ??= parent;
        record.CloseInFlight = true;
        }
        try
        {
        NeutralOwnedRegionCloseResult external;
        try { external = record.CloseObservation ?? InvokeOriginalMappingClose(record, mapping, parent); }
        catch
        {
            lock (_childCreateCommitGate) record.CloseUncertain = true;
            throw;
        }
        NeutralOriginalMappingCloseRequest? nativeRequest;
        lock (_childCreateCommitGate)
        {
            record.CloseObservation = external;
            nativeRequest = record.OriginalNeutralCloseRequest;
        }
        Guid? nativeSource = null;
        if (nativeRequest is not null)
        {
            try
            {
                nativeSource = (_runtime as INeutralOriginalMappingClosureProvider)?.MappingClosureIncarnation;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                    $"Original mapping Close outcome retained while native source is unavailable: {exception.Message}");
            }
        }
        lock (_childCreateCommitGate)
        {
        if (external is null)
        {
            record.CloseUncertain = true;
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original mapping close observation is absent.");
        }
        if ((nativeRequest is { } intended &&
                (nativeSource != intended.RuntimeIncarnation || record.OriginalNeutralCloseRequest != intended)) ||
            !_originalProviderMappings.TryGetValue(mapping.MappingId, out var original) || !ReferenceEquals(original, record) ||
            !_providerMappings.TryGetValue(mapping.MappingId, out var current) || !ReferenceEquals(current, record) ||
            record.Mapping.Lease != mapping || record.HybridCpuLease.DomainLease != parent.HybridCpuLease ||
            !_domains.TryGetValue(mapping.DomainLease.LeaseId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
            parent.Lease != mapping.DomainLease || !ValidateDomainIdentity(parent.Lease).IsSuccess ||
            parent.Revoked || parent.RootCloseInFlight || HasActiveProviderDmaGrants(mapping))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Original mapping close observation retained without current owner/source publication.");
        switch (external.Decision)
        {
            case NeutralOwnedRegionCloseDecision.Closed:
            case NeutralOwnedRegionCloseDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();
            case NeutralOwnedRegionCloseDecision.ActiveDependents:
                record.CloseObservation = null;
                record.CloseParent = null;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    external.Reason);
            case NeutralOwnedRegionCloseDecision.Stale:
                record.CloseUncertain = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU rejected the provider-owned mapping lease as stale; closure is not proven.");
            case NeutralOwnedRegionCloseDecision.NotFound:
                record.CloseUncertain = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU no longer recognizes the provider-owned mapping lease; closure is not proven.");
            default:
                record.CloseUncertain = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
        }
        }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }

    private PlatformAuthorityResult ValidateProviderMapping(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionSlice? expectedSlice,
        bool allowClose = false)
    {
        lock (_childCreateCommitGate)
        {
        if (!_providerMappings.TryGetValue(mapping.MappingId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping does not exist.");
        }
        if (!_originalProviderMappings.TryGetValue(mapping.MappingId, out var original) || !ReferenceEquals(original, record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Provider mapping route is not the original owner.");
        if (record.CleanupOnly && !allowClose)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Resolved original mapping is cleanup-only.");

        if (record.Mapping.Lease.Generation != mapping.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider mapping generation is stale.");
        }

        if (record.Mapping.Lease.DomainLease.LeaseId != mapping.DomainLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider mapping belongs to a different domain lease.");
        }

        if (record.Mapping.Lease.DomainLease.Generation != mapping.DomainLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider mapping domain generation is stale.");
        }

        if (record.Mapping.Lease.DomainLease.Subject != mapping.DomainLease.Subject ||
            record.Mapping.Lease.Region.Owner != mapping.Region.Owner)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider mapping belongs to a different owner subject.");
        }

        if (record.Mapping.Lease.Region.Handle.RegionId != mapping.Region.Handle.RegionId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping refers to a different region.");
        }

        if (record.Mapping.Lease.Region.Handle.Generation != mapping.Region.Handle.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider mapping region generation is stale.");
        }

        if (record.Mapping.Lease.Region.ByteLength != mapping.Region.ByteLength ||
            record.Mapping.Lease.Access != mapping.Access)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping range authority or access does not match the materialized mapping.");
        }

        if (expectedSlice is { } slice && record.Mapping.Slice != slice)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider mapping does not match the exact region slice.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider mapping has already been revoked.");
        }
        if (!allowClose && (record.CloseInFlight || record.CloseUncertain || record.CloseObservation is not null))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Original mapping close is pending or retained; new mapping effects refused.");
        if (!_domains.TryGetValue(mapping.DomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, record.MapParent))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original mapping parent changed before admission.");

        return PlatformAuthorityResult.Ok();
        }
    }

    private static PlatformRegionVisibilityResult VisibilityResult(
        PlatformRegionVisibilityRequest request,
        PlatformMemoryVisibilityOutcome outcome) =>
        new(
            request.Mapping.MappingId,
            request.Mapping.Generation,
            request.Slice,
            request.Consumer,
            request.Requirement,
            outcome);

    private static NeutralMemoryAccess ToNeutralAccess(PlatformMemoryAccess access) =>
        access switch
        {
            PlatformMemoryAccess.Read => NeutralMemoryAccess.Read,
            PlatformMemoryAccess.Write => NeutralMemoryAccess.Write,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write =>
                NeutralMemoryAccess.Read | NeutralMemoryAccess.Write,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };

    private static NeutralMemoryVisibilityRequirement ToNeutralVisibilityRequirement(
        PlatformMemoryVisibilityRequirement requirement) =>
        requirement switch
        {
            PlatformMemoryVisibilityRequirement.CoherentAccess =>
                NeutralMemoryVisibilityRequirement.CoherentAccess,
            PlatformMemoryVisibilityRequirement.PublicationFence =>
                NeutralMemoryVisibilityRequirement.PublicationFence,
            PlatformMemoryVisibilityRequirement.CacheMaintenance =>
                NeutralMemoryVisibilityRequirement.CacheMaintenance,
            _ => throw new ArgumentOutOfRangeException(nameof(requirement)),
        };

    private static PlatformMemoryVisibilityOutcome FromNeutralVisibilityOutcome(
        NeutralMemoryVisibilityOutcome outcome) =>
        outcome switch
        {
            NeutralMemoryVisibilityOutcome.Coherent => PlatformMemoryVisibilityOutcome.Coherent,
            NeutralMemoryVisibilityOutcome.PublicationFenceSatisfied =>
                PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            NeutralMemoryVisibilityOutcome.CacheMaintenanceSatisfied =>
                PlatformMemoryVisibilityOutcome.CacheMaintenanceSatisfied,
            NeutralMemoryVisibilityOutcome.Unsupported => PlatformMemoryVisibilityOutcome.Unsupported,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
