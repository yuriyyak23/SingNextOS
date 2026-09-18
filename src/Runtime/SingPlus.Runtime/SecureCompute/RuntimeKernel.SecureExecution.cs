using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private enum SecureExecutionState { Binding, Active, Checking, Closing, Quarantined, Closed }
    private sealed class SecureExecutionRecord(SecureExecutionRequest request, ProcessHandle owner,
        CapabilityId virtualCapability, CapabilityId secureCapability, ISecureExecutionProvider provider)
    {
        internal readonly SecureExecutionRequest Request = request;
        internal readonly ProcessHandle Owner = owner;
        internal readonly CapabilityId VirtualCapability = virtualCapability;
        internal readonly CapabilityId SecureCapability = secureCapability;
        internal readonly ISecureExecutionProvider Provider = provider;
        internal SecureExecutionReceipt? Receipt;
        internal SecureExecutionState State = SecureExecutionState.Binding;
        internal int EffectUses;
    }
    private readonly object _secureExecutionGate = new();
    private readonly Dictionary<ulong, SecureExecutionRecord> _secureExecutions = [];
    private readonly HashSet<ProcessHandle> _secureExecutionDrainingOwners = [];
    private readonly HashSet<VirtualDomainHandle> _secureExecutionDrainingVirtual = [];
    private readonly HashSet<SecureDomainHandle> _secureExecutionDrainingSecure = [];
    private readonly HashSet<(ulong, ulong)> _secureExecutionCorrelations = [];
    private ulong _nextSecureExecution = 1;

    internal KernelResult<SecureExecutionBinding> BindSecureExecution(ProcessHandle owner,
        VirtualDomainHandle virtualDomain, CapabilityId virtualCapability,
        SecureDomainHandle secureDomain, CapabilityId secureCapability)
    {
        SecureExecutionRecord record;
        lock (_secureExecutionGate)
        {
            var context = ResolveComposition(owner, virtualDomain, virtualCapability, secureDomain, secureCapability);
            if (!context.IsSuccess) return KernelResult<SecureExecutionBinding>.Fail(context.Error, context.Message!);
            var provider = PlatformAuthority.SecureExecutionProvider;
            if (provider is null) return KernelResult<SecureExecutionBinding>.Fail(KernelError.PlatformUnsupported,
                "Exact ProductionSecure execution composition provider contract is unavailable.");
            if (_secureExecutionDrainingOwners.Contains(owner) || _secureExecutionDrainingVirtual.Contains(virtualDomain) ||
                _secureExecutionDrainingSecure.Contains(secureDomain) || _secureExecutions.Values.Any(x =>
                    x.State != SecureExecutionState.Closed && (x.Request.Context.Virtual == virtualDomain || x.Request.Context.Secure == secureDomain)))
                return KernelResult<SecureExecutionBinding>.Fail(KernelError.PlatformBindingActive, "Composition is already pinned or admissions are draining.");
            record = new(new(new(_nextSecureExecution++, 1), context.Value), owner, virtualCapability, secureCapability, provider);
            _secureExecutions.Add(record.Request.Binding.Id, record);
        }
        try
        {
            var result = record.Provider.BindSecureExecution(record.Request);
            lock (_secureExecutionGate)
            {
                // Preserve even malformed materialization for exact compensation.
                if (result.IsSuccess) record.Receipt = result.Value;
                var current = ResolveComposition(owner, virtualDomain, virtualCapability, secureDomain, secureCapability);
                if (record.State == SecureExecutionState.Binding && result.IsSuccess &&
                    PlatformAuthority.SecureExecutionProvider == record.Provider &&
                    ExactAdmission(record.Request, result.Value) && current.IsSuccess && current.Value == record.Request.Context &&
                    !_secureExecutionDrainingOwners.Contains(owner) && !_secureExecutionDrainingVirtual.Contains(virtualDomain) &&
                    !_secureExecutionDrainingSecure.Contains(secureDomain) &&
                    _secureExecutionCorrelations.Add((result.Value.Correlation, result.Value.ProviderGeneration)))
                {
                    record.State = SecureExecutionState.Active;
                    return KernelResult<SecureExecutionBinding>.Ok(record.Request.Binding);
                }
                record.State = SecureExecutionState.Quarantined;
            }
        }
        catch (Exception) { lock (_secureExecutionGate) record.State = SecureExecutionState.Quarantined; }
        _ = CloseSecureExecution(record.Request.Binding);
        return KernelResult<SecureExecutionBinding>.Fail(KernelError.PlatformFaulted,
            "Admission did not prove exact composition; compensation is terminal or authority remains quarantined.");
    }

    private static bool ExactAdmission(SecureExecutionRequest request, SecureExecutionReceipt receipt) =>
        receipt.Request == request && receipt.ContractVersion == 1 && receipt.ProductionSecure &&
        receipt.Correlation != 0 && receipt.ProviderGeneration != 0 && receipt.PolicyGeneration != 0 && receipt.ProtectionGeneration != 0;

    private KernelResult<SecureExecutionContext> ResolveComposition(ProcessHandle owner,
        VirtualDomainHandle virtualDomain, CapabilityId virtualCapability, SecureDomainHandle secureDomain, CapabilityId secureCapability)
    {
        var virtualRecord = _virtualDomains.Resolve(owner, virtualDomain);
        if (!virtualRecord.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(virtualRecord.Error, virtualRecord.Message!);
        var vc = ValidateCapability(owner, virtualCapability, CapabilityRights.Configure);
        var sc = ValidateCapability(owner, secureCapability, CapabilityRights.Configure);
        if (!vc.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(vc.Error, vc.Message!);
        if (!sc.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(sc.Error, sc.Message!);
        if (vc.Value!.ResourceKind != ResourceKind.Virtualization || vc.Value.ResourceId != VirtualizationResourceIds.Domain(virtualDomain.DomainId) ||
            sc.Value!.ResourceKind != ResourceKind.SecureCompute || sc.Value.ResourceId != SecureComputeResourceIds.Domain(secureDomain.DomainId))
            return KernelResult<SecureExecutionContext>.Fail(KernelError.WrongCapabilityResource, "Both exact local configure authorities are required.");
        if (!_secureDomainRecords.TryGetValue(secureDomain.DomainId, out var secureRecord) ||
            secureRecord.Handle != secureDomain || secureRecord.Owner != owner)
            return KernelResult<SecureExecutionContext>.Fail(KernelError.StaleGeneration, "Secure domain identity or generation is absent or stale.");
        if (virtualRecord.Value!.State is VirtualDomainState.Draining or VirtualDomainState.Quarantined or VirtualDomainState.Faulted ||
            secureRecord.State is SecureDomainState.Draining or SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed)
            return KernelResult<SecureExecutionContext>.Fail(KernelError.PlatformFaulted, "Participating domain is not admitting effects.");
        if (virtualRecord.Value.ChildBinding is not { } child || virtualRecord.Value.ParentDomain is not null)
            return KernelResult<SecureExecutionContext>.Fail(KernelError.PlatformUnsupported, "Exact provider child required; model and nested secure composition are unsupported.");
        if (child.Owner != owner || virtualRecord.Value.ParentBinding != child.ParentBinding)
            return KernelResult<SecureExecutionContext>.Fail(KernelError.WrongPlatformDomain, "Virtual record must own the exact child and parent binding.");
        return PlatformAuthority.ResolveSecureExecutionContext(virtualDomain, secureDomain, child, secureRecord.Binding,
            secureRecord.PolicyGeneration, secureRecord.ProtectionGeneration);
    }

    internal KernelResult RevalidateSecureExecution(SecureExecutionBinding binding)
    {
        SecureExecutionRecord record;
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out record!) || record.Request.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact composition identity/generation required.");
            if (record.State != SecureExecutionState.Active) return KernelResult.Fail(KernelError.PlatformFaulted, "Composition is not active.");
            var current = ResolveComposition(record.Owner, record.Request.Context.Virtual, record.VirtualCapability,
                record.Request.Context.Secure, record.SecureCapability);
            if (!current.IsSuccess || current.Value != record.Request.Context ||
                PlatformAuthority.SecureExecutionProvider != record.Provider ||
                _secureExecutionDrainingOwners.Contains(record.Owner) ||
                _secureExecutionDrainingVirtual.Contains(record.Request.Context.Virtual) || _secureExecutionDrainingSecure.Contains(record.Request.Context.Secure))
            { record.State = SecureExecutionState.Quarantined; return KernelResult.Fail(KernelError.StaleGeneration, "Composition authority changed."); }
            record.State = SecureExecutionState.Checking;
        }
        bool exact;
        try { var result = record.Provider.RevalidateSecureExecution(record.Receipt!.Value); exact = result.IsSuccess && result.Value == record.Receipt.Value; }
        catch (Exception) { exact = false; }
        lock (_secureExecutionGate)
        {
            var current = ResolveComposition(record.Owner, record.Request.Context.Virtual, record.VirtualCapability,
                record.Request.Context.Secure, record.SecureCapability);
            exact &= record.State == SecureExecutionState.Checking && current.IsSuccess && current.Value == record.Request.Context &&
                PlatformAuthority.SecureExecutionProvider == record.Provider &&
                !_secureExecutionDrainingOwners.Contains(record.Owner) && !_secureExecutionDrainingVirtual.Contains(record.Request.Context.Virtual) &&
                !_secureExecutionDrainingSecure.Contains(record.Request.Context.Secure);
            record.State = exact ? SecureExecutionState.Active : SecureExecutionState.Quarantined;
            return exact ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "Provider or local composition generation changed; quarantine pins authority.");
        }
    }

    internal KernelResult CloseSecureExecution(SecureExecutionBinding binding)
    {
        SecureExecutionRecord record;
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out record!) || record.Request.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact composition identity/generation required for closure.");
            if (record.State == SecureExecutionState.Closed) return KernelResult.Ok();
            if (HasSecureGuestRegionsForExecution(binding) || HasVirtualIoForSecureExecution(binding) || HasVirtualComputePin(binding))
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Secure execution remains pinned by a secure guest, Virtual-I/O, or virtualized compute dependency.");
            if (record.EffectUses != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Composed provider effect/publication is in flight.");
            if (record.State is SecureExecutionState.Binding or SecureExecutionState.Checking or SecureExecutionState.Closing)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Composition provider call is in flight.");
            record.State = SecureExecutionState.Closing;
        }
        bool terminal;
        try
        {
            var result = record.Provider.CloseSecureExecution(record.Request, record.Receipt);
            terminal = result.IsSuccess && result.Value.Request == record.Request && result.Value.Receipt == record.Receipt &&
                (result.Value.ProviderClosed || result.Value.ProviderEffectContained);
        }
        catch (Exception) { terminal = false; }
        lock (_secureExecutionGate)
        {
            terminal &= record.State == SecureExecutionState.Closing;
            record.State = terminal ? SecureExecutionState.Closed : SecureExecutionState.Quarantined;
            return terminal ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "Exact terminal closure/containment missing; composition quarantined.");
        }
    }

    private KernelResult DrainSecureExecutions(ProcessHandle owner, VirtualDomainHandle? virtualDomain = null, SecureDomainHandle? secureDomain = null)
    {
        SecureExecutionBinding[] bindings;
        lock (_secureExecutionGate)
        {
            if (virtualDomain is { } v) _secureExecutionDrainingVirtual.Add(v);
            else if (secureDomain is { } s) _secureExecutionDrainingSecure.Add(s);
            else _secureExecutionDrainingOwners.Add(owner);
            bindings = _secureExecutions.Values.Where(x => x.Owner == owner && x.State != SecureExecutionState.Closed &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain)).Select(x => x.Request.Binding).ToArray();
        }
        foreach (var binding in bindings) { var close = CloseSecureExecution(binding); if (!close.IsSuccess) return close; }
        return KernelResult.Ok();
    }

    private KernelResult RevalidateComposedDomain(ProcessHandle owner, VirtualDomainHandle? virtualDomain = null, SecureDomainHandle? secureDomain = null)
    {
        SecureExecutionBinding[] bindings;
        lock (_secureExecutionGate)
            bindings = _secureExecutions.Values.Where(x => x.Owner == owner && x.State != SecureExecutionState.Closed &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain)).Select(x => x.Request.Binding).ToArray();
        foreach (var binding in bindings) { var valid = RevalidateSecureExecution(binding); if (!valid.IsSuccess) return valid; }
        return KernelResult.Ok();
    }

    private sealed class ComposedUse(RuntimeKernel kernel, SecureExecutionRecord[] records, KernelResult result) : IDisposable
    {
        internal KernelResult Result { get; } = result;
        internal bool HasBindings => records.Length != 0;
        private bool _disposed;
        public void Dispose()
        {
            lock (kernel._secureExecutionGate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var record in records) record.EffectUses--;
            }
        }
    }

    private ComposedUse PinComposedDomain(ProcessHandle owner, VirtualDomainHandle? virtualDomain = null, SecureDomainHandle? secureDomain = null, bool allowDraining = false)
    {
        lock (_secureExecutionGate)
        {
            var records = _secureExecutions.Values.Where(x => x.Owner == owner && x.State != SecureExecutionState.Closed &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain)).ToArray();
            var participated = _secureExecutions.Values.Any(x => x.Owner == owner &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain));
            if (records.Any(x => x.State != SecureExecutionState.Active) ||
                participated && !allowDraining && (_secureExecutionDrainingOwners.Contains(owner) ||
                    virtualDomain is { } v && _secureExecutionDrainingVirtual.Contains(v) ||
                    secureDomain is { } s && _secureExecutionDrainingSecure.Contains(s)))
                return new(this, [], KernelResult.Fail(KernelError.PlatformFaulted, "Composition blocks new effects/publication."));
            foreach (var record in records) record.EffectUses++;
            return new(this, records, KernelResult.Ok());
        }
    }

    private void QuarantineSecureExecutionsForBackendReset()
    {
        lock (_secureExecutionGate)
            foreach (var record in _secureExecutions.Values)
                if (record.State != SecureExecutionState.Closed) record.State = SecureExecutionState.Quarantined;
    }

    private bool HasPinnedSecureExecution(ProcessHandle owner)
    {
        lock (_secureExecutionGate)
            return _secureExecutions.Values.Any(x => x.Owner == owner && x.State != SecureExecutionState.Closed);
    }
}
