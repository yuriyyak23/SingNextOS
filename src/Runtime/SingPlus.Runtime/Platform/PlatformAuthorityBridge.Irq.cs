using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformIrqBindingId(ulong Value);
public readonly record struct PlatformIrqBindingGeneration(ulong Value);

public readonly record struct PlatformIrqBinding(
    PlatformIrqBindingId BindingId,
    PlatformIrqBindingGeneration Generation,
    PlatformDeviceLease DeviceLease,
    PlatformInterruptSourceIdentity Source,
    KernelEventEndpoint EventEndpoint);

internal readonly record struct PlatformIrqDeliveryEvidence(
    bool DeliveryAvailable,
    PlatformProviderInterruptDeliverySequence ProviderSequence);

public sealed partial class PlatformAuthorityBridge
{
    private sealed class IrqBindingRecord(
        PlatformIrqBinding binding,
        PlatformProviderIrqBinding providerBinding,
        CapabilityId authorityCapabilityId)
    {
        public PlatformIrqBinding Binding { get; } = binding;
        public PlatformProviderIrqBinding ProviderBinding { get; } = providerBinding;
        public CapabilityId AuthorityCapabilityId { get; } = authorityCapabilityId;
        public bool LocalAuthorizationRevoked { get; set; }
        public bool PlatformClosed { get; set; }
        public bool ProviderClosureConfirmed { get; set; } // Exact known reply, not local release.
        public bool RevokeMayHaveEffect { get; set; }
        public PlatformAuthorityResult? RevokeObservation { get; set; }
        public DomainRecord? RevokeParent { get; set; } // Original cleanup source, never authority.
        public DomainRecord? BindParentSource { get; set; }
        public DeviceLeaseRecord? BindDeviceSource { get; set; }
        public KernelEventRegistry.IrqSourceReservation? EndpointSource { get; set; }
        public PlatformAuthorityResult<PlatformProviderIrqBinding>? BindObservation { get; set; }
        public bool FaultPinned { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool DeliveryInFlight { get; set; }
        public PlatformAuthorityResult<PlatformInterruptDeliveryObservation>? PollObservation { get; set; }
        public PlatformProviderInterruptDeliverySequence CompletionSequence { get; set; }
        public bool CompletionMayHaveEffect { get; set; }
        public PlatformAuthorityResult? CompletionObservation { get; set; }
    }

    private readonly Dictionary<PlatformIrqBindingId, IrqBindingRecord> _irqBindings = [];
    private ulong _nextIrqBindingId = 1;

    internal KernelResult<PlatformIrqBinding> BindIrq(
        PlatformDeviceLease deviceLease, PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId, PlatformInterruptSourceIdentity source,
        KernelEventEndpoint eventEndpoint, Func<KernelResult> revalidateAuthorization,
        Func<PlatformIrqBinding, Func<KernelResult>, KernelResult> commitAuthorization,
        Func<KernelResult<KernelEventRegistry.IrqSourceReservation>> reserveEndpointSource,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null)
    {
        DeviceLeaseRecord deviceRecord = null!;
        DomainRecord domainRecord = null!;
        ulong localId = 0;
        KernelEventRegistry.IrqSourceReservation? endpointSource = null;
        var effectMayHaveOccurred = false;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateDeviceLeaseIdentity(deviceLease, expectedSubject);
            if (!identity.IsSuccess) return identity;
            deviceRecord = _deviceLeases[deviceLease.LeaseId];
            if (!TryGetCanonicalRootRecord(deviceLease.DomainBinding.BindingId, out domainRecord!))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "IRQ parent domain is absent.");
            if (deviceRecord.ClosureInFlight || deviceRecord.PendingIrqBinds != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "IRQ admission cannot overlap parent device closure or IRQ admission.");
            if (_nextIrqBindingId is 0 or ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "IRQ identity space is exhausted.");
            localId = _nextIrqBindingId++;
            deviceRecord.PendingIrqBinds++;
            return KernelResult.Ok();
        }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformIrqBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
            var deviceValidation = ValidateDeviceLease(deviceLease, expectedSubject);
            if (!deviceValidation.IsSuccess) return Failure(deviceValidation);
            var request = PlatformIrqBindingContract.ValidateRequest(source);
            if (!request.IsSuccess)
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformDenied, request.Message!);
            if ((deviceLease.Rights & PlatformDeviceRights.Configure) == 0)
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.InsufficientRights, "IRQ routing requires device Configure authority.");
            if (_provider is not IPlatformIrqBindingProvider irqProvider)
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformUnsupported, "Provider lacks semantic IRQ binding.");
            lock (_secureDomainLifecycleGate)
            {
                if (_irqBindings.Values.Any(record => !record.PlatformClosed &&
                    record.Binding.DeviceLease.LeaseId == deviceLease.LeaseId && record.Binding.Source == source))
                    return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformBindingActive, "Exact IRQ source is already bound.");
            }
            if (!ValidateDeviceClosureGeneration(deviceRecord))
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted, "IRQ parent generation continuity was lost.");
            var authorization = revalidateAuthorization();
            if (!authorization.IsSuccess) return Failure(authorization);
            var parent = CheckParent();
            if (!parent.IsSuccess) return Failure(parent);
            var reserved = reserveEndpointSource();
            if (!reserved.IsSuccess) return KernelResult<PlatformIrqBinding>.Fail(reserved.Error, reserved.Message!);
            endpointSource = reserved.Value!;
            endpointSource.BindSource(deviceRecord);
            authorization = revalidateAuthorization();
            if (!authorization.IsSuccess) return Failure(authorization);
            if (!ValidateDeviceClosureGeneration(deviceRecord))
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted, "IRQ source changed before bind effect intent.");
            lock (_secureDomainLifecycleGate)
            {
                parent = CheckParent();
                if (!parent.IsSuccess) return Failure(parent);
                deviceRecord.UnresolvedIrqCapabilityId = authorityCapabilityId;
                deviceRecord.IrqBindParent = domainRecord;
                deviceRecord.IrqBindSource = source;
                deviceRecord.IrqBindEndpointSource = endpointSource;
                deviceRecord.IrqBindMayHaveEffect = true;
            }
            effectMayHaveOccurred = true;
            var providerResult = irqProvider.BindInterrupt(deviceRecord.ProviderLease, source);
            lock (_secureDomainLifecycleGate) deviceRecord.IrqBindObservation = providerResult;
            if (!ValidateDeviceClosureGeneration(deviceRecord))
            {
                if (providerResult.IsSuccess && PlatformIrqBindingContract.ValidateBinding(
                    deviceRecord.ProviderLease, source, providerResult.Value!).IsSuccess)
                {
                    var retained = new PlatformIrqBinding(new PlatformIrqBindingId(localId),
                        new PlatformIrqBindingGeneration(1), deviceLease, source, eventEndpoint);
                    var retainedRecord = new IrqBindingRecord(retained, providerResult.Value!, authorityCapabilityId)
                    { LocalAuthorizationRevoked = true, FaultPinned = true, EndpointSource = endpointSource, BindObservation = providerResult };
                    endpointSource.TransferSource(deviceRecord, retainedRecord);
                    lock (_secureDomainLifecycleGate)
                    {
                        _irqBindings.Add(retained.BindingId, retainedRecord);
                        TransferIrqBindObservationLocked(deviceRecord, retainedRecord);
                    }
                }
                PinAmbiguousDeviceClosure(deviceRecord, domainRecord);
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted, "IRQ parent generation changed during binding.");
            }
            if (!providerResult.IsSuccess)
            {
                PinAmbiguousDeviceClosure(deviceRecord, domainRecord);
                return FromProviderFailure<PlatformIrqBinding>(providerResult.Status, providerResult.Message);
            }
            var providerBinding = providerResult.Value!;
            var validReceipt = PlatformIrqBindingContract.ValidateBinding(deviceRecord.ProviderLease, source, providerBinding);
            if (!validReceipt.IsSuccess)
            {
                // Best-effort cleanup of the returned object cannot establish closure
                // of the requested effect: its exact receipt contract already failed.
                lock (_secureDomainLifecycleGate)
                {
                    deviceRecord.FaultPinned = true;
                    deviceRecord.MalformedIrqReceipt = providerBinding;
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
                            deviceRecord.UnresolvedIrqCapabilityId == authorityCapabilityId && !deviceRecord.MalformedIrqCleanupMayHaveEffect;
                        if (cleanupAdmitted) deviceRecord.MalformedIrqCleanupMayHaveEffect = true;
                    }
                    if (cleanupAdmitted)
                    {
                        var cleanup = irqProvider.RevokeInterrupt(providerBinding);
                        lock (_secureDomainLifecycleGate) deviceRecord.MalformedIrqCleanupObservation = cleanup;
                    }
                }
                catch (Exception exception) when (exception is not StackOverflowException) { }
                // Preserve UnresolvedIrqCapabilityId for original admission cleanup.
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted, validReceipt.Message!);
            }
            var binding = new PlatformIrqBinding(new PlatformIrqBindingId(localId),
                new PlatformIrqBindingGeneration(1), deviceLease, source, eventEndpoint);
            var record = new IrqBindingRecord(binding, providerBinding, authorityCapabilityId)
            { EndpointSource = endpointSource, BindObservation = providerResult };
            endpointSource.TransferSource(deviceRecord, record);
            authorization = revalidateAuthorization();
            if (!authorization.IsSuccess) return RejectKnown(authorization);
            var published = commitAuthorization(binding, () =>
            {
                lock (_secureDomainLifecycleGate)
                {
                    var currentParent = CheckParent();
                    if (!currentParent.IsSuccess) return currentParent;
                    _irqBindings.Add(binding.BindingId, record);
                    TransferIrqBindObservationLocked(deviceRecord, record);
                    return KernelResult.Ok();
                }
            });
            if (!published.IsSuccess) return RejectKnown(published);
            return KernelResult<PlatformIrqBinding>.Ok(binding);

            KernelResult<PlatformIrqBinding> RejectKnown(KernelResult rejection)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.LocalAuthorizationRevoked = true;
                    record.ClosureInFlight = true;
                    _irqBindings.Add(binding.BindingId, record);
                    // Transfer the known creation receipt to its existing owner.
                    TransferIrqBindObservationLocked(deviceRecord, record);
                }
                try
                {
                    var sourceCurrent = ValidateDeviceClosureGeneration(deviceRecord);
                    bool cleanupAdmitted;
                    lock (_secureDomainLifecycleGate)
                    {
                        cleanupAdmitted = sourceCurrent && !_backendEpochExhausted && BackendEpoch == deviceRecord.BackendEpoch &&
                            _irqBindings.TryGetValue(binding.BindingId, out var exactChild) && ReferenceEquals(exactChild, record) &&
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
                    if (!cleanupAdmitted)
                    {
                        FaultIrqDelivery(record, deviceRecord);
                        return Failure(rejection);
                    }
                    var cleanup = irqProvider.RevokeInterrupt(providerBinding); // Outside owner locks.
                    lock (_secureDomainLifecycleGate) record.RevokeObservation = cleanup;
                    if (cleanup.IsSuccess)
                    {
                        lock (_secureDomainLifecycleGate) record.ProviderClosureConfirmed = true;
                        var closed = CompleteKnownIrqClosure(binding, record, deviceRecord);
                        if (closed.IsSuccess)
                            lock (_secureDomainLifecycleGate) _irqBindings.Remove(binding.BindingId);
                    }
                    else
                    {
                        FaultIrqDelivery(record, deviceRecord);
                    }
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (_secureDomainLifecycleGate)
                    {
                        record.FaultPinned = true;
                        deviceRecord.FaultPinned = true;
                    }
                }
                finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
                return Failure(rejection);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            if (effectMayHaveOccurred) PinAmbiguousDeviceClosure(deviceRecord, domainRecord);
            else lock (_secureDomainLifecycleGate) deviceRecord.FaultPinned = true;
            return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted,
                $"IRQ admission continuity was lost; parent remains pinned: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate) deviceRecord.PendingIrqBinds--;
            if (!effectMayHaveOccurred && endpointSource is not null)
                _ = endpointSource.CommitRelease(deviceRecord, static () => KernelResult.Ok());
        }

        KernelResult CheckParent()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!TryGetOriginalDeviceRecord(deviceLease.LeaseId, out var currentDevice) ||
                    !ReferenceEquals(currentDevice, deviceRecord) ||
                    !TryGetCanonicalRootRecord(deviceLease.DomainBinding.BindingId, out var currentDomain) ||
                    !ReferenceEquals(currentDomain, domainRecord) || deviceRecord.PlatformClosed ||
                    deviceRecord.FaultPinned || BackendEpoch != deviceRecord.BackendEpoch)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Exact IRQ parent continuity was lost.");
                if (deviceRecord.LocalAuthorizationRevoked)
                    return KernelResult.Fail(KernelError.CapabilityRevoked, "IRQ parent device authorization was revoked.");
                if (domainRecord.AuthorityState != DomainAuthorityState.Active || domainRecord.ParentRevokeMayHaveEffect)
                    return KernelResult.Fail(KernelError.PlatformBindingDraining, "IRQ parent domain is closing.");
                return KernelResult.Ok();
            }
        }
        static KernelResult<PlatformIrqBinding> Failure(KernelResult result) =>
            KernelResult<PlatformIrqBinding>.Fail(result.Error, result.Message!);
    }

    private static void TransferIrqBindObservationLocked(DeviceLeaseRecord device, IrqBindingRecord record)
    {
        record.BindObservation = device.IrqBindObservation;
        record.BindParentSource = device.IrqBindParent;
        record.BindDeviceSource = device;
        record.BindParentSource!.IrqSources.Add(record);
        device.UnresolvedIrqCapabilityId = null;
        device.IrqBindMayHaveEffect = false;
        device.IrqBindObservation = null;
        device.IrqBindParent = null;
        device.IrqBindSource = null;
        device.IrqBindEndpointSource = null;
    }

    internal bool HasUnresolvedIrqEffect(Func<CapabilityId, bool> dependsOnRevokedCapability,
        IReadOnlyList<object> endpointSources)
    {
        lock (_secureDomainLifecycleGate)
        {
            var devices = DeviceDependencyRecordsLocked().Concat(endpointSources.OfType<DeviceLeaseRecord>());
            if (devices.Any(record => record.UnresolvedIrqCapabilityId is { } id && dependsOnRevokedCapability(id)))
                return true;
            // A captured device source can transfer to a known IRQ while the snapshot
            // is being consumed. Its original parent retains the same admitted fact
            // before the device's unresolved marker is cleared, under this gate.
            var sources = _domains.Values.Concat(devices.Select(device => device.BindParentSource).OfType<DomainRecord>())
                .SelectMany(domain => domain.IrqSources).OfType<IrqBindingRecord>()
                .Concat(endpointSources.OfType<IrqBindingRecord>());
            return sources.Any(record => !record.PlatformClosed && dependsOnRevokedCapability(record.AuthorityCapabilityId));
        }
    }
    internal KernelResult BeginIrqDelivery(PlatformIrqBinding binding, PlatformDomainIdentity subject,
        out Action? release, out Action? fault,
        out Func<PlatformProviderInterruptDeliverySequence, Func<CapabilityId, Func<KernelResult>, KernelResult>, KernelResult>? complete)
    {
        release = null;
        fault = null;
        complete = null;
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateIrqBinding(binding, subject);
            if (!valid.IsSuccess) return valid;
            var record = _irqBindings[binding.BindingId];
            if (record.DeliveryInFlight || record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Exact IRQ delivery or closure is already in flight.");
            record.DeliveryInFlight = true;
            var device = _deviceLeases[binding.DeviceLease.LeaseId];
            release = () => EndIrqDelivery(record, device);
            fault = () => FaultIrqDelivery(record, device);
            complete = (sequence, authorize) => CompleteIrqDelivery(record, device, binding, subject, sequence, authorize);
            return KernelResult.Ok();
        }
    }

    private void EndIrqDelivery(IrqBindingRecord record, DeviceLeaseRecord device)
    {
        lock (_secureDomainLifecycleGate)
        {
            record.DeliveryInFlight = false;
            if (CurrentOriginalIrqOwnerLocked(record, device) is { } current) current.DeliveryInFlight = false;
        }
    }

    private IrqBindingRecord? CurrentOriginalIrqOwnerLocked(IrqBindingRecord record, DeviceLeaseRecord device)
    {
        if (_irqBindings.TryGetValue(record.Binding.BindingId, out var current) && ReferenceEquals(current, record) && current.Binding == record.Binding &&
            current.ProviderBinding == record.ProviderBinding && current.AuthorityCapabilityId == record.AuthorityCapabilityId &&
            TryGetOriginalDeviceRecord(device.Lease.LeaseId, out var parent) && ReferenceEquals(parent, device) && parent.Lease == device.Lease &&
            parent.ProviderLease == device.ProviderLease && parent.ProviderIncarnation == device.ProviderIncarnation &&
            parent.BackendEpoch == device.BackendEpoch)
            return current;
        return null;
    }

    private void FaultIrqDelivery(IrqBindingRecord record, DeviceLeaseRecord device)
    {
        lock (_secureDomainLifecycleGate)
        {
            record.FaultPinned = true;
            device.FaultPinned = true;
            if (CurrentOriginalIrqOwnerLocked(record, device) is { } current) current.FaultPinned = true;
        }
    }

    private KernelResult AdmitOriginalIrqEffect(IrqBindingRecord record, DeviceLeaseRecord device,
        PlatformIrqBinding binding, PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (!_irqBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
                !TryGetOriginalDeviceRecord(binding.DeviceLease.LeaseId, out var parent) || !ReferenceEquals(parent, device) ||
                !record.DeliveryInFlight || BackendEpoch != device.BackendEpoch || _backendEpochExhausted)
            {
                FaultIrqDelivery(record, device);
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original IRQ delivery owner/admission changed; effect refused.");
            }
            return AdmitIrqEffect(binding, subject);
        }
    }

    internal KernelResult<PlatformIrqDeliveryEvidence> PollIrq(PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject, Func<CapabilityId, Func<KernelResult>, KernelResult> authorize)
    {
        KernelResult valid;
        IrqBindingRecord record;
        DeviceLeaseRecord device;
        lock (_secureDomainLifecycleGate)
        {
            valid = ValidateIrqBinding(binding, expectedSubject);
            if (!valid.IsSuccess) return KernelResult<PlatformIrqDeliveryEvidence>.Fail(valid.Error, valid.Message!);
            record = _irqBindings[binding.BindingId];
            device = _deviceLeases[binding.DeviceLease.LeaseId];
            if (!record.DeliveryInFlight)
                return KernelResult<PlatformIrqDeliveryEvidence>.Fail(KernelError.InvalidTransition, "IRQ delivery admission is absent.");
        }
        try
        {
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ poll generation continuity was lost.");
            var permission = authorize(record.AuthorityCapabilityId, () => AdmitOriginalIrqEffect(record, device, binding, expectedSubject));
            if (!permission.IsSuccess) return KernelResult<PlatformIrqDeliveryEvidence>.Fail(permission.Error, permission.Message!);
            valid = ValidateIrqBinding(binding, expectedSubject);
            if (!valid.IsSuccess) return KernelResult<PlatformIrqDeliveryEvidence>.Fail(valid.Error, valid.Message!);
            if (_provider is not IPlatformIrqBindingProvider provider) return Fault("Provider lacks IRQ delivery.");
            var observed = provider.PollInterrupt(record.ProviderBinding);
            // Original cleanup correlation, retained even when later publication is refused.
            lock (_secureDomainLifecycleGate) record.PollObservation = observed;
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ poll generation changed during observation.");
            if (!observed.IsSuccess) return Fault("IRQ observation failed without exact no-effect evidence.");
            var observation = observed.Value!;
            var receipt = PlatformIrqBindingContract.ValidateObservation(record.ProviderBinding, observation);
            if (!receipt.IsSuccess) return Fault(receipt.Message!);
            permission = authorize(record.AuthorityCapabilityId, () => AdmitOriginalIrqEffect(record, device, binding, expectedSubject));
            if (!permission.IsSuccess)
            {
                FaultIrqDelivery(record, device);
                return KernelResult<PlatformIrqDeliveryEvidence>.Fail(permission.Error, permission.Message!);
            }
            valid = ValidateIrqBinding(binding, expectedSubject);
            if (!valid.IsSuccess)
            {
                FaultIrqDelivery(record, device);
                return KernelResult<PlatformIrqDeliveryEvidence>.Fail(valid.Error, valid.Message!);
            }
            return KernelResult<PlatformIrqDeliveryEvidence>.Ok(new(observation.DeliveryAvailable, observation.Sequence));
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fault(exception.Message); }
        KernelResult<PlatformIrqDeliveryEvidence> Fault(string message)
        {
            FaultIrqDelivery(record, device);
            return KernelResult<PlatformIrqDeliveryEvidence>.Fail(KernelError.PlatformFaulted, message);
        }
    }

    private KernelResult CompleteIrqDelivery(IrqBindingRecord record, DeviceLeaseRecord device,
        PlatformIrqBinding binding, PlatformDomainIdentity expectedSubject,
        PlatformProviderInterruptDeliverySequence sequence, Func<CapabilityId, Func<KernelResult>, KernelResult> authorize)
    {
        lock (_secureDomainLifecycleGate)
        {
            var exact = ValidateCompletionTupleLocked();
            if (!exact.IsSuccess) return exact;
        }
        try
        {
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ completion generation continuity was lost.");
            var permission = authorize(record.AuthorityCapabilityId, () =>
            {
                lock (_secureDomainLifecycleGate)
                {
                    var exact = ValidateCompletionTupleLocked();
                    if (!exact.IsSuccess) return exact;
                    exact = AdmitOriginalIrqEffect(record, device, binding, expectedSubject);
                    if (!exact.IsSuccess) return exact;
                    record.CompletionSequence = sequence;
                    record.CompletionObservation = null;
                    record.CompletionMayHaveEffect = true;
                    return KernelResult.Ok();
                }
            });
            if (!permission.IsSuccess) { FaultIrqDelivery(record, device); return permission; }
            if (_provider is not IPlatformIrqBindingProvider provider) return Fault("Provider lacks IRQ completion.");
            var completed = provider.CompleteInterruptDelivery(record.ProviderBinding, sequence);
            lock (_secureDomainLifecycleGate)
            {
                record.CompletionObservation = completed;
                if (CurrentOriginalIrqOwnerLocked(record, device) is { } current &&
                    current.CompletionSequence == sequence && current.CompletionMayHaveEffect)
                    current.CompletionObservation = completed;
            }
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ completion generation changed during effect.");
            if (!completed.IsSuccess)
            {
                FaultIrqDelivery(record, device);
                return FromProviderFailure(completed.Status, completed.Message);
            }
            lock (_secureDomainLifecycleGate)
            {
                if (!_irqBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
                    !TryGetOriginalDeviceRecord(binding.DeviceLease.LeaseId, out var parent) || !ReferenceEquals(parent, device) ||
                    !record.DeliveryInFlight || BackendEpoch != device.BackendEpoch || _backendEpochExhausted)
                    return Fault("Original IRQ completion owner changed before staged publication.");
            }
            // Completion was admitted before the callback. Exact already-staged publication is owned by the mailbox.
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fault(exception.Message); }
        KernelResult Fault(string message)
        {
            FaultIrqDelivery(record, device);
            return KernelResult.Fail(KernelError.PlatformFaulted, message);
        }
        KernelResult ValidateCompletionTupleLocked()
        {
            if (sequence.Value == 0 || record.PollObservation is not { IsSuccess: true } poll ||
                poll.Value.Binding != record.ProviderBinding || !poll.Value.DeliveryAvailable || poll.Value.Sequence != sequence ||
                record.CompletionMayHaveEffect && record.CompletionSequence == sequence)
                return Fault("IRQ completion lacks an unused exact original poll sequence.");
            return KernelResult.Ok();
        }
    }
    // Trusted local admission only: capability owner holds its gate while this owner
    // checks the exact delivery lifetime. No provider callbacks run under either gate.
    private KernelResult AdmitIrqEffect(PlatformIrqBinding binding, PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateIrqBinding(binding, subject);
            if (!valid.IsSuccess) return valid;
            var record = _irqBindings[binding.BindingId];
            return record.DeliveryInFlight && !record.ClosureInFlight
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.InvalidTransition, "Exact IRQ delivery admission was lost.");
        }
    }

    internal KernelResult RevokeIrq(PlatformIrqBinding binding, PlatformDomainIdentity expectedSubject)
    {
        IrqBindingRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateIrqBindingIdentity(binding, expectedSubject);
            if (!identity.IsSuccess) return identity;
            record = _irqBindings[binding.BindingId];
            if (record.PlatformClosed)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Exact IRQ binding is closed.");
            if (record.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Exact IRQ binding has unclosed ambiguity.");
            if (record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Exact IRQ closure is already in flight.");
            if (record.RevokeMayHaveEffect && !record.ProviderClosureConfirmed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original IRQ closure remains ambiguous; repeat refused.");
            record.LocalAuthorizationRevoked = true;
            if (record.DeliveryInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Admitted IRQ delivery must settle before closure.");
            record.ClosureInFlight = true;
        }
        try { return RevokeIrqCore(binding, expectedSubject, record); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"IRQ closure lost continuity; exact binding remains pinned: {exception.Message}");
        }
        finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
    }
    private KernelResult RevokeIrqCore(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject, IrqBindingRecord record)
    {
        DeviceLeaseRecord device;
        DomainRecord domain;
        bool knownClosure;
        lock (_secureDomainLifecycleGate)
        {
            var validation = ValidateIrqBindingIdentity(binding, expectedSubject);
            if (!validation.IsSuccess) return validation;
            if (!_irqBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) || !record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original IRQ cleanup owner changed.");
            if (record.PlatformClosed)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "The platform interrupt binding has already been closed.");
            if (record.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformFaulted, "The interrupt binding has ambiguous provider closure and remains pinned.");
            device = _deviceLeases[binding.DeviceLease.LeaseId];
            domain = _domains[binding.DeviceLease.DomainBinding.BindingId];
            if (!ReferenceEquals(device, record.BindDeviceSource) || !ReferenceEquals(domain, record.BindParentSource) ||
                !domain.IrqSources.Contains(record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original IRQ device/parent cleanup source changed before effect.");
            knownClosure = record.ProviderClosureConfirmed;
        }
        if (_provider is not IPlatformIrqBindingProvider irqProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the interrupt binding no longer exposes interrupt closure.");
        }

        if (!knownClosure)
        {
            if (!ValidateDeviceClosureGeneration(device))
            {
                record.FaultPinned = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "The device generation changed before interrupt closure.");
            }
            lock (_secureDomainLifecycleGate)
            {
                var identity = ValidateIrqBindingIdentity(binding, expectedSubject);
                if (!identity.IsSuccess) return identity;
                if (!_irqBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
                    !TryGetOriginalDeviceRecord(binding.DeviceLease.LeaseId, out var parent) || !ReferenceEquals(parent, device) ||
                    !record.ClosureInFlight || record.FaultPinned || record.PlatformClosed || device.FaultPinned || device.PlatformClosed ||
                    BackendEpoch != device.BackendEpoch || _backendEpochExhausted || !HasExactChildCleanupDomainLocked(device, domain) ||
                    !ReferenceEquals(device, record.BindDeviceSource) || !ReferenceEquals(domain, record.BindParentSource) || !domain.IrqSources.Contains(record))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "IRQ cleanup admission changed during source observation; effect refused.");
                record.RevokeParent = domain;
                record.RevokeMayHaveEffect = true;
            }
            PlatformAuthorityResult revoked;
            try { revoked = irqProvider.RevokeInterrupt(record.ProviderBinding); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                FaultIrqDelivery(record, device);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    $"Interrupt closure may have taken effect without a receipt: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate) record.RevokeObservation = revoked;
            if (!revoked.IsSuccess)
            {
                FaultIrqDelivery(record, device);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Interrupt closure lacks exact containment evidence; the original reply and binding remain pinned.");
            }
            // Retain the exact provider reply before any fallible source observation.
            // Retry completes local cleanup without issuing another provider revoke.
            lock (_secureDomainLifecycleGate) record.ProviderClosureConfirmed = true;
        }
        return CompleteKnownIrqClosure(binding, record, device);
    }

    private KernelResult CompleteKnownIrqClosure(PlatformIrqBinding binding,
        IrqBindingRecord record, DeviceLeaseRecord device)
    {
        PlatformProviderIncarnation current;
        try { current = CurrentProviderIncarnation(); } // Provider code outside owner locks.
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!_irqBindings.TryGetValue(binding.BindingId, out var exactChild) || !ReferenceEquals(exactChild, record) ||
                    !TryGetOriginalDeviceRecord(binding.DeviceLease.LeaseId, out var exactParent) || !ReferenceEquals(exactParent, device) ||
                    !HasExactChildCleanupDomainLocked(device, record.RevokeParent) || !ReferenceEquals(device, record.BindDeviceSource) ||
                    !ReferenceEquals(record.RevokeParent, record.BindParentSource) || record.BindParentSource is null || !record.BindParentSource.IrqSources.Contains(record))
                {
                    record.FaultPinned = true;
                    device.FaultPinned = true;
                }
                if (BackendEpoch != device.BackendEpoch || _backendEpochExhausted || device.FaultPinned)
                    record.FaultPinned = true;
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Known IRQ closure reply retained while source observation failed: {exception.Message}");
        }
        KernelResult CommitClose()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (current.Value == 0 || current != device.ProviderIncarnation ||
                    BackendEpoch != device.BackendEpoch || _backendEpochExhausted || device.PlatformClosed ||
                    !_irqBindings.TryGetValue(binding.BindingId, out var exactChild) || !ReferenceEquals(exactChild, record) ||
                    !TryGetOriginalDeviceRecord(binding.DeviceLease.LeaseId, out var exactDevice) || !ReferenceEquals(exactDevice, device) ||
                    !HasExactChildCleanupDomainLocked(device, record.RevokeParent) || !ReferenceEquals(device, record.BindDeviceSource) ||
                    !ReferenceEquals(record.RevokeParent, record.BindParentSource) || record.BindParentSource is null || !record.BindParentSource.IrqSources.Contains(record))
                {
                    device.FaultPinned = true;
                    record.FaultPinned = true;
                }
                if (!record.ProviderClosureConfirmed || record.FaultPinned || device.FaultPinned || !record.ClosureInFlight ||
                    !_irqBindings.TryGetValue(binding.BindingId, out var exact) || !ReferenceEquals(exact, record))
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "IRQ closure reply retained without exact source continuity for local publication.");
                record.PlatformClosed = true;
                record.BindParentSource!.IrqSources.Remove(record);
            }
            return KernelResult.Ok();
        }
        return record.EndpointSource is { } endpointSource ? endpointSource.CommitRelease(record, CommitClose) :
            KernelResult.Fail(KernelError.PlatformFaulted, "Original IRQ endpoint source reservation is absent.");
    }
    internal KernelResult ValidateIrqBinding(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateIrqBindingIdentity(binding, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _irqBindings[binding.BindingId];
        if (record.LocalAuthorizationRevoked)
        {
            return KernelResult.Fail(
                KernelError.CapabilityRevoked,
                "The local capability that authorized this interrupt binding has been revoked.");
        }

        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform interrupt binding has been closed.");
        }

        if (record.FaultPinned)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The interrupt binding is fault-pinned after ambiguous closure.");

        return ValidateDeviceLease(binding.DeviceLease, expectedSubject);
    }

    internal IReadOnlyList<PlatformIrqBinding> BeginIrqCapabilityRevocation(
        CapabilityId capabilityId, Func<CapabilityId, bool> dependsOnRevokedCapability)
    {
        lock (_secureDomainLifecycleGate)
        {
            var affected = _irqBindings.Values
                .Where(record =>
                    !record.PlatformClosed &&
                    dependsOnRevokedCapability(record.AuthorityCapabilityId))
                .OrderBy(record => record.Binding.BindingId.Value)
                .ToArray();

            foreach (var record in affected)
                record.LocalAuthorizationRevoked = true;

            return affected.Select(static record => record.Binding).ToArray();
        }
    }

    // Cleanup selector from the existing owner, including refused-admission receipts.
    // The process routing list contains only published bindings and is not lifetime truth.
    internal IReadOnlyList<PlatformIrqBinding> ActiveIrqBindingsForProcess(PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
            return _irqBindings.Values.Where(record => !record.PlatformClosed &&
                record.Binding.DeviceLease.DomainBinding.Subject == subject)
                .Select(static record => record.Binding).ToArray();
    }
    internal IReadOnlyList<PlatformIrqBinding> ActiveIrqBindingsForDevice(PlatformDeviceLease deviceLease)
    {
        lock (_secureDomainLifecycleGate)
            return _irqBindings.Values.Where(record => !record.PlatformClosed &&
                record.Binding.DeviceLease.LeaseId == deviceLease.LeaseId)
                .OrderBy(record => record.Binding.BindingId.Value).Select(static record => record.Binding).ToArray();
    }

    internal IReadOnlyList<PlatformIrqBinding> ActiveIrqBindingsForEndpoint(KernelEventEndpoint endpoint)
    {
        lock (_secureDomainLifecycleGate)
            return _irqBindings.Values.Where(record => !record.PlatformClosed && record.Binding.EventEndpoint == endpoint)
                .OrderBy(record => record.Binding.BindingId.Value).Select(static record => record.Binding).ToArray();
    }
    private KernelResult ValidateIrqBindingIdentity(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_irqBindings.TryGetValue(binding.BindingId, out var record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform interrupt binding does not exist.");
        }

        if (record.Binding.Generation != binding.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform interrupt binding generation is stale.");
        }

        if (record.Binding != binding)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform interrupt binding identity is malformed.");
        }

        // Structural closure/completion remains possible after local authorization dies.
        return ValidateDeviceLeaseIdentity(binding.DeviceLease, expectedSubject);
    }
}
