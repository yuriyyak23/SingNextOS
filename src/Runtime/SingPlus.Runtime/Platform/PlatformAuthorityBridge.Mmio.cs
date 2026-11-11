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
    private sealed class MmioLeaseRecord
    {
        public MmioLeaseRecord(PlatformMmioLease lease,PlatformProviderMmioLease providerLease,CapabilityId authorityCapabilityId)
        { OwnerSelf = this; Lease = lease; ProviderLease = providerLease; AuthorityCapabilityId = authorityCapabilityId; }
        public MmioLeaseRecord OwnerSelf { get; }
        public PlatformMmioLease Lease { get; }
        private PlatformProviderMmioLease _providerLease;
        private bool _platformClosed;
        public ulong SourceRevision { get; private set; }
        public bool SourceExhausted { get; private set; }
        private void Advance() { if (SourceRevision == ulong.MaxValue) SourceExhausted = true; else SourceRevision++; }
        public PlatformProviderMmioLease ProviderLease { get => _providerLease; set { Advance(); _providerLease = value; } }
        public CapabilityId AuthorityCapabilityId { get; }
        public OriginalMmioMapInvocation? OriginalMap { get; init; }
        public OriginalMmioCloseInvocation? OriginalClose { get; set; }
        public List<OriginalMmio32Fact> Accesses { get; } = [];
        public ulong AccessesIssued { get; set; }
        public bool AccessInFlight { get; set; }
        public List<OriginalMmioCloseInvocation> CloseHistory { get; } = [];
        public bool CleanupOnly { get; init; }
        public bool OwnCloseUncertain { get; set; }
        public bool LocalAuthorizationRevoked { get; set; }
        public bool PlatformClosed { get => _platformClosed; set { Advance(); _platformClosed = value; } }
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
    private readonly Dictionary<PlatformMmioLeaseId,MmioLeaseRecord> _originalMmioOwners = [];
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
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        MmioBufferPayload? backing = null)
    {
        DeviceLeaseRecord device = null!;
        DomainRecord domain = null!;
        ulong localId = 0;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
                KernelResult valid = ValidateDeviceLease(deviceLease, expectedSubject);
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
        KernelResult admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(admitted.Error, admitted.Message!);
        try { return BindMmioCore(deviceLease, expectedSubject, authorityCapabilityId, region, range, access, device, domain, localId, authorize, publish,backing); }
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
                    .Any(record => !HasConsumedOriginalMmioClosure(record) && depends(record.AuthorityCapabilityId));
    }

    private KernelResult<PlatformMmioLease> BindMmioCore(
        PlatformDeviceLease deviceLease, PlatformDomainIdentity expectedSubject, CapabilityId authorityCapabilityId,
        PlatformMmioRegionIdentity region, PlatformMmioRange range, PlatformMmioAccess access,
        DeviceLeaseRecord deviceRecord, DomainRecord domainRecord, ulong localId, Func<Func<KernelResult>, KernelResult> authorize,
        Func<PlatformMmioLease, Func<KernelResult>, KernelResult> publish,MmioBufferPayload? backing)
    {
        KernelResult deviceValidation = ValidateDeviceLease(deviceLease, expectedSubject);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        PlatformAuthorityResult requestValidation = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformDenied,
                requestValidation.Message ?? "The platform MMIO lease request is invalid.");
        }

        PlatformDeviceRights requiredDeviceRights = PlatformDeviceRights.Configure;
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

        if (mmioProvider is IPlatformOriginalMmioProvider original)
            return BindOriginalMmio(deviceLease,expectedSubject,authorityCapabilityId,region,range,access,deviceRecord,domainRecord,localId,authorize,publish,original,backing);

        if(backing is not null)return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformUnsupported,"Register backing requires an original MMIO producer.");

        lock (_secureDomainLifecycleGate)
        {
        if (MmioDependencyRecordsLocked().Any(record =>
                !HasConsumedOriginalMmioClosure(record) &&
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
        KernelResult admission = authorize(() =>
        {
            lock (_secureDomainLifecycleGate)
            {
                KernelResult parent = ValidateDeviceLease(deviceLease, expectedSubject);
                if (!parent.IsSuccess) return parent;
                if (!TryGetOriginalDeviceRecord(deviceLease.LeaseId, out DeviceLeaseRecord? currentDevice) || !ReferenceEquals(currentDevice, deviceRecord) ||
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
                PlatformMmioLease retained = new PlatformMmioLease(new PlatformMmioLeaseId(localId),
                    new PlatformMmioLeaseGeneration(1), deviceLease, region, range, access);
                lock (_secureDomainLifecycleGate)
                {
                    MmioLeaseRecord retainedRecord = new MmioLeaseRecord(retained, providerResult.Value!, authorityCapabilityId)
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

        PlatformProviderMmioLease providerLease = providerResult.Value!;
        PlatformAuthorityResult providerValidation = PlatformMmioLeaseContract.ValidateLease(
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
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed MMIO authority.");
        }

        PlatformMmioLease lease = new PlatformMmioLease(
            new PlatformMmioLeaseId(localId),
            new PlatformMmioLeaseGeneration(1),
            deviceLease,
            region,
            range,
            access);
        MmioLeaseRecord record = new MmioLeaseRecord(lease, providerLease, authorityCapabilityId);
        KernelResult published = publish(lease, () =>
        {
            lock (_secureDomainLifecycleGate)
            {
                KernelResult parent = ValidateDeviceLease(deviceLease, expectedSubject);
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
            bool cleaned = false;
            try { cleaned = RevokeOriginalMmio(lease,expectedSubject,record).IsSuccess; }
            finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
            lock (_secureDomainLifecycleGate)
                if (!cleaned && (record.FaultPinned || deviceRecord.FaultPinned || deviceRecord.BackendEpoch != BackendEpoch || !record.ProviderClosureConfirmed))
                { record.FaultPinned = true; deviceRecord.FaultPinned = true; }
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
            KernelResult identity = ValidateMmioLeaseIdentity(lease, expectedSubject);
            if (!identity.IsSuccess) return identity;
            record = _mmioLeases[lease.LeaseId];
            if (record.PlatformClosed)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Exact MMIO lease is closed.");
            if (record.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Exact MMIO closure is ambiguous.");
            if (record.ClosureInFlight || record.AccessInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Exact MMIO closure is already in flight.");
            if (record.RevokeMayHaveEffect && !record.ProviderClosureConfirmed && _provider is not IPlatformOriginalMmioProvider)
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

    private KernelResult RevokeMmioCore(PlatformMmioLease lease,PlatformDomainIdentity expectedSubject,MmioLeaseRecord record) => RevokeOriginalMmio(lease,expectedSubject,record);

    private void TransferMmioMapObservationLocked(DeviceLeaseRecord device, DomainRecord parent, MmioLeaseRecord record)
    {
        _originalMmioOwners.TryAdd(record.Lease.LeaseId,record);
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
        KernelResult validation = ValidateMmioLeaseIdentity(lease, expectedSubject);
        if (!validation.IsSuccess) return validation;

        MmioLeaseRecord record = _mmioLeases[lease.LeaseId];
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

        if (record.CleanupOnly || record.OriginalClose is not null || record.OwnCloseUncertain || record.FaultPinned)
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO cleanup/pending/uncertain cannot authorize use.");

        return ValidateDeviceLease(lease.DeviceLease, expectedSubject);
    }

    internal IReadOnlyList<PlatformMmioLease> BeginMmioCapabilityRevocation(
        CapabilityId capabilityId, Func<CapabilityId, bool> dependsOnRevokedCapability)
    {
        lock (_secureDomainLifecycleGate)
        {
            MmioLeaseRecord[] affected = MmioDependencyRecordsLocked()
            .Where(record =>
                !HasConsumedOriginalMmioClosure(record) &&
                dependsOnRevokedCapability(record.AuthorityCapabilityId))
            .OrderBy(record => record.Lease.LeaseId.Value)
            .ToArray();

        foreach (MmioLeaseRecord? record in affected)
            record.LocalAuthorizationRevoked = true;

        return affected.Select(static record => record.Lease).ToArray();
        }
    }

    internal bool HasActiveMmioLeases(PlatformDeviceLease deviceLease)
    {
        lock (_secureDomainLifecycleGate) return MmioDependencyRecordsLocked().Any(record =>
            !HasConsumedOriginalMmioClosure(record) &&
            record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId);
    }

    internal IReadOnlyList<PlatformMmioLease> ActiveMmioLeasesForDevice(
        PlatformDeviceLease deviceLease)
    {
        lock (_secureDomainLifecycleGate) return MmioDependencyRecordsLocked()
            .Where(record =>
                !HasConsumedOriginalMmioClosure(record) &&
                record.Lease.DeviceLease.LeaseId == deviceLease.LeaseId)
            .OrderBy(record => record.Lease.LeaseId.Value)
            .Select(static record => record.Lease)
            .ToArray();
    }

    internal IReadOnlyList<PlatformMmioLease> ActiveMmioLeasesForProcess(PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
            return MmioDependencyRecordsLocked().Where(record => !HasConsumedOriginalMmioClosure(record) &&
                record.Lease.DeviceLease.DomainBinding.Subject == subject)
                .OrderBy(record => record.Lease.LeaseId.Value).Select(record => record.Lease).ToArray();
    }

    private KernelResult ValidateMmioLeaseIdentity(
        PlatformMmioLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_mmioLeases.TryGetValue(lease.LeaseId, out MmioLeaseRecord? record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform MMIO lease does not exist.");
        }

        if (!ReferenceEquals(record.OwnerSelf,record) || !_originalMmioOwners.TryGetValue(lease.LeaseId,out MmioLeaseRecord? owner) || !ReferenceEquals(owner,record))
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO owner absent/foreign.");

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
