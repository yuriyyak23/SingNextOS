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

// An opaque parent-lifetime reservation, never an authorization or closure receipt.
internal sealed class CxlDeviceReservation(PlatformDeviceLease lease)
{
    internal PlatformDeviceLease Lease { get; } = lease;
}

public sealed partial class PlatformAuthorityBridge
{
    private sealed class DeviceLeaseRecord(
        PlatformDeviceLease lease,
        PlatformProviderDeviceLease providerLease,
        CapabilityId authorityCapabilityId,
        PlatformProviderIncarnation providerIncarnation,
        PlatformBackendEpoch backendEpoch)
    {
        public PlatformDeviceLease Lease { get; } = lease;
        public PlatformProviderDeviceLease ProviderLease { get; } = providerLease;
        public CapabilityId AuthorityCapabilityId { get; } = authorityCapabilityId;
        public PlatformProviderIncarnation ProviderIncarnation { get; } = providerIncarnation;
        public PlatformBackendEpoch BackendEpoch { get; } = backendEpoch;
        public bool LocalAuthorizationRevoked { get; set; }
        public bool PlatformClosed { get; set; }
        public bool FaultPinned { get; set; }
        public int PendingIrqBinds { get; set; }
        public CapabilityId? UnresolvedIrqCapabilityId { get; set; }
        public int PendingMmioBinds { get; set; }
        public CapabilityId? UnresolvedMmioCapabilityId { get; set; }
        public bool ClosureInFlight { get; set; }
        public HashSet<CxlDeviceReservation> CxlChildren { get; } = [];
    }

    private readonly Dictionary<PlatformDeviceLeaseId, DeviceLeaseRecord> _deviceLeases = [];
    private ulong _nextDeviceLeaseId = 1;

