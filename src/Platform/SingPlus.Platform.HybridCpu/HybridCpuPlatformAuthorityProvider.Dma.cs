using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDmaGrantProvider
{
    private sealed class DmaGrantRecord(
        PlatformProviderDmaGrant grant,
        NeutralDmaGrant hybridCpuGrant)
    {
        public PlatformProviderDmaGrant Grant { get; } = grant;
        public NeutralDmaGrant HybridCpuGrant { get; } = hybridCpuGrant;
        public bool Revoked { get; set; }
    }

    private readonly Dictionary<PlatformProviderDmaGrantId, DmaGrantRecord> _dmaGrants = [];
    private ulong _nextProviderDmaGrantId = 1;

    public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
        PlatformDmaGrantRequest request)
    {
        var requestValidation = PlatformDmaGrantContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                requestValidation.Status,
                requestValidation.Message ?? "The DMA grant admission request is invalid.");
        }

        var deviceValidation = ValidateProviderDeviceLease(request.DeviceLease);
        if (!deviceValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                deviceValidation.Status,
                deviceValidation.Message ?? "The provider device lease is not live for DMA admission.");
        }

        var mappingValidation = ValidateProviderMapping(
            request.MappingLease,
            request.MappingSlice);
        if (!mappingValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                mappingValidation.Status,
                mappingValidation.Message ?? "The provider mapping is not live for DMA admission.");
        }

        if (_dmaGrants.Values.Any(record =>
                !record.Revoked &&
                record.Grant.MappingLease.MappingId == request.MappingLease.MappingId))
        {
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                PlatformAuthorityStatus.Denied,
                "The exact provider mapping already has a live DMA grant in this admission-only slice.");
        }

        var deviceRecord = _deviceLeases[request.DeviceLease.LeaseId];
        var mappingRecord = _providerMappings[request.MappingLease.MappingId];
        var neutralRange = new NeutralDmaRange(request.Range.Offset, request.Range.Length);
        var neutralDirection = ToNeutralDmaDirection(request.Direction);
        var external = _runtime.BindDmaGrant(
            deviceRecord.HybridCpuLease,
            mappingRecord.HybridCpuLease,
            neutralRange,
            neutralDirection);
        if (!external.IsGranted)
            return FromNeutralDmaGrantFailure(external);

        if (external.Grant.DeviceLease != deviceRecord.HybridCpuLease ||
            external.Grant.MappingLease != mappingRecord.HybridCpuLease ||
            external.Grant.Range != neutralRange ||
            external.Grant.Direction != neutralDirection)
        {
            _ = _runtime.CloseDmaGrant(external.Grant);
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned DMA authority that does not match the exact provider admission request.");
        }

        var grant = new PlatformProviderDmaGrant(
            new PlatformProviderDmaGrantId(NextNonZero(ref _nextProviderDmaGrantId)),
            new PlatformProviderLeaseGeneration(1),
            request.DeviceLease,
            request.MappingLease,
            request.Range,
            request.Direction);
        _dmaGrants.Add(grant.GrantId, new DmaGrantRecord(grant, external.Grant));
        return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
    }

    public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
    {
        if (!_dmaGrants.TryGetValue(grant.GrantId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider DMA grant does not exist.");
        }

        if (record.Grant.Generation != grant.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider DMA grant generation is stale.");
        }

        if (record.Grant != grant)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider DMA grant identity is malformed.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider DMA grant has already been revoked.");
        }

        var external = _runtime.CloseDmaGrant(record.HybridCpuGrant);
        switch (external.Decision)
        {
            case NeutralDmaGrantCloseDecision.Closed:
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();
            case NeutralDmaGrantCloseDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Revoked,
                    external.Reason);
            case NeutralDmaGrantCloseDecision.Stale:
            case NeutralDmaGrantCloseDecision.NotFound:
            case NeutralDmaGrantCloseDecision.Faulted:
            default:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    internal bool HasActiveProviderDmaGrants(PlatformProviderDeviceLease deviceLease) =>
        _dmaGrants.Values.Any(record =>
            !record.Revoked && record.Grant.DeviceLease == deviceLease);

    internal bool HasActiveProviderDmaGrants(PlatformProviderRegionMappingLease mappingLease) =>
        _dmaGrants.Values.Any(record =>
            !record.Revoked && record.Grant.MappingLease == mappingLease);

    private static NeutralDmaDirection ToNeutralDmaDirection(PlatformDmaDirection direction) =>
        direction switch
        {
            PlatformDmaDirection.DeviceReadsMemory => NeutralDmaDirection.DeviceReadsMemory,
            PlatformDmaDirection.DeviceWritesMemory => NeutralDmaDirection.DeviceWritesMemory,
            PlatformDmaDirection.Bidirectional => NeutralDmaDirection.Bidirectional,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

    private static PlatformAuthorityResult<PlatformProviderDmaGrant> FromNeutralDmaGrantFailure(
        NeutralDmaGrantResult result)
    {
        var status = result.Decision switch
        {
            NeutralDmaGrantDecision.InvalidRange => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.InvalidDirection => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.InsufficientDeviceRights => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.InsufficientMappingAccess => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.WrongDomain => PlatformAuthorityStatus.WrongDomain,
            NeutralDmaGrantDecision.AlreadyGranted => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralDmaGrantDecision.Stale => PlatformAuthorityStatus.Faulted,
            NeutralDmaGrantDecision.NotFound => PlatformAuthorityStatus.Faulted,
            NeutralDmaGrantDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(status, result.Reason);
    }
}
