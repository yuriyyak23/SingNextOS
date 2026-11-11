using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

/// <summary>Kernel-private overlay over an existing guest mapping; never a memory authority.</summary>
internal readonly record struct SecureGuestRegionBinding(ulong Id, ulong Generation);

public sealed partial class RuntimeKernel
{
    private enum SecureGuestRegionState { Binding, Active, Closing, Quarantined, Closed }
    private sealed class SecureGuestRegionRecord
    {
        private readonly object _gate;
        private SecureGuestRegionState _state = SecureGuestRegionState.Binding;
        internal SecureGuestRegionRecord(SecureGuestRegionBinding binding, ProcessHandle owner,
            SecureExecutionBinding execution, SecureExecutionRecord executionSource, GuestRegionMapping mapping,
            PlatformRegionMapping parent, PlatformSecureRegionClass regionClass, object gate)
        { OriginalOwner = this; Binding = binding; Owner = owner; Execution = execution; ExecutionSource = executionSource;
          Mapping = mapping; Parent = parent; RegionClass = regionClass; _gate = gate; }
        internal SecureGuestRegionRecord OriginalOwner { get; }
        internal SecureGuestRegionBinding Binding { get; }
        internal ProcessHandle Owner { get; }
        internal SecureExecutionBinding Execution { get; }
        internal SecureExecutionRecord ExecutionSource { get; }
        internal GuestRegionMapping Mapping { get; }
        internal PlatformRegionMapping Parent { get; }
        internal PlatformSecureRegionClass RegionClass { get; }
        internal object? OriginalRegionBindingSource { get; set; }
        internal bool ClosureConsumed { get; set; }
        internal bool CloseInFlight { get; set; }
        internal bool ClosureSourceChanged { get; set; }
        internal long SourceRevision { get; private set; }
        internal bool SourceRevisionExhausted { get; private set; }
        internal SecureGuestRegionState State
        {
            get { lock (_gate) return _state; }
            set { lock (_gate) { if (CloseInFlight) ClosureSourceChanged = true; if (SourceRevision == long.MaxValue) SourceRevisionExhausted = true; else SourceRevision++; _state = value; } }
        }
    }

    private readonly object _secureGuestRegionGate = new();
    private readonly Dictionary<ulong, SecureGuestRegionRecord> _secureGuestRegions = [];
    private readonly List<SecureGuestRegionRecord> _secureGuestRegionOwners = [];
    private bool HasOriginalSecureGuestRegion(SecureGuestRegionRecord record) =>
        ReferenceEquals(record.OriginalOwner, record) && _secureGuestRegionOwners.Any(x => ReferenceEquals(x, record)) &&
        _secureGuestRegions.TryGetValue(record.Binding.Id, out SecureGuestRegionRecord? route) && ReferenceEquals(route, record);
    private static bool HasCompletedSecureGuestRegion(SecureGuestRegionRecord record) =>
        ReferenceEquals(record.OriginalOwner, record) && record.ClosureConsumed;
    private ulong _nextSecureGuestRegionId = 1;
    // Controlled fault seams outside all source/owner commit locks.
    internal Action? BeforeSecureGuestRegionAdmission { get; set; }
    internal Action? BeforeSecureGuestRegionPublication { get; set; }
    internal Action? BeforeSecureGuestRegionClosureLocalCommit { get; set; }

