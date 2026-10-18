using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDmaGrantId(ulong Value);
public readonly record struct PlatformDmaGrantGeneration(ulong Value);

public readonly record struct PlatformDmaGrant(
    PlatformDmaGrantId GrantId,
    PlatformDmaGrantGeneration Generation,
    PlatformDeviceLease DeviceLease,
    PlatformOwnedRegionSliceMapping Mapping,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction);

public sealed partial class PlatformAuthorityBridge
{
    private sealed class DmaGrantRecord
    {
        public DmaGrantRecord(PlatformDmaGrant grant, PlatformProviderDmaGrant providerGrant, PlatformProviderIncarnation providerIncarnation)
        { OwnerSelf = this; Grant = grant; ProviderGrant = providerGrant; ProviderIncarnation = providerIncarnation; }
        public DmaGrantRecord OwnerSelf { get; }
        public PlatformDmaGrant Grant { get; }
        private PlatformProviderDmaGrant _providerGrant;
        private bool _platformClosed;
        public PlatformProviderDmaGrant ProviderGrant { get => _providerGrant; set { AdvanceSource(); _providerGrant = value; } }
        public ulong SourceRevision { get; private set; }
        public bool SourceExhausted { get; private set; }
        private void AdvanceSource() { if (SourceRevision == ulong.MaxValue) SourceExhausted = true; else SourceRevision++; }
        public PlatformProviderIncarnation ProviderIncarnation { get; }
        public PlatformDmaGrantRequest OriginalRequest { get; init; }
        public DeviceLeaseRecord OriginalDeviceSource { get; init; } = null!;
        public MappingRecord OriginalMappingSource { get; init; } = null!;
        public bool BindInvoked { get; set; }
        public PlatformAuthorityResult<PlatformProviderDmaGrant>? RawBindObservation { get; set; }
        public bool BindValidated { get; set; }
        public PlatformOriginalDmaGrantBindRequest OriginalBindRequest { get; init; } = null!;
        public PlatformBackendEpoch BindEpoch { get; init; }
        public ulong BindRevision { get; set; }
        public bool BindUncertain { get; set; }
        public bool OwnVisibilityUncertain { get; set; }
        public List<DmaVisibilityOwnerInvocation> VisibilityHistory { get; } = [];
        public ulong VisibilityIssued { get; set; }
        public PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>? RawBindResolution { get; set; }
        public bool NeverInvokedConsumed { get; set; }
        public bool BindNoEffectConsumed { get; set; }
        public List<DmaGrantClosureInvocation> ClosureHistory { get; } = [];
        public DmaGrantClosureInvocation? OriginalClosure { get; set; }
        public bool PlatformClosed { get => _platformClosed; set { AdvanceSource(); _platformClosed = value; } }
        // Reference to the original record, never a duplicate completion/visibility ledger.
        public DmaSubmissionRecord? CompletedCopySubmission { get; set; }
        public PlatformProviderDmaGrant? KnownClosureGrant { get; set; }
        public PlatformBackendEpoch KnownClosureEpoch { get; set; }
        // Exact observed provider fact only; never cycle admission or closure evidence.
        public PlatformProviderDmaPrepareEvidence? KnownPreparationEvidence { get; set; }
        public PlatformBackendEpoch KnownPreparationEpoch { get; set; }
        public bool PreparationInFlight { get; set; }
        public bool SubmissionInFlight { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool AcquisitionInFlight { get; set; }
        // Observation metadata only; never read by grant admission or reclaim.
        public ulong ClosureBackendEpoch { get; set; }
        public SemanticTraceEventV1? LastVisibleTrace { get; set; }
        public ulong LastVisibleProviderGeneration { get; set; }
        public ulong LastVisibleBackendEpoch { get; set; }
        public PlatformProviderDmaCopySubmissionId? CopySubmissionId { get; set; }
        public PlatformProviderDmaCopySubmissionGeneration? CopyGeneration { get; set; }
        public PlatformDmaGrantId? CopyPeerGrantId { get; set; }
        public OriginalCopyInvocation? OriginalCopy { get; set; }
        public List<OriginalCopyInvocation> CopyHistory { get; } = [];
        public ulong CopiesIssued { get; set; }
    }

