using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private static bool HasOriginalIoInvocationPins(DeviceLeaseRecord device, ChildBindingRecord? child = null) =>
        (ulong)device.VirtualIoBindInvocations.Count != device.VirtualIoBindInvocationsIssued ||
        device.VirtualIoBindInvocations.Any(f => !ReferenceEquals(f.OriginalOwner, f) || !ReferenceEquals(f.Device, device) ||
            !f.Consumed && (child is null || ReferenceEquals(f.Child, child)));
    internal KernelResult<ulong> DescribeVirtualIoPrivateBackingBytes(PlatformVirtualIoProfile profile)
    {
        if (_provider is not IPlatformVirtualIoPrivateBackingProvider producer) return KernelResult<ulong>.Ok(0); // V1 carries no additive private allocation contract.
        try
        {
            PlatformAuthorityResult<ulong> raw = producer.DescribeVirtualIoPrivateBackingBytes(profile);
            return raw.IsSuccess ? KernelResult<ulong>.Ok(raw.Value!) : FromProviderFailure<ulong>(raw.Status, raw.Message);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<ulong>.Fail(KernelError.PlatformFaulted, "Private backing requirement unknown before effect."); }
    }
    private sealed record UnpublishedVirtualIoBindReceipt(PlatformChildBinding Child,
        PlatformDeviceLease ParentDevice, PlatformVirtualIoRequest Request,
        PlatformBackendEpoch AdmissionEpoch, PlatformProviderIncarnation ProviderIncarnation,
        PlatformProviderVirtualIoLease Receipt)
    {
        public PlatformVirtualIoClosureReceipt? ReturnedClosureReceipt { get; set; }
    }

    internal KernelResult<PlatformVirtualIoBinding> BindChildVirtualIo(
        PlatformChildBinding child, PlatformDeviceLease parentDevice, PlatformVirtualIoProfile profile)
        => BindChildVirtualIo(child, parentDevice, profile, out _);

    // A known binding is a cleanup correlation only, including when publication fails.
    internal KernelResult<PlatformVirtualIoBinding> BindChildVirtualIo(
        PlatformChildBinding child, PlatformDeviceLease parentDevice, PlatformVirtualIoProfile profile,
        out PlatformVirtualIoBinding? knownBinding,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<PlatformVirtualIoBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        Action<PlatformOriginalVirtualIoBindRequest?>? noEffectConsumer = null,
        Action<PlatformOriginalVirtualIoBindRequest>? originalInvocationConsumer = null)
    {
        bool invoked = false;
        PlatformOriginalVirtualIoBindRequest? originalRequest=null;
        try { return BindChildVirtualIoCore(child, parentDevice, profile, out knownBinding, out invoked, out originalRequest, admissionConsumer, sourceCommitConsumer, completionConsumer, originalInvocationConsumer); }
        finally { if (!invoked) noEffectConsumer?.Invoke(originalRequest); } // Own producing-call boundary, outside owner gates; never provider failure/absence.
    }
    private KernelResult<PlatformVirtualIoBinding> BindChildVirtualIoCore(
        PlatformChildBinding child, PlatformDeviceLease parentDevice, PlatformVirtualIoProfile profile,
        out PlatformVirtualIoBinding? knownBinding, out bool invoked,out PlatformOriginalVirtualIoBindRequest? originalRequest,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer,
        Func<PlatformVirtualIoBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer,
        Action<PlatformOriginalVirtualIoBindRequest>? originalInvocationConsumer)
    {
        invoked = false;
        originalRequest=null;
        knownBinding = null;
        KernelResult<ChildBindingRecord> resolved = ResolveChild(child);
        if (!resolved.IsSuccess) return KernelResult<PlatformVirtualIoBinding>.Fail(resolved.Error, resolved.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.BoundedVirtualIo, PlatformVirtualIoContract.ContractVersion) ||
            _provider is not IPlatformVirtualIoProvider provider)
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformUnsupported, "Bounded virtual-I/O provider is unavailable.");
        if (!TryGetOriginalDeviceRecord(parentDevice.LeaseId, out DeviceLeaseRecord? device) || device.Lease != parentDevice)
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformBindingNotFound, "Exact parent device lease was not found.");
        if (device.LocalAuthorizationRevoked || device.PlatformClosed)
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformBindingRevoked, "Parent device authority is not active.");
        if (device.FaultPinned)
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                "Parent device authority is fault-pinned.");
        if (parentDevice.DomainBinding != child.ParentBinding)
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.WrongPlatformDomain, "Parent device belongs to another domain.");
        ChildBindingRecord childRecord = resolved.Value!;
        PlatformVirtualIoRequest request = new PlatformVirtualIoRequest(childRecord.ProviderLease, device.ProviderLease, profile);
        PlatformAuthorityResult validation = PlatformVirtualIoContract.ValidateRequest(request, childRecord.State);
        if (!validation.IsSuccess) return FromProviderFailure<PlatformVirtualIoBinding>(validation.Status, validation.Message);
        PlatformBackendEpoch backendEpoch = default;
        PlatformVirtualIoBindingId reservedBindingId = default;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
                KernelResult lineage = ValidateActiveChildLineage(childRecord);
            if (!lineage.IsSuccess)
                return lineage;
            if (childRecord.TransitionInFlight ||
                device.ClosureInFlight || device.PendingChildDrains != 0 ||
                !PlatformVirtualIoContract.ValidateRequest(request, childRecord.State).IsSuccess ||
                device.LocalAuthorizationRevoked || device.PlatformClosed || device.FaultPinned)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked,
                    "Child or parent device changed before virtual-I/O admission.");
            KernelResult CommitAdmission()
            {
                if (childRecord.PendingChildEffects == int.MaxValue || device.PendingVirtualIoBinds == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Virtual-I/O admission interlock capacity is exhausted.");
                childRecord.PendingChildEffects++;
                device.PendingVirtualIoBinds++;
                backendEpoch = BackendEpoch;
                return KernelResult.Ok();
            }
            return sourceCommitConsumer is null ? CommitAdmission() : sourceCommitConsumer(CommitAdmission);
        }
        }
        KernelResult admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformVirtualIoBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
        KernelResult ValidateSource(PlatformProviderIncarnation source)
        {
            if (source.Value == 0 || source != device.ProviderIncarnation ||
                BackendEpoch != backendEpoch || device.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !TryGetOriginalDeviceRecord(parentDevice.LeaseId, out DeviceLeaseRecord? exactDevice) || !ReferenceEquals(exactDevice, device) ||
                !_childBindings.TryGetValue(child.BindingId, out ChildBindingRecord? exactChild) || !ReferenceEquals(exactChild, childRecord) ||
                device.Lease != parentDevice || childRecord.Binding != child || device.ProviderLease != request.ParentDeviceLease ||
                childRecord.ProviderLease != request.ChildLease ||
                !TryGetCanonicalRootRecord(child.ParentBinding.BindingId, out DomainRecord? root) || root.Binding != child.ParentBinding ||
                root.ProviderIncarnation != source || root.BackendEpoch != backendEpoch ||
                root.ProviderLease != childRecord.ProviderLease.ParentDomainLease)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original virtual-I/O source changed; dependencies remain pinned.");
            }
                KernelResult lineage = ValidateActiveChildLineage(childRecord);
            if (!lineage.IsSuccess) return lineage;
            if (device.FaultPinned || device.PlatformClosed || device.LocalAuthorizationRevoked || device.ClosureInFlight ||
                device.PendingChildDrains != 0 || childRecord.TransitionInFlight ||
                !PlatformVirtualIoContract.ValidateRequest(request, childRecord.State).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Original virtual-I/O admission changed after source observation.");
            return KernelResult.Ok();
        }
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, "Virtual-I/O source unavailable before invocation."); }
        KernelResult FinalAdmission()
        {
            lock (_secureDomainLifecycleGate)
            {
                    KernelResult exact = ValidateSource(source);
                if (!exact.IsSuccess) return exact;
                if (_nextVirtualIoBindingId == 0 || _nextVirtualIoBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Virtual-I/O identity space is exhausted.");
                KernelResult Commit()
                {
                    reservedBindingId = new(_nextVirtualIoBindingId++);
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
            KernelResult final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
        if (!final.IsSuccess) return KernelResult<PlatformVirtualIoBinding>.Fail(final.Error, final.Message!);
        IPlatformOriginalVirtualIoBindProvider? originalProvider;
        try { originalProvider = provider is IPlatformOriginalVirtualIoBindProvider { SupportsOriginalVirtualIoBindCleanup: true } optional ? optional : null; }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, "Original Bind support unavailable before effect."); }
        VirtualIoBindInvocationFact invocation;
        lock (_secureDomainLifecycleGate)
        {
            if (!TryGetCanonicalRootRecord(child.ParentBinding.BindingId, out DomainRecord? originalRoot))
                return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O root unavailable before invocation.");
            if (device.VirtualIoBindInvocationsIssued == ulong.MaxValue || (ulong)device.VirtualIoBindInvocations.Count != device.VirtualIoBindInvocationsIssued)
                return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, "Original invocation history/capacity unavailable before effect.");
            invocation = new(childRecord, device, originalRoot, request, backendEpoch);
            invocation.OriginalProvider = originalProvider;
            device.VirtualIoBindInvocations.Add(invocation);
            device.VirtualIoBindInvocationsIssued++;
            _virtualIoOwners.EnsureCapacity(_virtualIoOwners.Count + 1);
        }
        if (originalProvider is not null)
        {
            originalRequest=invocation.OriginalRequest; // Before consumer/source callbacks.
            try { originalInvocationConsumer?.Invoke(invocation.OriginalRequest); } // Retain in actual consumer outside owner gates.
            catch { lock (_secureDomainLifecycleGate) invocation.Consumed = true; throw; } // Own never-invoked boundary.
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { lock (_secureDomainLifecycleGate) invocation.Consumed = true; return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, "Original source unavailable before invocation."); }
            lock (_secureDomainLifecycleGate)
            {
                    KernelResult fresh = ValidateSource(source);
                if (!fresh.IsSuccess) { invocation.Consumed = true; return KernelResult<PlatformVirtualIoBinding>.Fail(fresh.Error, fresh.Message!); }
            }
        }
        PlatformAuthorityResult<PlatformProviderVirtualIoLease> result;
        invoked = true; // Unknown/throw after this point cannot consume pre-effect capacity.
        try { result = originalProvider is null ? provider.BindVirtualIo(request) : originalProvider.BindOriginalVirtualIo(invocation.OriginalRequest); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            if (originalProvider is null) { childRecord.State = PlatformChildDomainState.Faulted; device.FaultPinned = true; }
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                $"Virtual-I/O binding may have taken effect without a receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate) invocation.Observation = result;
        if (!result.IsSuccess && (BackendEpoch != backendEpoch ||
            childRecord.State == PlatformChildDomainState.Faulted ||
            device.LocalAuthorizationRevoked || device.PlatformClosed || device.FaultPinned ||
            !ValidateDeviceClosureGeneration(device)))
        {
            childRecord.State = PlatformChildDomainState.Faulted;
            device.FaultPinned = true;
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                "Backend generation changed during virtual-I/O binding; parents remain pinned.");
        }
        if (!result.IsSuccess)
        {
            if (originalProvider is null) { // V1 failure alone does not prove original no-effect; optional invocation has its own pin.
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
            }
            return FromProviderFailure<PlatformVirtualIoBinding>(result.Status, result.Message);
        }
            PlatformAuthorityResult leaseValidation = PlatformVirtualIoContract.ValidateLease(request, result.Value!);
        if (!leaseValidation.IsSuccess)
        {
                UnpublishedVirtualIoBindReceipt fact = new UnpublishedVirtualIoBindReceipt(child, parentDevice, request,
                backendEpoch, device.ProviderIncarnation, result.Value!);
            lock (_secureDomainLifecycleGate)
            {
                device.UnpublishedVirtualIoBinds.Add(fact);
                if (originalProvider is null) { childRecord.State = PlatformChildDomainState.Faulted; device.FaultPinned = true; }
            }
            if (originalProvider is not null) return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                "Malformed returned lease is not original continuity; invocation cleanup retained.");
            try
            {
                    PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> cleanup = provider.RevokeVirtualIo(result.Value!);
                if (cleanup.IsSuccess && PlatformVirtualIoContract.ValidateClosureReceipt(result.Value!, cleanup.Value!).IsSuccess)
                {
                    lock (_secureDomainLifecycleGate) fact.ReturnedClosureReceipt = cleanup.Value;
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Lost cleanup receipt cannot release the parent device.
            }
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                $"{leaseValidation.Message} Original invocation remains pinned independently of returned lease cleanup.");
        }
        PlatformVirtualIoBinding binding;
        VirtualIoBindingRecord bindingRecord;
        lock (_secureDomainLifecycleGate)
        {
            binding = new PlatformVirtualIoBinding(reservedBindingId, new(1), child, parentDevice);
            bindingRecord = new VirtualIoBindingRecord(binding, result.Value!, invocation);
            _virtualIoOwners.Add(bindingRecord);
            _virtualIoBindings.Add(binding.BindingId, bindingRecord);
            knownBinding = binding;
        }
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, "Known virtual-I/O source unavailable; exact cleanup retained."); }
        KernelResult CommitCurrent(Func<KernelResult> commit)
        {
        lock (_secureDomainLifecycleGate)
        {
                    KernelResult sourceValid = ValidateSource(source);
            if (!ReferenceEquals(bindingRecord.Admission, invocation) || invocation.Observation is not { } retained || retained != result ||
                invocation.OriginalRequest.Request != request || bindingRecord.ProviderLease != result.Value)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O Bind observation custody changed; known cleanup retained.");
            if (!IsOriginalVirtualIo(bindingRecord) || !ReferenceEquals(invocation.OriginalOwner, invocation) ||
                !device.VirtualIoBindInvocations.Any(x => ReferenceEquals(x, invocation)) || BackendEpoch != backendEpoch || childRecord.State == PlatformChildDomainState.Faulted ||
                device.PlatformClosed || device.FaultPinned ||
                device.LocalAuthorizationRevoked && sourceCommitConsumer is null)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
                bindingRecord.Closure = PlatformExternalClosureState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Virtual-I/O dependencies changed before local publication.");
            }
            if (!sourceValid.IsSuccess) return sourceValid;
                    KernelResult lineage = ValidateActiveChildLineage(childRecord);
            if (!lineage.IsSuccess)
                return lineage;
            if (device.LocalAuthorizationRevoked || device.ClosureInFlight || device.PendingChildDrains != 0)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Device source revoked before virtual-I/O publication.");
            return sourceCommitConsumer is null ? commit() : sourceCommitConsumer(commit);
        }
        }
            KernelResult completed = completionConsumer is null ? CommitCurrent(KernelResult.Ok) : completionConsumer(binding, CommitCurrent);
        return completed.IsSuccess ? KernelResult<PlatformVirtualIoBinding>.Ok(binding)
            : KernelResult<PlatformVirtualIoBinding>.Fail(completed.Error, completed.Message!);
        }
        finally { lock (_secureDomainLifecycleGate) { childRecord.PendingChildEffects--; device.PendingVirtualIoBinds--; } }
    }

    internal KernelResult RevokeChildVirtualIo(PlatformVirtualIoBinding binding,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        VirtualIoBindingRecord record;
        VirtualIoCloseInvocationFact fact;
        bool invoke;
        bool completed;
        lock (_secureDomainLifecycleGate)
        {
            if (!_virtualIoBindings.TryGetValue(binding.BindingId, out record!) || !IsOriginalVirtualIo(record) || record.Binding != binding)
            {
                VirtualIoBindingRecord[] originals = _virtualIoOwners.Where(x => x.Binding == binding && HasConsumedVirtualIo(x)).ToArray();
                if (originals.Length != 1) return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O owner is unavailable.");
                record = originals[0];
            }
            if (record.ClosureInFlight || record.TransferInFlight) return KernelResult.Fail(KernelError.PlatformBindingActive, "Original Virtual-I/O transfer/closure is in flight.");
            completed = HasConsumedVirtualIo(record);
            if (record.Closure == PlatformExternalClosureState.Closed && !completed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Closed projection is not original Virtual-I/O consumption.");
            invoke = !record.CloseInvoked;
            if (invoke)
            {
                if (record.Closure == PlatformExternalClosureState.Faulted)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O admission remains fault-pinned.");
                record.CloseInvocation = new(record, record.Admission.Epoch);
            }
            if (record.CloseInvocation is not { } original || !ReferenceEquals(original.OriginalOwner, original) ||
                !ReferenceEquals(original.Source, record) || original.AdmittedLease != record.ProviderLease)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O Close fact unavailable; effect cannot repeat.");
            fact = original;
            record.ClosureInFlight = true;
        }
        try
        {
            VirtualIoBindInvocationFact admission = record.Admission;
            bool OriginalFact() => (completed ? HasConsumedVirtualIo(record) && _virtualIoOwners.Any(x => ReferenceEquals(x, record)) : IsOriginalVirtualIo(record)) && ReferenceEquals(record.CloseInvocation, fact) &&
                ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, record) &&
                ReferenceEquals(admission.OriginalOwner, admission) && ReferenceEquals(record.Admission, admission) &&
                admission.Device.VirtualIoBindInvocations.Any(x => ReferenceEquals(x, admission)) &&
                admission.Observation is { IsSuccess: true } bindRaw && bindRaw.Value == record.ProviderLease &&
                fact.Epoch == admission.Epoch && fact.AdmittedLease == record.ProviderLease && fact.Request.Lease == fact.AdmittedLease;
            bool OriginalObservation() => fact.Observation is { } observation && ReferenceEquals(observation.OriginalOwner, observation) &&
                ReferenceEquals(observation, fact.OriginalObservation) && ReferenceEquals(observation.Source, fact);
            bool OriginalClosedObservation() => OriginalObservation() && fact.OriginalClosure is { } closure &&
                ReferenceEquals(record.LastClosureReceipt, closure) && closure.AdmittedLease == fact.AdmittedLease && closure.AdmissionEpoch == fact.Epoch &&
                fact.Observation is { Result.IsSuccess: true } raw && raw.Result.Value == closure.Receipt;
            bool KnownClosed() => fact.ValidatedClosed && OriginalClosedObservation() && fact.Observation is { Result.IsSuccess: true } raw &&
                PlatformVirtualIoContract.ValidateClosureReceipt(fact.AdmittedLease, raw.Result.Value).IsSuccess;
            PlatformProviderIncarnation source = default;
            if (!completed)
            {
                IPlatformOriginalVirtualIoCloseProvider? continuation = fact.ContinuationProvider;
                if (invoke)
                {
                    try { continuation = _provider is IPlatformOriginalVirtualIoCloseProvider { SupportsOriginalVirtualIoClose: true } optional ? optional : null; }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Close support observation unavailable before invocation."); }
                }
                bool reconcile = false;
                try { source = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O closure source unavailable."); }
                lock (_secureDomainLifecycleGate)
                {
                    KernelResult exact = ValidateVirtualIoCloseSourceLocked(record, admission.Child, admission.Device, admission.Root, fact.Epoch, source);
                    if (!exact.IsSuccess || !OriginalFact()) return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O sources changed.");
                    if (!invoke)
                    {
                        if ((fact.OriginalObservation is not null && !OriginalObservation()) || fact.OriginalClosure is not null && !OriginalClosedObservation())
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Close observation custody lost; retained closure cannot be replaced.");
                        if (fact.Observation is { Result.IsSuccess: true } retained)
                        {
                            if (!OriginalClosedObservation() || !PlatformVirtualIoContract.ValidateClosureReceipt(fact.AdmittedLease, retained.Result.Value).IsSuccess)
                                return KernelResult.Fail(KernelError.PlatformFaulted, "Malformed original Close remains pinned.");
                            fact.ValidatedClosed = true;
                        }
                        else if (continuation is not null && ReferenceEquals(continuation, _provider)) reconcile = true;
                        else
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O outcome unknown; effect cannot repeat.");
                    }
                    record.Closure = PlatformExternalClosureState.Draining;
                    if (invoke) { fact.ContinuationProvider = continuation; record.CloseInvoked = true; }
                }
                if (invoke || reconcile)
                {
                    if (_provider is not IPlatformVirtualIoProvider provider)
                        return KernelResult.Fail(KernelError.PlatformUnsupported, "Original Virtual-I/O provider unavailable.");
                    PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> raw;
                    try { raw = continuation is not null
                        ? invoke ? continuation.CloseOriginalVirtualIo(fact.Request) : continuation.ContinueOriginalVirtualIoClose(fact.Request)
                        : provider.RevokeVirtualIo(record.ProviderLease); }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O Close reply lost; repeat refused."); }
                    lock (_secureDomainLifecycleGate)
                    {
                        fact.Observation = fact.OriginalObservation = new(fact, raw);
                        if (raw.IsSuccess) record.LastClosureReceipt = fact.OriginalClosure = new(record.ProviderLease, fact.Epoch, raw.Value!);
                    }
                    try { source = CurrentProviderIncarnation(); }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O Close raw retained; source unavailable."); }
                    lock (_secureDomainLifecycleGate)
                    {
                        KernelResult exact = ValidateVirtualIoCloseSourceLocked(record, admission.Child, admission.Device, admission.Root, fact.Epoch, source);
                        if (!exact.IsSuccess || !OriginalFact() || !raw.IsSuccess || !OriginalClosedObservation() ||
                            fact.OriginalClosure!.Receipt != raw.Value ||
                            !PlatformVirtualIoContract.ValidateClosureReceipt(fact.AdmittedLease, raw.Value!).IsSuccess)
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O Close not proven; pins retained.");
                        fact.ValidatedClosed = true;
                    }
                }
            }
            bool committed = false;
            KernelResult Commit(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalFact() || !KnownClosed() || (!completed &&
                        !ValidateVirtualIoCloseSourceLocked(record, admission.Child, admission.Device, admission.Root, fact.Epoch, source).IsSuccess))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O closure continuity changed before exact consumption.");
                    KernelResult local = commit();
                    if (!local.IsSuccess) return local;
                    record.ClosureConsumed = true;
                    record.Admission.Consumed = true;
                    record.Closure = PlatformExternalClosureState.Closed;
                    committed = true;
                    return KernelResult.Ok();
                }
            }
            KernelResult result = completionConsumer is null ? Commit(KernelResult.Ok) : completionConsumer(Commit);
            return result.IsSuccess && committed ? KernelResult.Ok() : KernelResult.Fail(
                result.IsSuccess ? KernelError.PlatformFaulted : result.Error, result.Message ?? "Original Virtual-I/O local completion not consumed.");
        }
        finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
    }

    private KernelResult ValidateVirtualIoCloseSourceLocked(VirtualIoBindingRecord record, ChildBindingRecord child,
        DeviceLeaseRecord device, DomainRecord root, PlatformBackendEpoch epoch, PlatformProviderIncarnation source)
    {
        PlatformVirtualIoBinding binding = record.Binding;
        if (_backendEpochExhausted || BackendEpoch != epoch || root.BackendEpoch != epoch ||
            source.Value == 0 || source != device.ProviderIncarnation || source != root.ProviderIncarnation ||
            !IsOriginalVirtualIo(record) ||
            !_childBindings.TryGetValue(binding.Child.BindingId, out ChildBindingRecord? exactChild) || !ReferenceEquals(exactChild, child) ||
            !TryGetOriginalDeviceRecord(binding.ParentDevice.LeaseId, out DeviceLeaseRecord? exactDevice) || !ReferenceEquals(exactDevice, device) ||
            !TryGetCanonicalRootRecord(binding.Child.ParentBinding.BindingId, out DomainRecord? exactRoot) || !ReferenceEquals(exactRoot, root) ||
            child.ProviderLease.ParentDomainLease != root.ProviderLease || device.ProviderLease.DomainLease != root.ProviderLease ||
            device.PlatformClosed || device.FaultPinned || device.ClosureInFlight || root.ParentRevokeInFlight || root.ParentRevokeMayHaveEffect ||
            root.AuthorityState == DomainAuthorityState.Closed ||
            child.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed || child.CloseInFlight)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O source/owners changed; pins retained.");
        return ValidateDomainIdentity(binding.Child.ParentBinding, binding.Child.ParentBinding.Subject);
    }

    internal KernelResult CleanupOriginalVirtualIoBind(PlatformOriginalVirtualIoBindRequest request,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult> completionConsumer)
    {
        VirtualIoBindInvocationFact fact;
        lock (_secureDomainLifecycleGate)
        {
            VirtualIoBindInvocationFact[] originals = DeviceDependencyRecordsLocked().SelectMany(d => d.VirtualIoBindInvocations)
                .Where(f => ReferenceEquals(f.OriginalRequest, request)).ToArray();
            if (originals.Length != 1 || !ReferenceEquals(originals[0].OriginalOwner, originals[0]))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original unknown Bind invocation unavailable.");
            fact = originals[0];
            if (fact.CleanupInFlight || fact.Device.PendingVirtualIoBinds != 0 ||
                _virtualIoOwners.Any(r => ReferenceEquals(r.Admission, fact)))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original invocation pending or has a published cleanup owner.");
            fact.CleanupInFlight = true;
        }
        bool Original() => ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.OriginalRequest, request) &&
            request.Request == fact.Request && (ulong)fact.Device.VirtualIoBindInvocations.Count == fact.Device.VirtualIoBindInvocationsIssued &&
            fact.Device.VirtualIoBindInvocations.Any(f => ReferenceEquals(f, fact));
        bool OriginalCleanup() => fact.CleanupRaw is { IsSuccess: true, Value: { NoFurtherUse: true } closure } &&
            ReferenceEquals(closure, fact.OriginalCleanupClosure) && ReferenceEquals(closure.Admission, request);
        bool Current(PlatformProviderIncarnation source) => Original() && !_backendEpochExhausted && BackendEpoch == fact.Epoch &&
            source.Value != 0 && source == fact.Device.ProviderIncarnation && source == fact.Root.ProviderIncarnation &&
            TryGetOriginalDeviceRecord(fact.Device.Lease.LeaseId, out DeviceLeaseRecord? device) && ReferenceEquals(device, fact.Device) &&
            _childBindings.TryGetValue(fact.Child.Binding.BindingId, out ChildBindingRecord? child) && ReferenceEquals(child, fact.Child) &&
            TryGetCanonicalRootRecord(fact.Root.Binding.BindingId, out DomainRecord? root) && ReferenceEquals(root, fact.Root) &&
            fact.Root.BackendEpoch == fact.Epoch && fact.Device.BackendEpoch == fact.Epoch &&
            fact.Child.ProviderLease == fact.Request.ChildLease && fact.Device.ProviderLease == fact.Request.ParentDeviceLease &&
            fact.Request.ChildLease.ParentDomainLease == fact.Root.ProviderLease && fact.Device.ProviderLease.DomainLease == fact.Root.ProviderLease &&
            !fact.Device.FaultPinned && !fact.Device.PlatformClosed && !fact.Device.ClosureInFlight &&
            !fact.Child.CloseInFlight && fact.Child.State is not (PlatformChildDomainState.Closed or PlatformChildDomainState.Faulted) &&
            !fact.Root.ParentRevokeInFlight && !fact.Root.ParentRevokeMayHaveEffect && fact.Root.AuthorityState != DomainAuthorityState.Closed &&
            ValidateDomainIdentity(fact.Root.Binding, fact.Root.Binding.Subject).IsSuccess;
        try
        {
            if (fact.OriginalProvider is not { } provider || !ReferenceEquals(provider, _provider))
                return KernelResult.Fail(KernelError.PlatformUnsupported, "Original invocation cleanup provider unavailable.");
            PlatformProviderIncarnation source = default;
            if (!fact.Consumed)
            {
                try { source = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { return KernelResult.Fail(KernelError.PlatformFaulted, "Unknown Bind source unavailable."); }
                lock (_secureDomainLifecycleGate)
                    if (!Current(source) || fact.OriginalCleanupClosure is not null && !OriginalCleanup())
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Unknown Bind original sources or retained cleanup custody changed.");
                PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>? raw = fact.CleanupRaw;
                if (raw is not { IsSuccess: true })
                {
                    try { raw = provider.CleanupOriginalVirtualIoBind(request); }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind cleanup reply lost; no native repeat."); }
                    lock (_secureDomainLifecycleGate)
                    {
                        fact.CleanupRaw = raw;
                        if (raw is { IsSuccess: true }) fact.OriginalCleanupClosure = raw.Value.Value;
                    }
                }
                PlatformOriginalVirtualIoBindCleanupClosure? resolved = provider.ResolveOriginalVirtualIoBindCleanup(request); // Pure same-original scope, never absence closure.
                try { source = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { return KernelResult.Fail(KernelError.PlatformFaulted, "Retained cleanup source unavailable."); }
                lock (_secureDomainLifecycleGate)
                    if (!Current(source) || !OriginalCleanup() || raw is not { IsSuccess: true, Value: { NoFurtherUse: true } closure } ||
                        !ReferenceEquals(closure, fact.OriginalCleanupClosure) ||
                        !ReferenceEquals(closure.Admission, request) || !ReferenceEquals(closure, resolved))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind containment not proven; pins retained.");
            }
            return completionConsumer(commit =>
            {
                // The local consumer may run callbacks before entering this commit.
                // Refresh lower-owner custody after them, outside all commit locks.
                PlatformOriginalVirtualIoBindCleanupClosure? finalResolved = fact.Consumed ? null : provider.ResolveOriginalVirtualIoBindCleanup(request);
                lock (_secureDomainLifecycleGate)
                {
                    if (!Original() || !fact.Consumed && (!Current(source) || !ReferenceEquals(fact.OriginalCleanupClosure, finalResolved)) || !OriginalCleanup())
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind cleanup continuity changed.");
                    KernelResult local = commit(); if (!local.IsSuccess) return local;
                    fact.Consumed = true; return KernelResult.Ok();
                }
            });
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind cleanup interrupted; facts retained."); }
        finally { lock (_secureDomainLifecycleGate) fact.CleanupInFlight = false; }
    }
}
