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
        public bool FaultPinned { get; set; }
        public bool ClosureInFlight { get; set; }
    }

    private readonly Dictionary<PlatformMmioLeaseId, MmioLeaseRecord> _mmioLeases = [];
    private ulong _nextMmioLeaseId = 1;

    internal KernelResult<PlatformMmioLease> BindMmio(
        PlatformDeviceLease deviceLease,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformMmioRegionIdentity region,
        PlatformMmioRange range,
        PlatformMmioAccess access,
        Func<Func<KernelResult>, KernelResult> authorize,
        Func<PlatformMmioLease, Func<KernelResult>, KernelResult> publish)
    {
        DeviceLeaseRecord device;
        ulong localId;
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateDeviceLease(deviceLease, expectedSubject);
            if (!valid.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(valid.Error, valid.Message!);
            device = _deviceLeases[deviceLease.LeaseId];
            if (device.ClosureInFlight || device.PendingMmioBinds != 0)
                return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformBindingActive, "MMIO parent closure/admission is in flight.");
            if (_nextMmioLeaseId is 0 or ulong.MaxValue)
                return KernelResult<PlatformMmioLease>.Fail(KernelError.CapacityExhausted, "MMIO identity space is exhausted.");
            device.PendingMmioBinds++;
            localId = _nextMmioLeaseId++;
        }
        try { return BindMmioCore(deviceLease, expectedSubject, authorityCapabilityId, region, range, access, device, localId, authorize, publish); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) device.FaultPinned = true;
            return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted, $"MMIO admission lost continuity: {exception.Message}");
        }
        finally { lock (_secureDomainLifecycleGate) device.PendingMmioBinds--; }
    }

    internal bool HasUnresolvedMmioEffect(Func<CapabilityId, bool> depends)
    {
        lock (_secureDomainLifecycleGate)
            return _deviceLeases.Values.Any(record => record.UnresolvedMmioCapabilityId is { } id && depends(id));
    }

    private KernelResult<PlatformMmioLease> BindMmioCore(
        PlatformDeviceLease deviceLease, PlatformDomainIdentity expectedSubject, CapabilityId authorityCapabilityId,
        PlatformMmioRegionIdentity region, PlatformMmioRange range, PlatformMmioAccess access,
        DeviceLeaseRecord deviceRecord, ulong localId, Func<Func<KernelResult>, KernelResult> authorize,
        Func<PlatformMmioLease, Func<KernelResult>, KernelResult> publish)
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

        lock (_secureDomainLifecycleGate)
        {
        if (_mmioLeases.Values.Any(record =>
                !record.PlatformClosed &&
                record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId &&
                string.Equals(record.Lease.Region.ResourceId, region.ResourceId, StringComparison.Ordinal)))
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformBindingActive,
                "The exact semantic MMIO region already has a live lease for this device lifetime.");
        }
        }

        if (!ValidateDeviceClosureGeneration(deviceRecord))
            return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted, "MMIO parent generation continuity was lost.");
        var admission = authorize(() =>
        {
            lock (_secureDomainLifecycleGate)
            {
                var parent = ValidateDeviceLease(deviceLease, expectedSubject);
                if (!parent.IsSuccess) return parent;
                deviceRecord.UnresolvedMmioCapabilityId = authorityCapabilityId;
                return KernelResult.Ok();
            }
        });
        if (!admission.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(admission.Error, admission.Message!);
        PlatformAuthorityResult<PlatformProviderMmioLease> providerResult;
        try { providerResult = mmioProvider.MapMmio(deviceRecord.ProviderLease, region, range, access); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            deviceRecord.FaultPinned = true;
            return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted,
                $"MMIO mapping may have taken effect without a lease: {exception.Message}");
        }
        if (!ValidateDeviceClosureGeneration(deviceRecord))
        {
            if (providerResult.IsSuccess && PlatformMmioLeaseContract.ValidateLease(
                deviceRecord.ProviderLease, region, range, access, providerResult.Value!).IsSuccess)
            {
                var retained = new PlatformMmioLease(new PlatformMmioLeaseId(localId),
                    new PlatformMmioLeaseGeneration(1), deviceLease, region, range, access);
                lock (_secureDomainLifecycleGate)
                {
                    _mmioLeases.Add(retained.LeaseId,
                        new MmioLeaseRecord(retained, providerResult.Value!, authorityCapabilityId)
                        { LocalAuthorizationRevoked = true, FaultPinned = true });
                    deviceRecord.UnresolvedMmioCapabilityId = null;
                }
            }
            return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted,
                "Device generation changed during MMIO mapping; the parent remains pinned.");
        }
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status != PlatformAuthorityStatus.NotAccepted)
                deviceRecord.FaultPinned = true;
            else { lock (_secureDomainLifecycleGate) deviceRecord.UnresolvedMmioCapabilityId = null; }
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
            var cleanupProven = false;
            try
            {
                var cleanup = mmioProvider.RevokeMmio(providerLease);
                cleanupProven = cleanup.IsSuccess && providerLease.LeaseId.Value != 0 &&
                    providerLease.Generation.Value != 0 &&
                    ValidateDeviceClosureGeneration(deviceRecord);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // A missing exact cleanup receipt leaves the parent device pinned.
            }
            if (!cleanupProven) deviceRecord.FaultPinned = true;
            else { lock (_secureDomainLifecycleGate) deviceRecord.UnresolvedMmioCapabilityId = null; }
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed MMIO authority.");
        }

        var lease = new PlatformMmioLease(
            new PlatformMmioLeaseId(localId),
            new PlatformMmioLeaseGeneration(1),
            deviceLease,
            region,
            range,
            access);
        var record = new MmioLeaseRecord(lease, providerLease, authorityCapabilityId);
        var published = publish(lease, () =>
        {
            lock (_secureDomainLifecycleGate)
            {
                var parent = ValidateDeviceLease(deviceLease, expectedSubject);
                if (!parent.IsSuccess) return parent;
                _mmioLeases.Add(lease.LeaseId, record);
                deviceRecord.UnresolvedMmioCapabilityId = null;
                return KernelResult.Ok();
            }
        });
        if (!published.IsSuccess)
        {
            var cleaned = false;
            try { cleaned = mmioProvider.RevokeMmio(providerLease).IsSuccess && ValidateDeviceClosureGeneration(deviceRecord); }
            catch (Exception exception) when (exception is not StackOverflowException) { }
            lock (_secureDomainLifecycleGate)
            {
                if (!cleaned)
                {
                    record.LocalAuthorizationRevoked = true;
                    record.FaultPinned = true;
                    deviceRecord.FaultPinned = true;
                    _mmioLeases.Add(lease.LeaseId, record);
                }
                deviceRecord.UnresolvedMmioCapabilityId = null;
            }
            return KernelResult<PlatformMmioLease>.Fail(published.Error, published.Message!);
        }
        return KernelResult<PlatformMmioLease>.Ok(lease);
    }

    internal KernelResult RevokeMmio(
        PlatformMmioLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        MmioLeaseRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateMmioLeaseIdentity(lease, expectedSubject);
            if (!identity.IsSuccess) return identity;
            record = _mmioLeases[lease.LeaseId];
            if (record.PlatformClosed)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Exact MMIO lease is closed.");
            if (record.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Exact MMIO closure is ambiguous.");
            if (record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Exact MMIO closure is already in flight.");
            record.LocalAuthorizationRevoked = true;
            record.ClosureInFlight = true;
        }
        try { return RevokeMmioCore(lease, expectedSubject, record); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"MMIO closure lost continuity; exact lease remains pinned: {exception.Message}");
        }
        finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
    }

    private KernelResult RevokeMmioCore(
        PlatformMmioLease lease, PlatformDomainIdentity expectedSubject, MmioLeaseRecord record)
    {
        var validation = ValidateMmioLeaseIdentity(lease, expectedSubject);
        if (!validation.IsSuccess) return validation;

        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform MMIO lease has already been closed.");
        }

        if (record.FaultPinned)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The MMIO lease has ambiguous provider closure and remains pinned.");

        if (_provider is not IPlatformMmioLeaseProvider mmioProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the MMIO lease no longer exposes MMIO closure.");
        }

        var device = _deviceLeases[lease.DeviceLease.LeaseId];
        if (!ValidateDeviceClosureGeneration(device))
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device generation changed before MMIO closure.");
        }
        PlatformAuthorityResult providerResult;
        try { providerResult = mmioProvider.RevokeMmio(record.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"MMIO closure may have taken effect without a receipt: {exception.Message}");
        }
        if (!ValidateDeviceClosureGeneration(device))
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device generation changed during MMIO closure.");
        }
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status != PlatformAuthorityStatus.NotAccepted)
            {
                record.FaultPinned = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "MMIO closure lacks exact containment evidence; the lease remains pinned.");
            }

            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        lock (_secureDomainLifecycleGate)
        {
            if (record.FaultPinned || !record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "MMIO closure continuity was lost before local publication.");
            record.PlatformClosed = true;
        }
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

        if (record.FaultPinned)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The MMIO lease is fault-pinned after ambiguous closure.");

        return ValidateDeviceLease(lease.DeviceLease, expectedSubject);
    }

    internal IReadOnlyList<PlatformMmioLease> BeginMmioCapabilityRevocation(
        CapabilityId capabilityId, Func<CapabilityId, bool> dependsOnRevokedCapability)
    {
        lock (_secureDomainLifecycleGate)
        {
        var affected = _mmioLeases.Values
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

    internal bool HasActiveMmioLeases(PlatformDeviceLease deviceLease)
    {
        lock (_secureDomainLifecycleGate) return _mmioLeases.Values.Any(record =>
            !record.PlatformClosed &&
            record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId);
    }

    internal IReadOnlyList<PlatformMmioLease> ActiveMmioLeasesForDevice(
        PlatformDeviceLease deviceLease)
    {
        lock (_secureDomainLifecycleGate) return _mmioLeases.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId)
            .OrderBy(record => record.Lease.LeaseId.Value)
            .Select(static record => record.Lease)
            .ToArray();
    }

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