    private sealed class OriginalCopyInvocation(PlatformOriginalDmaCopyRequest request,DmaGrantRecord source,DmaGrantRecord destination)
    {
        public OriginalCopyInvocation OwnerSelf { get; private set; } = null!;
        public void EstablishSelf() => OwnerSelf = this;
        public ulong SourceRevision { get; } = source.SourceRevision;
        public ulong DestinationRevision { get; } = destination.SourceRevision;
        public PlatformBackendEpoch Epoch { get; init; }
        public DmaVisibilityState SourceCycle { get; init; } = null!;
        public DmaVisibilityState DestinationCycle { get; init; } = null!;
        public bool Uncertain { get; set; }
        public bool LifetimeConsumed { get; set; }
        public DmaSubmissionRecord? AcceptedSource { get; set; }
        public DmaSubmissionRecord? AcceptedDestination { get; set; }
        public bool Published { get; set; }
        public bool CleanupInFlight { get; set; }
        public PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime>? LifetimeRaw { get; set; }
        public PlatformOriginalDmaCopyRequest Request { get; } = request;
        public DmaGrantRecord Source { get; } = source;
        public DmaGrantRecord Destination { get; } = destination;
        public bool Invoked { get; set; }
        public PlatformAuthorityResult<PlatformProviderDmaCopySubmission>? Raw { get; set; }
        public PlatformAuthorityResult<PlatformOriginalDmaCopyResolution>? Resolution { get; set; }
    }

    private sealed class DmaGrantClosureInvocation
    {
        public DmaGrantClosureInvocation(DmaGrantRecord source, PlatformProviderDmaGrant grant, PlatformBackendEpoch epoch)
        { OwnerSelf = this; Source = source; Grant = grant; Epoch = epoch; Revision = source.SourceRevision; }
        public DmaGrantClosureInvocation OwnerSelf { get; }
        public DmaGrantRecord Source { get; }
        public PlatformProviderDmaGrant Grant { get; }
        public PlatformBackendEpoch Epoch { get; }
        public ulong Revision { get; }
        public bool SourceChanged { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool Invoked { get; set; }
        public PlatformAuthorityResult? RawObservation { get; set; }
        public PlatformAuthorityResult? RawResolutionObservation { get; set; }
        public bool Consumed { get; set; }
    }
    private readonly List<DmaGrantRecord> _dmaGrantOwners = [];
    private bool IsOriginalDmaGrant(DmaGrantRecord record) => ReferenceEquals(record.OwnerSelf, record) &&
        _dmaGrantOwners.Any(owner => ReferenceEquals(owner, record)) &&
        _dmaGrants.TryGetValue(record.Grant.GrantId, out var routed) && ReferenceEquals(routed, record);
    private static bool HasConsumedOriginalDmaClosure(DmaGrantRecord record) => ReferenceEquals(record.OwnerSelf, record) &&
        (record.NeverInvokedConsumed && !record.BindInvoked || record.BindNoEffectConsumed && (record.RawBindObservation is { Status: PlatformAuthorityStatus.NotAccepted } || record.RawBindResolution is { IsSuccess: true } resolution && resolution.Value.NoEffectGuaranteed && ReferenceEquals(resolution.Value.Request, record.OriginalBindRequest)) || record.OriginalClosure is { Invoked: true, Consumed: true } fact &&
         (fact.RawObservation is { IsSuccess: true } || fact.RawResolutionObservation is { IsSuccess: true }) &&
         ReferenceEquals(fact.OwnerSelf, fact) && ReferenceEquals(fact.Source, record) &&
         PlatformDmaGrantContract.ValidateGrant(record.OriginalRequest, fact.Grant).IsSuccess);
    private readonly Dictionary<PlatformDmaGrantId, DmaGrantRecord> _dmaGrants = [];
    private ulong _nextDmaGrantId = 1;

