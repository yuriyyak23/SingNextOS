using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDeviceLeaseProvider
{
    private sealed class DeviceLeaseRecord(
        PlatformProviderDeviceLease lease,
        NeutralDeviceLease hybridCpuLease)
    {
        public PlatformProviderDeviceLease Lease { get; } = lease;
        public NeutralDeviceLease HybridCpuLease { get; } = hybridCpuLease;
        public bool Revoked { get; set; }
    }

    private readonly Dictionary<PlatformProviderDeviceLeaseId, DeviceLeaseRecord> _deviceLeases = [];
    private ulong _nextProviderDeviceLeaseId = 1;

    public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
        PlatformProviderDomainLease domainLease,
        PlatformDeviceIdentity device,
        PlatformDeviceRights rights)
    {
        var domain = ValidateDomain(domainLease);
        if (!domain.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                domain.Status,
                domain.Message ?? "The provider domain lease is not live for device binding.");
        }

        var request = PlatformDeviceLeaseContract.ValidateRequest(device, rights);
        if (!request.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                request.Status,
                request.Message ?? "The platform device request is invalid.");
        }

        var domainRecord = _domains[domainLease.LeaseId];
        var neutralDevice = new NeutralDeviceIdentity(device.ResourceId);
        var neutralRights = ToNeutralDeviceRights(rights);
        var external = _runtime.BindDevice(
            domainRecord.HybridCpuLease,
            neutralDevice,
            neutralRights);
        if (!external.IsBound)
            return FromNeutralDeviceBindFailure(external);

        if (external.Lease.DomainLease != domainRecord.HybridCpuLease ||
            external.Lease.Device != neutralDevice ||
            external.Lease.Rights != neutralRights)
        {
            _ = _runtime.CloseDevice(external.Lease);
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned device authority that does not match the exact provider request.");
        }

        var lease = new PlatformProviderDeviceLease(
            new PlatformProviderDeviceLeaseId(NextNonZero(ref _nextProviderDeviceLeaseId)),
            new PlatformProviderLeaseGeneration(1),
            domainRecord.Lease,
            device,
            rights);
        _deviceLeases.Add(
            lease.LeaseId,
            new DeviceLeaseRecord(lease, external.Lease));
        return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
    }

    public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
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

        var external = _runtime.CloseDevice(record.HybridCpuLease);
        switch (external.Decision)
        {
            case NeutralDeviceCloseDecision.Closed:
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();

            case NeutralDeviceCloseDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Revoked,
                    external.Reason);

            case NeutralDeviceCloseDecision.Stale:
            case NeutralDeviceCloseDecision.NotFound:
            case NeutralDeviceCloseDecision.Faulted:
            default:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    internal bool HasActiveProviderDeviceLeases(PlatformProviderDomainLease domainLease) =>
        _deviceLeases.Values.Any(record =>
            !record.Revoked && record.Lease.DomainLease == domainLease);

    private static NeutralDeviceRights ToNeutralDeviceRights(PlatformDeviceRights rights)
    {
        var result = NeutralDeviceRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0)
            result |= NeutralDeviceRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0)
            result |= NeutralDeviceRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0)
            result |= NeutralDeviceRights.Configure;
        return result;
    }

    private static PlatformAuthorityResult<PlatformProviderDeviceLease> FromNeutralDeviceBindFailure(
        NeutralDeviceBindResult result)
    {
        var status = result.Decision switch
        {
            NeutralDeviceBindDecision.InvalidDevice => PlatformAuthorityStatus.Denied,
            NeutralDeviceBindDecision.InvalidRights => PlatformAuthorityStatus.Denied,
            NeutralDeviceBindDecision.AlreadyBound => PlatformAuthorityStatus.Denied,
            NeutralDeviceBindDecision.NotFound => PlatformAuthorityStatus.Faulted,
            NeutralDeviceBindDecision.Stale => PlatformAuthorityStatus.Faulted,
            NeutralDeviceBindDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralDeviceBindDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
            status,
            result.Reason);
    }
}
