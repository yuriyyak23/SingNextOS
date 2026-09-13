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
        PlatformProviderDmaGrant providerGrant)
    {
        public PlatformDmaGrant Grant { get; } = grant;
        public PlatformProviderDmaGrant ProviderGrant { get; } = providerGrant;
        public bool PlatformClosed { get; set; }
    }

    private readonly Dictionary<PlatformDmaGrantId, DmaGrantRecord> _dmaGrants = [];
    private ulong _nextDmaGrantId = 1;

    internal KernelResult<PlatformDmaGrant> BindDmaGrant(
        PlatformDeviceLease deviceLease,
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformDmaRange range,
        PlatformDmaDirection direction)
    {
        lock (_dmaCompletionGate)
        {
            return BindDmaGrantLocked(
                deviceLease,
                mapping,
                expectedSubject,
                range,
                direction);
        }
    }

    private KernelResult<PlatformDmaGrant> BindDmaGrantLocked(
        PlatformDeviceLease deviceLease,
        PlatformOwnedRegionSliceMapping mapping,
        PlatformDomainIdentity expectedSubject,
        PlatformDmaRange range,
        PlatformDmaDirection direction)
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

        var providerResult = dmaProvider.BindDmaGrant(request);
        if (!providerResult.IsSuccess)
        {
            return FromProviderFailure<PlatformDmaGrant>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerGrant = providerResult.Value!;
        var providerValidation = PlatformDmaGrantContract.ValidateGrant(request, providerGrant);
        if (!providerValidation.IsSuccess)
        {
            _ = dmaProvider.RevokeDmaGrant(providerGrant);
            return KernelResult<PlatformDmaGrant>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed DMA grant authority.");
        }

        var grant = new PlatformDmaGrant(
            new PlatformDmaGrantId(_nextDmaGrantId++),
            new PlatformDmaGrantGeneration(1),
            deviceLease,
            mapping,
            range,
            direction);
        _dmaGrants.Add(grant.GrantId, new DmaGrantRecord(grant, providerGrant));
        return KernelResult<PlatformDmaGrant>.Ok(grant);
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

        var providerResult = dmaProvider.RevokeDmaGrant(record.ProviderGrant);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status == PlatformAuthorityStatus.Revoked)
            {
                MarkDmaGrantClosed(grant.GrantId, record);
                return KernelResult.Ok();
            }

            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        MarkDmaGrantClosed(grant.GrantId, record);
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

    private void MarkDmaGrantClosed(PlatformDmaGrantId grantId, DmaGrantRecord record)
    {
        record.PlatformClosed = true;
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
}
