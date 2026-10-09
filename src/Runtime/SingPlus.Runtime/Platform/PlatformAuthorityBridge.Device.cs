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
    internal object? OriginalDeviceSource { get; private set; }
    internal object? OriginalParentSource { get; private set; }
    // Original invocation/returned-object cleanup correlations, guarded by the CXL bridge gate.
    // They grant neither device permission nor original invocation closure.
    internal CxlFabricBindingRequest? OriginalFabricRequest { get; set; }
    internal PlatformAuthorityResult<CxlFabricBinding>? OriginalFabricOutcome { get; set; }
    internal PlatformAuthorityResult? ReturnedObjectCompensation { get; set; }
    internal bool OriginalFabricReceiptValidated { get; set; }
    internal object? OriginalFabricSource { get; private set; }
    internal bool CaptureFabricSource(object source)
    {
        if (OriginalFabricSource is not null) return false;
        OriginalFabricSource = source;
        return true;
    }
    internal bool CaptureSources(object device, object parent)
    {
        if (OriginalDeviceSource is not null) return false;
        OriginalDeviceSource = device; OriginalParentSource = parent;
        return true;
    }
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
        public PlatformAuthorityResult<PlatformProviderDeviceLease>? BindObservation { get; set; }
        public DomainRecord? BindParentSource { get; init; } // Original cleanup route, never permission.
        public bool LocalAuthorizationRevoked { get; set; }
        public bool PlatformClosed { get; set; }
        public bool ProviderClosureConfirmed { get; set; } // Exact reply, not local release.
        public bool RevokeMayHaveEffect { get; set; }
        public PlatformAuthorityResult? RevokeObservation { get; set; }
        public DomainRecord? RevokeParent { get; set; } // Original cleanup source, never authority.
        public bool FaultPinned { get; set; }
        public int PendingIrqBinds { get; set; }
        public CapabilityId? UnresolvedIrqCapabilityId { get; set; }
        public bool IrqBindMayHaveEffect { get; set; }
        public PlatformAuthorityResult<PlatformProviderIrqBinding>? IrqBindObservation { get; set; }
        public DomainRecord? IrqBindParent { get; set; }
        public PlatformInterruptSourceIdentity? IrqBindSource { get; set; }
        public KernelEventRegistry.IrqSourceReservation? IrqBindEndpointSource { get; set; }
        public PlatformProviderIrqBinding? MalformedIrqReceipt { get; set; }
        public bool MalformedIrqCleanupMayHaveEffect { get; set; }
        public PlatformAuthorityResult? MalformedIrqCleanupObservation { get; set; }
        public int PendingMmioBinds { get; set; }
        public int PendingVirtualIoBinds { get; set; }
        public List<UnpublishedVirtualIoBindReceipt> UnpublishedVirtualIoBinds { get; } = [];
        public CapabilityId? UnresolvedMmioCapabilityId { get; set; }
        public bool MmioMapMayHaveEffect { get; set; }
        public PlatformAuthorityResult<PlatformProviderMmioLease>? MmioMapObservation { get; set; }
        public DomainRecord? MmioMapParent { get; set; }
        public PlatformMmioRegionIdentity? MmioMapRegion { get; set; }
        public PlatformMmioRange? MmioMapRange { get; set; }
        public PlatformMmioAccess? MmioMapAccess { get; set; }
        public PlatformProviderMmioLease? MalformedMmioReceipt { get; set; }
        public bool MalformedMmioCleanupMayHaveEffect { get; set; }
        public PlatformAuthorityResult? MalformedMmioCleanupObservation { get; set; }
        public bool ClosureInFlight { get; set; }
        public int PendingChildDrains { get; set; } // Admission only, not containment.
        public HashSet<CxlDeviceReservation> CxlChildren { get; } = [];
    }

    private readonly Dictionary<PlatformDeviceLeaseId, DeviceLeaseRecord> _deviceLeases = [];
    private readonly Dictionary<PlatformDeviceLeaseId, DeviceLeaseRecord> _originalDeviceOwners = [];
    private ulong _nextDeviceLeaseId = 1;

    private bool TryGetOriginalDeviceRecord(PlatformDeviceLeaseId id, out DeviceLeaseRecord record) =>
        _deviceLeases.TryGetValue(id, out record!) &&
        _originalDeviceOwners.TryGetValue(id, out var original) && ReferenceEquals(original, record);

    private IEnumerable<DeviceLeaseRecord> DeviceDependencyRecordsLocked() =>
        _originalDeviceOwners.Values.Concat(_deviceLeases.Values).Distinct<DeviceLeaseRecord>(ReferenceEqualityComparer.Instance);

    internal KernelResult<PlatformDeviceLease> BindDevice(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformDeviceIdentity device,
        PlatformDeviceRights rights,
        Func<KernelResult> revalidateAuthorization,
        Func<PlatformDeviceLease, Func<KernelResult>, KernelResult> commitAuthorization,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null)
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


        var domainRecord = _domains[binding.BindingId];
        PlatformProviderIncarnation providerIncarnation;
        PlatformBackendEpoch backendEpoch = default;
        PlatformDeviceLeaseId localLeaseId = default;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            if (DeviceDependencyRecordsLocked().Any(record => !record.PlatformClosed &&
                    record.Lease.DomainBinding.BindingId == binding.BindingId && record.Lease.Device == device))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "The exact semantic device already has a live platform lease in this domain binding.");
            if (domainRecord.ParentRevokeMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Parent-domain revoke is already in flight.");
            if (domainRecord.PendingDeviceBinds != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Device admission is already pending in this parent domain.");
            if (domainRecord.AuthorityState != DomainAuthorityState.Active ||
                domainRecord.DeviceBindMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Parent domain changed before device binding admission.");
            if (_nextDeviceLeaseId is 0 or ulong.MaxValue || _deviceLeases.Count == int.MaxValue || _originalDeviceOwners.Count == int.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Local device lease identity capacity is exhausted.");
            _deviceLeases.EnsureCapacity(_deviceLeases.Count + 1);
            _originalDeviceOwners.EnsureCapacity(_originalDeviceOwners.Count + 1);
            localLeaseId = new(_nextDeviceLeaseId++);
            domainRecord.PendingDeviceBinds++;
            backendEpoch = BackendEpoch;
            return KernelResult.Ok();
        }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformDeviceLease>.Fail(admitted.Error, admitted.Message!);
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
            if (!authorization.IsSuccess)
                return KernelResult<PlatformDeviceLease>.Fail(authorization.Error, authorization.Message!);
            lock (_secureDomainLifecycleGate)
            {
                if (providerIncarnation.Value == 0 || BackendEpoch != backendEpoch || _backendEpochExhausted ||
                    !TryGetCanonicalRootRecord(binding.BindingId, out var exactDomain) || exactDomain != domainRecord ||
                    domainRecord.AuthorityState != DomainAuthorityState.Active || domainRecord.ParentRevokeMayHaveEffect)
                {
                    domainRecord.DeviceBindMayHaveEffect = true;
                    QuarantineDomain(domainRecord);
                    return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                        "Parent/device admission continuity changed during generation observation.");
                }
                domainRecord.DeviceBindCapabilityId = authorityCapabilityId;
                domainRecord.DeviceBindDevice = device;
                domainRecord.DeviceBindRights = rights;
                domainRecord.DeviceBindIncarnation = providerIncarnation;
                domainRecord.DeviceBindEpoch = backendEpoch;
                domainRecord.DeviceBindSource = domainRecord;
                domainRecord.DeviceBindMayHaveEffect = true;
            }
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
                lock (_secureDomainLifecycleGate)
                {
                    domainRecord.DeviceBindMayHaveEffect = true;
                    QuarantineDomain(domainRecord);
                }
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                    $"Device binding may have taken effect without a receipt: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate) domainRecord.DeviceBindObservation = providerResult;
            var generationStable = false;
            try
            {
                generationStable = CurrentProviderIncarnation() == providerIncarnation &&
                    BackendEpoch == backendEpoch && !_backendEpochExhausted &&
                    TryGetCanonicalRootRecord(binding.BindingId, out var currentDomain) && currentDomain == domainRecord;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Losing the generation read after the callback is also ambiguous.
            }
            if (!generationStable && !providerResult.IsSuccess)
            {
                lock (_secureDomainLifecycleGate)
                {
                    domainRecord.DeviceBindMayHaveEffect = true;
                    QuarantineDomain(domainRecord);
                }
                return KernelResult<PlatformDeviceLease>.Fail(KernelError.PlatformFaulted,
                    "Provider generation changed during device binding; the domain remains quarantined.");
            }
            if (!providerResult.IsSuccess)
            {
                lock (_secureDomainLifecycleGate)
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
                providerIncarnation, backendEpoch) { FaultPinned = !generationStable, BindParentSource = domainRecord };
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
                        !TryGetCanonicalRootRecord(binding.BindingId, out var currentDomain) || currentDomain != domainRecord ||
                        domainRecord.AuthorityState != DomainAuthorityState.Active || domainRecord.ParentRevokeMayHaveEffect)
                        return KernelResult.Fail(KernelError.PlatformFaulted,
                            "Parent domain changed before device lease publication.");
                    if (!_originalDeviceOwners.TryAdd(lease.LeaseId, leaseRecord))
                        return KernelResult.Fail(KernelError.PlatformFaulted,
                            "Original device owner index changed before publication; known original outcome remains pinned.");
                    _deviceLeases.Add(lease.LeaseId, leaseRecord);
                    TransferDeviceBindObservationLocked(domainRecord, leaseRecord);
                    return KernelResult.Ok();
                }
            });
            return publication.IsSuccess ? KernelResult<PlatformDeviceLease>.Ok(lease) : RejectLease(publication);

            KernelResult<PlatformDeviceLease> RejectLease(KernelResult rejection)
            {
                var malformed = !providerValidation.IsSuccess;
                lock (_secureDomainLifecycleGate)
                {
                    leaseRecord.FaultPinned |= malformed;
                    leaseRecord.LocalAuthorizationRevoked = true;
                    leaseRecord.ClosureInFlight = true;
                    _originalDeviceOwners.TryAdd(lease.LeaseId, leaseRecord);
                    _deviceLeases.Add(lease.LeaseId, leaseRecord);
                    if (!malformed) TransferDeviceBindObservationLocked(domainRecord, leaseRecord);
                    if (malformed) QuarantineDomain(domainRecord);
                }
                var cleanupProven = false;
                try
                {
                    var sourceCurrent = ValidateDeviceClosureGeneration(leaseRecord);
                    bool cleanupAdmitted;
                    lock (_secureDomainLifecycleGate)
                    {
                        cleanupAdmitted = sourceCurrent && !_backendEpochExhausted && BackendEpoch == backendEpoch &&
                            TryGetOriginalDeviceRecord(lease.LeaseId, out var exactDevice) && ReferenceEquals(exactDevice, leaseRecord) &&
                            TryGetCanonicalRootRecord(binding.BindingId, out var exactParent) && ReferenceEquals(exactParent, domainRecord) &&
                            !domainRecord.ParentRevokeMayHaveEffect && leaseRecord.ClosureInFlight;
                        if (cleanupAdmitted)
                        {
                            leaseRecord.RevokeParent = domainRecord;
                            leaseRecord.RevokeMayHaveEffect = true;
                        }
                    }
                    if (cleanupAdmitted)
                    {
                        var cleanup = deviceProvider.RevokeDevice(providerLease);
                        lock (_secureDomainLifecycleGate) leaseRecord.RevokeObservation = cleanup;
                        if (!malformed && cleanup.IsSuccess)
                        {
                            lock (_secureDomainLifecycleGate) leaseRecord.ProviderClosureConfirmed = true;
                            cleanupProven = CompleteKnownDeviceClosure(leaseRecord).IsSuccess;
                        }
                        else PinAmbiguousDeviceClosure(leaseRecord, domainRecord);
                    }
                    else PinAmbiguousDeviceClosure(leaseRecord, domainRecord);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    PinAmbiguousDeviceClosure(leaseRecord, domainRecord);
                }
                finally { lock (_secureDomainLifecycleGate) leaseRecord.ClosureInFlight = false; }
                lock (_secureDomainLifecycleGate)
                {
                    if (cleanupProven) _deviceLeases.Remove(lease.LeaseId);
                    else if (leaseRecord.FaultPinned || leaseRecord.BackendEpoch != BackendEpoch ||
                        !leaseRecord.ProviderClosureConfirmed)
                    {
                        leaseRecord.FaultPinned = true;
                        QuarantineDomain(domainRecord);
                    }
                }
                return KernelResult<PlatformDeviceLease>.Fail(rejection.Error, rejection.Message!);
            }
        }
        finally { lock (_secureDomainLifecycleGate) domainRecord.PendingDeviceBinds--; }
    }

    private static void TransferDeviceBindObservationLocked(DomainRecord domain, DeviceLeaseRecord device)
    {
        device.BindObservation = domain.DeviceBindObservation;
        domain.DeviceBindObservation = null;
        domain.DeviceBindCapabilityId = null;
        domain.DeviceBindDevice = null;
        domain.DeviceBindRights = null;
        domain.DeviceBindIncarnation = null;
        domain.DeviceBindEpoch = null;
        domain.DeviceBindSource = null;
        domain.DeviceBindMayHaveEffect = false;
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
            if (record.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Device closure remains fault-pinned.");
            if (record.PendingIrqBinds != 0 || record.PendingMmioBinds != 0 || record.ClosureInFlight || record.CxlChildren.Count != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Device closure requires completed IRQ/MMIO admission and exact CXL child closure.");
            if (record.PendingVirtualIoBinds != 0 || _virtualIoBindings.Values.Any(x =>
                    x.Binding.ParentDevice == lease && x.Closure != PlatformExternalClosureState.Closed))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Device closure requires completed virtual-I/O admission and exact child closure.");
            if (record.RevokeMayHaveEffect && !record.ProviderClosureConfirmed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original device closure remains ambiguous; repeat refused.");
            record.ClosureInFlight = true;
        }
        try { return RevokeDeviceCore(record, lease, expectedSubject); }
        finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
    }
    private KernelResult RevokeDeviceCore(
        DeviceLeaseRecord record,
        PlatformDeviceLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        bool knownClosure;
        DomainRecord parent;
        lock (_secureDomainLifecycleGate)
        {
            var validation = ValidateDeviceLeaseIdentity(lease, expectedSubject);
            if (!validation.IsSuccess) return validation;
            if (!TryGetOriginalDeviceRecord(lease.LeaseId, out var current) || !ReferenceEquals(current, record) ||
                !record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original device cleanup owner changed before closure.");
            if (record.PlatformClosed)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "The platform device lease has already been closed.");
            if (record.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformFaulted, "The device lease is fault-pinned and cannot prove provider closure.");
            knownClosure = record.ProviderClosureConfirmed;
            parent = _domains[lease.DomainBinding.BindingId];
        }
        if (knownClosure) return CompleteKnownDeviceClosure(record);
        if (_provider is not IPlatformDeviceLeaseProvider deviceProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the device lease no longer exposes device closure.");
        }

        if (!ValidateDeviceClosureGeneration(record))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device provider or local backend generation changed before closure; the lease remains pinned.");

        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateDeviceLeaseIdentity(lease, expectedSubject);
            if (!identity.IsSuccess) return identity;
            if (!TryGetOriginalDeviceRecord(lease.LeaseId, out var current) || !ReferenceEquals(current, record) ||
                !TryGetCanonicalRootRecord(lease.DomainBinding.BindingId, out var exactParent) || !ReferenceEquals(exactParent, parent) ||
                !record.ClosureInFlight || record.FaultPinned || record.PlatformClosed ||
                BackendEpoch != record.BackendEpoch || _backendEpochExhausted)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Original device cleanup admission changed during source observation; provider effect refused.");
            record.RevokeMayHaveEffect = true;
            record.RevokeParent = parent;
        }
        PlatformAuthorityResult providerResult;
        try { providerResult = deviceProvider.RevokeDevice(record.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PinAmbiguousDeviceClosure(record, parent);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"The device provider threw during closure; the lease remains pinned: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate) record.RevokeObservation = providerResult;
        if (!providerResult.IsSuccess)
        {
            PinAmbiguousDeviceClosure(record, parent);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Device closure lacks exact containment evidence; original reply and lease remain pinned.");
        }

        lock (_secureDomainLifecycleGate)
        {
            record.ProviderClosureConfirmed = true;
            record.LocalAuthorizationRevoked = true;
        }
        return CompleteKnownDeviceClosure(record);
    }

    private void PinAmbiguousDeviceClosure(DeviceLeaseRecord record, DomainRecord parent)
    {
        lock (_secureDomainLifecycleGate)
        {
            record.FaultPinned = true;
            if (!TryGetOriginalDeviceRecord(record.Lease.LeaseId, out var exact) || !ReferenceEquals(exact, record))
            {
                // Existing unresolved device-effect interlock retains parent lifetime
                // when the original child can no longer be enumerated for cleanup.
                parent.DeviceBindMayHaveEffect = true;
                if (TryGetCanonicalRootRecord(parent.Binding.BindingId, out var current) &&
                    current.Binding == parent.Binding && current.ProviderLease == parent.ProviderLease &&
                    current.ProviderIncarnation == parent.ProviderIncarnation && current.BackendEpoch == parent.BackendEpoch)
                    current.DeviceBindMayHaveEffect = true;
            }
        }
    }

    private KernelResult CompleteKnownDeviceClosure(DeviceLeaseRecord record)
    {
        PlatformProviderIncarnation current;
        try { current = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!HasExactDeviceCloseOwnersLocked(record))
                {
                    record.FaultPinned = true;
                    if (record.RevokeParent is { } parent) PinAmbiguousDeviceClosure(record, parent);
                }
                if (BackendEpoch != record.BackendEpoch || _backendEpochExhausted) record.FaultPinned = true;
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Known device closure retained while source observation failed: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
            if (!HasExactDeviceCloseOwnersLocked(record))
            {
                record.FaultPinned = true;
                if (record.RevokeParent is { } parent) PinAmbiguousDeviceClosure(record, parent);
            }
            if (current.Value == 0 || current != record.ProviderIncarnation ||
                BackendEpoch != record.BackendEpoch || _backendEpochExhausted)
                record.FaultPinned = true;
            if (!record.ProviderClosureConfirmed || record.FaultPinned || !record.ClosureInFlight ||
                !TryGetOriginalDeviceRecord(record.Lease.LeaseId, out var exact) || !ReferenceEquals(exact, record))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Known device closure lacks exact continuity for local release.");
            record.PlatformClosed = true;
        }
        return KernelResult.Ok();
    }

    internal KernelResult BeginDeviceChildDrain(PlatformDeviceLease lease, PlatformDomainIdentity subject,
        out Action release)
    {
        release = static () => { };
        lock (_secureDomainLifecycleGate)
        {
            var exact = ValidateDeviceLeaseIdentity(lease, subject);
            if (!exact.IsSuccess) return exact;
            var record = _deviceLeases[lease.LeaseId];
            if (record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Device closure is in flight.");
            if (record.PendingChildDrains != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Device child drain is in flight.");
            if (record.PlatformClosed) return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Device is closed.");
            if (record.FaultPinned) return KernelResult.Fail(KernelError.PlatformFaulted, "Device is fault-pinned.");
            record.PendingChildDrains++;
            release = () => { lock (_secureDomainLifecycleGate) record.PendingChildDrains--; };
            return KernelResult.Ok();
        }
    }

    internal KernelResult ValidateDeviceLease(
        PlatformDeviceLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDeviceLeaseIdentity(lease, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _deviceLeases[lease.LeaseId];
        if (record.ClosureInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "Device closure is in flight.");
        if (record.PendingChildDrains != 0)
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Device child drain is in flight.");
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
        => DeviceEffectSource(lease, subject);

    // Correlation only: consumers must freshly authorize with CapabilityAuthority.
    internal KernelResult<CapabilityId> DeviceEffectSource(PlatformDeviceLease lease, PlatformDomainIdentity subject)
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
            return KernelResult<CapabilityId>.Fail(KernelError.PlatformFaulted, "Device source continuity is lost.");
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
            if (!TryGetOriginalDeviceRecord(reservation.Lease.LeaseId, out var record) || record.Lease != reservation.Lease)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL parent reservation requires the exact device lease.");
            if (record.BindParentSource is not { } parent ||
                !TryGetCanonicalRootRecord(reservation.Lease.DomainBinding.BindingId, out var currentParent) || !ReferenceEquals(parent, currentParent))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL device parent is not current.");
            if (!reservation.CaptureSources(record, parent))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original CXL reservation cannot be re-registered.");
            record.CxlChildren.Add(reservation);
            return KernelResult.Ok();
        }
    }

    internal KernelResult ReleaseCxlDeviceReservation(CxlDeviceReservation reservation,
        Func<Func<KernelResult>, KernelResult>? commit = null)
    {
        DeviceLeaseRecord record;
        lock (_secureDomainLifecycleGate)
        {
            if (!TryGetOriginalDeviceRecord(reservation.Lease.LeaseId, out record!) || record.Lease != reservation.Lease ||
                !HasCurrentCxlReservationSourcesLocked(reservation, record) || !record.CxlChildren.Contains(reservation))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL parent reservation is not current or already released.");
        }
        if (!ValidateDeviceClosureGeneration(record))
            return KernelResult.Fail(KernelError.PlatformFaulted, "CXL parent continuity was lost before reservation release.");
        lock (_secureDomainLifecycleGate)
        {
            if (_backendEpochExhausted || BackendEpoch != record.BackendEpoch ||
                !HasCurrentCxlReservationSourcesLocked(reservation, record) || !record.CxlChildren.Contains(reservation))
                return KernelResult.Fail(KernelError.PlatformFaulted, "CXL parent reservation remains pinned after continuity loss.");
            // Trusted local owner commit only, after all provider queries. Existing platform -> CXL gate order.
            KernelResult Release() => record.CxlChildren.Remove(reservation)
                ? KernelResult.Ok() : KernelResult.Fail(KernelError.StaleGeneration, "Original CXL reservation is already released.");
            return commit is null ? Release() : commit(Release);
        }
    }

    // Original cleanup identity only, not renewed device-use permission.
    private bool HasCurrentCxlReservationSourcesLocked(CxlDeviceReservation reservation, DeviceLeaseRecord record) =>
        ReferenceEquals(reservation.OriginalDeviceSource, record) &&
        TryGetOriginalDeviceRecord(reservation.Lease.LeaseId, out var current) && ReferenceEquals(current, record) &&
        reservation.OriginalParentSource is DomainRecord parent && ReferenceEquals(record.BindParentSource, parent) &&
        TryGetCanonicalRootRecord(reservation.Lease.DomainBinding.BindingId, out var currentParent) && ReferenceEquals(currentParent, parent) &&
        parent.Binding == reservation.Lease.DomainBinding;

    private bool HasExactDeviceCloseOwnersLocked(DeviceLeaseRecord record) =>
        record.RevokeParent is { } parent &&
        TryGetOriginalDeviceRecord(record.Lease.LeaseId, out var exactDevice) && ReferenceEquals(exactDevice, record) &&
        TryGetCanonicalRootRecord(record.Lease.DomainBinding.BindingId, out var exactParent) && ReferenceEquals(exactParent, parent) &&
        parent.Binding == record.Lease.DomainBinding && parent.ProviderLease == record.ProviderLease.DomainLease &&
        parent.ProviderIncarnation == record.ProviderIncarnation && parent.BackendEpoch == record.BackendEpoch &&
        parent.AuthorityState == DomainAuthorityState.Active && !parent.ParentRevokeInFlight && !parent.ParentRevokeMayHaveEffect;

    private bool HasExactChildCleanupDomainLocked(DeviceLeaseRecord device, DomainRecord? domain) =>
        domain is not null &&
        TryGetCanonicalRootRecord(device.Lease.DomainBinding.BindingId, out var exactDomain) && ReferenceEquals(exactDomain, domain) &&
        domain.Binding == device.Lease.DomainBinding && domain.ProviderLease == device.ProviderLease.DomainLease &&
        domain.ProviderIncarnation == device.ProviderIncarnation && domain.BackendEpoch == device.BackendEpoch &&
        domain.AuthorityState == DomainAuthorityState.Active && !domain.ParentRevokeInFlight && !domain.ParentRevokeMayHaveEffect;

    internal IReadOnlyList<PlatformDeviceLease> BeginDeviceCapabilityRevocation(
        CapabilityId capabilityId, Func<CapabilityId, bool> dependsOnRevokedCapability)
    {
        lock (_secureDomainLifecycleGate)
        {
            var affected = DeviceDependencyRecordsLocked()
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

    internal bool HasActiveDeviceLeases(PlatformDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
            return DeviceDependencyRecordsLocked().Any(record =>
            !record.PlatformClosed &&
            record.Lease.DomainBinding.BindingId == binding.BindingId);
    }

    internal IReadOnlyList<PlatformDeviceLease> ActiveDeviceLeasesForProcess(PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
            return DeviceDependencyRecordsLocked().Where(record => !record.PlatformClosed &&
                record.Lease.DomainBinding.Subject == subject).OrderBy(record => record.Lease.LeaseId.Value)
                .Select(record => record.Lease).ToArray();
    }

    internal IReadOnlyList<PlatformDeviceLease> ActiveDeviceLeasesForBinding(
        PlatformDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
            return DeviceDependencyRecordsLocked()
            .Where(record =>
                !record.PlatformClosed &&
                record.Lease.DomainBinding.BindingId == binding.BindingId)
            .OrderBy(record => record.Lease.LeaseId.Value)
            .Select(static record => record.Lease)
            .ToArray();
    }

    private KernelResult ValidateDeviceLeaseIdentity(
        PlatformDeviceLease lease,
        PlatformDomainIdentity expectedSubject)
    {
        if (!TryGetOriginalDeviceRecord(lease.LeaseId, out var record))
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