    internal KernelResult BeginMappingChildDrain(PlatformRegionMapping mapping, PlatformDomainIdentity subject,
        out Action release)
    {
        release = static () => { };
        lock (_dmaCompletionGate)
        {
            var exact = ValidateMappingIdentity(mapping, subject);
            if (!exact.IsSuccess) return exact;
            var record = _mappings[mapping.MappingId];
            if (record.ClosureState == PlatformExternalClosureState.Faulted)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Mapping closure is faulted.");
            if (record.LocalReservationReleased || record.ClosureState == PlatformExternalClosureState.Closed)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Mapping is already closed.");
            if (record.PendingChildDrains != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Mapping child drain is in flight.");
            record.PendingChildDrains++;
            release = () => { lock (_dmaCompletionGate) record.PendingChildDrains--; };
            return KernelResult.Ok();
        }
    }

    internal KernelResult<PlatformDmaGrant> BindDmaGrant(
        PlatformDeviceLease deviceLease, PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject, PlatformDmaRange range, PlatformDmaDirection direction,
        Func<KernelResult> revalidateOwner, Func<Func<KernelResult>, KernelResult> ownerCommit,
        Action<PlatformDmaGrant, bool> trackLifetime,
        Func<CapabilityId, CapabilityId, Func<KernelResult>, KernelResult> authorizeCommit)
    {
        DeviceLeaseRecord deviceRecord = null!;
        MappingRecord mappingRecord = null!;
        PlatformDmaGrantRequest request = default;
        PlatformBackendEpoch backendEpoch = default;
        PlatformProviderIncarnation providerIncarnation = default;
        PlatformDmaGrant grant = default;
        DmaGrantRecord record = null!;
        IPlatformDmaGrantProvider dmaProvider = null!;
        ulong? bindPublicationRevision = null;
        var prepared = ownerCommit(() =>
        {
            lock (_dmaCompletionGate)
            {
                var result = Prepare();
                if (result.IsSuccess) trackLifetime(grant, true);
                return result;
            }
        });
        if (!prepared.IsSuccess) return KernelResult<PlatformDmaGrant>.Fail(prepared.Error, prepared.Message!);
        try { return Admit(); }
        finally { lock (_dmaCompletionGate) record.PreparationInFlight = false; }

        KernelResult Prepare()
        {
        var deviceValidation = ValidateDeviceLease(deviceLease, expectedSubject);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        var mappingValidation = ValidateExactMapping(mapping, expectedSubject);
        if (!mappingValidation.IsSuccess)
        {
            return KernelResult.Fail(
                mappingValidation.Error,
                mappingValidation.Message!);
        }

        if (deviceLease.DomainBinding != mapping.Mapping.DomainBinding)
        {
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "DMA device and exact region mapping must belong to the same local platform domain binding.");
        }

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaGrantContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission))
        {
            return KernelResult.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise admission-only DMA grant contract v1.");
        }

        if (_provider is not IPlatformDmaGrantProvider availableProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose admission-only DMA grants.");
        }

        dmaProvider = availableProvider;
        if (_dmaGrantOwners.Any(record =>
                !HasConsumedOriginalDmaClosure(record) &&
                record.Grant.Mapping.Mapping.MappingId == mapping.Mapping.MappingId))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingActive,
                "The exact region mapping already has a live DMA grant in this admission-only slice.");
        }

