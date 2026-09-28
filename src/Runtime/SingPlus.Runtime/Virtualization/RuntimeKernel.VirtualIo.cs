using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>Kernel-private identity for a bounded guest-I/O lease; it is never device authority.</summary>
internal readonly record struct VirtualIoBinding(ulong Id, ulong Generation);

public sealed partial class RuntimeKernel
{
    private enum VirtualIoState { Active, Closing, Quarantined, Closed }
    private sealed class VirtualIoRecord(VirtualIoBinding binding, ProcessHandle owner, VirtualDomainHandle domain,
        PlatformVirtualIoBinding platform, SecureExecutionBinding? secureExecution)
    {
        internal VirtualIoBinding Binding { get; } = binding;
        internal ProcessHandle Owner { get; } = owner;
        internal VirtualDomainHandle Domain { get; } = domain;
        internal PlatformVirtualIoBinding Platform { get; } = platform;
        internal SecureExecutionBinding? SecureExecution { get; } = secureExecution;
        internal VirtualIoState State { get; set; } = VirtualIoState.Active;
    }

    private readonly object _virtualIoGate = new();
    private readonly Dictionary<ulong, VirtualIoRecord> _virtualIo = [];
    private ulong _nextVirtualIoId = 1;

    internal KernelResult<VirtualIoBinding> BindVirtualIo(ProcessHandle owner, VirtualDomainHandle domain,
        PlatformDeviceLease device, PlatformVirtualIoProfile profile, SecureExecutionBinding? secureExecution = null)
    {
        var virtualRecord = _virtualDomains.Resolve(owner, domain);
        if (!virtualRecord.IsSuccess) return KernelResult<VirtualIoBinding>.Fail(virtualRecord.Error, virtualRecord.Message!);
        if (virtualRecord.Value!.ChildBinding is not { } child ||
            !virtualRecord.Value.Authority.HasFlag(PlatformChildAuthorityClass.Io))
            return KernelResult<VirtualIoBinding>.Fail(KernelError.PlatformUnsupported, "Virtual domain lacks exact child I/O authority.");
        if (secureExecution is { } secure)
        {
            var revalidated = RevalidateSecureExecution(secure);
            if (!revalidated.IsSuccess) return KernelResult<VirtualIoBinding>.Fail(revalidated.Error, revalidated.Message!);
            var execution = ResolveSecureGuestExecution(owner, secure);
            if (!execution.IsSuccess || execution.Value!.Request.Context.Virtual != domain)
                return KernelResult<VirtualIoBinding>.Fail(KernelError.WrongPlatformDomain, "Secure I/O requires the exact secure execution for this virtual domain.");
        }
        var bound = PlatformAuthority.BindChildVirtualIo(child, device, profile);
        if (!bound.IsSuccess) return KernelResult<VirtualIoBinding>.Fail(bound.Error, bound.Message!);
        var record = new VirtualIoRecord(new(_nextVirtualIoId++, 1), owner, domain, bound.Value!, secureExecution);
        lock (_virtualIoGate) _virtualIo.Add(record.Binding.Id, record);
        var exact = RevalidateVirtualIo(owner, record.Binding);
        if (exact.IsSuccess) return KernelResult<VirtualIoBinding>.Ok(record.Binding);
        var close = CloseVirtualIo(record.Binding);
        return KernelResult<VirtualIoBinding>.Fail(close.IsSuccess ? exact.Error : KernelError.PlatformFaulted,
            close.IsSuccess ? exact.Message! : "Virtual-I/O changed before activation and exact compensation failed; the binding is quarantined.");
    }

    internal KernelResult RevalidateVirtualIo(ProcessHandle owner, VirtualIoBinding binding)
    {
        VirtualIoRecord record;
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual-I/O identity/generation is required.");
            if (record.Owner != owner || record.State != VirtualIoState.Active)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O is not admitting effects.");
        }
        var domain = _virtualDomains.Resolve(owner, record.Domain);
        var device = PlatformAuthority.ValidateDeviceLease(record.Platform.ParentDevice, record.Platform.ParentDevice.DomainBinding.Subject);
        var secure = record.SecureExecution is { } execution ? RevalidateSecureExecution(execution) : KernelResult.Ok();
        if (domain.IsSuccess && domain.Value!.ChildBinding == record.Platform.Child && device.IsSuccess && secure.IsSuccess)
            return KernelResult.Ok();
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
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual-I/O identity/generation is required for event publication.");
            if (record.Owner != owner || record.State != VirtualIoState.Active)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O is not admitting guest-visible events.");
        }

        var exact = RevalidateVirtualIo(owner, binding);
        if (!exact.IsSuccess) return exact;
        var external = QueryExternalOperation(owner, operation);
        if (!external.IsSuccess) return KernelResult.Fail(external.Error, external.Message!);
        if (external.Value!.State != ExternalOperationState.Published)
            return KernelResult.Fail(KernelError.InvalidTransition,
                "Guest-visible virtual-I/O event requires the exact external operation to be Published.");
        return InjectVirtualEvent(owner, record.Domain, eventCapability, endpoint);
    }

    internal KernelResult CloseVirtualIo(VirtualIoBinding binding)
    {
        VirtualIoRecord record;
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual-I/O identity/generation is required for closure.");
            if (record.State == VirtualIoState.Closed) return KernelResult.Ok();
            if (HasVirtualComputePin(binding))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O remains pinned by virtualized provider work.");
            if (record.State == VirtualIoState.Closing) return KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O close is in flight.");
            record.State = VirtualIoState.Closing;
        }
        KernelResult close;
        try { close = PlatformAuthority.RevokeChildVirtualIo(record.Platform); }
        catch (Exception) { close = KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O provider close threw."); }
        lock (_virtualIoGate) record.State = close.IsSuccess ? VirtualIoState.Closed : VirtualIoState.Quarantined;
        return close;
    }

    private KernelResult CloseVirtualIoForDomain(ProcessHandle owner, VirtualDomainHandle domain) =>
        CloseVirtualIoWhere(x => x.Owner == owner && x.Domain == domain);

    private KernelResult CloseVirtualIoForDevice(ProcessHandle owner, PlatformDeviceLease device) =>
        CloseVirtualIoWhere(x => x.Owner == owner && x.Platform.ParentDevice == device);

    private KernelResult CloseVirtualIoForProcess(ProcessHandle owner) =>
        CloseVirtualIoWhere(x => x.Owner == owner);

    private bool HasVirtualIoForSecureExecution(SecureExecutionBinding binding)
    {
        lock (_virtualIoGate)
            return _virtualIo.Values.Any(x => x.SecureExecution == binding && x.State != VirtualIoState.Closed);
    }

    private KernelResult CloseVirtualIoWhere(Func<VirtualIoRecord, bool> predicate)
    {
        VirtualIoBinding[] bindings;
        lock (_virtualIoGate) bindings = _virtualIo.Values.Where(x => x.State != VirtualIoState.Closed && predicate(x)).Select(x => x.Binding).ToArray();
        foreach (var binding in bindings) { var close = CloseVirtualIo(binding); if (!close.IsSuccess) return close; }
        return KernelResult.Ok();
    }

    private void QuarantineVirtualIoForBackendReset()
    {
        lock (_virtualIoGate)
            foreach (var record in _virtualIo.Values)
                if (record.State != VirtualIoState.Closed) record.State = VirtualIoState.Quarantined;
    }
}
