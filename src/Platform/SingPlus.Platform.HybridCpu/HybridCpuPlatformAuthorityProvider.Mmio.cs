using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformMmioLeaseProvider
{
    private sealed class MmioLeaseRecord(
        PlatformProviderMmioLease lease,
        NeutralMmioLease hybridCpuLease)
    {
        public PlatformProviderMmioLease Lease { get; } = lease;
        public NeutralMmioLease HybridCpuLease { get; } = hybridCpuLease;
        public bool Revoked { get; set; }
    }

    private readonly Dictionary<PlatformProviderMmioLeaseId, MmioLeaseRecord> _mmioLeases = [];
    private ulong _nextProviderMmioLeaseId = 1;

    public PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(
        PlatformProviderDeviceLease deviceLease,
        PlatformMmioRegionIdentity region,
        PlatformMmioRange range,
        PlatformMmioAccess access)
    {
        var device = ValidateProviderDeviceLease(deviceLease);
        if (!device.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(
                device.Status,
                device.Message ?? "The provider device lease is not live for MMIO mapping.");
        }

        var request = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
        if (!request.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(
                request.Status,
                request.Message ?? "The platform MMIO request is invalid.");
        }

        var deviceRecord = _deviceLeases[deviceLease.LeaseId];
        var neutralRegion = new NeutralMmioRegionIdentity(region.ResourceId, region.ByteLength);
        var neutralRange = new NeutralMmioRange(range.Offset, range.Length);
        var neutralAccess = ToNeutralMmioAccess(access);
        var external = _runtime.MapMmio(
            deviceRecord.HybridCpuLease,
            neutralRegion,
            neutralRange,
            neutralAccess);
        if (!external.IsMapped)
            return FromNeutralMmioMapFailure(external);

        if (external.Lease.DeviceLease != deviceRecord.HybridCpuLease ||
            external.Lease.Region != neutralRegion ||
            external.Lease.Range != neutralRange ||
            external.Lease.Access != neutralAccess)
        {
            _ = _runtime.CloseMmio(external.Lease);
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned MMIO authority that does not match the exact provider request.");
        }

        var lease = new PlatformProviderMmioLease(
            new PlatformProviderMmioLeaseId(NextNonZero(ref _nextProviderMmioLeaseId)),
            new PlatformProviderLeaseGeneration(1),
            deviceRecord.Lease,
            region,
            range,
            access);
        _mmioLeases.Add(
            lease.LeaseId,
            new MmioLeaseRecord(lease, external.Lease));
        return PlatformAuthorityResult<PlatformProviderMmioLease>.Ok(lease);
    }

    public PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease)
    {
        if (!_mmioLeases.TryGetValue(lease.LeaseId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider MMIO lease does not exist.");
        }

        if (record.Lease.Generation != lease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider MMIO lease generation is stale.");
        }

        if (record.Lease != lease)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider MMIO lease identity is malformed.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider MMIO lease has already been revoked.");
        }

        var external = _runtime.CloseMmio(record.HybridCpuLease);
        switch (external.Decision)
        {
            case NeutralMmioCloseDecision.Closed:
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();

            case NeutralMmioCloseDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Revoked,
                    external.Reason);

            case NeutralMmioCloseDecision.Stale:
            case NeutralMmioCloseDecision.NotFound:
            case NeutralMmioCloseDecision.Faulted:
            default:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    internal bool HasActiveProviderMmioLeases(PlatformProviderDeviceLease deviceLease) =>
        _mmioLeases.Values.Any(record =>
            !record.Revoked && record.Lease.DeviceLease == deviceLease);

    private PlatformAuthorityResult ValidateProviderDeviceLease(PlatformProviderDeviceLease lease)
    {
        if (!_deviceLeases.TryGetValue(lease.LeaseId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease does not exist.");
        }

        if (record.Lease.Generation != lease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider device lease generation is stale.");
        }

        if (record.Lease != lease)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider device lease identity is malformed.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider device lease has already been revoked.");
        }

        return ValidateDomain(lease.DomainLease);
    }

    private static NeutralMmioAccess ToNeutralMmioAccess(PlatformMmioAccess access)
    {
        var result = NeutralMmioAccess.None;
        if ((access & PlatformMmioAccess.Read) != 0)
            result |= NeutralMmioAccess.Read;
        if ((access & PlatformMmioAccess.Write) != 0)
            result |= NeutralMmioAccess.Write;
        return result;
    }

    private static PlatformAuthorityResult<PlatformProviderMmioLease> FromNeutralMmioMapFailure(
        NeutralMmioMapResult result)
    {
        var status = result.Decision switch
        {
            NeutralMmioMapDecision.InvalidRegion => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.InvalidRange => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.InvalidAccess => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.InsufficientDeviceRights => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.AlreadyMapped => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.NotFound => PlatformAuthorityStatus.Faulted,
            NeutralMmioMapDecision.Stale => PlatformAuthorityStatus.Faulted,
            NeutralMmioMapDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralMmioMapDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(
            status,
            result.Reason);
    }
}