    internal KernelResult<SecureGuestRegionBinding> BindSecureGuestRegion(ProcessHandle owner,
        SecureExecutionBinding execution, GuestRegionMappingHandle guestMapping, PlatformSecureRegionClass regionClass)
    {
        if (!Enum.IsDefined(regionClass))
            return KernelResult<SecureGuestRegionBinding>.Fail(KernelError.PlatformDenied, "Secure guest region class is invalid.");
        KernelResult<SingProcess> process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(process.Error, process.Message!);
        RegionOwner regionOwner = new RegionOwner(process.Value!.DomainId, owner.Generation);
        KernelResult<SecureExecutionRecord> executionRecord = ResolveSecureGuestExecution(owner, execution);
        if (!executionRecord.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(executionRecord.Error, executionRecord.Message!);
        using ComposedUse composedUse = PinComposedDomain(owner, virtualDomain: executionRecord.Value!.Request.Context.Virtual,
            secureDomain: executionRecord.Value.Request.Context.Secure);
        if (!composedUse.Result.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        KernelResult providerAdmission = RevalidateSecureExecution(execution);
        if (!providerAdmission.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(providerAdmission.Error, providerAdmission.Message!);
        KernelResult<VirtualDomainAuthority.Record> virtualRecord = _virtualDomains.Resolve(owner, executionRecord.Value!.Request.Context.Virtual);
        if (!virtualRecord.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(virtualRecord.Error, virtualRecord.Message!);
        VirtualDomainAuthority.Record virtualValue = virtualRecord.Value!;
        VirtualDomainState expected = virtualValue.State;
        KernelResult<VirtualDomainAuthority.EffectSource> effectSource = _virtualDomains.CaptureEffectSource(virtualValue, expected);
        if (!effectSource.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(effectSource.Error, effectSource.Message!);
        KernelResult<SecureEffectSource> secureSource = CaptureSecureComputeSource(owner, execution);
        if (!secureSource.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(secureSource.Error, secureSource.Message!);
        KernelResult<GuestRegionMapping> mapping = _virtualDomains.ResolveMapping(virtualValue, guestMapping);
        if (!mapping.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(mapping.Error, mapping.Message!);
        if (!virtualValue.PlatformMappings.TryGetValue(guestMapping.MappingId, out (PlatformGuestMapping Guest, PlatformRegionMapping Parent) platform) ||
            mapping.Value!.Domain != executionRecord.Value.Request.Context.Virtual)
            return KernelResult<SecureGuestRegionBinding>.Fail(KernelError.StaleGeneration,
                "Secure guest overlay requires the exact existing provider guest/parent mapping lineage.");
        KernelResult<Func<Func<KernelResult>, KernelResult>> backing;
        try { backing = PrepareSecureGuestBackings(regionOwner, mapping.Value.Region, new(0, mapping.Value.GuestRange.ByteLength)); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<SecureGuestRegionBinding>.Fail(KernelError.PlatformFaulted, $"Secure guest backing query failed before admission: {exception.Message}"); }
        if (!backing.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(backing.Error, backing.Message!);
        KernelResult<(CapabilityId Memory, CapabilityId Region)> sources = _virtualDomains.ResolveMappingSource(virtualValue, guestMapping);
        if (!sources.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(sources.Error, sources.Message!);
        CapabilityRights rights = CapabilityRights.Map |
            ((mapping.Value.Access & GuestMemoryAccess.Read) != 0 ? CapabilityRights.Read : CapabilityRights.None) |
            ((mapping.Value.Access & GuestMemoryAccess.Write) != 0 ? CapabilityRights.Write : CapabilityRights.None) |
            ((mapping.Value.Access & GuestMemoryAccess.Execute) != 0 ? CapabilityRights.Execute : CapabilityRights.None);
        Action? endPending = null;
        Func<Func<KernelResult>, KernelResult>? publishPending = null;
        KernelResult CommitSources(bool begin, Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
                return CommitSecureEffectSource(secureSource.Value!, () => CommitComposedDomainSources(owner, mapping.Value.Domain, () =>
                    CapabilityAuthority.CommitVirtualDomainEffect(sources.Value.Memory, process.Value.DomainId, owner.Generation,
                        CapabilityRights.Map, VirtualizationResourceIds.Memory(mapping.Value.Domain.DomainId), () =>
                    CapabilityAuthority.CommitGuestRegionSource(sources.Value.Region, process.Value.DomainId, owner.Generation,
                        rights, CapabilityResourceIds.MemoryRegion(mapping.Value.Region.RegionId), () =>
                    _virtualDomains.CommitEffectAdmission(virtualValue, expected, () =>
                    {
                        KernelResult<GuestRegionMapping> current = _virtualDomains.ResolveMapping(virtualValue, guestMapping);
                        KernelResult<(CapabilityId Memory, CapabilityId Region)> source = _virtualDomains.ResolveMappingSource(virtualValue, guestMapping);
                        if (!_secureExecutions.TryGetValue(execution.Id, out SecureExecutionRecord? exact) ||
                            !HasOriginalSecureExecution(exact) || !ReferenceEquals(exact, executionRecord.Value) || exact.Request.Binding != execution ||
                            exact.State != SecureExecutionState.Active || !current.IsSuccess || current.Value != mapping.Value ||
                            !source.IsSuccess || source.Value != sources.Value ||
                            !virtualValue.PlatformMappings.TryGetValue(guestMapping.MappingId, out (PlatformGuestMapping Guest, PlatformRegionMapping Parent) currentPlatform) || currentPlatform != platform)
                            return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure guest source tuple changed.");
                        if (!begin) return publishPending!(() => backing.Value!(commit));
                        return PlatformAuthority.CommitSecureGuestMapping(platform.Guest, platform.Parent, true,
                            () => backing.Value!(commit), out endPending, out publishPending);
                    }, effectSource.Value!)))));
        }
        SecureGuestRegionRecord? record = null;
        try { BeforeSecureGuestRegionAdmission?.Invoke(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<SecureGuestRegionBinding>.Fail(KernelError.PlatformFaulted, $"Secure guest admission failed: {exception.Message}"); }
        KernelResult admission = CommitSources(true, () =>
        {
            lock (_secureGuestRegionGate)
            {
                if (_secureGuestRegionOwners.Any(x => !HasCompletedSecureGuestRegion(x) &&
                    x.Owner == owner && x.Mapping.Mapping == guestMapping))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "The exact guest mapping already has a live or quarantined secure overlay.");
                if (_nextSecureGuestRegionId is 0 or ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Secure guest identities cannot wrap or be reused.");
                _secureGuestRegionOwners.EnsureCapacity(_secureGuestRegionOwners.Count + 1);
                record = new(new(_nextSecureGuestRegionId++, 1), owner, execution, executionRecord.Value!, mapping.Value!, platform.Parent, regionClass, _secureGuestRegionGate);
                _secureGuestRegionOwners.Add(record);
                _secureGuestRegions.Add(record.Binding.Id, record);
                return KernelResult.Ok();
            }
        });
        if (!admission.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Fail(admission.Error, admission.Message!);
        try
        {
            long admittedRevision = record!.SourceRevision;
            KernelResult bound;
            try { bound = PlatformAuthority.BindSecureRegion(executionRecord.Value.Request.Context.SecureBinding, platform.Parent, regionClass); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { bound = KernelResult.Fail(KernelError.PlatformFaulted, $"Secure guest bind may have taken effect: {exception.Message}"); }
            if (!bound.IsSuccess)
            {
                lock (_secureGuestRegionGate) record!.State = SecureGuestRegionState.Quarantined;
                return KernelResult<SecureGuestRegionBinding>.Fail(bound.Error, bound.Message!);
            }
            KernelResult<object> bindingSource = PlatformAuthority.CaptureOriginalSecureRegionBinding(executionRecord.Value.Request.Context.SecureBinding, platform.Parent);
            if (!bindingSource.IsSuccess)
            {
                lock (_secureGuestRegionGate) record!.State = SecureGuestRegionState.Quarantined;
                return KernelResult<SecureGuestRegionBinding>.Fail(bindingSource.Error, bindingSource.Message!);
            }
            record!.OriginalRegionBindingSource = bindingSource.Value;
            KernelResult publication;
            try
            {
                publication = RevalidateSecureGuestLineage(record!);
                if (publication.IsSuccess) publication = RevalidateSecureGuestBackings(regionOwner, record!.Mapping.Region);
                BeforeSecureGuestRegionPublication?.Invoke();
                if (publication.IsSuccess) publication = PlatformAuthority.CommitOriginalSecureRegionBinding(bindingSource.Value!, () => CommitSources(false, () =>
                {
                    lock (_secureGuestRegionGate)
                    {
                        if (!_secureGuestRegions.TryGetValue(record!.Binding.Id, out SecureGuestRegionRecord? exact) ||
                            !HasOriginalSecureGuestRegion(record) || !ReferenceEquals(exact, record) || record.State != SecureGuestRegionState.Binding ||
                            record.SourceRevisionExhausted || record.SourceRevision != admittedRevision)
                            return KernelResult.Fail(KernelError.StaleGeneration, "Secure guest pending owner changed.");
                        record.State = SecureGuestRegionState.Active;
                        return KernelResult.Ok();
                    }
                }));
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            { publication = KernelResult.Fail(KernelError.PlatformFaulted, $"Secure guest publication failed: {exception.Message}"); }
            if (publication.IsSuccess) return KernelResult<SecureGuestRegionBinding>.Ok(record!.Binding);
            lock (_secureGuestRegionGate) record!.State = SecureGuestRegionState.Quarantined;
            KernelResult close = CloseSecureGuestRegion(record.Binding);
            return KernelResult<SecureGuestRegionBinding>.Fail(close.IsSuccess ? KernelError.StaleGeneration : KernelError.PlatformFaulted,
                close.IsSuccess ? "Secure guest overlay changed before activation and was exactly closed." :
                    "Secure guest overlay could not be exactly closed and remains quarantined.");
        }
        finally { endPending!(); }
    }

    internal KernelResult CloseSecureGuestRegion(SecureGuestRegionBinding binding)
    {
        SecureGuestRegionRecord record;
        long closingRevision;
        lock (_secureGuestRegionGate)
        {
            if (!_secureGuestRegions.TryGetValue(binding.Id, out record!) || !HasOriginalSecureGuestRegion(record) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure guest overlay identity/generation is required.");
            if (HasCompletedSecureGuestRegion(record)) return KernelResult.Ok();
            if (record.State == SecureGuestRegionState.Closed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Closed projection is not original overlay completion.");
            if (record.CloseInFlight || record.State is SecureGuestRegionState.Binding or SecureGuestRegionState.Closing)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Secure guest overlay provider call is in flight.");
            record.State = SecureGuestRegionState.Closing;
            closingRevision = record.SourceRevision;
            record.ClosureSourceChanged = false;
            record.CloseInFlight = true;
        }
        object? originalBindingSource = record.OriginalRegionBindingSource;
        KernelResult close = KernelResult.Fail(KernelError.PlatformFaulted, "Original secure guest closure has not completed.");
        try
        {
            KernelResult<SecureExecutionRecord> execution = ResolveSecureGuestExecution(record.Owner, record.Execution, allowQuarantined: true);
            close = originalBindingSource is null
                ? KernelResult.Fail(KernelError.PlatformFaulted, "Original secure guest binding fact unavailable.")
                : !execution.IsSuccess
                ? KernelResult.Fail(execution.Error, execution.Message!)
                : !ReferenceEquals(execution.Value, record.ExecutionSource)
                ? KernelResult.Fail(KernelError.PlatformFaulted, "Original secure execution source changed.")
                : PlatformAuthority.UnbindSecureRegion(execution.Value!.Request.Context.SecureBinding, record.Parent, bridgeCommit =>
                {
                    BeforeSecureGuestRegionClosureLocalCommit?.Invoke();
                    lock (_platformMemoryUseGate)
                        return bridgeCommit(() =>
                        {
                            lock (_secureGuestRegionGate)
                            {
                                if (!_secureGuestRegions.TryGetValue(binding.Id, out SecureGuestRegionRecord? current) ||
                                    !HasOriginalSecureGuestRegion(record) || !ReferenceEquals(current, record) || current.Binding != binding ||
                                    record.State != SecureGuestRegionState.Closing || record.ClosureSourceChanged || record.SourceRevision != closingRevision ||
                                    !ReferenceEquals(record.OriginalRegionBindingSource, originalBindingSource))
                                    return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure guest closure owner changed before completion.");
                                record.ClosureConsumed = true;
                                record.State = SecureGuestRegionState.Closed;
                                return KernelResult.Ok();
                            }
                        });
                }, originalBindingSource);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            close = KernelResult.Fail(KernelError.PlatformFaulted, "Secure guest provider close threw; overlay is quarantined.");
        }
        finally
        {
            lock (_secureGuestRegionGate)
            {
                if (!close.IsSuccess && !record.ClosureConsumed) record.State = SecureGuestRegionState.Quarantined;
                record.CloseInFlight = false;
            }
        }
        return close.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(close.Error, close.Message!);
    }

    private KernelResult<SecureExecutionRecord> ResolveSecureGuestExecution(ProcessHandle owner,
        SecureExecutionBinding binding, bool allowQuarantined = false)
    {
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out SecureExecutionRecord? record) || !HasOriginalSecureExecution(record) || record.Request.Binding != binding)
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
        KernelResult execution = RevalidateSecureExecution(record.Execution);
        if (!execution.IsSuccess) return execution;
        KernelResult<VirtualDomainAuthority.Record> virtualRecord = _virtualDomains.Resolve(record.Owner, record.Mapping.Domain);
        if (!virtualRecord.IsSuccess) return KernelResult.Fail(virtualRecord.Error, virtualRecord.Message!);
        KernelResult<GuestRegionMapping> mapping = _virtualDomains.ResolveMapping(virtualRecord.Value!, record.Mapping.Mapping);
        if (!mapping.IsSuccess || mapping.Value != record.Mapping ||
            !virtualRecord.Value!.PlatformMappings.TryGetValue(record.Mapping.Mapping.MappingId, out (PlatformGuestMapping Guest, PlatformRegionMapping Parent) platform) ||
            platform.Parent != record.Parent)
            return KernelResult.Fail(KernelError.StaleGeneration, "Secure guest overlay lineage is stale or has changed.");
        return KernelResult.Ok();
    }

    private KernelResult CloseSecureGuestRegionsForGuestMapping(ProcessHandle owner, GuestRegionMappingHandle mapping)
    {
        SecureGuestRegionBinding[] bindings;
        lock (_secureGuestRegionGate)
            bindings = _secureGuestRegionOwners.Where(x => x.Owner == owner && !HasCompletedSecureGuestRegion(x) &&
                x.Mapping.Mapping == mapping).Select(x => x.Binding).ToArray();
        foreach (SecureGuestRegionBinding binding in bindings)
        {
            KernelResult close = CloseSecureGuestRegion(binding);
            if (!close.IsSuccess) return close;
        }
        return KernelResult.Ok();
    }

    private bool HasSecureGuestRegionsForExecution(SecureExecutionBinding binding)
    {
        lock (_secureGuestRegionGate)
            return _secureGuestRegionOwners.Any(x => x.Execution == binding && !HasCompletedSecureGuestRegion(x));
    }

    private KernelResult CloseSecureGuestRegionsForProcess(ProcessHandle owner) =>
        CloseSecureGuestRegionsWhere(x => x.Owner == owner);

    private KernelResult CloseSecureGuestRegionsForVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain) =>
        CloseSecureGuestRegionsWhere(x => x.Owner == owner && x.Mapping.Domain == domain);

    private KernelResult CloseSecureGuestRegionsWhere(Func<SecureGuestRegionRecord, bool> predicate)
    {
        SecureGuestRegionBinding[] bindings;
        lock (_secureGuestRegionGate)
            bindings = _secureGuestRegionOwners.Where(x => !HasCompletedSecureGuestRegion(x) && predicate(x))
                .Select(x => x.Binding).ToArray();
        foreach (SecureGuestRegionBinding binding in bindings)
        {
            KernelResult close = CloseSecureGuestRegion(binding);
            if (!close.IsSuccess) return close;
        }
        return KernelResult.Ok();
    }

    private KernelResult CloseSecureGuestRegionsForSecureDomain(ProcessHandle owner, SecureDomainHandle domain)
    {
        SecureGuestRegionRecord[] candidates;
        lock (_secureGuestRegionGate)
            candidates = _secureGuestRegionOwners.Where(x => x.Owner == owner && !HasCompletedSecureGuestRegion(x)).ToArray();
        SecureGuestRegionBinding[] bindings = candidates.Where(x => x.ExecutionSource.Request.Context.Secure == domain).Select(x => x.Binding).ToArray();
        foreach (SecureGuestRegionBinding binding in bindings)
        {
            KernelResult close = CloseSecureGuestRegion(binding);
            if (!close.IsSuccess) return close;
        }
        return KernelResult.Ok();
    }
}
