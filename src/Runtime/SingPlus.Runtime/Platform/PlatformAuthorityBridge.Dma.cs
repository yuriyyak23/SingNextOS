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
    private sealed class DmaGrantRecord(
        PlatformDmaGrant grant,
        PlatformProviderDmaGrant providerGrant,
        PlatformProviderIncarnation providerIncarnation)
    {
        public PlatformDmaGrant Grant { get; } = grant;
        public PlatformProviderDmaGrant ProviderGrant { get; set; } = providerGrant;
        public PlatformProviderIncarnation ProviderIncarnation { get; } = providerIncarnation;
        public bool PlatformClosed { get; set; }
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
    }

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
        if (_dmaGrants.Values.Any(record =>
                !record.PlatformClosed &&
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
        record = new DmaGrantRecord(grant, default, providerIncarnation) { PreparationInFlight = true };
        return authorizeCommit(deviceRecord.AuthorityCapabilityId, mappingRecord.AuthorityCapabilityId, () =>
        {
            _dmaGrants.Add(grant.GrantId, record);
            return KernelResult.Ok();
        });
        }
        KernelResult Revalidate()
        {
            if (BackendEpoch != backendEpoch || _dmaSubmissionFaultPins.Contains(grant.GrantId))
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
                    _dmaGrants.Remove(grant.GrantId);
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
            try { response = dmaProvider.BindDmaGrant(request); }
            catch (Exception exception)
            {
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted, exception.Message);
            }
            if (!response.IsSuccess)
            {
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
                return FromProviderFailure<PlatformDmaGrant>(response.Status, response.Message);
            }
            lock (_dmaCompletionGate) record.ProviderGrant = response.Value!;
            var exactReceipt = PlatformDmaGrantContract.ValidateGrant(request, record.ProviderGrant);
            if (!exactReceipt.IsSuccess)
            {
                // The returned tuple is not evidence of the original invocation's closure.
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
                try { dmaProvider.RevokeDmaGrant(record.ProviderGrant); }
                catch (Exception exception) when (exception is not StackOverflowException) { }
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted,
                    exactReceipt.Message ?? "Malformed DMA receipt; original effect remains pinned.");
            }
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
        DmaGrantRecord record;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
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
        // Reading provider generation is itself a callback boundary.
        PlatformBackendEpoch backendEpoch;
        PlatformProviderDmaGrant invokedGrant;
        bool knownClosure;
        lock (_dmaCompletionGate)
        {
            if (!_dmaGrants.TryGetValue(grant.GrantId, out var original) || !ReferenceEquals(original, record) ||
                record.Grant != grant || !record.ClosureInFlight || record.PlatformClosed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA cleanup owner or pending admission is unavailable.");
            knownClosure = record.KnownClosureGrant is not null;
            backendEpoch = knownClosure ? record.KnownClosureEpoch : BackendEpoch;
            invokedGrant = record.KnownClosureGrant ?? record.ProviderGrant;
        }
        PlatformProviderIncarnation providerIncarnation;
        try { providerIncarnation = CurrentProviderIncarnation(); }
        catch (Exception exception)
        {
            lock (_dmaCompletionGate)
                if (record.KnownClosureGrant is null || BackendEpoch != backendEpoch)
                    FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"DMA provider generation is unavailable before closure; the grant and mapping remain pinned: {exception.Message}");
        }
        lock (_dmaCompletionGate)
        if (!_dmaGrants.TryGetValue(grant.GrantId, out var admitted) || !ReferenceEquals(admitted, record) ||
            record.Grant != grant || !record.ClosureInFlight || record.PlatformClosed || record.ProviderGrant != invokedGrant ||
            BackendEpoch != backendEpoch || _backendEpochExhausted || HasFaultPinnedDmaSubmission(grant.GrantId) ||
            HasActiveDmaSubmission(grant.GrantId) || providerIncarnation.Value == 0 ||
            providerIncarnation != record.ProviderIncarnation)
        {
            FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The DMA provider incarnation changed before exact grant closure; the grant and mapping remain pinned.");
        }

        PlatformAuthorityResult providerResult;
        PlatformProviderIncarnation closureIncarnation;
        try
        {
            providerResult = knownClosure
                ? PlatformAuthorityResult.Ok() : dmaProvider.RevokeDmaGrant(invokedGrant);
        }
        catch (Exception exception)
        {
            lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"The DMA provider threw during grant closure; the grant and mapping remain pinned: {exception.Message}");
        }
        if (providerResult.IsSuccess)
        {
            lock (_dmaCompletionGate)
            {
                record.KnownClosureGrant = invokedGrant;
                record.KnownClosureEpoch = backendEpoch;
            }
        }
        try { closureIncarnation = CurrentProviderIncarnation(); }
        catch (Exception exception)
        {
            lock (_dmaCompletionGate)
                if (!providerResult.IsSuccess || BackendEpoch != backendEpoch) FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"DMA closure reply retained while source observation failed: {exception.Message}");
        }
        lock (_dmaCompletionGate)
        {
        if (closureIncarnation != providerIncarnation ||
            BackendEpoch != backendEpoch || record.ProviderGrant != invokedGrant ||
            !_dmaGrants.TryGetValue(grant.GrantId, out var exact) || !ReferenceEquals(exact, record) ||
            !record.ClosureInFlight || HasFaultPinnedDmaSubmission(grant.GrantId) || HasActiveDmaSubmission(grant.GrantId))
        {
            FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "DMA provider or local backend generation changed during grant closure; the grant and mapping remain pinned.");
        }
        if (!providerResult.IsSuccess)
        {
            // A terminal or already-revoked status does not identify the exact
            // grant generation whose effects have been contained. Once the
            // closure callback ran, an unsuccessful result may follow an effect.
            if (providerResult.Status != PlatformAuthorityStatus.NotAccepted)
            {
                FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "DMA grant closure lacks exact containment evidence; the grant and mapping remain pinned.");
            }
            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        MarkDmaGrantClosed(grant.GrantId, record, backendEpoch);
        return KernelResult.Ok();
        }
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
        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform DMA grant has been closed.");
        }

        var device = ValidateDeviceLease(grant.DeviceLease, expectedSubject);
        if (!device.IsSuccess) return device;
        return ValidateExactMapping(grant.Mapping, expectedSubject);
    }

    internal bool HasActiveDmaGrants(PlatformDeviceLease deviceLease)
    {
        lock (_dmaCompletionGate)
        {
            return _dmaGrants.Values.Any(record =>
                !record.PlatformClosed &&
                record.Grant.DeviceLease.LeaseId == deviceLease.LeaseId);
        }
    }

    internal bool HasActiveDmaGrants(PlatformRegionMapping mapping)
    {
        lock (_dmaCompletionGate)
        {
            return _dmaGrants.Values.Any(record =>
                !record.PlatformClosed &&
                record.Grant.Mapping.Mapping.MappingId == mapping.MappingId);
        }
    }

    internal IReadOnlyList<PlatformDmaGrant> ActiveDmaGrantsForDevice(
        PlatformDeviceLease deviceLease)
    {
        lock (_dmaCompletionGate)
        {
            return _dmaGrants.Values
                .Where(record =>
                    !record.PlatformClosed &&
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
            return _dmaGrants.Values
                .Where(record =>
                    !record.PlatformClosed &&
                    record.Grant.Mapping.Mapping.MappingId == mapping.MappingId)
                .OrderBy(record => record.Grant.GrantId.Value)
                .Select(static record => record.Grant)
                .ToArray();
        }
    }

    private void MarkDmaGrantClosed(PlatformDmaGrantId grantId, DmaGrantRecord record,
        PlatformBackendEpoch closureEpoch)
    {
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
