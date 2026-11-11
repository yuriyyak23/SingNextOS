using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformIrqBindingProvider, IPlatformIrqRevocationNoEffectProvider, IPlatformOriginalIrqProvider, IPlatformOriginalIrqContainmentProvider, IPlatformOriginalIrqAdmissionProvider
{
    private sealed record IrqBindFailure(PlatformProviderIrqBindingId InvocationId, PlatformProviderDeviceLease Device,
        DomainRecord Parent, NeutralDeviceLease NeutralDevice, PlatformInterruptSourceIdentity Source, NeutralInterruptBindResult? Observation)
    { public PlatformOriginalIrqBindRequest? OriginalRequest { get; init; } public NeutralOriginalIrqBindRequest? NativeRequest { get; init; } public NeutralOriginalIrqBindObservation? NativeObservation { get; set; } }
    private sealed record IrqCloseSource(DeviceLeaseRecord Device, DomainRecord Parent);
    private sealed record IrqPollFact(IrqCloseSource Source, NeutralInterruptPollResult? Observation)
    {
        public NeutralOriginalIrqPollRequest? Request { get; init; }
        public NeutralOriginalIrqPollObservation? Raw { get; set; }
        public bool Invoked { get; set; }
    }
    private sealed record IrqCompletionFact(IrqCloseSource Source, PlatformProviderInterruptDeliverySequence Sequence,
        NeutralInterruptCompleteResult? Observation)
    {
        public NeutralOriginalIrqCompleteRequest? Request { get; init; }
        public NeutralOriginalIrqCompleteObservation? Raw { get; set; }
        public bool Invoked { get; set; }
    }
    private sealed class IrqBindingRecord(
        PlatformProviderIrqBinding binding,
        NeutralInterruptLease hybridCpuLease)
    {
        public PlatformProviderIrqBinding Binding { get; } = binding;
        public NeutralInterruptLease HybridCpuLease { get; } = hybridCpuLease;
        public IrqCloseSource? BindSource { get; init; }
        public NeutralOriginalIrqBindRequest? NativeBind { get; init; }
        public PlatformOriginalIrqBindRequest? OriginalBindRequest { get; init; }
        public bool CleanupOnly { get; init; }
        public NeutralOriginalIrqCloseRequest? NativeClose { get; set; }
        public NeutralOriginalIrqCloseObservation? NativeCloseObservation { get; set; }
        public bool NativeCloseInvoked { get; set; }
        public NeutralOriginalIrqContainRequest? NativeContainment { get; set; }
        public NeutralOriginalIrqContainObservation? NativeContainmentObservation { get; set; }
        public bool NativeContainmentInvoked { get; set; }
        public bool PendingDeliveryCloseRefused { get; set; }
        public bool Revoked { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralInterruptCloseResult? CloseObservation { get; set; }
        public IrqCloseSource? CloseSource { get; set; }
        public bool DeliveryInFlight { get; set; }
        public bool DeliveryUncertain { get; set; }
        public IrqPollFact? PendingPoll { get; set; }
        public IrqPollFact? PublishedPoll { get; set; }
        public IrqCompletionFact? Completion { get; set; }
        public bool CompletionPublished { get; set; }
    }

    private readonly Dictionary<PlatformProviderIrqBindingId, IrqBindingRecord> _irqBindings = [];
    private readonly Dictionary<PlatformProviderIrqBindingId, IrqBindingRecord> _originalIrqBindings = [];
    private ulong _nextProviderIrqBindingId = 1;

    public PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(
        PlatformProviderDeviceLease deviceLease,
        PlatformInterruptSourceIdentity source) => BindInterruptCore(deviceLease,source,null);
    public PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(PlatformOriginalIrqBindRequest request) => BindInterruptCore(request.Device,request.Source,request);
    private PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterruptCore(
        PlatformProviderDeviceLease deviceLease,PlatformInterruptSourceIdentity source,PlatformOriginalIrqBindRequest? originalRequest)
    {
        PlatformAuthorityResult request = PlatformIrqBindingContract.ValidateRequest(source);
        if (!request.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(request.Status, request.Message!);
        DeviceLeaseRecord device;
        DomainRecord parent;
        PlatformProviderIrqBindingId reserved;
        NeutralInterruptBindResult? retained;
        NeutralOriginalIrqBindRequest? nativeBind;
        NeutralInterruptSourceIdentity neutralSource = new NeutralInterruptSourceIdentity(source.ResourceId, ToNeutralInterruptTrigger(source.Trigger));
        bool Exact(NeutralInterruptBindResult? result) => result is { IsBound: true, Decision: NeutralInterruptBindDecision.Bound } &&
            result.Lease.Handle.Value != 0 && result.Lease.Epoch.Value != 0 &&
            result.Lease.DeviceLease == device.HybridCpuLease && result.Lease.Source == neutralSource;
        lock (_childCreateCommitGate)
        {
            PlatformAuthorityResult validation = ValidateProviderDeviceForInterrupt(deviceLease);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(validation.Status, validation.Message!);
            device = _deviceLeases[deviceLease.LeaseId];
            parent = _domains[deviceLease.DomainLease.LeaseId];
            if (device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null || device.IrqBindInFlight)
                return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original IRQ source is closing, retained or pending; repeat refused.");
            if (device.FailedIrqBind is { } original)
            {
                if (originalRequest is not null || original.Device != deviceLease || !ReferenceEquals(original.Parent, parent) ||
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
            nativeBind = device.FailedIrqBind?.NativeRequest;
            if (nativeBind is null && _runtime is INeutralOriginalIrqProvider && device.AdmissionSource?.NativeRequest is { } factory && _originalNativeMappingIncarnation is { } realm)
                nativeBind = new(Guid.NewGuid(),realm,factory.InvocationId,device.HybridCpuLease,neutralSource);
            device.IrqBindInFlight = true;
            device.FailedIrqBind ??= new(reserved,deviceLease,parent,device.HybridCpuLease,source,null) { NativeRequest = nativeBind,OriginalRequest = originalRequest };
        }
        void Retain(NeutralInterruptBindResult? observation) => device.FailedIrqBind =
            new(reserved, deviceLease, parent, device.HybridCpuLease, source, observation) { NativeRequest = nativeBind,OriginalRequest = originalRequest, NativeObservation = device.FailedIrqBind?.NativeObservation };
        try
        {
            NeutralInterruptBindResult external;
            try
            {
                if (retained is not null) external = retained;
                else if (nativeBind is { } nativeRequest && _runtime is INeutralOriginalIrqProvider producing)
                {
                    NeutralOriginalIrqBindObservation raw = producing.BindInterrupt(nativeRequest);
                    lock (_childCreateCommitGate) device.FailedIrqBind!.NativeObservation = raw;
                    if (raw.Request != nativeRequest || raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed || raw.NoEffectGuaranteed && (raw.Observation.IsBound || raw.Observation.Lease!=default || raw.Observation.Decision==NeutralInterruptBindDecision.Bound))
                        return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,"Original IRQ Bind raw malformed/foreign.");
                    if (raw.NoEffectGuaranteed) { lock (_childCreateCommitGate) { Retain(raw.Observation); if (originalRequest is null) device.FailedIrqBind = null; } return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.NotAccepted,"Exact original IRQ Bind no effect."); }
                    external = raw.Observation;
                    lock (_childCreateCommitGate) Retain(external);
                    PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
                    if (!fresh.IsSuccess || !producing.ValidateIrqAdmission(nativeRequest)) return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,"Original IRQ Bind retained without current native source.");
                }
                else external = _runtime.BindInterrupt(device.HybridCpuLease, neutralSource);
            }
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
                if (!_deviceLeases.TryGetValue(deviceLease.LeaseId, out DeviceLeaseRecord? currentDevice) || !ReferenceEquals(currentDevice, device) ||
                    !_domains.TryGetValue(deviceLease.DomainLease.LeaseId, out DomainRecord? currentParent) || !ReferenceEquals(currentParent, parent) ||
                    !ValidateProviderDeviceForInterrupt(deviceLease).IsSuccess || device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                {
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original IRQ lease retained without current source publication.");
                }
                Retain(external);
                PlatformProviderIrqBinding lease = new PlatformProviderIrqBinding(reserved, new(1), deviceLease, source);
                IrqBindingRecord originalRecord = new IrqBindingRecord(lease, external.Lease) { BindSource = new(device, parent), NativeBind = nativeBind,OriginalBindRequest = originalRequest };
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
            PlatformAuthorityResult validation = ValidateProviderInterruptBinding(binding);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(validation.Status, validation.Message!);
            record = _irqBindings[binding.BindingId];
            if (record.DeliveryInFlight || record.DeliveryUncertain && record.PendingPoll?.Request is null || record.Completion is not null && !record.CompletionPublished)
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original IRQ delivery is pending or retained; poll refused.");
            source = record.PendingPoll?.Source ?? new(_deviceLeases[binding.DeviceLease.LeaseId], _domains[binding.DeviceLease.DomainLease.LeaseId]);
            if (!IsCurrentIrqDeliverySource(record, source))
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ poll source is not current.");
            record.PendingPoll ??= new(source, null) { Request = record.NativeBind is { } admission && _runtime is INeutralOriginalIrqProvider ? new(Guid.NewGuid(), admission, record.HybridCpuLease) : null };
            record.DeliveryInFlight = true;
        }
        try
        {
            NeutralInterruptPollResult external;
            try
            {
                IrqPollFact fact = record.PendingPoll!;
                if (fact.Request is { } request && _runtime is INeutralOriginalIrqProvider producing)
                {
                    bool invoke;
                    lock (_childCreateCommitGate) { invoke = !fact.Invoked; fact.Invoked = true; }
                    NeutralOriginalIrqPollObservation? raw = fact.Raw ?? (invoke ? producing.PollInterrupt(request) : producing.ResolveIrqPoll(request));
                    lock (_childCreateCommitGate) fact.Raw = raw;
                    if (raw is not { } value || value.Request != request || value.Observation is null || !Enum.IsDefined(value.Observation.Decision) ||
                        !value.SourceFenceMatched && !value.NoEffectGuaranteed || value.NoEffectGuaranteed && value.Observation.DeliveryAvailable)
                    { lock (_childCreateCommitGate) record.DeliveryUncertain = true; return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ Poll raw missing/foreign; pure reconciliation only."); }
                    if (value.NoEffectGuaranteed)
                    { lock (_childCreateCommitGate) { record.PendingPoll = null; record.DeliveryUncertain = false; } return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact IRQ Poll no effect, not closure."); }
                    external = value.Observation;
                    lock (_childCreateCommitGate) record.PendingPoll = fact with { Observation = external };
                    if (!ValidateOriginalNativeMappingSource().IsSuccess || !producing.ValidateIrqPoll(request))
                        return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ Poll retained without fresh native source/revision.");
                }
                else external = fact.Observation ?? _runtime.PollInterrupt(record.HybridCpuLease);
            }
            catch { lock (_childCreateCommitGate) record.DeliveryUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.PendingPoll = record.PendingPoll! with { Observation = external };
                bool observed = external is { IsObserved: true, DeliveryAvailable: true, Decision: NeutralInterruptPollDecision.Observed } && external.Sequence.Value != 0;
                bool noDelivery = external is { IsObserved: false, DeliveryAvailable: false, Decision: NeutralInterruptPollDecision.NoDelivery };
                if (external is null || external.Lease != record.HybridCpuLease || !(observed || noDelivery))
                {
                    record.DeliveryUncertain = true;
                    return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ poll outcome is malformed or unknown.");
                }
                if (!IsCurrentIrqDeliverySource(record, source))
                    return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ poll retained without current source publication.");
                record.PublishedPoll = record.PendingPoll;
                record.PendingPoll = null;
                record.DeliveryUncertain = false;
                if (noDelivery) return record.NativeBind is not null ? PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(new(record.Binding,false,default)) : FromNeutralInterruptPollFailure(external);
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
            PlatformAuthorityResult validation = ValidateProviderInterruptBinding(binding);
            if (!validation.IsSuccess) return validation;
            record = _irqBindings[binding.BindingId];
            if (record.DeliveryInFlight || record.DeliveryUncertain && record.Completion?.Request is null || record.PendingPoll is not null)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ delivery is pending or retained; completion refused.");
            IrqCompletionFact? original = record.Completion;
            if (original is not null && original.Sequence != sequence && !record.CompletionPublished)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ completion sequence remains retained.");
            source = original is not null && original.Sequence == sequence ? original.Source :
                new(_deviceLeases[binding.DeviceLease.LeaseId], _domains[binding.DeviceLease.DomainLease.LeaseId]);
            if (!IsCurrentIrqDeliverySource(record, source))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ completion source is not current.");
            if (original is null || original.Sequence != sequence)
            {
                if (record.NativeBind is not null && (record.PublishedPoll?.Request is null || record.PublishedPoll.Observation?.Sequence.Value != sequence.Value))
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Native completion requires the exact published original Poll.");
                record.Completion = new(source, sequence, null) { Request = record.NativeBind is { } admission && record.PublishedPoll?.Request is { } poll ?
                    new(Guid.NewGuid(), admission, record.HybridCpuLease, new(sequence.Value), poll.InvocationId) : null };
                record.CompletionPublished = false;
            }
            record.DeliveryInFlight = true;
        }
        try
        {
            NeutralInterruptCompleteResult external;
            try
            {
                IrqCompletionFact fact = record.Completion!;
                if (fact.Request is { } request && _runtime is INeutralOriginalIrqProvider producing)
                {
                    bool invoke;
                    lock (_childCreateCommitGate) { invoke = !fact.Invoked; fact.Invoked = true; }
                    NeutralOriginalIrqCompleteObservation? raw = fact.Raw ?? (invoke ? producing.CompleteInterruptDelivery(request) : producing.ResolveIrqCompletion(request));
                    lock (_childCreateCommitGate) fact.Raw = raw;
                    if (raw is not { } value || value.Request != request || value.Observation is null || !Enum.IsDefined(value.Observation.Decision) ||
                        !value.SourceFenceMatched && !value.NoEffectGuaranteed || value.NoEffectGuaranteed && value.Observation.Decision == NeutralInterruptCompleteDecision.Completed)
                    { lock (_childCreateCommitGate) record.DeliveryUncertain = true; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ Complete raw missing/foreign; pure reconciliation only."); }
                    if (value.NoEffectGuaranteed)
                    { lock (_childCreateCommitGate) { record.Completion = null; record.DeliveryUncertain = false; } return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Exact IRQ Complete no effect, not closure."); }
                    external = value.Observation;
                    lock (_childCreateCommitGate) record.Completion = fact with { Observation = external };
                    if (!ValidateOriginalNativeMappingSource().IsSuccess || !producing.ValidateIrqCompletion(request))
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ Complete retained without current native source.");
                }
                else external = fact.Observation ?? _runtime.CompleteInterruptDelivery(record.HybridCpuLease, new(sequence.Value));
            }
            catch { lock (_childCreateCommitGate) record.DeliveryUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.Completion = record.Completion! with { Observation = external };
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
                    record.DeliveryUncertain = false;
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
        _irqBindings.TryGetValue(record.Binding.BindingId, out IrqBindingRecord? current) && ReferenceEquals(current, record) &&
        _deviceLeases.TryGetValue(record.Binding.DeviceLease.LeaseId, out DeviceLeaseRecord? device) && ReferenceEquals(device, source.Device) &&
        _domains.TryGetValue(record.Binding.DeviceLease.DomainLease.LeaseId, out DomainRecord? parent) && ReferenceEquals(parent, source.Parent) &&
        ValidateProviderInterruptBinding(record.Binding).IsSuccess && !device.CloseInFlight && !device.CloseUncertain && device.CloseObservation is null &&
        record.HybridCpuLease.DeviceLease == device.HybridCpuLease && device.HybridCpuLease.DomainLease == parent.HybridCpuLease;

    public PlatformAuthorityResult RevokeInterrupt(PlatformProviderIrqBinding binding)
    {
        bool containing;
        lock(_childCreateCommitGate) containing=_irqBindings.TryGetValue(binding.BindingId,out IrqBindingRecord? prior) &&
            prior.Binding==binding && (prior.PendingDeliveryCloseRefused || prior.NativeContainment is not null);
        if(containing)return ContainIrqDelivery(binding);
        PlatformAuthorityResult closed =RevokeInterruptCore(binding,false);
        lock(_childCreateCommitGate) containing=closed.Status==PlatformAuthorityStatus.NotAccepted &&
            _irqBindings.TryGetValue(binding.BindingId,out IrqBindingRecord? original) && original.Binding==binding && original.PendingDeliveryCloseRefused;
        return containing ? ContainIrqDelivery(binding) : closed;
    }
    public PlatformAuthorityResult ContainIrqDelivery(PlatformProviderIrqBinding binding)
    {
        IrqBindingRecord record;
        IrqCloseSource source;
        NeutralOriginalIrqContainRequest request;
        bool invoke;
        lock (_childCreateCommitGate)
        {
            PlatformAuthorityResult identity = ValidateProviderInterruptBindingIdentity(binding);
            if (!identity.IsSuccess) return identity;
            record = _irqBindings[binding.BindingId];
            if (record.Revoked && record.NativeContainmentObservation is { NoFurtherDeliveryGuaranteed: true }) return PlatformAuthorityResult.Ok();
            if (_runtime is not INeutralOriginalIrqProvider || record.NativeBind is not { } admission || record.CloseInFlight || record.DeliveryInFlight ||
                !ValidateProviderDeviceForInterrupt(binding.DeviceLease).IsSuccess || record.BindSource is not { } original ||
                !ReferenceEquals(original.Device,_deviceLeases[binding.DeviceLease.LeaseId]) || !ReferenceEquals(original.Parent,_domains[binding.DeviceLease.DomainLease.LeaseId]) ||
                original.Device.CloseInFlight || original.Device.CloseUncertain || original.Device.CloseObservation is not null)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Exact native IRQ containment source unsupported, lost or busy.");
            source = original;
            record.NativeContainment ??= new(Guid.NewGuid(),admission,record.HybridCpuLease);
            request = record.NativeContainment.Value;
            invoke = !record.NativeContainmentInvoked; record.NativeContainmentInvoked = true;
            record.CloseInFlight = true;
        }
        try
        {
            INeutralOriginalIrqProvider producing = (INeutralOriginalIrqProvider)_runtime;
            NeutralOriginalIrqContainObservation? raw = record.NativeContainmentObservation ?? (invoke ? producing.ContainInterrupt(request) : producing.ResolveIrqContainment(request));
            lock (_childCreateCommitGate) record.NativeContainmentObservation = raw;
            if (raw is not { NoFurtherDeliveryGuaranteed: true } value || value.Request != request)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Exact native IRQ containment missing/foreign; pure retry only.");
            if (!ValidateOriginalNativeMappingSource().IsSuccess || producing.ResolveIrqContainment(request) != raw)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original IRQ containment retained without current native factory.");
            lock (_childCreateCommitGate)
            {
                if (!_irqBindings.TryGetValue(binding.BindingId,out IrqBindingRecord? route) || !ReferenceEquals(route,record) ||
                    !_originalIrqBindings.TryGetValue(binding.BindingId,out IrqBindingRecord? owner) || !ReferenceEquals(owner,record) ||
                    !_deviceLeases.TryGetValue(binding.DeviceLease.LeaseId,out DeviceLeaseRecord? device) || !ReferenceEquals(device,source.Device) ||
                    !_domains.TryGetValue(binding.DeviceLease.DomainLease.LeaseId,out DomainRecord? parent) || !ReferenceEquals(parent,source.Parent) ||
                    !ValidateProviderDeviceForInterrupt(binding.DeviceLease).IsSuccess || device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original IRQ containment retained without local owner continuity.");
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();
            }
        }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }
    public PlatformAuthorityResult ResolveIrqRevocation(PlatformProviderIrqBinding binding)
    {
        bool containing;
        lock(_childCreateCommitGate) containing=_irqBindings.TryGetValue(binding.BindingId,out IrqBindingRecord? original) &&
            original.Binding==binding && original.NativeContainment is not null;
        return containing ? ContainIrqDelivery(binding) : RevokeInterruptCore(binding,true);
    }
    private PlatformAuthorityResult RevokeInterruptCore(PlatformProviderIrqBinding binding,bool lookup)
    {
        IrqBindingRecord record;
        IrqCloseSource source;
        lock (_childCreateCommitGate)
        {
            if (!_irqBindings.TryGetValue(binding.BindingId, out record!))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "The provider IRQ lease does not exist.");
            if (!_originalIrqBindings.TryGetValue(binding.BindingId, out IrqBindingRecord? original) || !ReferenceEquals(original, record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ owner is not current before close.");
            if (record.Binding.Generation != binding.Generation)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "The provider IRQ lease generation is stale.");
            if (record.Binding != binding)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "The provider IRQ lease identity is malformed.");
            if (lookup && (record.NativeClose is null || !record.NativeCloseInvoked)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Pure IRQ lookup cannot initiate close.");
            if (record.Revoked)
                return lookup && record.NativeCloseObservation is { SourceFenceMatched: true,NoEffectGuaranteed: false } closed && closed.Observation.Decision == NeutralInterruptCloseDecision.Closed ? PlatformAuthorityResult.Ok() : PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Revoked, "The provider IRQ lease has already been revoked.");
            if (record.CloseInFlight || record.CloseUncertain && record.NativeClose is null || record.DeliveryInFlight)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ close is pending or unknown; repeat refused.");
            if (!ValidateProviderDeviceForInterrupt(binding.DeviceLease).IsSuccess)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ device source is not current before close.");
            DeviceLeaseRecord device = _deviceLeases[binding.DeviceLease.LeaseId];
            DomainRecord parent = _domains[binding.DeviceLease.DomainLease.LeaseId];
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
            try
            {
                if (record.NativeBind is { } nativeBind && _runtime is INeutralOriginalIrqProvider producing)
                {
                    NeutralOriginalIrqCloseRequest request; bool invoke;
                    lock (_childCreateCommitGate)
                    {
                        record.NativeClose ??= new(Guid.NewGuid(),nativeBind.RuntimeIncarnation,nativeBind.InvocationId,record.HybridCpuLease);
                        request = record.NativeClose.Value; invoke = !record.NativeCloseInvoked; record.NativeCloseInvoked = true;
                    }
                    NeutralOriginalIrqCloseObservation? raw = record.NativeCloseObservation ?? (invoke ? producing.CloseInterrupt(request) : producing.ResolveIrqClose(request));
                    lock (_childCreateCommitGate) record.NativeCloseObservation = raw;
                    if (raw is not { } value || value.Request != request || value.Observation is null || !Enum.IsDefined(value.Observation.Decision) || !value.SourceFenceMatched && !value.NoEffectGuaranteed || value.NoEffectGuaranteed && value.Observation.Decision == NeutralInterruptCloseDecision.Closed)
                    { lock (_childCreateCommitGate) record.CloseUncertain = true; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original IRQ Close raw missing/foreign; no repeat."); }
                    if (value.NoEffectGuaranteed)
                    {
                        lock (_childCreateCommitGate) { record.PendingDeliveryCloseRefused=value.SourceFenceMatched && value.Observation.Decision==NeutralInterruptCloseDecision.PendingDelivery;
                            record.NativeClose = null; record.NativeCloseObservation = null; record.NativeCloseInvoked = false; record.CloseUncertain = false; record.CloseSource = null; }
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Exact IRQ Close no effect; no closure.");
                    }
                    external = value.Observation;
                    lock (_childCreateCommitGate) record.CloseObservation = external;
                    PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
                    if (!fresh.IsSuccess || producing.ResolveIrqClose(request) != record.NativeCloseObservation)
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original IRQ Closed retained without current native factory.");
                }
                else { if (lookup) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Pure IRQ closure lookup unsupported."); external = record.CloseObservation ?? _runtime.CloseInterrupt(record.HybridCpuLease); }
            }
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
                if (!_irqBindings.TryGetValue(binding.BindingId, out IrqBindingRecord? current) || !ReferenceEquals(current, record) ||
                    !_originalIrqBindings.TryGetValue(binding.BindingId, out IrqBindingRecord? original) || !ReferenceEquals(original, record) ||
                    !_deviceLeases.TryGetValue(binding.DeviceLease.LeaseId, out DeviceLeaseRecord? device) || !ReferenceEquals(device, source.Device) ||
                    !_domains.TryGetValue(binding.DeviceLease.DomainLease.LeaseId, out DomainRecord? parent) || !ReferenceEquals(parent, source.Parent) ||
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
                record.Revoked = true; record.CloseUncertain = false;
                return PlatformAuthorityResult.Ok();
            }
        }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }

    internal bool HasActiveProviderInterruptBindings(PlatformProviderDeviceLease deviceLease) =>
        (_originalDeviceLeases.TryGetValue(deviceLease.LeaseId, out DeviceLeaseRecord? owner) &&
            (owner.IrqBindInFlight || owner.FailedIrqBind is not null)) ||
        _originalIrqBindings.Values.Any(record =>
            !record.Revoked && record.Binding.DeviceLease == deviceLease);

    private PlatformAuthorityResult ValidateProviderDeviceForInterrupt(
        PlatformProviderDeviceLease deviceLease)
    {
        if (!_deviceLeases.TryGetValue(deviceLease.LeaseId, out DeviceLeaseRecord? record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease does not exist.");
        }

        if (!_originalDeviceLeases.TryGetValue(deviceLease.LeaseId, out DeviceLeaseRecord? original) || !ReferenceEquals(original, record))
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

        PlatformAuthorityResult domainValidation = ValidateDomain(deviceLease.DomainLease);
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
            PlatformAuthorityResult identity = ValidateProviderInterruptBindingIdentity(binding);
            if (!identity.IsSuccess) return identity;

            IrqBindingRecord record = _irqBindings[binding.BindingId];
            if (record.Revoked)
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Revoked,
                    "The provider interrupt binding has been revoked.");
            }

            if (record.CleanupOnly || record.CloseInFlight || record.CloseUncertain || record.CloseObservation is not null || record.NativeContainmentInvoked)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ close is pending or retained; delivery effect refused.");
            if (record.BindSource is not { } admitted ||
                !_deviceLeases.TryGetValue(binding.DeviceLease.LeaseId, out DeviceLeaseRecord? device) || !ReferenceEquals(device, admitted.Device) ||
                !_domains.TryGetValue(binding.DeviceLease.DomainLease.LeaseId, out DomainRecord? parent) || !ReferenceEquals(parent, admitted.Parent))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original IRQ bind source is not current before delivery.");
            return ValidateProviderDeviceForInterrupt(binding.DeviceLease);
        }
    }

    private PlatformAuthorityResult ValidateProviderInterruptBindingIdentity(
        PlatformProviderIrqBinding binding)
    {
        if (!_irqBindings.TryGetValue(binding.BindingId, out IrqBindingRecord? record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider interrupt binding does not exist.");
        }

        if (!_originalIrqBindings.TryGetValue(binding.BindingId, out IrqBindingRecord? original) || !ReferenceEquals(original, record))
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
        PlatformAuthorityStatus status = result.Decision switch
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
        PlatformAuthorityStatus status = result.Decision switch
        {
            NeutralInterruptPollDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralInterruptPollDecision.Stale => PlatformAuthorityStatus.Stale,
            NeutralInterruptPollDecision.NotFound => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };
        return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(status, result.Reason);
    }
}
