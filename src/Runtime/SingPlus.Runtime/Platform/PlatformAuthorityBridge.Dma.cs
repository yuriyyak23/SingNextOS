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

    internal KernelResult<PlatformDmaGrant> BindDmaGrant(
        PlatformDeviceLease deviceLease,
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformDmaRange range,
        PlatformDmaDirection direction,
        Func<KernelResult> revalidateOwner)
    {
        lock (_dmaCompletionGate)
        {
            return BindDmaGrantLocked(
                deviceLease,
                mapping,
                expectedSubject,
                range,
                direction, revalidateOwner);
        }
    }

    private KernelResult<PlatformDmaGrant> BindDmaGrantLocked(
        PlatformDeviceLease deviceLease,
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformDmaRange range,
        PlatformDmaDirection direction,
        Func<KernelResult> revalidateOwner)
    {
        var deviceValidation = ValidateDeviceLease(deviceLease, expectedSubject);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        var mappingValidation = ValidateExactMapping(mapping, expectedSubject);
        if (!mappingValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                mappingValidation.Error,
                mappingValidation.Message!);
        }

        if (deviceLease.DomainBinding != mapping.Mapping.DomainBinding)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                KernelError.WrongPlatformDomain,
                "DMA device and exact region mapping must belong to the same local platform domain binding.");
        }

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaGrantContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission))
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise admission-only DMA grant contract v1.");
        }

        if (_provider is not IPlatformDmaGrantProvider dmaProvider)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose admission-only DMA grants.");
        }

        if (_dmaGrants.Values.Any(record =>
                !record.PlatformClosed &&
                record.Grant.Mapping.Mapping.MappingId == mapping.Mapping.MappingId))
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                KernelError.PlatformBindingActive,
                "The exact region mapping already has a live DMA grant in this admission-only slice.");
        }

        var deviceRecord = _deviceLeases[deviceLease.LeaseId];
        var mappingRecord = _mappings[mapping.Mapping.MappingId];
        var mappingSlice = _exactMappingSlices[mapping.Mapping.MappingId];
        var request = new PlatformDmaGrantRequest(
            deviceRecord.ProviderLease,
            mappingRecord.ProviderLease,
            mappingSlice,
            range,
            direction);
        var requestValidation = PlatformDmaGrantContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                requestValidation.Status == PlatformAuthorityStatus.WrongDomain
                    ? KernelError.WrongPlatformDomain
                    : KernelError.PlatformDenied,
                requestValidation.Message ?? "The DMA grant admission request is invalid.");
        }

        if (_nextDmaGrantId is 0 or ulong.MaxValue)
            return KernelResult<PlatformDmaGrant>.Fail(KernelError.CapacityExhausted,
                "Local DMA grant identity capacity is exhausted before provider admission.");
        var backendEpoch = BackendEpoch;
        var providerIncarnation = deviceRecord.ProviderIncarnation;
        var grant = new PlatformDmaGrant(new(_nextDmaGrantId++), new(1),
            deviceLease, mapping, range, direction);
        var record = new DmaGrantRecord(grant, default, providerIncarnation) { PreparationInFlight = true };
        _dmaGrants.Add(grant.GrantId, record);
        try { return AdmitPreparedGrant(); }
        finally { record.PreparationInFlight = false; }

        KernelResult RevalidateAdmission()
        {
            if (BackendEpoch != backendEpoch || _dmaSubmissionFaultPins.Contains(grant.GrantId))
                return KernelResult.Fail(KernelError.PlatformFaulted, "DMA grant admission lost backend continuity.");
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

        KernelResult<PlatformDmaGrant> AdmitPreparedGrant()
        {
            PlatformProviderIncarnation observed;
            try { observed = CurrentProviderIncarnation(); }
            catch (Exception exception)
            {
                FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted,
                    $"DMA admission generation read failed; dependencies remain pinned: {exception.Message}");
            }
            var admission = RevalidateAdmission();
            if (!admission.IsSuccess || observed.Value == 0 || observed != providerIncarnation)
            {
                if (BackendEpoch != backendEpoch || observed.Value == 0 || observed != providerIncarnation ||
                    _dmaSubmissionFaultPins.Contains(grant.GrantId))
                {
                    FaultPinDmaSubmissionLocked(grant.GrantId);
                    return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted,
                        "DMA admission continuity changed before the provider callback; dependencies remain pinned.");
                }
                _dmaGrants.Remove(grant.GrantId); // Pure pre-effect refusal, not provider closure.
                return KernelResult<PlatformDmaGrant>.Fail(admission.Error, admission.Message!);
            }

            PlatformAuthorityResult<PlatformProviderDmaGrant> providerResult;
            try
            {
                providerResult = dmaProvider.BindDmaGrant(request);
                if (providerResult.IsSuccess) record.ProviderGrant = providerResult.Value!;
                observed = CurrentProviderIncarnation();
            }
            catch (Exception exception)
            {
                FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult<PlatformDmaGrant>.Fail(KernelError.PlatformFaulted,
                    $"DMA admission or post-response generation read failed; dependencies remain pinned: {exception.Message}");
            }
            if (!providerResult.IsSuccess)
            {
                FaultPinDmaSubmissionLocked(grant.GrantId);
                return FromProviderFailure<PlatformDmaGrant>(providerResult.Status, providerResult.Message);
            }
            if (observed != providerIncarnation || BackendEpoch != backendEpoch)
                return Reject(observed != providerIncarnation ? KernelError.StaleGeneration : KernelError.PlatformFaulted,
                    "DMA provider or backend generation changed during grant admission.");
            var fresh = RevalidateAdmission();
            if (!fresh.IsSuccess) return Reject(fresh.Error, fresh.Message!);
            var providerValidation = PlatformDmaGrantContract.ValidateGrant(request, record.ProviderGrant);
            if (!providerValidation.IsSuccess)
                return Reject(KernelError.PlatformFaulted, providerValidation.Message ?? "Malformed provider DMA grant.");
            return KernelResult<PlatformDmaGrant>.Ok(grant);
        }

        KernelResult<PlatformDmaGrant> Reject(KernelError error, string message)
        {
            if (RevokeRejectedDmaAdmissionOrPin(dmaProvider, record, backendEpoch))
                _dmaGrants.Remove(grant.GrantId);
            return KernelResult<PlatformDmaGrant>.Fail(error, message);
        }
    }

    private bool RevokeRejectedDmaAdmissionOrPin(
        IPlatformDmaGrantProvider dmaProvider, DmaGrantRecord record, PlatformBackendEpoch backendEpoch)
    {
        try
        {
            var closure = dmaProvider.RevokeDmaGrant(record.ProviderGrant);
            if (closure.IsSuccess && CurrentProviderIncarnation() == record.ProviderIncarnation &&
                BackendEpoch == backendEpoch)
                return true;
        }
        catch (Exception) { /* An exception cannot establish closure. */ }
        FaultPinDmaSubmissionLocked(record.Grant.GrantId);
        return false;
    }
    internal KernelResult RevokeDmaGrant(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_dmaCompletionGate)
            return RevokeDmaGrantLocked(grant, expectedSubject);
    }

    private KernelResult RevokeDmaGrantLocked(
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
        try { return RevokeDmaGrantProviderLocked(grant, record, dmaProvider); }
        finally { record.ClosureInFlight = false; }
    }

    private KernelResult RevokeDmaGrantProviderLocked(
        PlatformDmaGrant grant, DmaGrantRecord record, IPlatformDmaGrantProvider dmaProvider)
    {
        // Reading provider generation is itself a callback boundary.
        var backendEpoch = BackendEpoch;
        PlatformProviderIncarnation providerIncarnation;
        try { providerIncarnation = CurrentProviderIncarnation(); }
        catch (Exception exception)
        {
            FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"DMA provider generation is unavailable before closure; the grant and mapping remain pinned: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch || HasFaultPinnedDmaSubmission(grant.GrantId) ||
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
            providerResult = dmaProvider.RevokeDmaGrant(record.ProviderGrant);
            closureIncarnation = CurrentProviderIncarnation();
        }
        catch (Exception exception)
        {
            FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"The DMA provider threw during grant closure; the grant and mapping remain pinned: {exception.Message}");
        }
        if (closureIncarnation != providerIncarnation ||
            BackendEpoch != backendEpoch)
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

    internal KernelResult ValidateDmaGrant(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
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
