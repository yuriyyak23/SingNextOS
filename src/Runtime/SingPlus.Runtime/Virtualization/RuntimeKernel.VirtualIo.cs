using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>Kernel-private identity for a bounded guest-I/O lease; it is never device authority.</summary>
internal readonly record struct VirtualIoBinding(ulong Id, ulong Generation);

public sealed partial class RuntimeKernel
{
    private enum VirtualIoState { Staged, Active, Closing, Quarantined, Closed }
    private sealed class VirtualIoRecord
    {
        private readonly object _gate;
        private VirtualIoState _state = VirtualIoState.Staged;
        internal VirtualIoRecord(VirtualIoBinding binding, ProcessHandle owner, VirtualDomainHandle domain,
            PlatformVirtualIoBinding platform, SecureExecutionBinding? secureExecution, PlatformDeviceRights rights, object gate, BudgetReservationHandle? backingReservation = null)
        { OriginalOwner = this; Binding = binding; Owner = owner; Domain = domain; Platform = platform;
          SecureExecution = secureExecution; Rights = rights; _gate = gate; BackingReservation = backingReservation; BackingChargeExpected = backingReservation is not null; }
        internal VirtualIoRecord OriginalOwner { get; }
        internal VirtualIoBinding Binding { get; }
        internal ProcessHandle Owner { get; }
        internal VirtualDomainHandle Domain { get; }
        internal PlatformVirtualIoBinding Platform { get; set; }
        internal BudgetReservationHandle? BackingReservation { get; set; }
        internal bool BackingChargeExpected { get; }
        internal object? BackingChargeOwner { get; set; }
        internal bool BackingChargeBound { get; set; }
        internal EndpointSessionInvocationHandle? BindSessionInvocation { get; set; }
        internal bool BindSessionBound { get; set; }
        internal bool BindSessionClosureConsumed { get; set; }
        internal bool PlatformKnown { get; set; }
        internal PlatformOriginalVirtualIoBindRequest? OriginalBindRequest { get; set; }
        internal bool OwnNoEffect { get; set; }
        internal SecureExecutionBinding? SecureExecution { get; }
        internal PlatformDeviceRights Rights { get; }
        internal int PendingEvents { get; set; }
        internal List<KernelIoTransferFact> Transfers { get; } = [];
        internal ulong TransfersIssued { get; set; }
        internal bool TransferInFlight { get; set; }
        internal bool CloseInFlight { get; set; }
        internal bool ClosureConsumed { get; set; }
        internal bool ClosureSourceChanged { get; set; }
        internal long SourceRevision { get; private set; }
        internal bool SourceRevisionExhausted { get; private set; }
        internal VirtualIoState State
        {
            get { lock (_gate) return _state; }
            set { lock (_gate) { if (CloseInFlight) ClosureSourceChanged = true; if (SourceRevision == long.MaxValue) SourceRevisionExhausted = true; else SourceRevision++; _state = value; } }
        }
    }

    private readonly object _virtualIoGate = new();
    private readonly Dictionary<ulong, VirtualIoRecord> _virtualIo = [];
    private readonly List<VirtualIoRecord> _virtualIoOwners = [];
    private bool HasOriginalVirtualIo(VirtualIoRecord record) => ReferenceEquals(record.OriginalOwner, record) &&
        _virtualIoOwners.Any(x => ReferenceEquals(x, record)) && _virtualIo.TryGetValue(record.Binding.Id, out var route) && ReferenceEquals(route, record);
    private static bool HasCompletedVirtualIo(VirtualIoRecord record) => ReferenceEquals(record.OriginalOwner, record) && record.ClosureConsumed;
    internal Action? BeforeVirtualIoClosureLocalCommit { get; set; }
    private ulong _nextVirtualIoId = 1;

