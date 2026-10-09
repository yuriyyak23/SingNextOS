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
        public bool ProviderClosureConfirmed { get; set; } // Known exact reply, not local release.
        public bool FaultPinned { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool RevokeMayHaveEffect { get; set; }
        public PlatformAuthorityResult? RevokeObservation { get; set; }
        public DomainRecord? RevokeParent { get; set; } // Original cleanup source, never authority.
        public DomainRecord? MapParent { get; set; }
        public DeviceLeaseRecord? MapDeviceSource { get; set; } // Exact original cleanup source, not authority.
        public PlatformAuthorityResult<PlatformProviderMmioLease>? MapObservation { get; set; }
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
        Func<PlatformMmioLease, Func<KernelResult>, KernelResult> publish,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null)
    {
        DeviceLeaseRecord device = null!;
        DomainRecord domain = null!;
        ulong localId = 0;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateDeviceLease(deviceLease, expectedSubject);
            if (!valid.IsSuccess) return valid;
            device = _deviceLeases[deviceLease.LeaseId];
            domain = _domains[deviceLease.DomainBinding.BindingId];
            if (device.ClosureInFlight || device.PendingMmioBinds != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "MMIO parent closure/admission is in flight.");
            if (_nextMmioLeaseId is 0 or ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "MMIO identity space is exhausted.");
            device.PendingMmioBinds++;
            localId = _nextMmioLeaseId++;
            return KernelResult.Ok();
        }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(admitted.Error, admitted.Message!);
        try { return BindMmioCore(deviceLease, expectedSubject, authorityCapabilityId, region, range, access, device, domain, localId, authorize, publish); }
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
            return DeviceDependencyRecordsLocked().Any(record => record.UnresolvedMmioCapabilityId is { } id && depends(id)) ||
                _domains.Values.Concat(DeviceDependencyRecordsLocked().Select(device => device.BindParentSource)
                        .OfType<DomainRecord>()).SelectMany(domain => domain.MmioSources).OfType<MmioLeaseRecord>()
                    .Any(record => !record.PlatformClosed && depends(record.AuthorityCapabilityId));
    }

    private KernelResult<PlatformMmioLease> BindMmioCore(
        PlatformDeviceLease deviceLease, PlatformDomainIdentity expectedSubject, CapabilityId authorityCapabilityId,
        PlatformMmioRegionIdentity region, PlatformMmioRange range, PlatformMmioAccess access,
        DeviceLeaseRecord deviceRecord, DomainRecord domainRecord, ulong localId, Func<Func<KernelResult>, KernelResult> authorize,
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
                if (!TryGetOriginalDeviceRecord(deviceLease.LeaseId, out var currentDevice) || !ReferenceEquals(currentDevice, deviceRecord) ||
                    !HasExactChildCleanupDomainLocked(deviceRecord, domainRecord))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original MMIO map source changed before effect intent.");
                domainRecord.MmioSources.Add(deviceRecord);
                deviceRecord.UnresolvedMmioCapabilityId = authorityCapabilityId;
                deviceRecord.MmioMapParent = domainRecord;
                deviceRecord.MmioMapRegion = region;
                deviceRecord.MmioMapRange = range;
                deviceRecord.MmioMapAccess = access;
                deviceRecord.MmioMapMayHaveEffect = true;
                return KernelResult.Ok();
            }
        });
        if (!admission.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(admission.Error, admission.Message!);
        PlatformAuthorityResult<PlatformProviderMmioLease> providerResult;
        try { providerResult = mmioProvider.MapMmio(deviceRecord.ProviderLease, region, range, access); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PinAmbiguousDeviceClosure(deviceRecord, domainRecord);
            return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted,
                $"MMIO mapping may have taken effect without a lease: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate) deviceRecord.MmioMapObservation = providerResult;
        if (!ValidateDeviceClosureGeneration(deviceRecord))
        {
            if (providerResult.IsSuccess && PlatformMmioLeaseContract.ValidateLease(
                deviceRecord.ProviderLease, region, range, access, providerResult.Value!).IsSuccess)
            {
                var retained = new PlatformMmioLease(new PlatformMmioLeaseId(localId),
                    new PlatformMmioLeaseGeneration(1), deviceLease, region, range, access);
                lock (_secureDomainLifecycleGate)
                {
                    var retainedRecord = new MmioLeaseRecord(retained, providerResult.Value!, authorityCapabilityId)
                    { LocalAuthorizationRevoked = true, FaultPinned = true };
                    _mmioLeases.Add(retained.LeaseId, retainedRecord);
                    TransferMmioMapObservationLocked(deviceRecord, domainRecord, retainedRecord);
                }
            }
            PinAmbiguousDeviceClosure(deviceRecord, domainRecord);
            return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted,
                "Device generation changed during MMIO mapping; the parent remains pinned.");
        }
        if (!providerResult.IsSuccess)
        {
            PinAmbiguousDeviceClosure(deviceRecord, domainRecord);
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
            // Cleanup of the malformed returned tuple cannot close the original effect.
            lock (_secureDomainLifecycleGate)
            {
                deviceRecord.FaultPinned = true;
                deviceRecord.MalformedMmioReceipt = providerLease;
            }
            try
            {
                var sourceCurrent = ValidateDeviceClosureGeneration(deviceRecord);
                bool cleanupAdmitted;
                lock (_secureDomainLifecycleGate)
                {
                    cleanupAdmitted = sourceCurrent && !_backendEpochExhausted && BackendEpoch == deviceRecord.BackendEpoch &&
                        TryGetOriginalDeviceRecord(deviceLease.LeaseId, out var exactDevice) && ReferenceEquals(exactDevice, deviceRecord) &&
                        TryGetCanonicalRootRecord(deviceLease.DomainBinding.BindingId, out var exactDomain) && ReferenceEquals(exactDomain, domainRecord) &&
                        !deviceRecord.PlatformClosed && !deviceRecord.ClosureInFlight &&
                        domainRecord.AuthorityState == DomainAuthorityState.Active && !domainRecord.ParentRevokeMayHaveEffect &&
                        deviceRecord.UnresolvedMmioCapabilityId == authorityCapabilityId && !deviceRecord.MalformedMmioCleanupMayHaveEffect;
                    if (cleanupAdmitted) deviceRecord.MalformedMmioCleanupMayHaveEffect = true;
                }
                if (cleanupAdmitted)
                {
                    var cleanup = mmioProvider.RevokeMmio(providerLease);
                    lock (_secureDomainLifecycleGate) deviceRecord.MalformedMmioCleanupObservation = cleanup;
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // A missing exact cleanup receipt leaves the parent device pinned.
            }
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
                TransferMmioMapObservationLocked(deviceRecord, domainRecord, record);
                return KernelResult.Ok();
            }
        });
        if (!published.IsSuccess)
        {
            lock (_secureDomainLifecycleGate)
            {
                record.LocalAuthorizationRevoked = true;
                record.ClosureInFlight = true;
                _mmioLeases.Add(lease.LeaseId, record);
                TransferMmioMapObservationLocked(deviceRecord, domainRecord, record);
            }
            var cleaned = false;
            try
            {
                var sourceCurrent = ValidateDeviceClosureGeneration(deviceRecord);
                bool cleanupAdmitted;
                lock (_secureDomainLifecycleGate)
                {
                    cleanupAdmitted = sourceCurrent && !_backendEpochExhausted && BackendEpoch == deviceRecord.BackendEpoch &&
                        _mmioLeases.TryGetValue(lease.LeaseId, out var exactChild) && ReferenceEquals(exactChild, record) &&
                        TryGetOriginalDeviceRecord(deviceLease.LeaseId, out var exactDevice) && ReferenceEquals(exactDevice, deviceRecord) &&
                        TryGetCanonicalRootRecord(deviceLease.DomainBinding.BindingId, out var exactDomain) && ReferenceEquals(exactDomain, domainRecord) &&
                        !deviceRecord.FaultPinned && !deviceRecord.PlatformClosed && !deviceRecord.ClosureInFlight &&
                        domainRecord.AuthorityState == DomainAuthorityState.Active && !domainRecord.ParentRevokeMayHaveEffect &&
                        record.ClosureInFlight;
                    if (cleanupAdmitted)
                    {
                        record.RevokeParent = domainRecord;
                        record.RevokeMayHaveEffect = true;
                    }
                }
                if (cleanupAdmitted)
                {
                    var cleanup = mmioProvider.RevokeMmio(providerLease);
                    lock (_secureDomainLifecycleGate) record.RevokeObservation = cleanup;
                    if (cleanup.IsSuccess)
                    {
                        lock (_secureDomainLifecycleGate) record.ProviderClosureConfirmed = true;
                        cleaned = CompleteKnownMmioClosure(record, deviceRecord).IsSuccess;
                    }
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException) { }
            finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
            lock (_secureDomainLifecycleGate)
            {
                if (cleaned) _mmioLeases.Remove(lease.LeaseId);
                else if (record.FaultPinned || deviceRecord.FaultPinned || deviceRecord.BackendEpoch != BackendEpoch ||
                    !record.ProviderClosureConfirmed)
                {
                    record.FaultPinned = true;
                    deviceRecord.FaultPinned = true;
                }
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
            if (record.RevokeMayHaveEffect && !record.ProviderClosureConfirmed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original MMIO closure remains ambiguous; repeat refused.");
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
        DeviceLeaseRecord device;
        DomainRecord domain;
        bool knownClosure;
        lock (_secureDomainLifecycleGate)
        {
            var validation = ValidateMmioLeaseIdentity(lease, expectedSubject);
            if (!validation.IsSuccess) return validation;
            if (!_mmioLeases.TryGetValue(lease.LeaseId, out var current) || !ReferenceEquals(current, record) || !record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original MMIO cleanup owner changed.");
            if (record.PlatformClosed)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "The platform MMIO lease has already been closed.");
            if (record.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformFaulted, "The MMIO lease has ambiguous provider closure and remains pinned.");
            device = _deviceLeases[lease.DeviceLease.LeaseId];
            if (!ReferenceEquals(device, record.MapDeviceSource))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original MMIO device cleanup owner is absent or replaced.");
            domain = _domains[lease.DeviceLease.DomainBinding.BindingId];
            knownClosure = record.ProviderClosureConfirmed;
        }
        if (knownClosure) return CompleteKnownMmioClosure(record, device);
        if (_provider is not IPlatformMmioLeaseProvider mmioProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the MMIO lease no longer exposes MMIO closure.");
        }

        if (!ValidateDeviceClosureGeneration(device))
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device generation changed before MMIO closure.");
        }
        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateMmioLeaseIdentity(lease, expectedSubject);
            if (!identity.IsSuccess) return identity;
            if (!_mmioLeases.TryGetValue(lease.LeaseId, out var current) || !ReferenceEquals(current, record) ||
                !TryGetOriginalDeviceRecord(lease.DeviceLease.LeaseId, out var parent) || !ReferenceEquals(parent, device) ||
                !record.ClosureInFlight || record.FaultPinned || record.PlatformClosed || device.FaultPinned || device.PlatformClosed ||
                BackendEpoch != device.BackendEpoch || _backendEpochExhausted || !HasExactChildCleanupDomainLocked(device, domain) ||
                !ReferenceEquals(record.MapParent, domain) || !domain.MmioSources.Contains(record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "MMIO cleanup admission changed during source observation; effect refused.");
            record.RevokeParent = domain;
            record.RevokeMayHaveEffect = true;
        }
        PlatformAuthorityResult providerResult;
        try { providerResult = mmioProvider.RevokeMmio(record.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) { record.FaultPinned = true; device.FaultPinned = true; }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"MMIO closure may have taken effect without a receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate) record.RevokeObservation = providerResult;
        if (!providerResult.IsSuccess)
        {
            lock (_secureDomainLifecycleGate) { record.FaultPinned = true; device.FaultPinned = true; }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "MMIO closure lacks exact containment evidence; the original reply and lease remain pinned.");
        }

        lock (_secureDomainLifecycleGate) record.ProviderClosureConfirmed = true;
        return CompleteKnownMmioClosure(record, device);
    }

    private KernelResult CompleteKnownMmioClosure(MmioLeaseRecord record, DeviceLeaseRecord device)
    {
        PlatformProviderIncarnation current;
        try { current = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!_mmioLeases.TryGetValue(record.Lease.LeaseId, out var exactChild) || !ReferenceEquals(exactChild, record) ||
                    !TryGetOriginalDeviceRecord(record.Lease.DeviceLease.LeaseId, out var exactParent) || !ReferenceEquals(exactParent, device) ||
                    !HasExactChildCleanupDomainLocked(device, record.RevokeParent) ||
                    !ReferenceEquals(record.MapParent, record.RevokeParent) || record.MapParent is null || !record.MapParent.MmioSources.Contains(record))
                {
                    record.FaultPinned = true;
                    device.FaultPinned = true;
                }
                if (BackendEpoch != device.BackendEpoch || _backendEpochExhausted || device.FaultPinned)
                    record.FaultPinned = true;
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Known MMIO closure retained while source observation failed: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
            if (current.Value == 0 || current != device.ProviderIncarnation ||
                BackendEpoch != device.BackendEpoch || _backendEpochExhausted || device.PlatformClosed ||
                !_mmioLeases.TryGetValue(record.Lease.LeaseId, out var exactChild) || !ReferenceEquals(exactChild, record) ||
                !TryGetOriginalDeviceRecord(record.Lease.DeviceLease.LeaseId, out var exactDevice) ||
                !ReferenceEquals(exactDevice, device) || !HasExactChildCleanupDomainLocked(device, record.RevokeParent) ||
                !ReferenceEquals(record.MapParent, record.RevokeParent) || record.MapParent is null || !record.MapParent.MmioSources.Contains(record))
            {
                device.FaultPinned = true;
                record.FaultPinned = true;
            }
            if (!record.ProviderClosureConfirmed || record.FaultPinned || device.FaultPinned || !record.ClosureInFlight ||
                !_mmioLeases.TryGetValue(record.Lease.LeaseId, out var exact) || !ReferenceEquals(exact, record))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "MMIO closure continuity was lost before local publication.");
            record.PlatformClosed = true;
            record.MapParent!.MmioSources.Remove(record);
        }
        return KernelResult.Ok();
    }

    private static void TransferMmioMapObservationLocked(DeviceLeaseRecord device, DomainRecord parent, MmioLeaseRecord record)
    {
        record.MapObservation = device.MmioMapObservation;
        record.MapParent = parent;
        record.MapDeviceSource = device;
        parent.MmioSources.Add(record);
        parent.MmioSources.Remove(device);
        device.UnresolvedMmioCapabilityId = null;
        device.MmioMapMayHaveEffect = false;
        device.MmioMapObservation = null;
        device.MmioMapParent = null;
        device.MmioMapRegion = null;
        device.MmioMapRange = null;
        device.MmioMapAccess = null;
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

    internal IReadOnlyList<PlatformMmioLease> ActiveMmioLeasesForProcess(PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
            return _mmioLeases.Values.Where(record => !record.PlatformClosed &&
                record.Lease.DeviceLease.DomainBinding.Subject == subject)
                .OrderBy(record => record.Lease.LeaseId.Value).Select(record => record.Lease).ToArray();
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
