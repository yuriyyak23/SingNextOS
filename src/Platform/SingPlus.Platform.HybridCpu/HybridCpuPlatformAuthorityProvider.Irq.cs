using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformIrqBindingProvider
{
    private sealed record IrqBindFailure(PlatformProviderIrqBindingId InvocationId, PlatformProviderDeviceLease Device,
        DomainRecord Parent, NeutralDeviceLease NeutralDevice, PlatformInterruptSourceIdentity Source, NeutralInterruptBindResult? Observation);
    private sealed record IrqCloseSource(DeviceLeaseRecord Device, DomainRecord Parent);
    private sealed record IrqPollFact(IrqCloseSource Source, NeutralInterruptPollResult? Observation);
    private sealed record IrqCompletionFact(IrqCloseSource Source, PlatformProviderInterruptDeliverySequence Sequence,
        NeutralInterruptCompleteResult? Observation);
    private sealed class IrqBindingRecord(
        PlatformProviderIrqBinding binding,
        NeutralInterruptLease hybridCpuLease)
    {
        public PlatformProviderIrqBinding Binding { get; } = binding;
        public NeutralInterruptLease HybridCpuLease { get; } = hybridCpuLease;
        public IrqCloseSource? BindSource { get; init; }
        public bool Revoked { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralInterruptCloseResult? CloseObservation { get; set; }
        public IrqCloseSource? CloseSource { get; set; }
        public bool DeliveryInFlight { get; set; }
        public bool DeliveryUncertain { get; set; }
        public IrqPollFact? PendingPoll { get; set; }
        public IrqCompletionFact? Completion { get; set; }
        public bool CompletionPublished { get; set; }
    }

    private readonly Dictionary<PlatformProviderIrqBindingId, IrqBindingRecord> _irqBindings = [];
    private readonly Dictionary<PlatformProviderIrqBindingId, IrqBindingRecord> _originalIrqBindings = [];
    private ulong _nextProviderIrqBindingId = 1;

    public PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(
        PlatformProviderDeviceLease deviceLease,
        PlatformInterruptSourceIdentity source)
    {
        var request = PlatformIrqBindingContract.ValidateRequest(source);
        if (!request.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(request.Status, request.Message!);
        DeviceLeaseRecord device;
        DomainRecord parent;
        PlatformProviderIrqBindingId reserved;
        NeutralInterruptBindResult? retained;
        var neutralSource = new NeutralInterruptSourceIdentity(source.ResourceId, ToNeutralInterruptTrigger(source.Trigger));
        bool Exact(NeutralInterruptBindResult? result) => result is { IsBound: true, Decision: NeutralInterruptBindDecision.Bound } &&
            result.Lease.Handle.Value != 0 && result.Lease.Epoch.Value != 0 &&
            result.Lease.DeviceLease == device.HybridCpuLease && result.Lease.Source == neutralSource;
        lock (_childCreateCommitGate)
        {
            var validation = ValidateProviderDeviceForInterrupt(deviceLease);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(validation.Status, validation.Message!);
            device = _deviceLeases[deviceLease.LeaseId];
            parent = _domains[deviceLease.DomainLease.LeaseId];
            if (device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null || device.IrqBindInFlight)
                return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original IRQ source is closing, retained or pending; repeat refused.");
            if (device.FailedIrqBind is { } original)
            {
                if (original.Device != deviceLease || !ReferenceEquals(original.Parent, parent) ||
                    original.NeutralDevice != device.HybridCpuLease || original.Source != source || !Exact(original.Observation))
                    return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original IRQ mapping is pinned or belongs to a different invocation; repeat effect refused.");
                reserved = original.InvocationId;
                retained = original.Observation;
            }
            else
            {
                if (_originalIrqBindings.Values.Any(r => !r.Revoked && r.Binding.DeviceLease == deviceLease && r.Binding.Source == source))
                    return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Denied,
                        "Exact interrupt source already has a live binding.");
                if (_nextProviderIrqBindingId is 0 or ulong.MaxValue)
                    return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,
                        "IRQ lease capacity exhausted before neutral effect.");
                reserved = new(_nextProviderIrqBindingId++);
                retained = null;
            }
            _originalIrqBindings.EnsureCapacity(_originalIrqBindings.Count + 1);
            _irqBindings.EnsureCapacity(_originalIrqBindings.Count + 1);
            device.IrqBindInFlight = true;
        }
        void Retain(NeutralInterruptBindResult? observation) => device.FailedIrqBind =
            new(reserved, deviceLease, parent, device.HybridCpuLease, source, observation);
        try
        {
            NeutralInterruptBindResult external;
            try { external = retained ?? _runtime.BindInterrupt(device.HybridCpuLease, neutralSource); }
            catch { lock (_childCreateCommitGate) Retain(null); throw; }
            lock (_childCreateCommitGate)
            {
                if (!Exact(external))
                {
                    if (external is not null && !external.IsBound && external.Lease == default &&
                        external.Decision is NeutralInterruptBindDecision.InvalidSource or NeutralInterruptBindDecision.InsufficientDeviceRights or NeutralInterruptBindDecision.AlreadyBound)
                        return FromNeutralInterruptBindFailure(external);
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original IRQ outcome retained without closure; no returned-object compensation.");
                }
                if (!_deviceLeases.TryGetValue(deviceLease.LeaseId, out var currentDevice) || !ReferenceEquals(currentDevice, device) ||
                    !_domains.TryGetValue(deviceLease.DomainLease.LeaseId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
                    !ValidateProviderDeviceForInterrupt(deviceLease).IsSuccess || device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                {
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original IRQ lease retained without current source publication.");
                }
                Retain(external);
                var lease = new PlatformProviderIrqBinding(reserved, new(1), deviceLease, source);
                var originalRecord = new IrqBindingRecord(lease, external.Lease) { BindSource = new(device, parent) };
                _originalIrqBindings.Add(reserved, originalRecord);
                _irqBindings.Add(reserved, originalRecord);
                device.FailedIrqBind = null;
                return PlatformAuthorityResult<PlatformProviderIrqBinding>.Ok(lease);
            }
        }
        finally { lock (_childCreateCommitGate) device.IrqBindInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformInterruptDeliveryObservation> PollInterrupt(
        PlatformProviderIrqBinding binding)
    {
        IrqBindingRecord record;
        IrqCloseSource source;
        lock (_childCreateCommitGate)
        {
            var validation = ValidateProviderInterruptBinding(binding);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(validation.Status, validation.Message!);
            record = _irqBindings[binding.BindingId];
            if (record.DeliveryInFlight || record.DeliveryUncertain || record.Completion is not null && !record.CompletionPublished)
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original IRQ delivery is pending or retained; poll refused.");
            source = record.PendingPoll?.Source ?? new(_deviceLeases[binding.DeviceLease.LeaseId], _domains[binding.DeviceLease.DomainLease.LeaseId]);
            if (!IsCurrentIrqDeliverySource(record, source))
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ poll source is not current.");
            record.PendingPoll ??= new(source, null);
            record.DeliveryInFlight = true;
        }
        try
        {
            NeutralInterruptPollResult external;
            try { external = record.PendingPoll.Observation ?? _runtime.PollInterrupt(record.HybridCpuLease); }
            catch { lock (_childCreateCommitGate) record.DeliveryUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.PendingPoll = new(source, external);
                var observed = external is { IsObserved: true, DeliveryAvailable: true, Decision: NeutralInterruptPollDecision.Observed } && external.Sequence.Value != 0;
                var noDelivery = external is { IsObserved: false, DeliveryAvailable: false, Decision: NeutralInterruptPollDecision.NoDelivery };
                if (external is null || external.Lease != record.HybridCpuLease || !(observed || noDelivery))
                {
                    record.DeliveryUncertain = true;
                    return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ poll outcome is malformed or unknown.");
                }
                if (!IsCurrentIrqDeliverySource(record, source))
                    return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ poll retained without current source publication.");
                record.PendingPoll = null;
                if (noDelivery) return FromNeutralInterruptPollFailure(external);
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(new(record.Binding, true, new(external.Sequence.Value)));
            }
        }
        finally { lock (_childCreateCommitGate) record.DeliveryInFlight = false; }
    }

    public PlatformAuthorityResult CompleteInterruptDelivery(
        PlatformProviderIrqBinding binding,
        PlatformProviderInterruptDeliverySequence sequence)
    {
        if (sequence.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider interrupt completion requires an exact nonzero delivery sequence.");
        }

        IrqBindingRecord record;
        IrqCloseSource source;
        lock (_childCreateCommitGate)
        {
            var validation = ValidateProviderInterruptBinding(binding);
            if (!validation.IsSuccess) return validation;
            record = _irqBindings[binding.BindingId];
            if (record.DeliveryInFlight || record.DeliveryUncertain || record.PendingPoll is not null)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ delivery is pending or retained; completion refused.");
            var original = record.Completion;
            if (original is not null && original.Sequence != sequence && !record.CompletionPublished)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ completion sequence remains retained.");
            source = original is not null && original.Sequence == sequence ? original.Source :
                new(_deviceLeases[binding.DeviceLease.LeaseId], _domains[binding.DeviceLease.DomainLease.LeaseId]);
            if (!IsCurrentIrqDeliverySource(record, source))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ completion source is not current.");
            if (original is null || original.Sequence != sequence)
            {
                record.Completion = new(source, sequence, null);
                record.CompletionPublished = false;
            }
            record.DeliveryInFlight = true;
        }
        try
        {
            NeutralInterruptCompleteResult external;
            try { external = record.Completion!.Observation ?? _runtime.CompleteInterruptDelivery(record.HybridCpuLease, new(sequence.Value)); }
            catch { lock (_childCreateCommitGate) record.DeliveryUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.Completion = new(source, sequence, external);
                if (external is null || external.Decision is not (NeutralInterruptCompleteDecision.Completed or NeutralInterruptCompleteDecision.NoPendingDelivery or NeutralInterruptCompleteDecision.WrongSequence))
                {
                    record.DeliveryUncertain = true;
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ completion is unknown; repeat effect refused.");
                }
                if (!IsCurrentIrqDeliverySource(record, source))
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ completion retained without current source publication.");
                if (external.Decision == NeutralInterruptCompleteDecision.Completed)
                {
                    record.CompletionPublished = true;
                    return PlatformAuthorityResult.Ok();
                }
                record.Completion = null;
                record.CompletionPublished = false;
                return PlatformAuthorityResult.Fail(external.Decision == NeutralInterruptCompleteDecision.WrongSequence ? PlatformAuthorityStatus.Stale : PlatformAuthorityStatus.Faulted, external.Reason);
            }
        }
        finally { lock (_childCreateCommitGate) record.DeliveryInFlight = false; }
    }

    // Called under the existing local commit gate, including the final publication check.
    private bool IsCurrentIrqDeliverySource(IrqBindingRecord record, IrqCloseSource source) =>
        _irqBindings.TryGetValue(record.Binding.BindingId, out var current) && ReferenceEquals(current, record) &&
        _deviceLeases.TryGetValue(record.Binding.DeviceLease.LeaseId, out var device) && ReferenceEquals(device, source.Device) &&
        _domains.TryGetValue(record.Binding.DeviceLease.DomainLease.LeaseId, out var parent) && ReferenceEquals(parent, source.Parent) &&
        ValidateProviderInterruptBinding(record.Binding).IsSuccess && !device.CloseInFlight && !device.CloseUncertain && device.CloseObservation is null &&
        record.HybridCpuLease.DeviceLease == device.HybridCpuLease && device.HybridCpuLease.DomainLease == parent.HybridCpuLease;

    public PlatformAuthorityResult RevokeInterrupt(PlatformProviderIrqBinding binding)
    {
        IrqBindingRecord record;
        IrqCloseSource source;
        lock (_childCreateCommitGate)
        {
            if (!_irqBindings.TryGetValue(binding.BindingId, out record!))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "The provider IRQ lease does not exist.");
            if (!_originalIrqBindings.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ owner is not current before close.");
            if (record.Binding.Generation != binding.Generation)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "The provider IRQ lease generation is stale.");
            if (record.Binding != binding)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "The provider IRQ lease identity is malformed.");
            if (record.Revoked)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Revoked, "The provider IRQ lease has already been revoked.");
            if (record.CloseInFlight || record.CloseUncertain || record.DeliveryInFlight)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ close is pending or unknown; repeat refused.");
            if (!ValidateProviderDeviceForInterrupt(binding.DeviceLease).IsSuccess)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ device source is not current before close.");
            var device = _deviceLeases[binding.DeviceLease.LeaseId];
            var parent = _domains[binding.DeviceLease.DomainLease.LeaseId];
            if (record.BindSource is not { } admitted || !ReferenceEquals(admitted.Device, device) ||
                !ReferenceEquals(admitted.Parent, parent))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ bind source is not current before close.");
            source = record.CloseSource ?? new(device, parent);
            if (!ReferenceEquals(source.Device, device) || !ReferenceEquals(source.Parent, parent) ||
                device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ close source changed or is closing.");
            record.CloseSource = source;
            record.CloseInFlight = true;
        }
        try
        {
            NeutralInterruptCloseResult external;
            try { external = record.CloseObservation ?? _runtime.CloseInterrupt(record.HybridCpuLease); }
            catch { lock (_childCreateCommitGate) record.CloseUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.CloseObservation = external;
                if (external is null || external.Decision is not (NeutralInterruptCloseDecision.Closed or NeutralInterruptCloseDecision.PendingDelivery))
                {
                    record.CloseUncertain = true;
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Original IRQ close lacks exact Closed outcome; mapping/device remain pinned.");
                }
                if (!_irqBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
                    !_originalIrqBindings.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record) ||
                    !_deviceLeases.TryGetValue(binding.DeviceLease.LeaseId, out var device) || !ReferenceEquals(device, source.Device) ||
                    !_domains.TryGetValue(binding.DeviceLease.DomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, source.Parent) ||
                    !ValidateProviderDeviceForInterrupt(binding.DeviceLease).IsSuccess || source.Device.CloseInFlight ||
                    source.Device.CloseUncertain || source.Device.CloseObservation is not null ||
                    record.HybridCpuLease.DeviceLease != source.Device.HybridCpuLease ||
                    source.Device.HybridCpuLease.DomainLease != source.Parent.HybridCpuLease)
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original IRQ close retained without current owner/source publication.");
                if (external.Decision == NeutralInterruptCloseDecision.PendingDelivery)
                {
                    record.CloseObservation = null;
                    record.CloseSource = null;
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                        external.Reason);
                }
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();
            }
        }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }

    internal bool HasActiveProviderInterruptBindings(PlatformProviderDeviceLease deviceLease) =>
        (_originalDeviceLeases.TryGetValue(deviceLease.LeaseId, out var owner) &&
            (owner.IrqBindInFlight || owner.FailedIrqBind is not null)) ||
        _originalIrqBindings.Values.Any(record =>
            !record.Revoked && record.Binding.DeviceLease == deviceLease);

    private PlatformAuthorityResult ValidateProviderDeviceForInterrupt(
        PlatformProviderDeviceLease deviceLease)
    {
        if (!_deviceLeases.TryGetValue(deviceLease.LeaseId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease does not exist.");
        }

        if (!_originalDeviceLeases.TryGetValue(deviceLease.LeaseId, out var original) || !ReferenceEquals(original, record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device owner is not current before interrupt admission.");

        if (record.Lease.Generation != deviceLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider device lease generation is stale.");
        }

        if (record.Lease != deviceLease)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider device lease identity is malformed.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider device lease has been revoked.");
        }

        if ((deviceLease.Rights & PlatformDeviceRights.Configure) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Provider interrupt binding requires Configure authority on the exact device lease.");
        }

        var domainValidation = ValidateDomain(deviceLease.DomainLease);
        if (!domainValidation.IsSuccess) return domainValidation;
        if (!ReferenceEquals(record.BindParent, _domains[deviceLease.DomainLease.LeaseId]))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device bind parent changed before interrupt admission.");
        return PlatformAuthorityResult.Ok();
    }

    private PlatformAuthorityResult ValidateProviderInterruptBinding(
        PlatformProviderIrqBinding binding)
    {
        lock (_childCreateCommitGate)
        {
            var identity = ValidateProviderInterruptBindingIdentity(binding);
            if (!identity.IsSuccess) return identity;

            var record = _irqBindings[binding.BindingId];
            if (record.Revoked)
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Revoked,
                    "The provider interrupt binding has been revoked.");
            }

            if (record.CloseInFlight || record.CloseUncertain || record.CloseObservation is not null)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ close is pending or retained; delivery effect refused.");
            if (record.BindSource is not { } admitted ||
                !_deviceLeases.TryGetValue(binding.DeviceLease.LeaseId, out var device) || !ReferenceEquals(device, admitted.Device) ||
                !_domains.TryGetValue(binding.DeviceLease.DomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, admitted.Parent))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ bind source is not current before delivery.");
            return ValidateProviderDeviceForInterrupt(binding.DeviceLease);
        }
    }

    private PlatformAuthorityResult ValidateProviderInterruptBindingIdentity(
        PlatformProviderIrqBinding binding)
    {
        if (!_irqBindings.TryGetValue(binding.BindingId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider interrupt binding does not exist.");
        }

        if (!_originalIrqBindings.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ owner is not current before delivery.");

        if (record.Binding.Generation != binding.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider interrupt binding generation is stale.");
        }

        if (record.Binding != binding)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider interrupt binding identity is malformed.");
        }

        return PlatformAuthorityResult.Ok();
    }

    private static NeutralInterruptTrigger ToNeutralInterruptTrigger(
        PlatformInterruptTrigger trigger) =>
        trigger switch
        {
            PlatformInterruptTrigger.Edge => NeutralInterruptTrigger.Edge,
            PlatformInterruptTrigger.Level => NeutralInterruptTrigger.Level,
            _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
        };

    private static PlatformAuthorityResult<PlatformProviderIrqBinding> FromNeutralInterruptBindFailure(
        NeutralInterruptBindResult result)
    {
        var status = result.Decision switch
        {
            NeutralInterruptBindDecision.InvalidSource => PlatformAuthorityStatus.Denied,
            NeutralInterruptBindDecision.InsufficientDeviceRights => PlatformAuthorityStatus.Denied,
            NeutralInterruptBindDecision.AlreadyBound => PlatformAuthorityStatus.Denied,
            NeutralInterruptBindDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralInterruptBindDecision.Stale => PlatformAuthorityStatus.Stale,
            NeutralInterruptBindDecision.NotFound => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };
        return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(status, result.Reason);
    }

    private static PlatformAuthorityResult<PlatformInterruptDeliveryObservation> FromNeutralInterruptPollFailure(
        NeutralInterruptPollResult result)
    {
        var status = result.Decision switch
        {
            NeutralInterruptPollDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralInterruptPollDecision.Stale => PlatformAuthorityStatus.Stale,
            NeutralInterruptPollDecision.NotFound => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };
        return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(status, result.Reason);
    }
}