        deviceRecord = _deviceLeases[deviceLease.LeaseId];
        mappingRecord = _mappings[mapping.Mapping.MappingId];
        var mappingSlice = _exactMappingSlices[mapping.Mapping.MappingId];
        request = new PlatformDmaGrantRequest(
            deviceRecord.ProviderLease,
            mappingRecord.ProviderLease,
            mappingSlice,
            range,
            direction);
        var requestValidation = PlatformDmaGrantContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult.Fail(
                requestValidation.Status == PlatformAuthorityStatus.WrongDomain
                    ? KernelError.WrongPlatformDomain
                    : KernelError.PlatformDenied,
                requestValidation.Message ?? "The DMA grant admission request is invalid.");
        }

        if (_nextDmaGrantId is 0 or ulong.MaxValue)
            return KernelResult.Fail(KernelError.CapacityExhausted,
                "Local DMA grant identity capacity is exhausted before provider admission.");
        var owner = revalidateOwner();
        if (!owner.IsSuccess) return owner;
        backendEpoch = BackendEpoch;
        providerIncarnation = deviceRecord.ProviderIncarnation;
        grant = new PlatformDmaGrant(new(_nextDmaGrantId++), new(1),
            deviceLease, mapping, range, direction);
        record = new DmaGrantRecord(grant, default, providerIncarnation) { PreparationInFlight = true, OriginalRequest = request, OriginalBindRequest = new(request), BindEpoch = backendEpoch, OriginalDeviceSource = deviceRecord, OriginalMappingSource = mappingRecord };
        record.BindRevision = record.SourceRevision;
        return authorizeCommit(deviceRecord.AuthorityCapabilityId, mappingRecord.AuthorityCapabilityId, () =>
        {
            _dmaGrantOwners.Add(record); // Existing authoritative owner retained before effect; route is correlation.
            _dmaGrants.Add(grant.GrantId, record);
            return KernelResult.Ok();
        });
        }
        KernelResult Revalidate()
        {
            if (!IsOriginalDmaGrant(record) || record.SourceExhausted || bindPublicationRevision is { } revision && record.SourceRevision != revision ||
                BackendEpoch != backendEpoch || _dmaSubmissionFaultPins.Contains(grant.GrantId))
                return KernelResult.Fail(KernelError.PlatformFaulted, "DMA admission lost backend continuity.");
            var device = ValidateDeviceLease(deviceLease, expectedSubject);
            if (!device.IsSuccess) return device;
            var exactMapping = ValidateExactMapping(mapping, expectedSubject);
            if (!exactMapping.IsSuccess) return exactMapping;
            if (!ReferenceEquals(_deviceLeases[deviceLease.LeaseId], deviceRecord) ||
                !ReferenceEquals(_mappings[mapping.Mapping.MappingId], mappingRecord) ||
                mappingRecord.ProviderIncarnation != providerIncarnation)
                return KernelResult.Fail(KernelError.PlatformFaulted, "DMA admission owner identity changed.");
            return revalidateOwner();
        }
        void ForgetPureLocalRecord()
        {
            ownerCommit(() =>
            {
                lock (_dmaCompletionGate)
                {
                    if (!ReferenceEquals(record.OwnerSelf, record) || !_dmaGrantOwners.Any(owner => ReferenceEquals(owner, record)))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA owner is unavailable for local consumption.");
                    if (!record.BindInvoked) record.NeverInvokedConsumed = true;
                    if (!HasConsumedOriginalDmaClosure(record))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA effect has not closed.");
                    if (_dmaGrants.TryGetValue(grant.GrantId, out var routed) && ReferenceEquals(routed, record)) _dmaGrants.Remove(grant.GrantId);
                    trackLifetime(grant, false);
                    return KernelResult.Ok();
                }
            });
        }
        KernelResult<PlatformDmaGrant> Admit()
        {
            PlatformProviderIncarnation observed;
            try { observed = CurrentProviderIncarnation(); }
            catch (Exception exception)
            {
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted, exception.Message);
            }
            var admission = ownerCommit(() => { lock (_dmaCompletionGate) return authorizeCommit(deviceRecord.AuthorityCapabilityId, mappingRecord.AuthorityCapabilityId, Revalidate); });
            if (!admission.IsSuccess || observed.Value == 0 || observed != providerIncarnation)
            {
                if (BackendEpoch != backendEpoch || observed.Value == 0 || observed != providerIncarnation)
                {
                    lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
                    return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted, "DMA source changed before admission.");
                }
                ForgetPureLocalRecord();
                return KernelResult<PlatformDmaGrant>.Fail(admission.Error, admission.Message!);
            }
            PlatformAuthorityResult<PlatformProviderDmaGrant> response;
            lock (_dmaCompletionGate)
            {
                if (!IsOriginalDmaGrant(record) || record.BindInvoked)
                    return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted, "Original DMA admission changed or was already invoked.");
                record.BindInvoked = true;
            }
            try { response = dmaProvider is IPlatformOriginalDmaGrantProvider original ? original.BindDmaGrant(record.OriginalBindRequest) : dmaProvider.BindDmaGrant(request); }
            catch (Exception exception)
            {
                lock (_dmaCompletionGate) { if (dmaProvider is IPlatformOriginalDmaGrantProvider) record.BindUncertain = true; else FaultPinDmaSubmissionLocked(grant.GrantId); }
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted, exception.Message);
            }
            lock (_dmaCompletionGate) record.RawBindObservation = response; // All raw statuses before source callbacks.
            if (!response.IsSuccess)
            {
                if (response.Status == PlatformAuthorityStatus.NotAccepted && dmaProvider is IPlatformDmaGrantAdmissionNoEffectProvider)
                {
                    PlatformProviderIncarnation fresh;
                    try { fresh = CurrentProviderIncarnation(); }
                    catch { return FromProviderFailure<PlatformDmaGrant>(response.Status, response.Message); }
                    var consumed = ownerCommit(() =>
                    {
                        lock (_dmaCompletionGate)
                        {
                            if (!IsOriginalDmaGrant(record) || record.RawBindObservation != response || BackendEpoch != backendEpoch || fresh != providerIncarnation)
                                return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA Bind no-effect retained without current source.");
                            record.BindNoEffectConsumed = true;
                            _dmaGrants.Remove(grant.GrantId); trackLifetime(grant, false);
                            return KernelResult.Ok();
                        }
                    });
                    return FromProviderFailure<PlatformDmaGrant>(consumed.IsSuccess ? response.Status : PlatformAuthorityStatus.Faulted, response.Message);
                }
                lock (_dmaCompletionGate) { if (dmaProvider is IPlatformOriginalDmaGrantProvider) record.BindUncertain = true; else FaultPinDmaSubmissionLocked(grant.GrantId); }
                return FromProviderFailure<PlatformDmaGrant>(response.Status, response.Message);
            }
            lock (_dmaCompletionGate) record.ProviderGrant = response.Value!;
            var exactReceipt = PlatformDmaGrantContract.ValidateGrant(request, record.ProviderGrant);
            if (!exactReceipt.IsSuccess)
            {
                // The returned tuple is not evidence of the original invocation's closure.
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted,
                    exactReceipt.Message ?? "Malformed DMA receipt; original effect remains pinned.");
            }
            lock (_dmaCompletionGate) { record.BindValidated = true; bindPublicationRevision = record.SourceRevision; }
            try { observed = CurrentProviderIncarnation(); }
            catch (Exception exception) { return Reject(KernelError.PlatformFaulted, exception.Message); }
            var publication = ownerCommit(() =>
            {
                lock (_dmaCompletionGate)
                {
                    if (observed.Value == 0 || observed != providerIncarnation)
                        return KernelResult.Fail(KernelError.StaleGeneration, "DMA source changed before publication.");
                    return authorizeCommit(deviceRecord.AuthorityCapabilityId, mappingRecord.AuthorityCapabilityId, Revalidate);
                }
            });
            return publication.IsSuccess ? KernelResult<PlatformDmaGrant>.Ok(grant)
                : Reject(publication.Error, publication.Message!);
        }
        KernelResult<PlatformDmaGrant> Reject(KernelError error, string message)
        {
            lock (_dmaCompletionGate) record.ClosureInFlight = true;
            KernelResult cleanup;
            try { cleanup = RevokeDmaGrantProvider(grant, record, dmaProvider); }
            finally { lock (_dmaCompletionGate) record.ClosureInFlight = false; }
            if (cleanup.IsSuccess) ForgetPureLocalRecord();
            return KernelResult<PlatformDmaGrant>.Fail(error, message);
        }
    }
    internal KernelResult RevokeDmaGrant(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var copyCleanup = ResolveOriginalCopyLifetimeForCleanup(grant,expectedSubject);
        if (!copyCleanup.IsSuccess) return copyCleanup;
        DmaGrantRecord record;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
            var completed = _dmaGrantOwners.Where(owner => owner.Grant == grant && HasConsumedOriginalDmaClosure(owner)).ToArray();
            if (completed.Length == 1 && grant.DeviceLease.DomainBinding.Subject == expectedSubject)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Exact original DMA grant closure was already consumed.");
            var admission = BeginDmaGrantClosureLocked(grant, expectedSubject);
            if (!admission.IsSuccess) return admission;
            record = _dmaGrants[grant.GrantId];
        }
        try { return RevokeDmaGrantProvider(grant, record, (IPlatformDmaGrantProvider)_provider); }
        finally { lock (_dmaCompletionGate) record.ClosureInFlight = false; }
    }

    private KernelResult BeginDmaGrantClosureLocked(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDmaGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess) return validation;
        if (_dmaGrants[grant.GrantId].PreparationInFlight || _dmaGrants[grant.GrantId].SubmissionInFlight ||
            _dmaGrants[grant.GrantId].ClosureInFlight || _dmaGrants[grant.GrantId].AcquisitionInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "DMA grant admission is in flight.");

        if (HasFaultPinnedDmaSubmission(grant.GrantId))
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The DMA grant cannot close because submission state is fault-pinned and an external effect may still exist.");
        }

        if (HasActiveDmaSubmission(grant.GrantId))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "The DMA grant cannot close until exact completion and required post-completion visibility have both finished.");
        }

        var record = _dmaGrants[grant.GrantId];
        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform DMA grant has already been closed.");
        }

        if (_provider is not IPlatformDmaGrantProvider dmaProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the DMA grant no longer exposes DMA grant closure.");
        }

        record.ClosureInFlight = true;
        return KernelResult.Ok();
    }

    private KernelResult RevokeDmaGrantProvider(
        PlatformDmaGrant grant, DmaGrantRecord record, IPlatformDmaGrantProvider dmaProvider)
    {
        if (!HasExactDmaCleanupGrant(record))
        {
            var resolved = ResolveOriginalDmaBindForCleanup(record, dmaProvider);
            if (!resolved.IsSuccess || record.BindNoEffectConsumed) return resolved;
        }
        DmaGrantClosureInvocation fact;
        lock (_dmaCompletionGate)
        {
            if (!IsOriginalDmaGrant(record) || record.Grant != grant || !record.ClosureInFlight || record.PlatformClosed ||
                !HasExactDmaCleanupGrant(record) || !CanContainPendingOriginalDmaVisibility(record) ||
                !PlatformDmaGrantContract.ValidateGrant(record.OriginalRequest, record.ProviderGrant).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA Bind owner/raw response is unavailable for closure.");
            fact = record.OriginalClosure ?? new(record, record.ProviderGrant, BackendEpoch);
            if (record.OriginalClosure is null) { record.ClosureHistory.Add(fact); record.OriginalClosure = fact; }
            if (!ExactFact() || fact.Invoked && fact.RawObservation is not { IsSuccess: true } &&
                !HasExactNoEffect() &&
                dmaProvider is not IPlatformDmaGrantRevocationResolutionProvider)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA closure outcome is unknown; repeat refused.");
        }
        try
        {
            var incarnation = CurrentProviderIncarnation(); // Callback outside owner gate.
            bool invoke;
            lock (_dmaCompletionGate)
            {
                if (!CurrentSource(incarnation))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA source changed before closure/consumption.");
                invoke = !fact.Invoked;
                if (invoke) fact.Invoked = true; // Before possible external effect.
            }
            if (invoke)
            {
                var result = dmaProvider.RevokeDmaGrant(fact.Grant);
                lock (_dmaCompletionGate)
                {
                    fact.RawObservation = result; // Every original raw status before later callbacks.
                    if (result.IsSuccess) { record.KnownClosureGrant = fact.Grant; record.KnownClosureEpoch = fact.Epoch; }
                }
            }
            if (fact.RawObservation is not { IsSuccess: true } && fact.RawResolutionObservation is not { IsSuccess: true } &&
                !HasExactNoEffect() &&
                dmaProvider is IPlatformDmaGrantRevocationResolutionProvider resolver)
            {
                var resolution = resolver.ResolveDmaGrantRevocation(fact.Grant); // Pure producer lookup, never a repeated effect.
                lock (_dmaCompletionGate) fact.RawResolutionObservation = resolution;
            }
            var freshIncarnation = CurrentProviderIncarnation();
            lock (_dmaCompletionGate)
            {
                if (!CurrentSource(freshIncarnation))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA closure lacks exact source; grant remains pinned.");
                if (HasExactNoEffect())
                {
                    var noEffect = dmaProvider is IPlatformDmaGrantRevocationNoEffectProvider && fact.RawObservation is { Status: PlatformAuthorityStatus.NotAccepted } direct ? direct : fact.RawResolutionObservation!.Value;
                    fact.NoEffectConsumed = true; record.OriginalClosure = null;
                    return FromProviderFailure(noEffect.Status, noEffect.Message); // No closure/publication or release.
                }
                if (fact.RawObservation is not { IsSuccess: true } && fact.RawResolutionObservation is not { IsSuccess: true })
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA closure outcome unknown; no repeat.");
                fact.Consumed = true;
                MarkDmaGrantClosed(grant.GrantId, record, fact.Epoch);
                return KernelResult.Ok();
            }
        }
        catch (Exception exception)
        {
            return KernelResult.Fail(KernelError.PlatformFaulted, $"Original DMA closure retained without repeat: {exception.Message}");
        }
        bool HasExactNoEffect() =>
            dmaProvider is IPlatformDmaGrantRevocationNoEffectProvider && fact.RawObservation is { Status: PlatformAuthorityStatus.NotAccepted } ||
            dmaProvider is IPlatformDmaGrantRevocationResolutionNoEffectProvider && fact.RawResolutionObservation is { Status: PlatformAuthorityStatus.NotAccepted };
        bool ExactFact() => ReferenceEquals(record.OriginalClosure, fact) && record.ClosureHistory.Any(old => ReferenceEquals(old, fact)) && ReferenceEquals(fact.OwnerSelf, fact) &&
            ReferenceEquals(fact.Source, record) && fact.Grant == record.ProviderGrant;
        bool CurrentSource(PlatformProviderIncarnation incarnation)
        {
            if (incarnation.Value != 0 && incarnation != record.ProviderIncarnation || BackendEpoch != fact.Epoch || record.SourceRevision != fact.Revision || record.SourceExhausted || !record.ClosureInFlight)
                fact.SourceChanged = true; // Independent observed source loss cannot be repaired by an equal/ABA projection.
            return !fact.SourceChanged && IsOriginalDmaGrant(record) && ExactFact() &&
                _deviceLeases.TryGetValue(grant.DeviceLease.LeaseId, out var device) && ReferenceEquals(device, record.OriginalDeviceSource) &&
                _mappings.TryGetValue(grant.Mapping.Mapping.MappingId, out var mapping) && ReferenceEquals(mapping, record.OriginalMappingSource) &&
                HasExactDmaCleanupGrant(record) &&
                record.Grant == grant && record.ClosureInFlight && !record.PlatformClosed && BackendEpoch == fact.Epoch && !_backendEpochExhausted &&
                incarnation.Value != 0 && incarnation == record.ProviderIncarnation && !HasFaultPinnedDmaSubmission(grant.GrantId) && !HasActiveDmaSubmission(grant.GrantId);
        }
    }

    private static bool HasExactDmaCleanupGrant(DmaGrantRecord record) =>
        record.BindValidated && record.RawBindObservation is { IsSuccess: true } raw && raw.Value == record.ProviderGrant ||
        record.RawBindResolution is { IsSuccess: true } resolved && ReferenceEquals(resolved.Value.Request, record.OriginalBindRequest) &&
        !resolved.Value.NoEffectGuaranteed && resolved.Value.CleanupGrant is { } cleanup && cleanup == record.ProviderGrant &&
        PlatformDmaGrantContract.ValidateGrant(record.OriginalRequest, cleanup).IsSuccess;

    private KernelResult ResolveOriginalDmaBindForCleanup(DmaGrantRecord record, IPlatformDmaGrantProvider provider)
    {
        if (provider is not IPlatformOriginalDmaGrantProvider original)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA Bind remains unknown; provider has no original lookup.");
        try
        {
            var before = CurrentProviderIncarnation();
            lock (_dmaCompletionGate)
                if (!Exact(before)) return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA Bind source unavailable before lookup.");
            var raw = record.RawBindResolution is { IsSuccess: true } retained ? retained : original.ResolveDmaGrantBind(record.OriginalBindRequest);
            lock (_dmaCompletionGate) record.RawBindResolution = raw; // Retain before source callback.
            var after = CurrentProviderIncarnation();
            lock (_dmaCompletionGate)
            {
                if (!Exact(after) || !raw.IsSuccess || !ReferenceEquals(raw.Value.Request, record.OriginalBindRequest) ||
                    raw.Value.NoEffectGuaranteed == raw.Value.CleanupGrant.HasValue)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA Bind resolution missing/foreign/malformed; pinned without repeat.");
                if (raw.Value.NoEffectGuaranteed)
                {
                    record.BindNoEffectConsumed = true; record.BindUncertain = false;
                    return KernelResult.Ok(); // No Close or fabricated Closed projection.
                }
                var cleanup = raw.Value.CleanupGrant!.Value;
                if (!PlatformDmaGrantContract.ValidateGrant(record.OriginalRequest, cleanup).IsSuccess)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA cleanup receipt malformed.");
                record.ProviderGrant = cleanup; record.BindUncertain = false;
                return KernelResult.Ok(); // Cleanup only; BindValidated remains false, publication remains denied.
            }
        }
        catch (Exception exception) { return KernelResult.Fail(KernelError.PlatformFaulted, exception.Message); }
        bool Exact(PlatformProviderIncarnation incarnation) => IsOriginalDmaGrant(record) && record.BindInvoked && record.ClosureInFlight &&
            !record.PreparationInFlight && !record.SourceExhausted && record.SourceRevision == record.BindRevision &&
            BackendEpoch == record.BindEpoch && !_backendEpochExhausted && incarnation.Value != 0 && incarnation == record.ProviderIncarnation &&
            !HasFaultPinnedDmaSubmission(record.Grant.GrantId) && !HasActiveDmaSubmission(record.Grant.GrantId) &&
            _deviceLeases.TryGetValue(record.Grant.DeviceLease.LeaseId, out var device) && ReferenceEquals(device, record.OriginalDeviceSource) &&
            _mappings.TryGetValue(record.Grant.Mapping.Mapping.MappingId, out var mapping) && ReferenceEquals(mapping, record.OriginalMappingSource);
    }

    internal KernelResult ValidateDmaGrant(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
            return ValidateDmaGrantLocked(grant, expectedSubject);
    }

    private KernelResult ValidateDmaGrantLocked(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDmaGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _dmaGrants[grant.GrantId];
        if (record.PreparationInFlight || record.SubmissionInFlight || record.ClosureInFlight || record.AcquisitionInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "DMA grant admission is in flight.");
        if (record.PlatformClosed || HasConsumedOriginalDmaClosure(record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform DMA grant has been closed.");
        }

        if (HasPendingOriginalDmaVisibility(record) || record.OriginalClosure is not null || record.SourceExhausted || !record.BindValidated || record.RawBindObservation is not { IsSuccess: true } raw || raw.Value != record.ProviderGrant)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA admission is closing, unknown or source-exhausted.");
        var device = ValidateDeviceLease(grant.DeviceLease, expectedSubject);
        if (!device.IsSuccess) return device;
        return ValidateExactMapping(grant.Mapping, expectedSubject);
    }

    internal bool HasActiveDmaGrants(PlatformDeviceLease deviceLease)
    {
        lock (_dmaCompletionGate)
        {
            return _dmaGrantOwners.Any(record =>
                !HasConsumedOriginalDmaClosure(record) &&
                record.Grant.DeviceLease.LeaseId == deviceLease.LeaseId);
        }
    }

    internal bool HasActiveDmaGrants(PlatformRegionMapping mapping)
    {
        lock (_dmaCompletionGate)
        {
            return _dmaGrantOwners.Any(record =>
                !HasConsumedOriginalDmaClosure(record) &&
                record.Grant.Mapping.Mapping.MappingId == mapping.MappingId);
        }
    }

    internal IReadOnlyList<PlatformDmaGrant> ActiveDmaGrantsForDevice(
        PlatformDeviceLease deviceLease)
    {
        lock (_dmaCompletionGate)
        {
            return _dmaGrantOwners
                .Where(record =>
                    !HasConsumedOriginalDmaClosure(record) &&
                    record.Grant.DeviceLease.LeaseId == deviceLease.LeaseId)
                .OrderBy(record => record.Grant.GrantId.Value)
                .Select(static record => record.Grant)
                .ToArray();
        }
    }

    internal IReadOnlyList<PlatformDmaGrant> ActiveDmaGrantsForMapping(
        PlatformRegionMapping mapping)
    {
        lock (_dmaCompletionGate)
        {
            return _dmaGrantOwners
                .Where(record =>
                    !HasConsumedOriginalDmaClosure(record) &&
                    record.Grant.Mapping.Mapping.MappingId == mapping.MappingId)
                .OrderBy(record => record.Grant.GrantId.Value)
                .Select(static record => record.Grant)
                .ToArray();
        }
    }

    internal IReadOnlyList<PlatformDmaGrant> ActiveDmaGrantsForSubject(PlatformDomainIdentity subject)
    {
        lock (_dmaCompletionGate)
            return _dmaGrantOwners.Where(owner => !HasConsumedOriginalDmaClosure(owner) && owner.Grant.DeviceLease.DomainBinding.Subject == subject)
                .Select(owner => owner.Grant).ToArray();
    }

    internal bool IsOriginalDmaGrantClosureConsumed(PlatformDmaGrant grant, PlatformDomainIdentity subject)
    {
        lock (_dmaCompletionGate)
            return grant.DeviceLease.DomainBinding.Subject == subject &&
                _dmaGrantOwners.Count(owner => owner.Grant == grant && HasConsumedOriginalDmaClosure(owner)) == 1;
    }

    private void MarkDmaGrantClosed(PlatformDmaGrantId grantId, DmaGrantRecord record,
        PlatformBackendEpoch closureEpoch)
    {
        record.OwnVisibilityUncertain = false;
        foreach (var visibility in record.VisibilityHistory) visibility.ClosedByOriginalGrant = record.OriginalClosure;
        record.PlatformClosed = true;
        // Preserve the exact epoch validated for closure, not a later observation.
        record.ClosureBackendEpoch = closureEpoch.Value;
        _dmaVisibilityStates.Remove(grantId);
        _activeDmaSubmissions.Remove(grantId);
        _dmaSubmissionFaultPins.Remove(grantId);
    }

    private KernelResult ValidateDmaGrantIdentity(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_dmaGrants.TryGetValue(grant.GrantId, out var record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform DMA grant does not exist.");
        }

        if (!IsOriginalDmaGrant(record))
            return KernelResult.Fail(KernelError.PlatformFaulted, "DMA routing does not identify the original admitted owner.");
        if (record.Grant.Generation != grant.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform DMA grant generation is stale.");
        }

        if (record.Grant != grant)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform DMA grant identity is malformed.");
        }

        return ValidateDeviceLeaseIdentity(grant.DeviceLease, expectedSubject);
    }

    private PlatformProviderIncarnation CurrentProviderIncarnation() =>
        _provider is IPlatformProviderIncarnationSource source
            ? source.CurrentIncarnation
            : new PlatformProviderIncarnation(1);
}