    internal KernelResult<PlatformDeviceLease> BindDevice(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformDeviceIdentity device,
        PlatformDeviceRights rights,
        Func<KernelResult> revalidateAuthorization,
        Func<PlatformDeviceLease, Func<KernelResult>, KernelResult> commitAuthorization)
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
        PlatformProviderIncarnation providerIncarnation;
        PlatformBackendEpoch backendEpoch;
        PlatformDeviceLeaseId localLeaseId;
        lock (_secureDomainLifecycleGate)
        {
            if (domainRecord.ParentRevokeMayHaveEffect)
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformBindingActive,
                    "Parent-domain revoke is already in flight.");
            if (domainRecord.PendingDeviceBinds != 0)
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformBindingActive,
                    "Device admission is already pending in this parent domain.");
            if (domainRecord.AuthorityState != DomainAuthorityState.Active ||
                domainRecord.DeviceBindMayHaveEffect)
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                    "Parent domain changed before device binding admission.");
            if (_nextDeviceLeaseId is 0 or ulong.MaxValue)
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.CapacityExhausted, "Local device lease identity capacity is exhausted.");
            localLeaseId = new(_nextDeviceLeaseId++);
            domainRecord.PendingDeviceBinds++;
            backendEpoch = BackendEpoch;
        }
        try
        {
            try { providerIncarnation = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                domainRecord.DeviceBindMayHaveEffect = true;
                QuarantineDomain(domainRecord);
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                    $"Device admission generation read failed; the parent remains quarantined: {exception.Message}");
            }
            var authorization = revalidateAuthorization();
            lock (_secureDomainLifecycleGate)
            {
                if (providerIncarnation.Value == 0 || BackendEpoch != backendEpoch || _backendEpochExhausted ||
                    !_domains.TryGetValue(binding.BindingId, out var exactDomain) || exactDomain != domainRecord ||
                    domainRecord.AuthorityState != DomainAuthorityState.Active || domainRecord.ParentRevokeMayHaveEffect)
                {
                    domainRecord.DeviceBindMayHaveEffect = true;
                    QuarantineDomain(domainRecord);
                    return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                        "Parent/device admission continuity changed during generation observation.");
                }
            }
            if (!authorization.IsSuccess)
                return KernelResult<PlatformDeviceLease>.Fail(authorization.Error, authorization.Message!);
            PlatformAuthorityResult<PlatformProviderDeviceLease> providerResult;
            try
            {
                providerResult = deviceProvider.BindDevice(
                    domainRecord.ProviderLease,
                    device,
                    rights);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // The callback can materialize a device lease before losing its receipt.
                // The domain is the only published owner that can retain that effect.
                domainRecord.DeviceBindMayHaveEffect = true;
                QuarantineDomain(domainRecord);
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                    $"Device binding may have taken effect without a receipt: {exception.Message}");
            }
            var generationStable = false;
            try
            {
                generationStable = CurrentProviderIncarnation() == providerIncarnation &&
                    BackendEpoch == backendEpoch && !_backendEpochExhausted &&
                    _domains.TryGetValue(binding.BindingId, out var currentDomain) && currentDomain == domainRecord;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Losing the generation read after the callback is also ambiguous.
            }
            if (!generationStable && !providerResult.IsSuccess)
            {
                domainRecord.DeviceBindMayHaveEffect = true;
                QuarantineDomain(domainRecord);
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                    "Provider generation changed during device binding; the domain remains quarantined.");
            }
            if (!providerResult.IsSuccess)
            {
                if (providerResult.Status != PlatformAuthorityStatus.NotAccepted)
                {
                    domainRecord.DeviceBindMayHaveEffect = true;
                    QuarantineDomain(domainRecord);
                }

                return FromProviderFailure<PlatformDeviceLease>(
                    providerResult.Status,
                    providerResult.Message);
            }

            var providerLease = providerResult.Value!;
            var lease = new PlatformDeviceLease(localLeaseId, new(1), binding, device, rights);
            var leaseRecord = new DeviceLeaseRecord(lease, providerLease, authorityCapabilityId,
                providerIncarnation, backendEpoch) { FaultPinned = !generationStable };
            authorization = revalidateAuthorization();
            var providerValidation = PlatformDeviceLeaseContract.ValidateLease(
                domainRecord.ProviderLease, device, rights, providerLease);
            if (!providerValidation.IsSuccess || !authorization.IsSuccess)
                return RejectLease(authorization.IsSuccess
                    ? KernelResult.Fail(KernelError.PlatformFaulted,
                        providerValidation.Message ?? "The provider returned malformed device authority.")
                    : authorization);

            var publication = commitAuthorization(lease, () =>
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
                        !_domains.TryGetValue(binding.BindingId, out var currentDomain) || currentDomain != domainRecord ||
                        domainRecord.AuthorityState != DomainAuthorityState.Active || domainRecord.ParentRevokeMayHaveEffect)
                        return KernelResult.Fail(KernelError.PlatformFaulted,
                            "Parent domain changed before device lease publication.");
                    _deviceLeases.Add(lease.LeaseId, leaseRecord);
                    return KernelResult.Ok();
                }
            });
            return publication.IsSuccess ? KernelResult<PlatformDeviceLease>.Ok(lease) : RejectLease(publication);

            KernelResult<PlatformDeviceLease> RejectLease(KernelResult rejection)
            {
                var cleanupProven = false;
                try
                {
                    var cleanup = deviceProvider.RevokeDevice(providerLease);
                    cleanupProven = cleanup.IsSuccess && providerLease.LeaseId.Value != 0 &&
                        providerLease.Generation.Value != 0 && CurrentProviderIncarnation() == providerIncarnation &&
                        BackendEpoch == backendEpoch;
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // A missing cleanup receipt leaves the provider lease ambiguous.
                }
                if (!cleanupProven)
                {
                    leaseRecord.FaultPinned = true;
                    leaseRecord.LocalAuthorizationRevoked = !authorization.IsSuccess ||
                        rejection.Error == KernelError.CapabilityRevoked;
                    lock (_secureDomainLifecycleGate) _deviceLeases.Add(lease.LeaseId, leaseRecord);
                    QuarantineDomain(domainRecord);
                }
                return KernelResult<PlatformDeviceLease>.Fail(rejection.Error, rejection.Message!);
            }
        }
        finally { lock (_secureDomainLifecycleGate) domainRecord.PendingDeviceBinds--; }
    }

    internal KernelResult RevokeDevice(
        PlatformDeviceLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        DeviceLeaseRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateDeviceLeaseIdentity(lease, expectedSubject);
            if (!identity.IsSuccess) return identity;
            record = _deviceLeases[lease.LeaseId];
            if (record.PendingIrqBinds != 0 || record.PendingMmioBinds != 0 || record.ClosureInFlight || record.CxlChildren.Count != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Device closure requires completed IRQ/MMIO admission and exact CXL child closure.");
            record.ClosureInFlight = true;
        }
        try { return RevokeDeviceCore(lease, expectedSubject); }
        finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
    }
    private KernelResult RevokeDeviceCore(
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

        if (record.FaultPinned)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device lease is fault-pinned and cannot prove provider closure.");

        if (_provider is not IPlatformDeviceLeaseProvider deviceProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the device lease no longer exposes device closure.");
        }

        if (!ValidateDeviceClosureGeneration(record))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device provider or local backend generation changed before closure; the lease remains pinned.");

        PlatformAuthorityResult providerResult;
        try { providerResult = deviceProvider.RevokeDevice(record.ProviderLease); }
        catch (Exception exception)
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"The device provider threw during closure; the lease remains pinned: {exception.Message}");
        }
        if (!ValidateDeviceClosureGeneration(record))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device provider or local backend generation changed during closure; the lease remains pinned.");
        if (!providerResult.IsSuccess)
        {
            // Revoked reports provider state, not exact containment of this
            // lease generation. Callback failure may follow an external effect.
            if (providerResult.Status != PlatformAuthorityStatus.NotAccepted)
            {
                record.FaultPinned = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Device closure lacks exact containment evidence; the lease and parent remain pinned.");
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

        if (record.FaultPinned)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The platform device lease is fault-pinned and cannot authorize effects.");

        return KernelResult.Ok();
    }

    internal KernelResult<CapabilityId> CxlDeviceSource(PlatformDeviceLease lease, PlatformDomainIdentity subject)
    {
        DeviceLeaseRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateDeviceLease(lease, subject);
            if (!valid.IsSuccess) return KernelResult<CapabilityId>.Fail(valid.Error, valid.Message!);
            record = _deviceLeases[lease.LeaseId];
        }
        // Provider generation reads must stay outside local admission locks.
        if (!ValidateDeviceClosureGeneration(record))
            return KernelResult<CapabilityId>.Fail(KernelError.PlatformFaulted, "CXL device source continuity is lost.");
        return KernelResult<CapabilityId>.Ok(record.AuthorityCapabilityId);
    }

    internal KernelResult CommitCxlDeviceAdmission(PlatformDeviceLease lease, PlatformDomainIdentity subject,
        Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateDeviceLease(lease, subject);
            if (!valid.IsSuccess) return valid;
            var record = _deviceLeases[lease.LeaseId];
            if (record.ClosureInFlight || BackendEpoch != record.BackendEpoch)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "CXL admission cannot overlap device closure or reset.");
            // Trusted local state only. No provider callbacks under this gate.
            return commit();
        }
    }

    internal KernelResult AddCxlDeviceReservation(CxlDeviceReservation reservation)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (!_deviceLeases.TryGetValue(reservation.Lease.LeaseId, out var record) || record.Lease != reservation.Lease)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL parent reservation requires the exact device lease.");
            return record.CxlChildren.Add(reservation) ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformBindingActive, "CXL parent reservation is already registered.");
        }
    }

    internal KernelResult ReleaseCxlDeviceReservation(CxlDeviceReservation reservation)
    {
        DeviceLeaseRecord record;
        lock (_secureDomainLifecycleGate)
        {
            if (!_deviceLeases.TryGetValue(reservation.Lease.LeaseId, out record!) || record.Lease != reservation.Lease ||
                !record.CxlChildren.Contains(reservation))
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL parent reservation is not exact or already released.");
        }
        if (!ValidateDeviceClosureGeneration(record))
            return KernelResult.Fail(KernelError.PlatformFaulted, "CXL parent continuity was lost before reservation release.");
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != record.BackendEpoch || !record.CxlChildren.Remove(reservation))
                return KernelResult.Fail(KernelError.PlatformFaulted, "CXL parent reservation remains pinned after continuity loss.");
            return KernelResult.Ok();
        }
    }

    internal IReadOnlyList<PlatformDeviceLease> BeginDeviceCapabilityRevocation(
        CapabilityId capabilityId, Func<CapabilityId, bool> dependsOnRevokedCapability)
    {
        lock (_secureDomainLifecycleGate)
        {
            var affected = _deviceLeases.Values
                .Where(record =>
                    !record.PlatformClosed &&
                    dependsOnRevokedCapability(record.AuthorityCapabilityId))
                .OrderBy(record => record.Lease.LeaseId.Value)
                .ToArray();

            foreach (var record in affected)
                record.LocalAuthorizationRevoked = true;

            return affected.Select(static record => record.Lease).ToArray();
        }
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

    private bool ValidateDeviceClosureGeneration(DeviceLeaseRecord record)
    {
        try
        {
            var current = CurrentProviderIncarnation();
            if (current.Value != 0 && current == record.ProviderIncarnation &&
                BackendEpoch == record.BackendEpoch)
                return true;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            // Generation read failure cannot make a device lease reusable.
        }

        record.FaultPinned = true;
        return false;
    }
}
