using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDeviceLeaseId(ulong Value);
public readonly record struct PlatformDeviceLeaseGeneration(ulong Value);

public readonly record struct PlatformDeviceLease(
    PlatformDeviceLeaseId LeaseId,
    PlatformDeviceLeaseGeneration Generation,
    PlatformDomainBinding DomainBinding,
    PlatformDeviceIdentity Device,
    PlatformDeviceRights Rights);

public sealed partial class PlatformAuthorityBridge
{
    private sealed class DeviceLeaseRecord(
        PlatformDeviceLease lease,
        PlatformProviderDeviceLease providerLease,
        CapabilityId authorityCapabilityId)
    {
        public PlatformDeviceLease Lease { get; } = lease;
        public PlatformProviderDeviceLease ProviderLease { get; } = providerLease;
        public CapabilityId AuthorityCapabilityId { get; } = authorityCapabilityId;
        public bool LocalAuthorizationRevoked { get; set; }
        public bool PlatformClosed { get; set; }
    }

    private readonly Dictionary<PlatformDeviceLeaseId, DeviceLeaseRecord> _deviceLeases = [];
    private ulong _nextDeviceLeaseId = 1;

    internal KernelResult<PlatformDeviceLease> BindDevice(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformDeviceIdentity device,
        PlatformDeviceRights rights)
    {
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        var requestValidation = PlatformDeviceLeaseContract.ValidateRequest(device, rights);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                KernelError.PlatformDenied,
                requestValidation.Message ?? "The platform device lease request is invalid.");
        }

        if (_provider is not IPlatformDeviceLeaseProvider deviceProvider)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                KernelError.PlatformUnsupported,
                "The bound platform provider does not expose semantic device leases.");
        }

        if (_deviceLeases.Values.Any(record =>
                !record.PlatformClosed &&
                record.Lease.DomainBinding.BindingId == binding.BindingId &&
                record.Lease.Device == device))
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                KernelError.PlatformBindingActive,
                "The exact semantic device already has a live platform lease in this domain binding.");
        }

        var domainRecord = _domains[binding.BindingId];
        var providerResult = deviceProvider.BindDevice(
            domainRecord.ProviderLease,
            device,
            rights);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status is PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.Stale)
                QuarantineDomain(domainRecord);

            return FromProviderFailure<PlatformDeviceLease>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerLease = providerResult.Value!;
        var providerValidation = PlatformDeviceLeaseContract.ValidateLease(
            domainRecord.ProviderLease,
            device,
            rights,
            providerLease);
        if (!providerValidation.IsSuccess)
        {
            _ = deviceProvider.RevokeDevice(providerLease);
            return KernelResult<PlatformDeviceLease>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed device authority.");
        }

        var lease = new PlatformDeviceLease(
            new PlatformDeviceLeaseId(_nextDeviceLeaseId++),
            new PlatformDeviceLeaseGeneration(1),
            binding,
            device,
            rights);
        _deviceLeases.Add(
            lease.LeaseId,
            new DeviceLeaseRecord(lease, providerLease, authorityCapabilityId));
        return KernelResult<PlatformDeviceLease>.Ok(lease);
    }

    internal KernelResult RevokeDevice(
        PlatformDeviceLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDeviceLeaseIdentity(lease, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _deviceLeases[lease.LeaseId];
        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform device lease has already been closed.");
        }

        if (_provider is not IPlatformDeviceLeaseProvider deviceProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the device lease no longer exposes device closure.");
        }

        var providerResult = deviceProvider.RevokeDevice(record.ProviderLease);
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

    internal KernelResult ValidateDeviceLease(
        PlatformDeviceLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDeviceLeaseIdentity(lease, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _deviceLeases[lease.LeaseId];
        if (record.LocalAuthorizationRevoked)
        {
            return KernelResult.Fail(
                KernelError.CapabilityRevoked,
                "The local capability that authorized this platform device lease has been revoked.");
        }

        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform device lease has been closed.");
        }

        return KernelResult.Ok();
    }

    internal IReadOnlyList<PlatformDeviceLease> BeginDeviceCapabilityRevocation(
        CapabilityId capabilityId)
    {
        var affected = _deviceLeases.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.AuthorityCapabilityId == capabilityId)
            .OrderBy(record => record.Lease.LeaseId.Value)
            .ToArray();

        foreach (var record in affected)
            record.LocalAuthorizationRevoked = true;

        return affected.Select(static record => record.Lease).ToArray();
    }

    internal bool HasActiveDeviceLeases(PlatformDomainBinding binding) =>
        _deviceLeases.Values.Any(record =>
            !record.PlatformClosed &&
            record.Lease.DomainBinding.BindingId == binding.BindingId);

    internal IReadOnlyList<PlatformDeviceLease> ActiveDeviceLeasesForBinding(
        PlatformDomainBinding binding) =>
        _deviceLeases.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.Lease.DomainBinding.BindingId == binding.BindingId)
            .OrderBy(record => record.Lease.LeaseId.Value)
            .Select(static record => record.Lease)
            .ToArray();

    private KernelResult ValidateDeviceLeaseIdentity(
        PlatformDeviceLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_deviceLeases.TryGetValue(lease.LeaseId, out var record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform device lease does not exist.");
        }

        if (record.Lease.Generation != lease.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform device lease generation is stale.");
        }

        if (record.Lease != lease)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform device lease identity is malformed.");
        }

        return ValidateDomain(lease.DomainBinding, expectedSubject);
    }
}
