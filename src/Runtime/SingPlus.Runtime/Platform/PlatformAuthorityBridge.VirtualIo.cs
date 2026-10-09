using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
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
        Func<PlatformVirtualIoBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        knownBinding = null;
        var resolved = ResolveChild(child);
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
        var request = new PlatformVirtualIoRequest(childRecord.ProviderLease, device.ProviderLease, profile);
        var validation = PlatformVirtualIoContract.ValidateRequest(request, childRecord.State);
        if (!validation.IsSuccess) return FromProviderFailure<PlatformVirtualIoBinding>(validation.Status, validation.Message);
        PlatformBackendEpoch backendEpoch = default;
        PlatformVirtualIoBindingId reservedBindingId = default;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            var lineage = ValidateActiveChildLineage(childRecord);
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
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformVirtualIoBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
        KernelResult ValidateSource(PlatformProviderIncarnation source)
        {
            if (source.Value == 0 || source != device.ProviderIncarnation ||
                BackendEpoch != backendEpoch || device.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !TryGetOriginalDeviceRecord(parentDevice.LeaseId, out var exactDevice) || !ReferenceEquals(exactDevice, device) ||
                !_childBindings.TryGetValue(child.BindingId, out var exactChild) || !ReferenceEquals(exactChild, childRecord) ||
                device.Lease != parentDevice || childRecord.Binding != child || device.ProviderLease != request.ParentDeviceLease ||
                childRecord.ProviderLease != request.ChildLease ||
                !TryGetCanonicalRootRecord(child.ParentBinding.BindingId, out var root) || root.Binding != child.ParentBinding ||
                root.ProviderIncarnation != source || root.BackendEpoch != backendEpoch ||
                root.ProviderLease != childRecord.ProviderLease.ParentDomainLease)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original virtual-I/O source changed; dependencies remain pinned.");
            }
            var lineage = ValidateActiveChildLineage(childRecord);
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
                var exact = ValidateSource(source);
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
        var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
        if (!final.IsSuccess) return KernelResult<PlatformVirtualIoBinding>.Fail(final.Error, final.Message!);
        PlatformAuthorityResult<PlatformProviderVirtualIoLease> result;
        try { result = provider.BindVirtualIo(request); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            childRecord.State = PlatformChildDomainState.Faulted;
            device.FaultPinned = true;
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                $"Virtual-I/O binding may have taken effect without a receipt: {exception.Message}");
        }
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
            if (result.Status != PlatformAuthorityStatus.NotAccepted)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
            }
            return FromProviderFailure<PlatformVirtualIoBinding>(result.Status, result.Message);
        }
        var leaseValidation = PlatformVirtualIoContract.ValidateLease(request, result.Value!);
        if (!leaseValidation.IsSuccess)
        {
            var fact = new UnpublishedVirtualIoBindReceipt(child, parentDevice, request,
                backendEpoch, device.ProviderIncarnation, result.Value!);
            lock (_secureDomainLifecycleGate)
            {
                device.UnpublishedVirtualIoBinds.Add(fact);
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
            }
            try
            {
                var cleanup = provider.RevokeVirtualIo(result.Value!);
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
            bindingRecord = new VirtualIoBindingRecord(binding, result.Value!);
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
            var sourceValid = ValidateSource(source);
            if (BackendEpoch != backendEpoch || childRecord.State == PlatformChildDomainState.Faulted ||
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
            var lineage = ValidateActiveChildLineage(childRecord);
            if (!lineage.IsSuccess)
                return lineage;
            if (device.LocalAuthorizationRevoked || device.ClosureInFlight || device.PendingChildDrains != 0)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Device source revoked before virtual-I/O publication.");
            return sourceCommitConsumer is null ? commit() : sourceCommitConsumer(commit);
        }
        }
        var completed = completionConsumer is null ? CommitCurrent(KernelResult.Ok) : completionConsumer(binding, CommitCurrent);
        return completed.IsSuccess ? KernelResult<PlatformVirtualIoBinding>.Ok(binding)
            : KernelResult<PlatformVirtualIoBinding>.Fail(completed.Error, completed.Message!);
        }
        finally { lock (_secureDomainLifecycleGate) { childRecord.PendingChildEffects--; device.PendingVirtualIoBinds--; } }
    }

    internal KernelResult RevokeChildVirtualIo(PlatformVirtualIoBinding binding)
    {
        if (!_virtualIoBindings.TryGetValue(binding.BindingId, out VirtualIoBindingRecord? record))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Virtual-I/O binding was not found.");
        if (record.Binding.Generation != binding.Generation)
            return KernelResult.Fail(KernelError.StaleGeneration, "Virtual-I/O binding generation is stale.");
        if (record.Binding != binding)
            return KernelResult.Fail(KernelError.WrongPlatformDomain, "Virtual-I/O binding belongs to another child or device.");
        if (_provider is not IPlatformVirtualIoProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Bounded virtual-I/O provider is unavailable.");
        ChildBindingRecord child;
        DeviceLeaseRecord device;
        DomainRecord root;
        PlatformBackendEpoch epoch;
        lock (_secureDomainLifecycleGate)
        {
            if (record.Closure == PlatformExternalClosureState.Closed) return KernelResult.Ok();
            if (record.Closure == PlatformExternalClosureState.Faulted)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original virtual-I/O closure remains pinned.");
            if (record.ClosureInFlight || record.Closure == PlatformExternalClosureState.Draining && record.LastClosureReceipt is null)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Virtual-I/O closure is in flight.");
            if (!_childBindings.TryGetValue(binding.Child.BindingId, out child!) || child.Binding != binding.Child ||
                !TryGetOriginalDeviceRecord(binding.ParentDevice.LeaseId, out device!) || device.Lease != binding.ParentDevice ||
                !TryGetCanonicalRootRecord(binding.Child.ParentBinding.BindingId, out root!) || root.Binding != binding.Child.ParentBinding)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original virtual-I/O owners unavailable.");
            epoch = device.BackendEpoch;
            record.ClosureInFlight = true;
        }
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O source unavailable; original state retained."); }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateVirtualIoCloseSourceLocked(record, child, device, root, epoch, source);
                if (!exact.IsSuccess) return exact;
                record.Closure = PlatformExternalClosureState.Draining;
            }
            if (record.LastClosureReceipt is null)
            {
                PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> result;
                try { result = provider.RevokeVirtualIo(record.ProviderLease); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (_secureDomainLifecycleGate) record.Closure = PlatformExternalClosureState.Faulted;
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O revoke outcome unknown; original pin retained.");
                }
                lock (_secureDomainLifecycleGate)
                {
                    if (result.IsSuccess) record.LastClosureReceipt = new(record.ProviderLease, epoch, result.Value!);
                    else
                    {
                        record.Closure = PlatformExternalClosureState.Faulted;
                        return FromProviderFailure(result.Status, result.Message);
                    }
                }
            }
            var fact = record.LastClosureReceipt!;
            var receipt = PlatformVirtualIoContract.ValidateClosureReceipt(record.ProviderLease, fact.Receipt);
            if (fact.AdmittedLease != record.ProviderLease || fact.AdmissionEpoch != epoch || !receipt.IsSuccess)
            {
                lock (_secureDomainLifecycleGate) record.Closure = PlatformExternalClosureState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original virtual-I/O closure receipt is malformed.");
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Known virtual-I/O closure source unavailable; receipt retained for retry."); }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateVirtualIoCloseSourceLocked(record, child, device, root, epoch, source);
                if (!exact.IsSuccess) return exact;
                record.Closure = PlatformExternalClosureState.Closed;
                return KernelResult.Ok();
            }
        }
        finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
    }

    private KernelResult ValidateVirtualIoCloseSourceLocked(VirtualIoBindingRecord record, ChildBindingRecord child,
        DeviceLeaseRecord device, DomainRecord root, PlatformBackendEpoch epoch, PlatformProviderIncarnation source)
    {
        var binding = record.Binding;
        if (_backendEpochExhausted || BackendEpoch != epoch || root.BackendEpoch != epoch ||
            source.Value == 0 || source != device.ProviderIncarnation || source != root.ProviderIncarnation ||
            !_virtualIoBindings.TryGetValue(binding.BindingId, out var exact) || !ReferenceEquals(exact, record) ||
            !_childBindings.TryGetValue(binding.Child.BindingId, out var exactChild) || !ReferenceEquals(exactChild, child) ||
            !TryGetOriginalDeviceRecord(binding.ParentDevice.LeaseId, out var exactDevice) || !ReferenceEquals(exactDevice, device) ||
            !TryGetCanonicalRootRecord(binding.Child.ParentBinding.BindingId, out var exactRoot) || !ReferenceEquals(exactRoot, root) ||
            record.ProviderLease.ChildLease != child.ProviderLease || record.ProviderLease.ParentDeviceLease != device.ProviderLease ||
            device.Lease.DomainBinding != root.Binding || child.ProviderLease.ParentDomainLease != root.ProviderLease ||
            device.PlatformClosed || device.FaultPinned || device.ClosureInFlight || root.ParentRevokeInFlight ||
            root.AuthorityState == DomainAuthorityState.Closed ||
            child.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed ||
            child.CloseInFlight || record.Closure == PlatformExternalClosureState.Faulted)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original virtual-I/O source/owners changed; closure and reclaim remain pinned.");
        }
        return ValidateDomainIdentity(binding.Child.ParentBinding, binding.Child.ParentBinding.Subject);
    }
}