    internal KernelResult<VirtualIoBinding> BindVirtualIo(ProcessHandle owner, VirtualDomainHandle domain,
        PlatformDeviceLease device, PlatformVirtualIoProfile profile, SecureExecutionBinding? secureExecution = null,
        EndpointSessionInvocationHandle? sessionInvocation=null)
    {
        KernelResult<VirtualIoBinding> Refuse(KernelError error,string message)
        {
            var consumed=sessionInvocation is { } invocation ? SessionInvocations.ConsumeVirtualIoBindNeverInvoked(invocation,owner) : KernelResult.Ok();
            return consumed.IsSuccess ? KernelResult<VirtualIoBinding>.Fail(error,message) : KernelResult<VirtualIoBinding>.Fail(consumed.Error,consumed.Message!);
        }
        var virtualRecord = _virtualDomains.ResolveAdmission(owner, domain);
        if (!virtualRecord.IsSuccess) return Refuse(virtualRecord.Error, virtualRecord.Message!);
        if (virtualRecord.Value!.ChildBinding is not { } child ||
            !virtualRecord.Value.Authority.HasFlag(PlatformChildAuthorityClass.Io))
            return Refuse(KernelError.PlatformUnsupported, "Virtual domain lacks exact child I/O authority.");
        if (secureExecution is { } secure)
        {
            var revalidated = RevalidateSecureExecution(secure);
            if (!revalidated.IsSuccess) return Refuse(revalidated.Error, revalidated.Message!);
            var execution = ResolveSecureGuestExecution(owner, secure);
            if (!execution.IsSuccess || execution.Value!.Request.Context.Virtual != domain)
                return Refuse(KernelError.WrongPlatformDomain, "Secure I/O requires the exact secure execution for this virtual domain.");
        }
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return Refuse(process.Error, process.Message!);
        SecureEffectSource? secureSource = null;
        if (secureExecution is { } secureBinding)
        {
            var captured = CaptureSecureComputeSource(owner, secureBinding);
            if (!captured.IsSuccess) return Refuse(captured.Error, captured.Message!);
            secureSource = captured.Value;
        }
        var source = PlatformAuthority.DeviceEffectSource(device, PlatformIdentity(process.Value!));
        if (!source.IsSuccess) return Refuse(source.Error, source.Message!);
        var backing = PlatformAuthority.DescribeVirtualIoPrivateBackingBytes(profile); // Provider callback before owner/authorization commit gates.
        if (!backing.IsSuccess) return Refuse(backing.Error, backing.Message!);
        VirtualIoRecord? record = null;
        PlatformVirtualIoBinding? known = null;
        VirtualIoRecord Retain(PlatformVirtualIoBinding platform)
        {
            lock (_virtualIoGate)
            {
                if (record is not null)
                {
                    if (record.PlatformKnown && record.Platform != platform) throw new InvalidOperationException("Original I/O platform identity cannot change.");
                    record.Platform = platform; record.PlatformKnown = true; return record;
                }
                throw new InvalidOperationException("Original I/O admission owner must precede provider invocation.");
            }
        }
        var result = UseVirtualDeliverySource(owner, domain, source.Value, virtualRecord.Value!, child,
            ToDeviceCapabilityRights(profile.Rights), device.Device.ResourceId, virtualRecord.Value!.State,
            (fresh, sourceCommit) =>
            {
                KernelResult FreshSecure(Func<KernelResult> commit) => fresh(() => secureSource is null ? commit() : CommitSecureEffectSource(secureSource, commit));
                KernelResult SourceSecure(Func<KernelResult> commit) => sourceCommit(() => secureSource is null ? commit() : CommitSecureEffectSource(secureSource, commit));
                var reserved = FreshSecure(() =>
                {
                    lock (_virtualIoGate)
                    {
                        if (_nextVirtualIoId is 0 or ulong.MaxValue || profile.MaximumDmaTransferBytes <= 0)
                            return KernelResult.Fail(KernelError.CapacityExhausted, "Original I/O backing admission capacity unavailable.");
                        if (_virtualIoOwners.Any(x => x.Owner == owner && x.Domain == domain && !HasCompletedVirtualIo(x) && x.State is VirtualIoState.Staged or VirtualIoState.Quarantined))
                            return KernelResult.Fail(KernelError.PlatformBindingActive, "Original unknown I/O admission must resolve before another reservation/effect.");
                        _virtualIoOwners.EnsureCapacity(_virtualIoOwners.Count + 1); _virtualIo.EnsureCapacity(_virtualIo.Count + 1);
                        var budget = backing.Value == 0 ? KernelResult<BudgetReservationSnapshot?>.Ok(null) :
                            Budgets.ReserveIfAttached(owner, [new(ServiceBudgetDimension.OwnedMemoryBytes, backing.Value)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None);
                        if (!budget.IsSuccess) return KernelResult.Fail(budget.Error, budget.Message!);
                        record = new(new(_nextVirtualIoId++, 1), owner, domain, default, secureExecution, profile.Rights, _virtualIoGate, budget.Value?.Reservation);
                        _virtualIoOwners.Add(record); _virtualIo.Add(record.Binding.Id, record);
                        if (record.BackingReservation is { } reservation)
                        {
                            var charge = Budgets.BindOriginalVirtualIoCharge(owner, reservation, record);
                            if (!charge.IsSuccess) return KernelResult.Fail(charge.Error, charge.Message!);
                            record.BackingChargeOwner = charge.Value;
                        }
                        record.BackingChargeBound = true;
                        if(sessionInvocation is { } invocation)
                        {
                            record.BindSessionInvocation=invocation;
                            var linked=SessionInvocations.BindVirtualIoAdmission(invocation,owner,record);
                            if(!linked.IsSuccess)
                            {
                                record.OwnNoEffect=true; // This caller has not invoked the provider.
                                var released=ConsumeVirtualIoBackingCharge(record);
                                if(!released.IsSuccess)return released;
                                record.ClosureConsumed=true;record.State=VirtualIoState.Closed;
                                return linked;
                            }
                            record.BindSessionBound=true;
                        }
                        return KernelResult.Ok();
                    }
                });
                if (!reserved.IsSuccess) return reserved;
                var bound = PlatformAuthority.BindChildVirtualIo(child, device, profile, out known,
                    FreshSecure, SourceSecure, (platform, commit) =>
                    {
                        var staged = Retain(platform);
                        var stagedRevision = staged.SourceRevision;
                        var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
                        if (!composed.IsSuccess) return composed;
                        var secureCurrent = secureExecution is { } execution
                            ? RevalidateSecureExecution(execution) : KernelResult.Ok();
                        if (!secureCurrent.IsSuccess) return secureCurrent;
                        return FreshSecure(() => commit(() =>
                        {
                            lock (_virtualIoGate)
                            {
                                if (!HasOriginalVirtualIo(staged) || staged.SourceRevisionExhausted || staged.SourceRevision != stagedRevision || staged.State != VirtualIoState.Staged)
                                    return KernelResult.Fail(KernelError.InvalidTransition, "Virtual-I/O staging changed before publication.");
                                staged.State = VirtualIoState.Active;
                                return KernelResult.Ok();
                            }
                        }));
                    }, noEffectConsumer: () =>
                    {
                        lock (_virtualIoGate)
                        {
                            if (record is null || !HasOriginalVirtualIo(record)) return;
                            record.OwnNoEffect = true;
                            if (!ConsumeVirtualIoBackingCharge(record).IsSuccess) return;
                            record.ClosureConsumed = true; record.State = VirtualIoState.Closed;
                        }
                    }, originalInvocationConsumer: request =>
                    {
                        lock (_virtualIoGate)
                        {
                            if (record is null || !HasOriginalVirtualIo(record) || record.OriginalBindRequest is not null)
                                throw new InvalidOperationException("Original kernel invocation retention unavailable.");
                            record.OriginalBindRequest = request;
                        }
                    });
                return bound.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(bound.Error, bound.Message!);
            }, deviceSource: true);
        if (result.IsSuccess) return KernelResult<VirtualIoBinding>.Ok(record!.Binding);
        // This path cannot call BindChildVirtualIo before retaining the local
        // owner. Absence here is our own control-flow fact, not a provider query.
        if(record is null)return Refuse(result.Error,result.Message!);
        if (known is { } cleanup)
        {
            var retained = Retain(cleanup);
            var close = CloseVirtualIo(retained.Binding);
            if (!close.IsSuccess) return KernelResult<VirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                "Virtual-I/O publication refused; exact cleanup remains quarantined.");
        }
        return KernelResult<VirtualIoBinding>.Fail(result.Error, result.Message!);
    }

