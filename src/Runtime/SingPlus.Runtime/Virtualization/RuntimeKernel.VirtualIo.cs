using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>Kernel-private identity for a bounded guest-I/O lease; it is never device authority.</summary>
internal readonly record struct VirtualIoBinding(ulong Id, ulong Generation);

public sealed partial class RuntimeKernel
{
    private enum VirtualIoState { Staged, Active, Closing, Quarantined, Closed }
    private sealed class VirtualIoRecord(VirtualIoBinding binding, ProcessHandle owner, VirtualDomainHandle domain,
        PlatformVirtualIoBinding platform, SecureExecutionBinding? secureExecution, PlatformDeviceRights rights)
    {
        internal VirtualIoBinding Binding { get; } = binding;
        internal ProcessHandle Owner { get; } = owner;
        internal VirtualDomainHandle Domain { get; } = domain;
        internal PlatformVirtualIoBinding Platform { get; } = platform;
        internal SecureExecutionBinding? SecureExecution { get; } = secureExecution;
        internal PlatformDeviceRights Rights { get; } = rights;
        internal int PendingEvents { get; set; }
        internal VirtualIoState State { get; set; } = VirtualIoState.Staged;
    }

    private readonly object _virtualIoGate = new();
    private readonly Dictionary<ulong, VirtualIoRecord> _virtualIo = [];
    private ulong _nextVirtualIoId = 1;

    internal KernelResult<VirtualIoBinding> BindVirtualIo(ProcessHandle owner, VirtualDomainHandle domain,
        PlatformDeviceLease device, PlatformVirtualIoProfile profile, SecureExecutionBinding? secureExecution = null)
    {
        var virtualRecord = _virtualDomains.ResolveAdmission(owner, domain);
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
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<VirtualIoBinding>.Fail(process.Error, process.Message!);
        var source = PlatformAuthority.DeviceEffectSource(device, PlatformIdentity(process.Value!));
        if (!source.IsSuccess) return KernelResult<VirtualIoBinding>.Fail(source.Error, source.Message!);
        VirtualIoRecord? record = null;
        PlatformVirtualIoBinding? known = null;
        VirtualIoRecord Retain(PlatformVirtualIoBinding platform)
        {
            lock (_virtualIoGate)
            {
                if (record is not null) return record;
                record = new(new(_nextVirtualIoId++, 1), owner, domain, platform, secureExecution, profile.Rights);
                _virtualIo.Add(record.Binding.Id, record);
                return record;
            }
        }
        var result = UseVirtualDeliverySource(owner, domain, source.Value, virtualRecord.Value!, child,
            ToDeviceCapabilityRights(profile.Rights), device.Device.ResourceId, virtualRecord.Value!.State,
            (fresh, sourceCommit) =>
            {
                var bound = PlatformAuthority.BindChildVirtualIo(child, device, profile, out known,
                    fresh, sourceCommit, (platform, commit) =>
                    {
                        var staged = Retain(platform);
                        var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
                        if (!composed.IsSuccess) return composed;
                        var secureCurrent = secureExecution is { } execution
                            ? RevalidateSecureExecution(execution) : KernelResult.Ok();
                        if (!secureCurrent.IsSuccess) return secureCurrent;
                        return fresh(() => commit(() =>
                        {
                            lock (_virtualIoGate)
                            {
                                if (staged.State != VirtualIoState.Staged)
                                    return KernelResult.Fail(KernelError.InvalidTransition, "Virtual-I/O staging changed before publication.");
                                staged.State = VirtualIoState.Active;
                                return KernelResult.Ok();
                            }
                        }));
                    });
                return bound.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(bound.Error, bound.Message!);
            }, deviceSource: true);
        if (result.IsSuccess) return KernelResult<VirtualIoBinding>.Ok(record!.Binding);
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
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual-I/O identity/generation is required.");
            if (record.Owner != owner || record.State != VirtualIoState.Active)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Virtual-I/O is not admitting effects.");
            if (expectedDomain is { } exactDomain && record.Domain != exactDomain)
                return KernelResult.Fail(KernelError.WrongPlatformDomain,
                    "Virtual compute I/O must belong to the exact operand domain generation.");
        }
        var domain = _virtualDomains.Resolve(owner, record.Domain);
        var expected = domain.IsSuccess ? domain.Value!.State : default;
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
                            return record.State == VirtualIoState.Active
                                ? KernelResult.Ok()
                                : KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O changed during source revalidation.");
                    }));
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
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var source = PlatformAuthority.DeviceEffectSource(record.Platform.ParentDevice, PlatformIdentity(process.Value!));
        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
        lock (_virtualIoGate)
        {
            if (record.State != VirtualIoState.Active)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O stopped before event admission.");
            record.PendingEvents++;
        }
        KernelResult CommitDeviceSource(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitDeviceLeaseAdmission(source.Value, process.Value!.DomainId, owner.Generation,
                ToDeviceCapabilityRights(record.Rights), record.Platform.ParentDevice.Device.ResourceId, () =>
                {
                    lock (_virtualIoGate)
                        return record.State == VirtualIoState.Active ? commit()
                            : KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual-I/O changed before event publication.");
                });
        try { return InjectVirtualEvent(owner, record.Domain, eventCapability, endpoint, CommitDeviceSource); }
        finally { lock (_virtualIoGate) record.PendingEvents--; }
    }

    internal KernelResult CloseVirtualIo(VirtualIoBinding binding)
    {
        VirtualIoRecord record;
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(binding.Id, out record!) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact virtual-I/O identity/generation is required for closure.");
            if (record.State == VirtualIoState.Closed) return KernelResult.Ok();
            if (record.PendingEvents != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Virtual-I/O event admission/publication remains pending.");
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
