using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformMmioLeaseId(ulong Value);
public readonly record struct PlatformMmioLeaseGeneration(ulong Value);

public readonly record struct PlatformMmioLease(
    PlatformMmioLeaseId LeaseId,
    PlatformMmioLeaseGeneration Generation,
    PlatformDeviceLease DeviceLease,
    PlatformMmioRegionIdentity Region,
    PlatformMmioRange Range,
    PlatformMmioAccess Access);

public sealed partial class PlatformAuthorityBridge
{
    private sealed class MmioLeaseRecord(
        PlatformMmioLease lease,
        PlatformProviderMmioLease providerLease,
        CapabilityId authorityCapabilityId)
    {
        public PlatformMmioLease Lease { get; } = lease;
        public PlatformProviderMmioLease ProviderLease { get; } = providerLease;
        public CapabilityId AuthorityCapabilityId { get; } = authorityCapabilityId;
        public bool LocalAuthorizationRevoked { get; set; }
        public bool PlatformClosed { get; set; }
    }

    private readonly Dictionary<PlatformMmioLeaseId, MmioLeaseRecord> _mmioLeases = [];
    private ulong _nextMmioLeaseId = 1;

    internal KernelResult<PlatformMmioLease> BindMmio(
        PlatformDeviceLease deviceLease,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformMmioRegionIdentity region,
        PlatformMmioRange range,
        PlatformMmioAccess access)
    {
        var deviceValidation = ValidateDeviceLease(deviceLease, expectedSubject);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        var requestValidation = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformDenied,
                requestValidation.Message ?? "The platform MMIO lease request is invalid.");
        }

        var requiredDeviceRights = PlatformDeviceRights.Configure;
        if ((access & PlatformMmioAccess.Read) != 0)
            requiredDeviceRights |= PlatformDeviceRights.Read;
        if ((access & PlatformMmioAccess.Write) != 0)
            requiredDeviceRights |= PlatformDeviceRights.Write;
        if ((deviceLease.Rights & requiredDeviceRights) != requiredDeviceRights)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.InsufficientRights,
                "The platform device lease does not carry Configure plus the requested MMIO access rights.");
        }

        if (_provider is not IPlatformMmioLeaseProvider mmioProvider)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformUnsupported,
                "The bound platform provider does not expose bounded semantic MMIO leases.");
        }

        if (_mmioLeases.Values.Any(record =>
                !record.PlatformClosed &&
                record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId &&
                string.Equals(record.Lease.Region.ResourceId, region.ResourceId, StringComparison.Ordinal)))
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformBindingActive,
                "The exact semantic MMIO region already has a live lease for this device lifetime.");
        }

        var deviceRecord = _deviceLeases[deviceLease.LeaseId];
        var providerResult = mmioProvider.MapMmio(
            deviceRecord.ProviderLease,
            region,
            range,
            access);
        if (!providerResult.IsSuccess)
        {
            return FromProviderFailure<PlatformMmioLease>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerLease = providerResult.Value!;
        var providerValidation = PlatformMmioLeaseContract.ValidateLease(
            deviceRecord.ProviderLease,
            region,
            range,
            access,
            providerLease);
        if (!providerValidation.IsSuccess)
        {
            _ = mmioProvider.RevokeMmio(providerLease);
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed MMIO authority.");
        }

        var lease = new PlatformMmioLease(
            new PlatformMmioLeaseId(_nextMmioLeaseId++),
            new PlatformMmioLeaseGeneration(1),
            deviceLease,
            region,
            range,
            access);
        _mmioLeases.Add(
            lease.LeaseId,
            new MmioLeaseRecord(lease, providerLease, authorityCapabilityId));
        return KernelResult<PlatformMmioLease>.Ok(lease);
    }

    internal KernelResult RevokeMmio(
        PlatformMmioLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateMmioLeaseIdentity(lease, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _mmioLeases[lease.LeaseId];
        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform MMIO lease has already been closed.");
        }

        if (_provider is not IPlatformMmioLeaseProvider mmioProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the MMIO lease no longer exposes MMIO closure.");
        }

        var providerResult = mmioProvider.RevokeMmio(record.ProviderLease);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status == PlatformAuthorityStatus.Revoked)
            {
                record.PlatformClosed = true;
                return KernelResult.Ok();
            }

            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        record.PlatformClosed = true;
        return KernelResult.Ok();
    }

    internal KernelResult ValidateMmioLease(
        PlatformMmioLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateMmioLeaseIdentity(lease, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _mmioLeases[lease.LeaseId];
        if (record.LocalAuthorizationRevoked)
        {
            return KernelResult.Fail(
                KernelError.CapabilityRevoked,
                "The local capability that authorized this MMIO lease has been revoked.");
        }

        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform MMIO lease has been closed.");
        }

        return ValidateDeviceLease(lease.DeviceLease, expectedSubject);
    }

    internal IReadOnlyList<PlatformMmioLease> BeginMmioCapabilityRevocation(
        CapabilityId capabilityId)
    {
        var affected = _mmioLeases.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.AuthorityCapabilityId == capabilityId)
            .OrderBy(record => record.Lease.LeaseId.Value)
            .ToArray();

        foreach (var record in affected)
            record.LocalAuthorizationRevoked = true;

        return affected.Select(static record => record.Lease).ToArray();
    }

    internal bool HasActiveMmioLeases(PlatformDeviceLease deviceLease) =>
        _mmioLeases.Values.Any(record =>
            !record.PlatformClosed &&
            record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId);

    internal IReadOnlyList<PlatformMmioLease> ActiveMmioLeasesForDevice(
        PlatformDeviceLease deviceLease) =>
        _mmioLeases.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId)
            .OrderBy(record => record.Lease.LeaseId.Value)
            .Select(static record => record.Lease)
            .ToArray();

    private KernelResult ValidateMmioLeaseIdentity(
        PlatformMmioLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_mmioLeases.TryGetValue(lease.LeaseId, out var record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform MMIO lease does not exist.");
        }

        if (record.Lease.Generation != lease.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform MMIO lease generation is stale.");
        }

        if (record.Lease != lease)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform MMIO lease identity is malformed.");
        }

        // Closure must remain possible after local device authorization is revoked.
        // Use structural device/domain identity here; ValidateMmioLease above adds
        // live-authorization checks for operations that would consume MMIO authority.
        return ValidateDeviceLeaseIdentity(lease.DeviceLease, expectedSubject);
    }
}
