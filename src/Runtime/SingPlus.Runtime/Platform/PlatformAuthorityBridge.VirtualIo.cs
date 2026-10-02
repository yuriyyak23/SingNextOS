using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformVirtualIoBinding> BindChildVirtualIo(
        PlatformChildBinding child, PlatformDeviceLease parentDevice, PlatformVirtualIoProfile profile)
    {
        var resolved = ResolveChild(child);
        if (!resolved.IsSuccess) return KernelResult<PlatformVirtualIoBinding>.Fail(resolved.Error, resolved.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.BoundedVirtualIo, PlatformVirtualIoContract.ContractVersion) ||
            _provider is not IPlatformVirtualIoProvider provider)
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformUnsupported, "Bounded virtual-I/O provider is unavailable.");
        if (!_deviceLeases.TryGetValue(parentDevice.LeaseId, out DeviceLeaseRecord? device) || device.Lease != parentDevice)
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
        if (!ValidateDeviceClosureGeneration(device))
        {
            childRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                "Parent device provider generation changed before virtual-I/O admission.");
        }
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            if (childRecord.TransitionInFlight ||
                !PlatformVirtualIoContract.ValidateRequest(request, childRecord.State).IsSuccess ||
                device.LocalAuthorizationRevoked || device.PlatformClosed || device.FaultPinned ||
                !ValidateDeviceClosureGeneration(device))
                return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformBindingRevoked,
                    "Child or parent device changed before virtual-I/O admission.");
            childRecord.PendingChildEffects++;
            backendEpoch = BackendEpoch;
        }
        try
        {
        PlatformAuthorityResult<PlatformProviderVirtualIoLease> result;
        try { result = provider.BindVirtualIo(request); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            childRecord.State = PlatformChildDomainState.Faulted;
            device.FaultPinned = true;
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                $"Virtual-I/O binding may have taken effect without a receipt: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch ||
            childRecord.State == PlatformChildDomainState.Faulted ||
            device.LocalAuthorizationRevoked || device.PlatformClosed || device.FaultPinned ||
            !ValidateDeviceClosureGeneration(device))
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
            var exactlyClosed = false;
            try
            {
                var cleanup = provider.RevokeVirtualIo(result.Value!);
                exactlyClosed = cleanup.IsSuccess &&
                    PlatformVirtualIoContract.ValidateClosureReceipt(result.Value!, cleanup.Value!).IsSuccess &&
                    BackendEpoch == backendEpoch;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Lost cleanup receipt cannot release the parent device.
            }
            if (!exactlyClosed)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
            }
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, exactlyClosed
                ? $"{leaseValidation.Message} The malformed lease was exactly compensated."
                : $"{leaseValidation.Message} Exact compensation failed and the child authority is quarantined.");
        }
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || childRecord.State == PlatformChildDomainState.Faulted ||
                device.LocalAuthorizationRevoked || device.PlatformClosed || device.FaultPinned ||
                !ValidateDeviceClosureGeneration(device))
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                device.FaultPinned = true;
                return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted,
                    "Virtual-I/O dependencies changed before local publication.");
            }
            var binding = new PlatformVirtualIoBinding(new(_nextVirtualIoBindingId++), new(1), child, parentDevice);
            _virtualIoBindings.Add(binding.BindingId, new(binding, result.Value!));
            return KernelResult<PlatformVirtualIoBinding>.Ok(binding);
        }
        }
        finally { lock (_secureDomainLifecycleGate) childRecord.PendingChildEffects--; }
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
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            if (record.Closure == PlatformExternalClosureState.Closed)
                return KernelResult.Ok();
            if (record.Closure == PlatformExternalClosureState.Faulted)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Virtual-I/O binding is quarantined without exact provider closure.");
            if (record.Closure == PlatformExternalClosureState.Draining)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Virtual-I/O closure is already in progress.");
            backendEpoch = BackendEpoch;
            record.Closure = PlatformExternalClosureState.Draining;
        }
        PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> result;
        try { result = provider.RevokeVirtualIo(record.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Virtual-I/O revoke may have taken effect without a closure receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
            if (BackendEpoch != backendEpoch || record.Closure != PlatformExternalClosureState.Draining)
            {
                record.Closure = PlatformExternalClosureState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Backend reset during virtual-I/O revoke leaves closure uncertain.");
            }
        if (!result.IsSuccess)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return FromProviderFailure(result.Status, result.Message);
        }
        var validation = PlatformVirtualIoContract.ValidateClosureReceipt(record.ProviderLease, result.Value!);
        if (!validation.IsSuccess)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted, validation.Message!);
        }
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || record.Closure != PlatformExternalClosureState.Draining)
            {
                record.Closure = PlatformExternalClosureState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Virtual-I/O closure changed before publication.");
            }
            record.Closure = PlatformExternalClosureState.Closed;
            return KernelResult.Ok();
        }
    }
}
