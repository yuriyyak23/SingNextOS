using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct PreparedSecurePublication(
    Func<Func<KernelResult>, KernelResult> Admission,
    Func<Func<KernelResult>, KernelResult> Publication);

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
        internal SecureExecutionClosureFact? LastClosureReceipt;
        // Observation-only identity; saturation disables tracing, never cleanup.
        internal ulong ClosureObservationSequence;
        // Optional observation consumer, never consulted for admission or reclaim.
        internal ISemanticTraceSinkV1? ClosureTraceSink;
    }
    // Cleanup fact only; preserve Closed and Contained independently, never permission.
    private sealed record SecureExecutionClosureFact(SecureExecutionRequest AdmittedRequest,
        SecureExecutionReceipt? AdmittedReceipt, PlatformBackendEpoch AdmissionEpoch, SecureExecutionClosure Receipt);
    private readonly object _secureExecutionGate = new();
    private readonly Dictionary<ulong, SecureExecutionRecord> _secureExecutions = [];
    private readonly HashSet<ProcessHandle> _secureExecutionDrainingOwners = [];
    private readonly HashSet<VirtualDomainHandle> _secureExecutionDrainingVirtual = [];
    private readonly HashSet<SecureDomainHandle> _secureExecutionDrainingSecure = [];
    private readonly HashSet<(ulong, ulong)> _secureExecutionCorrelations = [];
    private ulong _nextSecureExecution = 1;

    internal KernelResult<SecureExecutionBinding> BindSecureExecution(ProcessHandle owner,
        VirtualDomainHandle virtualDomain, CapabilityId virtualCapability,
        SecureDomainHandle secureDomain, CapabilityId secureCapability) =>
        BindSecureExecutionWithClosureTrace(owner, virtualDomain, virtualCapability, secureDomain, secureCapability, null);

    internal KernelResult<SecureExecutionBinding> BindSecureExecutionWithClosureTrace(ProcessHandle owner,
        VirtualDomainHandle virtualDomain, CapabilityId virtualCapability,
        SecureDomainHandle secureDomain, CapabilityId secureCapability, ISemanticTraceSinkV1? closureTraceSink)
    {
        // Provider property getters are callbacks too; query before owner locks.
        ISecureExecutionProvider? provider;
        try { provider = PlatformAuthority.SecureExecutionProvider; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<SecureExecutionBinding>.Fail(KernelError.PlatformFaulted,
                $"Secure provider contract query failed before bind admission: {exception.Message}");
        }
        if (provider is null) return KernelResult<SecureExecutionBinding>.Fail(KernelError.PlatformUnsupported,
            "Exact ProductionSecure execution composition provider contract is unavailable.");
        SecureExecutionRecord? admittedRecord = null;
        var admission = CommitSecureBindSources(owner, virtualDomain, virtualCapability, secureDomain, secureCapability, context =>
        {
            if (_secureExecutionDrainingOwners.Contains(owner) || _secureExecutionDrainingVirtual.Contains(virtualDomain) ||
                _secureExecutionDrainingSecure.Contains(secureDomain) || _secureExecutions.Values.Any(x =>
                    x.State != SecureExecutionState.Closed && (x.Request.Context.Virtual == virtualDomain || x.Request.Context.Secure == secureDomain)))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Composition is already pinned or admissions are draining.");
            if (_nextSecureExecution is 0 or ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Secure composition identity space exhausted.");
            admittedRecord = new(new(new(_nextSecureExecution++, 1), context), owner, virtualCapability, secureCapability, provider);
            admittedRecord.ClosureTraceSink = closureTraceSink;
            _secureExecutions.Add(admittedRecord.Request.Binding.Id, admittedRecord);
            return KernelResult.Ok();
        });
        if (!admission.IsSuccess) return KernelResult<SecureExecutionBinding>.Fail(admission.Error, admission.Message!);
        var record = admittedRecord!;
        try
        {
            var result = record.Provider.BindSecureExecution(record.Request);
            // Known materialization must survive even a subsequent query exception.
            lock (_secureExecutionGate)
                if (result.IsSuccess) record.Receipt = result.Value;
            var currentProvider = PlatformAuthority.SecureExecutionProvider;
            var publication = CommitSecureBindSources(owner, virtualDomain, virtualCapability, secureDomain, secureCapability, current =>
            {
                if (record.State == SecureExecutionState.Binding && result.IsSuccess &&
                    currentProvider == record.Provider &&
                    ExactAdmission(record.Request, result.Value) && current == record.Request.Context &&
                    !_secureExecutionDrainingOwners.Contains(owner) && !_secureExecutionDrainingVirtual.Contains(virtualDomain) &&
                    !_secureExecutionDrainingSecure.Contains(secureDomain) &&
                    _secureExecutionCorrelations.Add((result.Value.Correlation, result.Value.ProviderGeneration)))
                {
                    record.State = SecureExecutionState.Active;
                    return KernelResult.Ok();
                }
                return KernelResult.Fail(KernelError.PlatformFaulted, "Secure bind exact publication refused.");
            });
            if (publication.IsSuccess) return KernelResult<SecureExecutionBinding>.Ok(record.Request.Binding);
            lock (_secureExecutionGate) record.State = SecureExecutionState.Quarantined;
        }
        catch (Exception) { lock (_secureExecutionGate) record.State = SecureExecutionState.Quarantined; }
        _ = CloseSecureExecution(record.Request.Binding);
        return KernelResult<SecureExecutionBinding>.Fail(KernelError.PlatformFaulted,
            "Admission did not prove exact composition; compensation is terminal or authority remains quarantined.");
    }

    private static bool ExactAdmission(SecureExecutionRequest request, SecureExecutionReceipt receipt) =>
        receipt.Request == request && receipt.ContractVersion == 1 && receipt.ProductionSecure &&
        receipt.Correlation != 0 && receipt.ProviderGeneration != 0 && receipt.PolicyGeneration != 0 && receipt.ProtectionGeneration != 0;

    // Trusted pure owner admission/publication only; provider code runs outside these locks.
    private KernelResult CommitSecureBindSources(ProcessHandle owner, VirtualDomainHandle virtualDomain,
        CapabilityId virtualCapability, SecureDomainHandle secureDomain, CapabilityId secureCapability,
        Func<SecureExecutionContext, KernelResult> commit)
    {
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            var active = EnsureProcessAcceptsNewEffects(process.Value!);
            if (!active.IsSuccess) return active;
            return CapabilityAuthority.CommitVirtualDomainEffect(virtualCapability, process.Value!.DomainId, owner.Generation,
                CapabilityRights.Configure, VirtualizationResourceIds.Domain(virtualDomain.DomainId), () =>
                CapabilityAuthority.CommitSecureDomainEffect(secureCapability, process.Value.DomainId, owner.Generation,
                    SecureComputeResourceIds.Domain(secureDomain.DomainId), () =>
                {
                    var domain = _virtualDomains.ResolveAdmission(owner, virtualDomain);
                    if (!domain.IsSuccess) return KernelResult.Fail(domain.Error, domain.Message!);
                    return _virtualDomains.CommitEffectAdmission(domain.Value!, domain.Value!.State, () =>
                    {
                        var context = ResolveComposition(owner, virtualDomain, virtualCapability, secureDomain, secureCapability);
                        return context.IsSuccess
                            ? PlatformAuthority.CommitSecureExecutionContext(context.Value, () => commit(context.Value))
                            : KernelResult.Fail(context.Error, context.Message!);
                    });
                }));
        }
    }

    private KernelResult<SecureExecutionContext> ResolveComposition(ProcessHandle owner,
        VirtualDomainHandle virtualDomain, CapabilityId virtualCapability, SecureDomainHandle secureDomain, CapabilityId secureCapability)
    {
        var virtualRecord = _virtualDomains.ResolveAdmission(owner, virtualDomain);
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

    internal KernelResult<PreparedSecurePublication> PrepareSecurePublication(ProcessHandle owner,
        VirtualComputeContext context)
    {
        if (context.SecureExecution is not { } binding)
            return KernelResult<PreparedSecurePublication>.Fail(KernelError.PlatformDenied, "Exact secure execution required.");
        var valid = RevalidateSecureExecution(binding); // Provider callbacks before all commit locks.
        if (!valid.IsSuccess) return KernelResult<PreparedSecurePublication>.Fail(valid.Error, valid.Message!);
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out var record) || record.Request.Binding != binding ||
                record.Owner != owner || record.Request.Context.Virtual != context.VirtualDomain ||
                record.State != SecureExecutionState.Active || record.Receipt is not { } receipt ||
                !_secureDomainRecords.TryGetValue(record.Request.Context.Secure.DomainId, out var secure))
                return KernelResult<PreparedSecurePublication>.Fail(KernelError.StaleGeneration, "Secure publication composition changed.");
            var expectedState = secure.State;
            var expected = record.Request.Context;
            var subject = expected.Parent.Subject.DomainId;
            KernelResult Admission(Func<KernelResult> commit)
            {
                // Caller holds process admission gate; preserve process -> secure owner -> source order.
                lock (_secureExecutionGate)
                {
                    if (!_secureExecutions.TryGetValue(binding.Id, out var current) || !ReferenceEquals(current, record) ||
                        current.State != SecureExecutionState.Active || current.Receipt != receipt ||
                        _secureExecutionDrainingOwners.Contains(owner) || _secureExecutionDrainingVirtual.Contains(expected.Virtual) ||
                        _secureExecutionDrainingSecure.Contains(expected.Secure) ||
                        !_secureDomainRecords.TryGetValue(expected.Secure.DomainId, out var local) || !ReferenceEquals(local, secure) ||
                        local.Handle != expected.Secure || local.Owner != owner || local.Binding != expected.SecureBinding ||
                        local.State != expectedState || local.State is SecureDomainState.Draining or SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed ||
                        local.PolicyGeneration != expected.LocalPolicyGeneration || local.ProtectionGeneration != expected.LocalProtectionGeneration ||
                        local.TransitionInFlight || local.ClosureInFlight || local.RegionMutationInFlight)
                        return KernelResult.Fail(KernelError.PlatformBindingDraining, "Secure publication local composition stopped or changed.");
                    return CapabilityAuthority.CommitVirtualDomainEffect(record.VirtualCapability, subject, owner.Generation,
                        CapabilityRights.Configure, VirtualizationResourceIds.Domain(expected.Virtual.DomainId), () =>
                        CapabilityAuthority.CommitSecureDomainEffect(record.SecureCapability, subject, owner.Generation,
                            SecureComputeResourceIds.Domain(expected.Secure.DomainId), commit));
                }
            }
            return KernelResult<PreparedSecurePublication>.Ok(new(Admission,
                commit => PlatformAuthority.CommitSecureExecutionContext(expected, commit)));
        }
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
                _secureExecutionDrainingOwners.Contains(record.Owner) ||
                _secureExecutionDrainingVirtual.Contains(record.Request.Context.Virtual) || _secureExecutionDrainingSecure.Contains(record.Request.Context.Secure))
            { record.State = SecureExecutionState.Quarantined; return KernelResult.Fail(KernelError.StaleGeneration, "Composition authority changed."); }
            record.State = SecureExecutionState.Checking;
        }
        bool exact = false;
        ISecureExecutionProvider? currentProvider = null;
        try
        {
            if (PlatformAuthority.SecureExecutionProvider == record.Provider)
            {
                var result = record.Provider.RevalidateSecureExecution(record.Receipt!.Value);
                exact = result.IsSuccess && result.Value == record.Receipt.Value;
            }
            currentProvider = PlatformAuthority.SecureExecutionProvider;
        }
        catch (Exception) { exact = false; }
        lock (_secureExecutionGate)
        {
            var current = ResolveComposition(record.Owner, record.Request.Context.Virtual, record.VirtualCapability,
                record.Request.Context.Secure, record.SecureCapability);
            exact &= record.State == SecureExecutionState.Checking && current.IsSuccess && current.Value == record.Request.Context &&
                currentProvider == record.Provider &&
                !_secureExecutionDrainingOwners.Contains(record.Owner) && !_secureExecutionDrainingVirtual.Contains(record.Request.Context.Virtual) &&
                !_secureExecutionDrainingSecure.Contains(record.Request.Context.Secure);
            record.State = exact ? SecureExecutionState.Active : SecureExecutionState.Quarantined;
            return exact ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "Provider or local composition generation changed; quarantine pins authority.");
        }
    }

    internal KernelResult CloseSecureExecution(SecureExecutionBinding binding, ISemanticTraceSinkV1? traceSink = null)
    {
        SecureExecutionRecord record;
        SecureExecutionReceipt? admittedReceipt;
        PlatformBackendEpoch admissionEpoch;
        ulong observationSequence = 0;
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
            admittedReceipt = record.Receipt;
            admissionEpoch = PlatformAuthority.BackendEpoch;
            traceSink ??= record.ClosureTraceSink;
            if (traceSink is not null && record.ClosureObservationSequence != ulong.MaxValue)
                observationSequence = ++record.ClosureObservationSequence;
            else traceSink = null;
        }
        bool terminal;
        SecureExecutionClosure? rawClosure = null;
        try
        {
            var result = record.Provider.CloseSecureExecution(record.Request, admittedReceipt);
            if (result.IsSuccess)
            {
                rawClosure = result.Value;
                lock (_secureExecutionGate)
                    record.LastClosureReceipt = new(record.Request, admittedReceipt, admissionEpoch, result.Value);
            }
            terminal = result.IsSuccess && result.Value.Request == record.Request && result.Value.Receipt == admittedReceipt &&
                (result.Value.ProviderClosed || result.Value.ProviderEffectContained);
        }
        catch (Exception) { terminal = false; }
        KernelResult completed;
        PlatformBackendEpoch completionEpoch;
        lock (_platformMemoryUseGate)
        {
            completed = PlatformAuthority.CommitSecureExecutionClosure(admissionEpoch, () =>
            {
                lock (_secureExecutionGate)
                {
                    terminal &= _secureExecutions.TryGetValue(binding.Id, out var current) && ReferenceEquals(current, record) &&
                        record.State == SecureExecutionState.Closing;
                      record.State = terminal ? SecureExecutionState.Closed : SecureExecutionState.Quarantined;
                      if (terminal) record.ClosureTraceSink = null;
                    return terminal ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted,
                        "Exact terminal closure/containment missing; composition quarantined.");
                }
            }, out completionEpoch);
            if (!completed.IsSuccess)
                lock (_secureExecutionGate) record.State = SecureExecutionState.Quarantined;
        }
        if (traceSink is not null)
            ObserveSecureExecutionClosure(traceSink, record.Request, admittedReceipt, record.Owner,
                observationSequence, admissionEpoch, completionEpoch, rawClosure, completed.IsSuccess);
        return completed;
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

    // Independent source permission around pure admission/publication; a lifetime pin is insufficient.
    private KernelResult CommitComposedDomainSources(ProcessHandle owner, VirtualDomainHandle? virtualDomain,
        Func<KernelResult> commit, SecureDomainHandle? secureDomain = null)
    {
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
            var records = _secureExecutions.Values.Where(x => x.Owner == owner &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain) &&
                x.State != SecureExecutionState.Closed).ToArray();
            KernelResult Next(int index)
            {
                if (index == records.Length) return commit();
                var record = records[index];
                if (record.State != SecureExecutionState.Active ||
                    _secureExecutionDrainingOwners.Contains(owner) ||
                    _secureExecutionDrainingVirtual.Contains(record.Request.Context.Virtual) ||
                    _secureExecutionDrainingSecure.Contains(record.Request.Context.Secure))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Composed effect source is not active.");
                return CommitSecureBindSources(owner, record.Request.Context.Virtual, record.VirtualCapability,
                    record.Request.Context.Secure, record.SecureCapability, context =>
                        context == record.Request.Context ? Next(index + 1)
                            : KernelResult.Fail(KernelError.StaleGeneration, "Exact composed effect context changed."));
            }
            return Next(0);
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
