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
        public bool FaultPinned { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool DeliveryInFlight { get; set; }
    }

    private readonly Dictionary<PlatformIrqBindingId, IrqBindingRecord> _irqBindings = [];
    private ulong _nextIrqBindingId = 1;

    internal KernelResult<PlatformIrqBinding> BindIrq(
        PlatformDeviceLease deviceLease, PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId, PlatformInterruptSourceIdentity source,
        KernelEventEndpoint eventEndpoint, Func<KernelResult> revalidateAuthorization,
        Func<PlatformIrqBinding, Func<KernelResult>, KernelResult> commitAuthorization)
    {
        DeviceLeaseRecord deviceRecord;
        DomainRecord domainRecord;
        ulong localId;
        lock (_secureDomainLifecycleGate)
        {
            var identity = ValidateDeviceLeaseIdentity(deviceLease, expectedSubject);
            if (!identity.IsSuccess) return KernelResult<PlatformIrqBinding>.Fail(identity.Error, identity.Message!);
            deviceRecord = _deviceLeases[deviceLease.LeaseId];
            if (!_domains.TryGetValue(deviceLease.DomainBinding.BindingId, out domainRecord!))
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformBindingNotFound, "IRQ parent domain is absent.");
            if (deviceRecord.ClosureInFlight || deviceRecord.PendingIrqBinds != 0)
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformBindingActive,
                    "IRQ admission cannot overlap parent device closure or IRQ admission.");
            if (_nextIrqBindingId is 0 or ulong.MaxValue)
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.CapacityExhausted, "IRQ identity space is exhausted.");
            localId = _nextIrqBindingId++;
            deviceRecord.PendingIrqBinds++;
        }
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
            lock (_secureDomainLifecycleGate) deviceRecord.UnresolvedIrqCapabilityId = authorityCapabilityId;
            var providerResult = irqProvider.BindInterrupt(deviceRecord.ProviderLease, source);
            if (!ValidateDeviceClosureGeneration(deviceRecord))
            {
                if (providerResult.IsSuccess && PlatformIrqBindingContract.ValidateBinding(
                    deviceRecord.ProviderLease, source, providerResult.Value!).IsSuccess)
                {
                    var retained = new PlatformIrqBinding(new PlatformIrqBindingId(localId),
                        new PlatformIrqBindingGeneration(1), deviceLease, source, eventEndpoint);
                    lock (_secureDomainLifecycleGate)
                    {
                        _irqBindings.Add(retained.BindingId,
                            new IrqBindingRecord(retained, providerResult.Value!, authorityCapabilityId)
                            { LocalAuthorizationRevoked = true, FaultPinned = true });
                        deviceRecord.UnresolvedIrqCapabilityId = null;
                    }
                }
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted, "IRQ parent generation changed during binding.");
            }
            if (!providerResult.IsSuccess)
            {
                if (providerResult.Status == PlatformAuthorityStatus.NotAccepted)
                {
                    lock (_secureDomainLifecycleGate) deviceRecord.UnresolvedIrqCapabilityId = null;
                }
                else deviceRecord.FaultPinned = true;
                return FromProviderFailure<PlatformIrqBinding>(providerResult.Status, providerResult.Message);
            }
            var providerBinding = providerResult.Value!;
            var validReceipt = PlatformIrqBindingContract.ValidateBinding(deviceRecord.ProviderLease, source, providerBinding);
            if (!validReceipt.IsSuccess)
            {
                var cleaned = false;
                try
                {
                    var cleanup = irqProvider.RevokeInterrupt(providerBinding);
                    cleaned = cleanup.IsSuccess && providerBinding.BindingId.Value != 0 &&
                        providerBinding.Generation.Value != 0 && ValidateDeviceClosureGeneration(deviceRecord);
                }
                catch (Exception exception) when (exception is not StackOverflowException) { }
                if (cleaned) { lock (_secureDomainLifecycleGate) deviceRecord.UnresolvedIrqCapabilityId = null; }
                else deviceRecord.FaultPinned = true;
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted, validReceipt.Message!);
            }
            var binding = new PlatformIrqBinding(new PlatformIrqBindingId(localId),
                new PlatformIrqBindingGeneration(1), deviceLease, source, eventEndpoint);
            var record = new IrqBindingRecord(binding, providerBinding, authorityCapabilityId);
            authorization = revalidateAuthorization();
            if (!authorization.IsSuccess) return RejectKnown(authorization);
            var published = commitAuthorization(binding, () =>
            {
                lock (_secureDomainLifecycleGate)
                {
                    var currentParent = CheckParent();
                    if (!currentParent.IsSuccess) return currentParent;
                    _irqBindings.Add(binding.BindingId, record);
                    deviceRecord.UnresolvedIrqCapabilityId = null;
                    return KernelResult.Ok();
                }
            });
            if (!published.IsSuccess) return RejectKnown(published);
            return KernelResult<PlatformIrqBinding>.Ok(binding);

            KernelResult<PlatformIrqBinding> RejectKnown(KernelResult rejection)
            {
                var cleaned = false;
                try
                {
                    var cleanup = irqProvider.RevokeInterrupt(providerBinding);
                    cleaned = cleanup.IsSuccess && ValidateDeviceClosureGeneration(deviceRecord);
                }
                catch (Exception exception) when (exception is not StackOverflowException) { }
                lock (_secureDomainLifecycleGate)
                {
                    if (!cleaned)
                    {
                        record.LocalAuthorizationRevoked = true;
                        record.FaultPinned = true;
                        deviceRecord.FaultPinned = true;
                        _irqBindings.Add(binding.BindingId, record);
                    }
                    // Known receipt now belongs to the IRQ record; successful cleanup proved exact closure.
                    deviceRecord.UnresolvedIrqCapabilityId = null;
                }
                return Failure(rejection);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            deviceRecord.FaultPinned = true;
            return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted,
                $"IRQ admission continuity was lost; parent remains pinned: {exception.Message}");
        }
        finally { lock (_secureDomainLifecycleGate) deviceRecord.PendingIrqBinds--; }

        KernelResult CheckParent()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!_deviceLeases.TryGetValue(deviceLease.LeaseId, out var currentDevice) ||
                    !ReferenceEquals(currentDevice, deviceRecord) ||
                    !_domains.TryGetValue(deviceLease.DomainBinding.BindingId, out var currentDomain) ||
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

    internal bool HasUnresolvedIrqEffect(Func<CapabilityId, bool> dependsOnRevokedCapability)
    {
        lock (_secureDomainLifecycleGate)
            return _deviceLeases.Values.Any(record => record.UnresolvedIrqCapabilityId is { } id &&
                dependsOnRevokedCapability(id));
    }
    internal KernelResult BeginIrqDelivery(PlatformIrqBinding binding, PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateIrqBinding(binding, subject);
            if (!valid.IsSuccess) return valid;
            var record = _irqBindings[binding.BindingId];
            if (record.DeliveryInFlight || record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Exact IRQ delivery or closure is already in flight.");
            record.DeliveryInFlight = true;
            return KernelResult.Ok();
        }
    }

    internal void EndIrqDelivery(PlatformIrqBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
            var record = _irqBindings[binding.BindingId];
            if (record.Binding != binding || !record.DeliveryInFlight)
                throw new InvalidOperationException("Exact IRQ delivery accounting was lost.");
            record.DeliveryInFlight = false;
        }
    }

    internal void FaultIrqDelivery(PlatformIrqBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
            var record = _irqBindings[binding.BindingId];
            record.FaultPinned = true;
            _deviceLeases[binding.DeviceLease.LeaseId].FaultPinned = true;
        }
    }
    internal KernelResult<PlatformIrqDeliveryEvidence> PollIrq(PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject, Func<CapabilityId, Func<KernelResult>, KernelResult> authorize)
    {
        var valid = ValidateIrqBinding(binding, expectedSubject);
        if (!valid.IsSuccess) return KernelResult<PlatformIrqDeliveryEvidence>.Fail(valid.Error, valid.Message!);
        IrqBindingRecord record;
        DeviceLeaseRecord device;
        lock (_secureDomainLifecycleGate)
        {
            record = _irqBindings[binding.BindingId];
            device = _deviceLeases[binding.DeviceLease.LeaseId];
            if (!record.DeliveryInFlight)
                return KernelResult<PlatformIrqDeliveryEvidence>.Fail(KernelError.InvalidTransition, "IRQ delivery admission is absent.");
        }
        try
        {
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ poll generation continuity was lost.");
            var permission = authorize(record.AuthorityCapabilityId, () => AdmitIrqEffect(binding, expectedSubject));
            if (!permission.IsSuccess) return KernelResult<PlatformIrqDeliveryEvidence>.Fail(permission.Error, permission.Message!);
            valid = ValidateIrqBinding(binding, expectedSubject);
            if (!valid.IsSuccess) return KernelResult<PlatformIrqDeliveryEvidence>.Fail(valid.Error, valid.Message!);
            if (_provider is not IPlatformIrqBindingProvider provider) return Fault("Provider lacks IRQ delivery.");
            var observed = provider.PollInterrupt(record.ProviderBinding);
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ poll generation changed during observation.");
            if (!observed.IsSuccess) return Fault("IRQ observation failed without exact no-effect evidence.");
            var observation = observed.Value!;
            var receipt = PlatformIrqBindingContract.ValidateObservation(record.ProviderBinding, observation);
            if (!receipt.IsSuccess) return Fault(receipt.Message!);
            permission = authorize(record.AuthorityCapabilityId, () => AdmitIrqEffect(binding, expectedSubject));
            if (!permission.IsSuccess)
            {
                FaultIrqDelivery(binding);
                return KernelResult<PlatformIrqDeliveryEvidence>.Fail(permission.Error, permission.Message!);
            }
            valid = ValidateIrqBinding(binding, expectedSubject);
            if (!valid.IsSuccess)
            {
                FaultIrqDelivery(binding);
                return KernelResult<PlatformIrqDeliveryEvidence>.Fail(valid.Error, valid.Message!);
            }
            return KernelResult<PlatformIrqDeliveryEvidence>.Ok(new(observation.DeliveryAvailable, observation.Sequence));
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fault(exception.Message); }
        KernelResult<PlatformIrqDeliveryEvidence> Fault(string message)
        {
            FaultIrqDelivery(binding);
            return KernelResult<PlatformIrqDeliveryEvidence>.Fail(KernelError.PlatformFaulted, message);
        }
    }

    internal KernelResult CompleteIrqDelivery(PlatformIrqBinding binding, PlatformDomainIdentity expectedSubject,
        PlatformProviderInterruptDeliverySequence sequence, Func<CapabilityId, Func<KernelResult>, KernelResult> authorize)
    {
        var identity = ValidateIrqBindingIdentity(binding, expectedSubject);
        if (!identity.IsSuccess) return identity;
        if (sequence.Value == 0) return KernelResult.Fail(KernelError.PlatformFaulted, "IRQ completion sequence is absent.");
        IrqBindingRecord record;
        DeviceLeaseRecord device;
        lock (_secureDomainLifecycleGate)
        {
            record = _irqBindings[binding.BindingId];
            device = _deviceLeases[binding.DeviceLease.LeaseId];
            if (!record.DeliveryInFlight)
                return KernelResult.Fail(KernelError.InvalidTransition, "IRQ delivery admission is absent.");
        }
        try
        {
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ completion generation continuity was lost.");
            var permission = authorize(record.AuthorityCapabilityId, () => AdmitIrqEffect(binding, expectedSubject));
            if (!permission.IsSuccess) { FaultIrqDelivery(binding); return permission; }
            var valid = ValidateIrqBinding(binding, expectedSubject);
            if (!valid.IsSuccess) { FaultIrqDelivery(binding); return valid; }
            if (_provider is not IPlatformIrqBindingProvider provider) return Fault("Provider lacks IRQ completion.");
            var completed = provider.CompleteInterruptDelivery(record.ProviderBinding, sequence);
            if (!ValidateDeviceClosureGeneration(device)) return Fault("IRQ completion generation changed during effect.");
            if (!completed.IsSuccess)
            {
                if (completed.Status != PlatformAuthorityStatus.NotAccepted) FaultIrqDelivery(binding);
                return FromProviderFailure(completed.Status, completed.Message);
            }
            // Completion was admitted before the callback. Exact already-staged publication is owned by the mailbox.
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fault(exception.Message); }
        KernelResult Fault(string message)
        {
            FaultIrqDelivery(binding);
            return KernelResult.Fail(KernelError.PlatformFaulted, message);
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
        var validation = ValidateIrqBindingIdentity(binding, expectedSubject);
        if (!validation.IsSuccess) return validation;


        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform interrupt binding has already been closed.");
        }

        if (record.FaultPinned)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The interrupt binding has ambiguous provider closure and remains pinned.");

        if (_provider is not IPlatformIrqBindingProvider irqProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the interrupt binding no longer exposes interrupt closure.");
        }

        DeviceLeaseRecord device;
        lock (_secureDomainLifecycleGate) device = _deviceLeases[binding.DeviceLease.LeaseId];
        if (!ValidateDeviceClosureGeneration(device))
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device generation changed before interrupt closure.");
        }
        PlatformAuthorityResult revoked;
        try { revoked = irqProvider.RevokeInterrupt(record.ProviderBinding); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Interrupt closure may have taken effect without a receipt: {exception.Message}");
        }
        if (!ValidateDeviceClosureGeneration(device))
        {
            record.FaultPinned = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "The device generation changed during interrupt closure.");
        }
        if (!revoked.IsSuccess)
        {
            if (revoked.Status != PlatformAuthorityStatus.NotAccepted)
            {
                record.FaultPinned = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Interrupt closure lacks exact containment evidence; the binding remains pinned.");
            }

            return FromProviderFailure(revoked.Status, revoked.Message);
        }

        lock (_secureDomainLifecycleGate)
        {
            if (record.FaultPinned || !record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "IRQ closure continuity was lost before local publication.");
            record.PlatformClosed = true;
        }
        return KernelResult.Ok();
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