    internal KernelResult RevalidateVirtualIo(ProcessHandle owner, VirtualIoBinding binding)
        => RevalidateVirtualIo(owner, binding, null);

    internal KernelResult RevalidateVirtualIo(ProcessHandle owner, VirtualIoBinding binding,
        VirtualDomainHandle? expectedDomain)
    {
        VirtualIoRecord record;
        long localRevision;
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || !HasOriginalVirtualIo(record) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual-I/O identity/generation is required.");
            localRevision = record.SourceRevision;
            if (record.SourceRevisionExhausted || record.Owner != owner || record.State != VirtualIoState.Active)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O is not admitting effects.");
            if (expectedDomain is { } exactDomain && record.Domain != exactDomain)
                return KernelResult.Fail(KernelError.WrongPlatformDomain,
                    "Virtual compute I/O must belong to the exact operand domain generation.");
        }
        var domain = _virtualDomains.Resolve(owner, record.Domain);
        var expected = domain.IsSuccess ? domain.Value!.State : default;
        if (!domain.IsSuccess) return KernelResult.Fail(domain.Error, domain.Message!);
        var effectSource = _virtualDomains.CaptureEffectSource(domain.Value!, expected);
        if (!effectSource.IsSuccess) return KernelResult.Fail(effectSource.Error, effectSource.Message!);
        SecureEffectSource? secureSource = null;
        if (record.SecureExecution is { } secureBinding)
        {
            var captured = CaptureSecureComputeSource(owner, secureBinding);
            if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
            secureSource = captured.Value;
        }
        var device = PlatformAuthority.DeviceEffectSource(record.Platform.ParentDevice, record.Platform.ParentDevice.DomainBinding.Subject);
        var secure = record.SecureExecution is { } execution ? RevalidateSecureExecution(execution) : KernelResult.Ok();
        if (domain.IsSuccess && domain.Value!.ChildBinding == record.Platform.Child && device.IsSuccess && secure.IsSuccess)
        {
            lock (_platformMemoryUseGate)
            {
                var process = Processes.Resolve(owner);
                if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                var accepts = EnsureProcessAcceptsNewEffects(process.Value!);
                if (!accepts.IsSuccess) return accepts;
                var current = _virtualDomains.ResolveAdmission(owner, record.Domain);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, domain.Value) || current.Value!.ChildBinding != record.Platform.Child)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Virtual-I/O local child changed.");
                return CapabilityAuthority.CommitDeviceLeaseAdmission(device.Value, process.Value!.DomainId, owner.Generation,
                    ToDeviceCapabilityRights(record.Rights), record.Platform.ParentDevice.Device.ResourceId,
                    () => _virtualDomains.CommitEffectAdmission(current.Value, expected, () =>
                    {
                        lock (_virtualIoGate)
                            return HasOriginalVirtualIo(record) && !record.SourceRevisionExhausted && record.SourceRevision == localRevision && record.State == VirtualIoState.Active && (secureSource is null || CommitSecureEffectSource(secureSource, KernelResult.Ok).IsSuccess)
                                ? KernelResult.Ok()
                                : KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O changed during source revalidation.");
                    }, effectSource.Value!));
            }
        }
        lock (_virtualIoGate) record.State = VirtualIoState.Quarantined;
        return KernelResult.Fail(KernelError.StaleGeneration, "Virtual-I/O child, device, or secure generation changed; binding is quarantined.");
    }

    /// <summary>
    /// Delivers a guest-visible completion event only for the exact active virtual-I/O binding
    /// after the associated external operation has reached kernel-authoritative publication.
    /// </summary>
    internal KernelResult PublishVirtualIoEvent(ProcessHandle owner, VirtualIoBinding binding,
        ExternalOperationHandle operation, CapabilityId eventCapability, KernelEventEndpoint endpoint)
    {
        VirtualIoRecord record;
        long localRevision;
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || !HasOriginalVirtualIo(record) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual-I/O identity/generation is required for event publication.");
            localRevision = record.SourceRevision;
            if (record.SourceRevisionExhausted || record.Owner != owner || record.State != VirtualIoState.Active)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O is not admitting guest-visible events.");
        }

        var exact = RevalidateVirtualIo(owner, binding);
        if (!exact.IsSuccess) return exact;
        var external = QueryExternalOperation(owner, operation);
        if (!external.IsSuccess) return KernelResult.Fail(external.Error, external.Message!);
        if (external.Value!.State != ExternalOperationState.Published)
            return KernelResult.Fail(KernelError.InvalidTransition,
                "Guest-visible virtual-I/O event requires the exact external operation to be Published.");
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var source = PlatformAuthority.DeviceEffectSource(record.Platform.ParentDevice, PlatformIdentity(process.Value!));
        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
        lock (_virtualIoGate)
        {
            if (!HasOriginalVirtualIo(record) || record.SourceRevisionExhausted || record.SourceRevision != localRevision || record.State != VirtualIoState.Active)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O stopped before event admission.");
            if (record.PendingEvents == int.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Virtual-I/O event interlock capacity exhausted.");
            record.PendingEvents++;
        }
        KernelResult CommitDeviceSource(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitDeviceLeaseAdmission(source.Value, process.Value!.DomainId, owner.Generation,
                ToDeviceCapabilityRights(record.Rights), record.Platform.ParentDevice.Device.ResourceId, () =>
                {
                    lock (_virtualIoGate)
                        return HasOriginalVirtualIo(record) && !record.SourceRevisionExhausted && record.SourceRevision == localRevision && record.State == VirtualIoState.Active ? commit()
                            : KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O changed before event publication.");
                });
        try { return InjectVirtualEvent(owner, record.Domain, eventCapability, endpoint, CommitDeviceSource); }
        finally { lock (_virtualIoGate) record.PendingEvents--; }
    }

    internal KernelResult CloseVirtualIo(VirtualIoBinding binding)
    {
        VirtualIoRecord record;
        long revision;
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || !HasOriginalVirtualIo(record) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original Virtual-I/O identity required for closure.");
            if (HasCompletedVirtualIo(record)) return SessionInvocations.ConsumeVirtualIoCloseClosure(record.Owner,record,record.Binding);
            if (record.OwnNoEffect)
            {
                var released = ConsumeVirtualIoBackingCharge(record);
                if (!released.IsSuccess) return released;
                record.ClosureConsumed = true; record.State = VirtualIoState.Closed; return KernelResult.Ok();
            }
            if (record.State == VirtualIoState.Closed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Closed projection is not original Virtual-I/O completion.");
            if (record.PendingEvents != 0 || record.TransferInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Virtual-I/O event admission/publication remains pending.");
            if (record.CloseInFlight || record.State == VirtualIoState.Closing || HasVirtualComputePin(binding))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original Virtual-I/O work/closure remains pending.");
            record.State = VirtualIoState.Closing;
            revision = record.SourceRevision;
            record.ClosureSourceChanged = false;
            record.CloseInFlight = true;
        }
        var close = KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O closure not completed.");
        try
        {
            KernelResult Consume(Func<Func<KernelResult>, KernelResult> bridgeCommit)
            {
                BeforeVirtualIoClosureLocalCommit?.Invoke();
                return bridgeCommit(() =>
                {
                    lock (_virtualIoGate)
                    {
                        if (!HasOriginalVirtualIo(record) || record.State != VirtualIoState.Closing ||
                            record.ClosureSourceChanged || record.SourceRevision != revision)
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original local Virtual-I/O closure source changed.");
                        var released = ConsumeVirtualIoBackingCharge(record);
                        if (!released.IsSuccess) return released;
                        record.ClosureConsumed = true;
                        record.State = VirtualIoState.Closed;
                        return KernelResult.Ok();
                    }
                });
            }
            close = !record.PlatformKnown && record.OriginalBindRequest is { } original
                ? PlatformAuthority.CleanupOriginalVirtualIoBind(original, Consume)
                : PlatformAuthority.RevokeChildVirtualIo(record.Platform, Consume);
            return close;
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O Close interrupted; facts/pins retained."); }
        finally
        {
            lock (_virtualIoGate)
            {
                if (!close.IsSuccess && !record.ClosureConsumed) record.State = VirtualIoState.Quarantined;
                record.CloseInFlight = false;
            }
        }
    }

    private KernelResult CloseVirtualIoForDomain(ProcessHandle owner, VirtualDomainHandle domain) =>
        CloseVirtualIoWhere(x => x.Owner == owner && x.Domain == domain);

    private KernelResult ConsumeVirtualIoBackingCharge(VirtualIoRecord record)
    {
        if (!HasOriginalVirtualIo(record) || !record.BackingChargeBound ||
            record.BackingChargeExpected != (record.BackingReservation is not null) || record.BackingChargeExpected != (record.BackingChargeOwner is not null))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original I/O charge association/route is unknown or changed.");
        if(!HasKernelIoTransferHistory(record))
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original transfer charge history unavailable.");
        foreach(var transfer in record.Transfers)
        {
            if(transfer.Consumed) continue;
            if(!ReferenceEquals(transfer.Io,record) || (transfer.Reservation is not null)!=(transfer.ChargeOwner is not null))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original snapshot charge route changed.");
            // Exact native closure permits local cleanup, but delivered invocation
            // retirement must wait for its independent quantitative obligation.
            KernelResult ConsumeCharge() => transfer.Reservation is { } snapshot
                ? Budgets.ConsumeOriginalVirtualIoCharge(record.Owner,snapshot,transfer,transfer.ChargeOwner) : KernelResult.Ok();
            if(transfer.SessionEffectBound && !transfer.SessionClosureConsumed && transfer.SessionInvocation is { } invocation)
            {
                var contained=SessionInvocations.ConsumeVirtualIoTransferClosure(invocation,record.Owner,transfer,consumeOriginalCharge:ConsumeCharge);
                if(!contained.IsSuccess)return contained;
                transfer.SessionClosureConsumed=true;
            }
            else
            {
                var consumed=ConsumeCharge();
                if(!consumed.IsSuccess)return consumed;
            }
            transfer.Consumed=true;
        }
        var backing=record.BackingReservation is { } reservation
            ? Budgets.ConsumeOriginalVirtualIoCharge(record.Owner, reservation, record, record.BackingChargeOwner) : KernelResult.Ok();
        if(!backing.IsSuccess)return backing;
        if(record.BindSessionBound && !record.BindSessionClosureConsumed && record.BindSessionInvocation is { } bindInvocation)
        {
            var contained=SessionInvocations.ConsumeVirtualIoAdmissionClosure(bindInvocation,record.Owner,record);
            if(!contained.IsSuccess)return contained;
            record.BindSessionClosureConsumed=true;
        }
        return SessionInvocations.ConsumeVirtualIoCloseClosure(record.Owner,record,record.Binding);
    }

    private KernelResult CloseVirtualIoForDevice(ProcessHandle owner, PlatformDeviceLease device) =>
        CloseVirtualIoWhere(x => x.Owner == owner && x.Platform.ParentDevice == device);

    private KernelResult CloseVirtualIoForProcess(ProcessHandle owner) =>
        CloseVirtualIoWhere(x => x.Owner == owner);

    private bool HasVirtualIoForSecureExecution(SecureExecutionBinding binding)
    {
        lock (_virtualIoGate)
            return _virtualIoOwners.Any(x => x.SecureExecution == binding && !HasCompletedVirtualIo(x));
    }

    private KernelResult CloseVirtualIoWhere(Func<VirtualIoRecord, bool> predicate)
    {
        VirtualIoBinding[] bindings;
        lock (_virtualIoGate) bindings = _virtualIoOwners.Where(x => !HasCompletedVirtualIo(x) && predicate(x)).Select(x => x.Binding).ToArray();
        foreach (var binding in bindings) { var close = CloseVirtualIo(binding); if (!close.IsSuccess) return close; }
        return KernelResult.Ok();
    }

    private void QuarantineVirtualIoForBackendReset()
    {
        lock (_virtualIoGate)
            foreach (var record in _virtualIoOwners)
                if (!HasCompletedVirtualIo(record)) record.State = VirtualIoState.Quarantined;
    }
}
