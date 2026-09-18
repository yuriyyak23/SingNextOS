using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>Kernel-private overlay over an existing guest mapping; never a memory authority.</summary>
internal readonly record struct SecureGuestRegionBinding(ulong Id, ulong Generation);

public sealed partial class RuntimeKernel
{
    private enum SecureGuestRegionState { Binding, Active, Closing, Quarantined, Closed }
    private sealed class SecureGuestRegionRecord(SecureGuestRegionBinding binding, ProcessHandle owner,
        SecureExecutionBinding execution, GuestRegionMapping mapping, PlatformRegionMapping parent,
        PlatformSecureRegionClass regionClass)
    {
        internal SecureGuestRegionBinding Binding { get; } = binding;
        internal ProcessHandle Owner { get; } = owner;
        internal SecureExecutionBinding Execution { get; } = execution;
        internal GuestRegionMapping Mapping { get; } = mapping;
        internal PlatformRegionMapping Parent { get; } = parent;
        internal PlatformSecureRegionClass RegionClass { get; } = regionClass;
        internal SecureGuestRegionState State { get; set; } = SecureGuestRegionState.Binding;
    }

    private readonly object _secureGuestRegionGate = new();
    private readonly Dictionary<ulong, SecureGuestRegionRecord> _secureGuestRegions = [];
    private ulong _nextSecureGuestRegionId = 1;

