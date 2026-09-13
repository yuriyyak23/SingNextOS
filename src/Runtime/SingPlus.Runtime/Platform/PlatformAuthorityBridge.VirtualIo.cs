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
        if (parentDevice.DomainBinding != child.ParentBinding)
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.WrongPlatformDomain, "Parent device belongs to another domain.");
        ChildBindingRecord childRecord = resolved.Value!;
        var request = new PlatformVirtualIoRequest(childRecord.ProviderLease, device.ProviderLease, profile);
        var validation = PlatformVirtualIoContract.ValidateRequest(request, childRecord.State);
        if (!validation.IsSuccess) return FromProviderFailure<PlatformVirtualIoBinding>(validation.Status, validation.Message);
        var result = provider.BindVirtualIo(request);
        if (!result.IsSuccess)
        {
            QuarantineChild(childRecord, result.Status);
            return FromProviderFailure<PlatformVirtualIoBinding>(result.Status, result.Message);
        }
        var leaseValidation = PlatformVirtualIoContract.ValidateLease(request, result.Value!);
        if (!leaseValidation.IsSuccess)
        {
            _ = provider.RevokeVirtualIo(result.Value!);
            childRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformVirtualIoBinding>.Fail(KernelError.PlatformFaulted, leaseValidation.Message!);
        }
        var binding = new PlatformVirtualIoBinding(new(_nextVirtualIoBindingId++), new(1), child, parentDevice);
        _virtualIoBindings.Add(binding.BindingId, new(binding, result.Value!));
        return KernelResult<PlatformVirtualIoBinding>.Ok(binding);
    }

    internal KernelResult RevokeChildVirtualIo(PlatformVirtualIoBinding binding)
    {
        if (!_virtualIoBindings.TryGetValue(binding.BindingId, out VirtualIoBindingRecord? record))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Virtual-I/O binding was not found.");
        if (record.Binding.Generation != binding.Generation)
            return KernelResult.Fail(KernelError.StaleGeneration, "Virtual-I/O binding generation is stale.");
        if (record.Binding != binding)
            return KernelResult.Fail(KernelError.WrongPlatformDomain, "Virtual-I/O binding belongs to another child or device.");
        if (record.Closure == PlatformExternalClosureState.Closed)
            return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Virtual-I/O binding is already closed.");
        if (record.Closure == PlatformExternalClosureState.Faulted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O binding is quarantined.");
        if (_provider is not IPlatformVirtualIoProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Bounded virtual-I/O provider is unavailable.");
        record.Closure = PlatformExternalClosureState.Draining;
        var result = provider.RevokeVirtualIo(record.ProviderLease);
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
        record.Closure = PlatformExternalClosureState.Closed;
        return KernelResult.Ok();
    }
}