    internal KernelResult<SecureGuestRegionBinding> BindSecureGuestRegion(ProcessHandle owner,
        SecureExecutionBinding execution, GuestRegionMappingHandle guestMapping, PlatformSecureRegionClass regionClass)
    {
        if (!Enum.IsDefined(regionClass))
            return KernelResult<SecureGuestRegionBinding>.Fail(KernelError.PlatformDenied, "Secure guest region class is invalid.");
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(process.Error, process.Message!);
        var regionOwner = new RegionOwner(process.Value!.DomainId, owner.Generation);
        var executionRecord = ResolveSecureGuestExecution(owner, execution);
        if (!executionRecord.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(executionRecord.Error, executionRecord.Message!);
        var virtualRecord = _virtualDomains.Resolve(owner, executionRecord.Value!.Request.Context.Virtual);
        if (!virtualRecord.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(virtualRecord.Error, virtualRecord.Message!);
        var virtualValue = virtualRecord.Value!;
        var mapping = _virtualDomains.ResolveMapping(virtualValue, guestMapping);
        if (!mapping.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(mapping.Error, mapping.Message!);
        if (!virtualValue.PlatformMappings.TryGetValue(guestMapping.MappingId, out var platform) ||
            mapping.Value!.Domain != executionRecord.Value.Request.Context.Virtual)
            return KernelResult<SecureGuestRegionBinding>.Fail(KernelError.StaleGeneration,
                "Secure guest overlay requires the exact existing provider guest/parent mapping lineage.");
        var backing = RevalidateSecureGuestBackings(regionOwner, mapping.Value.Region);
        if (!backing.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(backing.Error, backing.Message!);

        SecureGuestRegionRecord record;
        lock (_secureGuestRegionGate)
        {
            if (_secureGuestRegions.Values.Any(x => x.State != SecureGuestRegionState.Closed &&
                x.Owner == owner && x.Mapping.Mapping == guestMapping))
                return KernelResult<SecureGuestRegionBinding>.Fail(KernelError.PlatformBindingActive,
                    "The exact guest mapping already has a live or quarantined secure overlay.");
            record = new(new(_nextSecureGuestRegionId++, 1), owner, execution, mapping.Value!, platform.Parent, regionClass);
            _secureGuestRegions.Add(record.Binding.Id, record);
        }

        KernelResult bound;
        try { bound = PlatformAuthority.BindSecureRegion(executionRecord.Value.Request.Context.SecureBinding, platform.Parent, regionClass); }
        catch (Exception) { bound = KernelResult.Fail(KernelError.PlatformFaulted, "Secure guest provider bind threw; terminal compensation is required."); }

        if (!bound.IsSuccess)
        {
            lock (_secureGuestRegionGate)
                record.State = bound.Error is KernelError.PlatformUnavailable or KernelError.PlatformFaulted or
                    KernelError.ExternalEffectUncontained
                    ? SecureGuestRegionState.Quarantined
                    : SecureGuestRegionState.Closed;
            return KernelResult<SecureGuestRegionBinding>.Fail(bound.Error, bound.Message!);
        }

        lock (_secureGuestRegionGate) record.State = SecureGuestRegionState.Active;
        if (RevalidateSecureGuestLineage(record).IsSuccess &&
            RevalidateSecureGuestBackings(regionOwner, record.Mapping.Region).IsSuccess)
        {
            return KernelResult<SecureGuestRegionBinding>.Ok(record.Binding);
        }

        var close = CloseSecureGuestRegion(record.Binding);
        return KernelResult<SecureGuestRegionBinding>.Fail(close.IsSuccess ? KernelError.StaleGeneration : KernelError.PlatformFaulted,
            close.IsSuccess ? "Secure guest overlay changed before activation and was exactly closed." :
                "Secure guest overlay could not be exactly closed and remains quarantined.");
    }

    internal KernelResult CloseSecureGuestRegion(SecureGuestRegionBinding binding)
    {
        SecureGuestRegionRecord record;
        lock (_secureGuestRegionGate)
        {
            if (!_secureGuestRegions.TryGetValue(binding.Id, out record!) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure guest overlay identity/generation is required.");
            if (record.State == SecureGuestRegionState.Closed) return KernelResult.Ok();
            if (record.State is SecureGuestRegionState.Binding or SecureGuestRegionState.Closing)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Secure guest overlay provider call is in flight.");
            record.State = SecureGuestRegionState.Closing;
        }
        var execution = ResolveSecureGuestExecution(record.Owner, record.Execution, allowQuarantined: true);
        KernelResult close;
        try
        {
            close = !execution.IsSuccess
                ? KernelResult.Fail(execution.Error, execution.Message!)
                : PlatformAuthority.UnbindSecureRegion(execution.Value!.Request.Context.SecureBinding, record.Parent);
        }
        catch (Exception)
        {
            close = KernelResult.Fail(KernelError.PlatformFaulted, "Secure guest provider close threw; overlay is quarantined.");
        }
        lock (_secureGuestRegionGate)
        {
            record.State = close.IsSuccess ? SecureGuestRegionState.Closed : SecureGuestRegionState.Quarantined;
            return close.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(close.Error, close.Message!);
        }
    }

    private KernelResult<SecureExecutionRecord> ResolveSecureGuestExecution(ProcessHandle owner,
        SecureExecutionBinding binding, bool allowQuarantined = false)
    {
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out var record) || record.Request.Binding != binding)
                return KernelResult<SecureExecutionRecord>.Fail(KernelError.StaleGeneration, "Exact secure execution identity/generation is required.");
            if (record.Owner != owner) return KernelResult<SecureExecutionRecord>.Fail(KernelError.WrongPlatformDomain, "Secure execution belongs to another process.");
            if (!allowQuarantined && record.State != SecureExecutionState.Active)
                return KernelResult<SecureExecutionRecord>.Fail(KernelError.PlatformFaulted, "Secure execution is not admitting secure guest effects.");
            if (allowQuarantined && record.State is SecureExecutionState.Binding or SecureExecutionState.Checking)
                return KernelResult<SecureExecutionRecord>.Fail(KernelError.PlatformBindingDraining, "Secure execution provider call is in flight.");
            return KernelResult<SecureExecutionRecord>.Ok(record);
        }
    }

    private KernelResult RevalidateSecureGuestLineage(SecureGuestRegionRecord record)
    {
        var execution = RevalidateSecureExecution(record.Execution);
        if (!execution.IsSuccess) return execution;
        var virtualRecord = _virtualDomains.Resolve(record.Owner, record.Mapping.Domain);
        if (!virtualRecord.IsSuccess) return KernelResult.Fail(virtualRecord.Error, virtualRecord.Message!);
        var mapping = _virtualDomains.ResolveMapping(virtualRecord.Value!, record.Mapping.Mapping);
        if (!mapping.IsSuccess || mapping.Value != record.Mapping ||
            !virtualRecord.Value!.PlatformMappings.TryGetValue(record.Mapping.Mapping.MappingId, out var platform) ||
            platform.Parent != record.Parent)
            return KernelResult.Fail(KernelError.StaleGeneration, "Secure guest overlay lineage is stale or has changed.");
        return KernelResult.Ok();
    }

    private KernelResult CloseSecureGuestRegionsForGuestMapping(ProcessHandle owner, GuestRegionMappingHandle mapping)
    {
        SecureGuestRegionBinding[] bindings;
        lock (_secureGuestRegionGate)
            bindings = _secureGuestRegions.Values.Where(x => x.Owner == owner && x.State != SecureGuestRegionState.Closed &&
                x.Mapping.Mapping == mapping).Select(x => x.Binding).ToArray();
        foreach (var binding in bindings)
        {
            var close = CloseSecureGuestRegion(binding);
            if (!close.IsSuccess) return close;
        }
        return KernelResult.Ok();
    }

    private bool HasSecureGuestRegionsForExecution(SecureExecutionBinding binding)
    {
        lock (_secureGuestRegionGate)
            return _secureGuestRegions.Values.Any(x => x.Execution == binding && x.State != SecureGuestRegionState.Closed);
    }

    private KernelResult CloseSecureGuestRegionsForProcess(ProcessHandle owner) =>
        CloseSecureGuestRegionsWhere(x => x.Owner == owner);

    private KernelResult CloseSecureGuestRegionsForVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain) =>
        CloseSecureGuestRegionsWhere(x => x.Owner == owner && x.Mapping.Domain == domain);

    private KernelResult CloseSecureGuestRegionsWhere(Func<SecureGuestRegionRecord, bool> predicate)
    {
        SecureGuestRegionBinding[] bindings;
        lock (_secureGuestRegionGate)
            bindings = _secureGuestRegions.Values.Where(x => x.State != SecureGuestRegionState.Closed && predicate(x))
                .Select(x => x.Binding).ToArray();
        foreach (var binding in bindings)
        {
            var close = CloseSecureGuestRegion(binding);
            if (!close.IsSuccess) return close;
        }
        return KernelResult.Ok();
    }

    private KernelResult CloseSecureGuestRegionsForSecureDomain(ProcessHandle owner, SecureDomainHandle domain)
    {
        SecureGuestRegionRecord[] candidates;
        lock (_secureGuestRegionGate)
            candidates = _secureGuestRegions.Values.Where(x => x.Owner == owner && x.State != SecureGuestRegionState.Closed).ToArray();
        var bindings = candidates.Where(x =>
        {
            var execution = ResolveSecureGuestExecution(x.Owner, x.Execution, allowQuarantined: true);
            return execution.IsSuccess && execution.Value!.Request.Context.Secure == domain;
        }).Select(x => x.Binding).ToArray();
        foreach (var binding in bindings)
        {
            var close = CloseSecureGuestRegion(binding);
            if (!close.IsSuccess) return close;
        }
        return KernelResult.Ok();
    }
}
