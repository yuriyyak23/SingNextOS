using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal readonly record struct PreparedSecurePublication(
    Func<Func<KernelResult>, KernelResult> Admission,
    Func<Func<KernelResult>, KernelResult> Publication);

public sealed partial class RuntimeKernel
{
    private enum SecureExecutionState { Binding, Active, Checking, Closing, Quarantined, Closed }
    private sealed class SecureExecutionRecord
    {
        internal SecureExecutionRecord(SecureExecutionRequest request, ProcessHandle owner, CapabilityId virtualCapability,
            CapabilityId secureCapability, ISecureExecutionProvider provider)
        { OriginalOwner = this; Request = request; Owner = owner; VirtualCapability = virtualCapability; SecureCapability = secureCapability; Provider = provider; }
        internal readonly SecureExecutionRecord OriginalOwner;
        internal readonly SecureExecutionRequest Request;
        internal readonly ProcessHandle Owner;
        internal readonly CapabilityId VirtualCapability;
        internal readonly CapabilityId SecureCapability;
        internal readonly ISecureExecutionProvider Provider;
        internal SecureExecutionReceipt? Receipt;
        internal PlatformAuthorityResult<SecureExecutionReceipt>? BindObservation;
        internal VirtualDomainAuthority.EffectSource? BindVirtualSource;
        internal SecureEffectSource? BindSecureSource;
        internal SecureExecutionState State = SecureExecutionState.Binding;
        internal int EffectUses;
        internal SecureExecutionClosureFact? LastClosureReceipt;
        internal bool CloseInvoked;
        internal bool CloseInFlight;
        internal bool ClosureConsumed;
        internal SecureExecutionCloseInvocationFact? CloseInvocation;
        // Observation-only identity; saturation disables tracing, never cleanup.
        internal ulong ClosureObservationSequence;
        // Optional observation consumer, never consulted for admission or reclaim.
        internal ISemanticTraceSinkV1? ClosureTraceSink;
    }
    // Cleanup fact only; preserve Closed and Contained independently, never permission.
    private sealed record SecureExecutionClosureFact(SecureExecutionRequest AdmittedRequest,
        SecureExecutionReceipt? AdmittedReceipt, PlatformBackendEpoch AdmissionEpoch, SecureExecutionClosure Receipt);
    private sealed class SecureExecutionCloseInvocationFact
    {
        internal SecureExecutionCloseInvocationFact(SecureExecutionRecord owner, PlatformBackendEpoch epoch)
        { OriginalOwner = this; Source = owner; Request = owner.Request; Receipt = owner.Receipt; Epoch = epoch; }
        internal readonly SecureExecutionCloseInvocationFact OriginalOwner;
        internal readonly SecureExecutionRecord Source;
        internal readonly SecureExecutionRequest Request;
        internal readonly SecureExecutionReceipt? Receipt;
        internal readonly PlatformBackendEpoch Epoch;
        internal PlatformAuthorityResult<SecureExecutionClosure>? Observation;
    }
    private readonly object _secureExecutionGate = new();
    private readonly Dictionary<ulong, SecureExecutionRecord> _secureExecutions = [];
    // Existing execution owners remain authoritative when a routing index is lost.
    private readonly List<SecureExecutionRecord> _secureExecutionOwners = [];
    private bool HasOriginalSecureExecution(SecureExecutionRecord record) =>
        ReferenceEquals(record.OriginalOwner, record) && _secureExecutionOwners.Any(x => ReferenceEquals(x, record)) &&
        _secureExecutions.TryGetValue(record.Request.Binding.Id, out SecureExecutionRecord? route) && ReferenceEquals(route, record);
    private IEnumerable<SecureExecutionRecord> SecureExecutionInventory() => _secureExecutionOwners;
    private static bool HasCompletedSecureExecution(SecureExecutionRecord record) =>
        ReferenceEquals(record.OriginalOwner, record) && record.ClosureConsumed;
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
        KernelResult admission = CommitSecureBindSources(owner, virtualDomain, virtualCapability, secureDomain, secureCapability, context =>
        {
            if (_secureExecutionDrainingOwners.Contains(owner) || _secureExecutionDrainingVirtual.Contains(virtualDomain) ||
                _secureExecutionDrainingSecure.Contains(secureDomain) || SecureExecutionInventory().Any(x =>
                    !HasCompletedSecureExecution(x) && (x.Request.Context.Virtual == virtualDomain || x.Request.Context.Secure == secureDomain)))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Composition is already pinned or admissions are draining.");
            if (_nextSecureExecution is 0 or ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Secure composition identity space exhausted.");
            admittedRecord = new(new(new(_nextSecureExecution++, 1), context), owner, virtualCapability, secureCapability, provider);
            KernelResult<VirtualDomainAuthority.Record> domainSource = _virtualDomains.ResolveAdmission(owner, virtualDomain);
            if (!domainSource.IsSuccess) return KernelResult.Fail(domainSource.Error, domainSource.Message!);
            KernelResult<VirtualDomainAuthority.EffectSource> captured = _virtualDomains.CaptureEffectSource(domainSource.Value!, domainSource.Value!.State);
            if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
            admittedRecord.BindVirtualSource = captured.Value;
            KernelResult<SecureEffectSource> secureSource = CaptureSecureEffectSource(_secureDomainRecords[secureDomain.DomainId]);
            if (!secureSource.IsSuccess) return KernelResult.Fail(secureSource.Error, secureSource.Message!);
            admittedRecord.BindSecureSource = secureSource.Value;
            admittedRecord.ClosureTraceSink = closureTraceSink;
            if (_secureExecutionOwners.Count == int.MaxValue || _secureExecutions.Count == int.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Original secure execution inventory is exhausted.");
            _secureExecutionOwners.EnsureCapacity(_secureExecutionOwners.Count + 1);
            _secureExecutions.EnsureCapacity(_secureExecutions.Count + 1);
            _secureExecutionOwners.Add(admittedRecord);
            _secureExecutions.Add(admittedRecord.Request.Binding.Id, admittedRecord);
            return KernelResult.Ok();
        });
        if (!admission.IsSuccess) return KernelResult<SecureExecutionBinding>.Fail(admission.Error, admission.Message!);
        SecureExecutionRecord record = admittedRecord!;
        try
        {
            PlatformAuthorityResult<SecureExecutionReceipt> result = record.Provider.BindSecureExecution(record.Request);
            // Known materialization must survive even a subsequent query exception.
            lock (_secureExecutionGate)
            {
                record.BindObservation = result;
                if (result.IsSuccess) record.Receipt = result.Value;
            }
            ISecureExecutionProvider? currentProvider = PlatformAuthority.SecureExecutionProvider;
            KernelResult publication = CommitSecureBindSources(owner, virtualDomain, virtualCapability, secureDomain, secureCapability, current =>
            {
                if (HasOriginalSecureExecution(record) && record.State == SecureExecutionState.Binding && result.IsSuccess &&
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
            }, record.BindVirtualSource, record.BindSecureSource);
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
        Func<SecureExecutionContext, KernelResult> commit, VirtualDomainAuthority.EffectSource? virtualSource = null,
        SecureEffectSource? secureSource = null)
    {
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
                KernelResult<SingProcess> process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                KernelResult active = EnsureProcessAcceptsNewEffects(process.Value!);
            if (!active.IsSuccess) return active;
            return CapabilityAuthority.CommitVirtualDomainEffect(virtualCapability, process.Value!.DomainId, owner.Generation,
                CapabilityRights.Configure, VirtualizationResourceIds.Domain(virtualDomain.DomainId), () =>
                CapabilityAuthority.CommitSecureDomainEffect(secureCapability, process.Value.DomainId, owner.Generation,
                    SecureComputeResourceIds.Domain(secureDomain.DomainId), () =>
                {
                    KernelResult<VirtualDomainAuthority.Record> domain = _virtualDomains.ResolveAdmission(owner, virtualDomain);
                    if (!domain.IsSuccess) return KernelResult.Fail(domain.Error, domain.Message!);
                    return _virtualDomains.CommitEffectAdmission(domain.Value!, domain.Value!.State, () =>
                    {
                        KernelResult<SecureExecutionContext> context = ResolveComposition(owner, virtualDomain, virtualCapability, secureDomain, secureCapability);
                        if (!context.IsSuccess) return KernelResult.Fail(context.Error, context.Message!);
                        KernelResult Commit() => PlatformAuthority.CommitSecureExecutionContext(context.Value, () => commit(context.Value));
                        return secureSource is null ? Commit() : CommitSecureEffectSource(secureSource, Commit);
                    }, virtualSource);
                }));
        }
    }

    private KernelResult<SecureExecutionContext> ResolveComposition(ProcessHandle owner,
        VirtualDomainHandle virtualDomain, CapabilityId virtualCapability, SecureDomainHandle secureDomain, CapabilityId secureCapability)
    {
        KernelResult<VirtualDomainAuthority.Record> virtualRecord = _virtualDomains.ResolveAdmission(owner, virtualDomain);
        if (!virtualRecord.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(virtualRecord.Error, virtualRecord.Message!);
        KernelResult<CapabilityDescriptorV1> vc = ValidateCapability(owner, virtualCapability, CapabilityRights.Configure);
        KernelResult<CapabilityDescriptorV1> sc = ValidateCapability(owner, secureCapability, CapabilityRights.Configure);
        if (!vc.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(vc.Error, vc.Message!);
        if (!sc.IsSuccess) return KernelResult<SecureExecutionContext>.Fail(sc.Error, sc.Message!);
        if (vc.Value!.ResourceKind != ResourceKind.Virtualization || vc.Value.ResourceId != VirtualizationResourceIds.Domain(virtualDomain.DomainId) ||
            sc.Value!.ResourceKind != ResourceKind.SecureCompute || sc.Value.ResourceId != SecureComputeResourceIds.Domain(secureDomain.DomainId))
            return KernelResult<SecureExecutionContext>.Fail(KernelError.WrongCapabilityResource, "Both exact local configure authorities are required.");
        if (!_secureDomainRecords.TryGetValue(secureDomain.DomainId, out SecureRecord? secureRecord) ||
            !HasOriginalSecureRecord(secureRecord) || secureRecord.Handle != secureDomain || secureRecord.Owner != owner || secureRecord.SourceRevisionExhausted)
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

    internal KernelResult<SecureEffectSource> CaptureSecureComputeSource(ProcessHandle owner, SecureExecutionBinding binding)
    {
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
            return _secureExecutions.TryGetValue(binding.Id, out SecureExecutionRecord? execution) && HasOriginalSecureExecution(execution) && execution.Request.Binding == binding && execution.Owner == owner &&
                _secureDomainRecords.TryGetValue(execution.Request.Context.Secure.DomainId, out SecureRecord? secure) && secure.Owner == owner
                ? CaptureSecureEffectSource(secure)
                : KernelResult<SecureEffectSource>.Fail(KernelError.StaleGeneration, "Original local secure compute source unavailable.");
    }

    internal KernelResult<PreparedSecurePublication> PrepareSecurePublication(ProcessHandle owner,
        VirtualComputeContext context, VirtualDomainAuthority.EffectSource? originalSource = null, SecureEffectSource? originalSecureSource = null)
    {
        if (context.SecureExecution is not { } binding)
            return KernelResult<PreparedSecurePublication>.Fail(KernelError.PlatformDenied, "Exact secure execution required.");
        KernelResult<SecureEffectSource> secureSource = originalSecureSource is null ? CaptureSecureComputeSource(owner, binding) : KernelResult<SecureEffectSource>.Ok(originalSecureSource);
        if (!secureSource.IsSuccess) return KernelResult<PreparedSecurePublication>.Fail(secureSource.Error, secureSource.Message!);
        KernelResult valid = RevalidateSecureExecution(binding); // Provider callbacks before all commit locks.
        if (!valid.IsSuccess) return KernelResult<PreparedSecurePublication>.Fail(valid.Error, valid.Message!);
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out SecureExecutionRecord? record) || !HasOriginalSecureExecution(record) || record.Request.Binding != binding ||
                record.Owner != owner || record.Request.Context.Virtual != context.VirtualDomain ||
                record.State != SecureExecutionState.Active || record.Receipt is not { } receipt ||
                !_secureDomainRecords.TryGetValue(record.Request.Context.Secure.DomainId, out SecureRecord? secure))
                return KernelResult<PreparedSecurePublication>.Fail(KernelError.StaleGeneration, "Secure publication composition changed.");
                SecureDomainState expectedState = secure.State;
                SecureExecutionContext expected = record.Request.Context;
                KernelResult<VirtualDomainAuthority.Record> domainSource = _virtualDomains.ResolveAdmission(owner, expected.Virtual);
            if (!domainSource.IsSuccess) return KernelResult<PreparedSecurePublication>.Fail(domainSource.Error, domainSource.Message!);
                KernelResult<VirtualDomainAuthority.EffectSource> captured = originalSource is null ? _virtualDomains.CaptureEffectSource(domainSource.Value!, domainSource.Value!.State) :
                KernelResult<VirtualDomainAuthority.EffectSource>.Ok(originalSource);
            if (!captured.IsSuccess) return KernelResult<PreparedSecurePublication>.Fail(captured.Error, captured.Message!);
                DomainId subject = expected.Parent.Subject.DomainId;
            KernelResult Admission(Func<KernelResult> commit)
            {
                // Caller holds process admission gate; preserve process -> secure owner -> source order.
                lock (_secureExecutionGate)
                {
                    if (!_secureExecutions.TryGetValue(binding.Id, out SecureExecutionRecord? current) || !ReferenceEquals(current, record) ||
                        !HasOriginalSecureExecution(current) || current.State != SecureExecutionState.Active || current.Receipt != receipt ||
                        _secureExecutionDrainingOwners.Contains(owner) || _secureExecutionDrainingVirtual.Contains(expected.Virtual) ||
                        _secureExecutionDrainingSecure.Contains(expected.Secure) ||
                        !_secureDomainRecords.TryGetValue(expected.Secure.DomainId, out SecureRecord? local) || !ReferenceEquals(local, secure) ||
                        local.Handle != expected.Secure || local.Owner != owner || local.Binding != expected.SecureBinding ||
                        local.State != expectedState || local.State is SecureDomainState.Draining or SecureDomainState.Quarantined or SecureDomainState.Faulted or SecureDomainState.Closed ||
                        local.PolicyGeneration != expected.LocalPolicyGeneration || local.ProtectionGeneration != expected.LocalProtectionGeneration ||
                        local.TransitionInFlight || local.ClosureInFlight || local.RegionMutationInFlight)
                        return KernelResult.Fail(KernelError.PlatformBindingDraining, "Secure publication local composition stopped or changed.");
                    return CommitSecureEffectSource(secureSource.Value!, () => CapabilityAuthority.CommitVirtualDomainEffect(record.VirtualCapability, subject, owner.Generation,
                        CapabilityRights.Configure, VirtualizationResourceIds.Domain(expected.Virtual.DomainId), () =>
                        CapabilityAuthority.CommitSecureDomainEffect(record.SecureCapability, subject, owner.Generation,
                            SecureComputeResourceIds.Domain(expected.Secure.DomainId), () =>
                            _virtualDomains.CommitEffectAdmission(captured.Value!.Owner, captured.Value.Lineage[0].State, commit, captured.Value))));
                }
            }
            return KernelResult<PreparedSecurePublication>.Ok(new(Admission,
                commit => PlatformAuthority.CommitSecureExecutionContext(expected, commit)));
        }
    }

    internal KernelResult RevalidateSecureExecution(SecureExecutionBinding binding)
    {
        SecureExecutionRecord record;
        VirtualDomainAuthority.EffectSource virtualSource;
        SecureEffectSource secureSource;
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out record!) || !HasOriginalSecureExecution(record) || record.Request.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact composition identity/generation required.");
            if (record.State != SecureExecutionState.Active) return KernelResult.Fail(KernelError.PlatformFaulted, "Composition is not active.");
                KernelResult<SecureExecutionContext> current = ResolveComposition(record.Owner, record.Request.Context.Virtual, record.VirtualCapability,
                record.Request.Context.Secure, record.SecureCapability);
            if (!current.IsSuccess || current.Value != record.Request.Context ||
                _secureExecutionDrainingOwners.Contains(record.Owner) ||
                _secureExecutionDrainingVirtual.Contains(record.Request.Context.Virtual) || _secureExecutionDrainingSecure.Contains(record.Request.Context.Secure))
            { record.State = SecureExecutionState.Quarantined; return KernelResult.Fail(KernelError.StaleGeneration, "Composition authority changed."); }
                KernelResult<VirtualDomainAuthority.Record> domainSource = _virtualDomains.ResolveAdmission(record.Owner, record.Request.Context.Virtual);
            if (!domainSource.IsSuccess) { record.State = SecureExecutionState.Quarantined; return KernelResult.Fail(domainSource.Error, domainSource.Message!); }
                KernelResult<VirtualDomainAuthority.EffectSource> captured = _virtualDomains.CaptureEffectSource(domainSource.Value!, domainSource.Value!.State);
            if (!captured.IsSuccess) { record.State = SecureExecutionState.Quarantined; return KernelResult.Fail(captured.Error, captured.Message!); }
            virtualSource = captured.Value!;
                KernelResult<SecureEffectSource> capturedSecure = CaptureSecureEffectSource(_secureDomainRecords[record.Request.Context.Secure.DomainId]);
            if (!capturedSecure.IsSuccess) { record.State = SecureExecutionState.Quarantined; return KernelResult.Fail(capturedSecure.Error, capturedSecure.Message!); }
            secureSource = capturedSecure.Value!;
            record.State = SecureExecutionState.Checking;
        }
        bool exact = false;
        ISecureExecutionProvider? currentProvider = null;
        try
        {
            if (PlatformAuthority.SecureExecutionProvider == record.Provider)
            {
                PlatformAuthorityResult<SecureExecutionReceipt> result = record.Provider.RevalidateSecureExecution(record.Receipt!.Value);
                exact = result.IsSuccess && result.Value == record.Receipt.Value;
            }
            currentProvider = PlatformAuthority.SecureExecutionProvider;
        }
        catch (Exception) { exact = false; }
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
                KernelResult<SecureExecutionContext> current = ResolveComposition(record.Owner, record.Request.Context.Virtual, record.VirtualCapability,
                record.Request.Context.Secure, record.SecureCapability);
            exact &= HasOriginalSecureExecution(record) && record.State == SecureExecutionState.Checking && current.IsSuccess && current.Value == record.Request.Context &&
                CommitSecureEffectSource(secureSource, KernelResult.Ok).IsSuccess &&
                _virtualDomains.CommitEffectAdmission(virtualSource.Owner, virtualSource.Lineage[0].State, KernelResult.Ok, virtualSource).IsSuccess &&
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
        SecureExecutionCloseInvocationFact invocation;
        bool invoke;
        ulong observationSequence = 0;
        lock (_secureExecutionGate)
        {
            if (!_secureExecutions.TryGetValue(binding.Id, out record!) || !HasOriginalSecureExecution(record) || record.Request.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact composition identity/generation required for closure.");
            if (record.ClosureConsumed) return KernelResult.Ok();
            if (HasSecureGuestRegionsForExecution(binding) || HasVirtualIoForSecureExecution(binding) || HasVirtualComputePin(binding))
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Secure execution remains pinned by a secure guest, Virtual-I/O, or virtualized compute dependency.");
            if (record.EffectUses != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Composed provider effect/publication is in flight.");
            if (record.CloseInFlight || record.State is SecureExecutionState.Binding or SecureExecutionState.Checking or SecureExecutionState.Closing)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Composition provider call is in flight.");
            invoke = !record.CloseInvoked;
            if (invoke)
            {
                record.CloseInvocation = new(record, PlatformAuthority.BackendEpoch);
                record.CloseInvoked = true;
            }
            if (record.CloseInvocation is not { } original || !ReferenceEquals(original.OriginalOwner, original) ||
                !ReferenceEquals(original.Source, record) || original.Request != record.Request || original.Receipt != record.Receipt)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original secure closure fact is unavailable; effect cannot repeat.");
            invocation = original;
            record.CloseInFlight = true;
            record.State = SecureExecutionState.Closing;
            admittedReceipt = invocation.Receipt;
            admissionEpoch = invocation.Epoch;
            traceSink ??= record.ClosureTraceSink;
            if (traceSink is not null && record.ClosureObservationSequence != ulong.MaxValue)
                observationSequence = ++record.ClosureObservationSequence;
            else traceSink = null;
        }
        try
        {
        bool terminal;
        SecureExecutionClosure? rawClosure = null;
        try
        {
            if (invoke)
            {
                    PlatformAuthorityResult<SecureExecutionClosure> observed = record.Provider.CloseSecureExecution(invocation.Request, admittedReceipt);
                lock (_secureExecutionGate)
                {
                    invocation.Observation = observed;
                    if (observed.IsSuccess)
                        record.LastClosureReceipt = new(invocation.Request, admittedReceipt, admissionEpoch, observed.Value);
                }
            }
            lock (_secureExecutionGate)
            {
                    PlatformAuthorityResult<SecureExecutionClosure>? result = invocation.Observation;
                if (result is { IsSuccess: true } known) rawClosure = known.Value;
                terminal = result is { IsSuccess: true } exact && exact.Value.Request == invocation.Request &&
                    exact.Value.Receipt == admittedReceipt && (exact.Value.ProviderClosed || exact.Value.ProviderEffectContained);
            }
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
                    terminal &= HasOriginalSecureExecution(record) && ReferenceEquals(record.CloseInvocation, invocation) &&
                        ReferenceEquals(invocation.OriginalOwner, invocation) && invocation.Request == record.Request && invocation.Receipt == record.Receipt &&
                        record.State == SecureExecutionState.Closing;
                      record.State = terminal ? SecureExecutionState.Closed : SecureExecutionState.Quarantined;
                      if (terminal) { record.ClosureConsumed = true; record.ClosureTraceSink = null; }
                    return terminal ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted,
                        "Exact terminal closure/containment missing; composition quarantined.");
                }
            }, out completionEpoch);
            if (!completed.IsSuccess)
                lock (_secureExecutionGate) record.State = SecureExecutionState.Quarantined;
        }
        lock (_secureExecutionGate) record.CloseInFlight = false;
        if (traceSink is not null)
            ObserveSecureExecutionClosure(traceSink, record.Request, admittedReceipt, record.Owner,
                observationSequence, admissionEpoch, completionEpoch, rawClosure, completed.IsSuccess);
        return completed;
        }
        finally { lock (_secureExecutionGate) record.CloseInFlight = false; }
    }

    private KernelResult DrainSecureExecutions(ProcessHandle owner, VirtualDomainHandle? virtualDomain = null, SecureDomainHandle? secureDomain = null)
    {
        SecureExecutionBinding[] bindings;
        lock (_secureExecutionGate)
        {
            if (virtualDomain is { } v) _secureExecutionDrainingVirtual.Add(v);
            else if (secureDomain is { } s) _secureExecutionDrainingSecure.Add(s);
            else _secureExecutionDrainingOwners.Add(owner);
            bindings = SecureExecutionInventory().Where(x => x.Owner == owner && !HasCompletedSecureExecution(x) &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain)).Select(x => x.Request.Binding).ToArray();
        }
        foreach (SecureExecutionBinding binding in bindings) { KernelResult close = CloseSecureExecution(binding); if (!close.IsSuccess) return close; }
        return KernelResult.Ok();
    }

    private KernelResult RevalidateComposedDomain(ProcessHandle owner, VirtualDomainHandle? virtualDomain = null, SecureDomainHandle? secureDomain = null)
    {
        SecureExecutionBinding[] bindings;
        lock (_secureExecutionGate)
            bindings = SecureExecutionInventory().Where(x => x.Owner == owner && !HasCompletedSecureExecution(x) &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain)).Select(x => x.Request.Binding).ToArray();
        foreach (SecureExecutionBinding binding in bindings) { KernelResult valid = RevalidateSecureExecution(binding); if (!valid.IsSuccess) return valid; }
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
                foreach (SecureExecutionRecord record in records) record.EffectUses--;
            }
        }
    }

    // Independent source permission around pure admission/publication; a lifetime pin is insufficient.
    private KernelResult<IReadOnlyDictionary<ulong, SecureEffectSource>> CaptureComposedSecureSources(ProcessHandle owner, VirtualDomainHandle domain)
    {
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
            Dictionary<ulong, SecureEffectSource> sources = [];
            foreach (SecureExecutionRecord? execution in SecureExecutionInventory().Where(x => x.Owner == owner && x.Request.Context.Virtual == domain && !HasCompletedSecureExecution(x)))
            {
                if (!HasOriginalSecureExecution(execution) || execution.State != SecureExecutionState.Active || !_secureDomainRecords.TryGetValue(execution.Request.Context.Secure.DomainId, out SecureRecord? secure))
                    return KernelResult<IReadOnlyDictionary<ulong, SecureEffectSource>>.Fail(KernelError.PlatformFaulted, "Original composed secure source unavailable.");
                    KernelResult<SecureEffectSource> captured = CaptureSecureEffectSource(secure);
                if (!captured.IsSuccess) return KernelResult<IReadOnlyDictionary<ulong, SecureEffectSource>>.Fail(captured.Error, captured.Message!);
                sources.Add(execution.Request.Binding.Id, captured.Value!);
            }
            return KernelResult<IReadOnlyDictionary<ulong, SecureEffectSource>>.Ok(sources);
        }
    }

    private KernelResult CommitComposedDomainSources(ProcessHandle owner, VirtualDomainHandle? virtualDomain,
        Func<KernelResult> commit, SecureDomainHandle? secureDomain = null,
        IReadOnlyDictionary<ulong, SecureEffectSource>? originalSources = null)
    {
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
                SecureExecutionRecord[] records = SecureExecutionInventory().Where(x => x.Owner == owner &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain) &&
                !HasCompletedSecureExecution(x)).ToArray();
            if (originalSources is not null && (records.Length != originalSources.Count || records.Any(x => !originalSources.ContainsKey(x.Request.Binding.Id))))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Captured composed secure inventory changed.");
            KernelResult Next(int index)
            {
                if (index == records.Length) return commit();
                    SecureExecutionRecord record = records[index];
                if (!HasOriginalSecureExecution(record) || record.State != SecureExecutionState.Active ||
                    _secureExecutionDrainingOwners.Contains(owner) ||
                    _secureExecutionDrainingVirtual.Contains(record.Request.Context.Virtual) ||
                    _secureExecutionDrainingSecure.Contains(record.Request.Context.Secure))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Composed effect source is not active.");
                return CommitSecureBindSources(owner, record.Request.Context.Virtual, record.VirtualCapability,
                    record.Request.Context.Secure, record.SecureCapability, context =>
                        context == record.Request.Context ? Next(index + 1)
                            : KernelResult.Fail(KernelError.StaleGeneration, "Exact composed effect context changed."),
                    secureSource: originalSources is null ? null : originalSources[record.Request.Binding.Id]);
            }
            return Next(0);
        }
    }

    // Child/artifact owner validates its own pending effect. Composed permission/revision
    // must not reclassify that same invocation's interlock as an unrelated child effect.
    private KernelResult CommitCapturedComposedSecureRevisions(ProcessHandle owner, VirtualDomainHandle domain,
        IReadOnlyDictionary<ulong, SecureEffectSource> sources, Func<KernelResult> commit)
    {
        lock (_platformMemoryUseGate)
        lock (_secureExecutionGate)
        {
                SecureExecutionRecord[] records = SecureExecutionInventory().Where(x => x.Owner == owner && x.Request.Context.Virtual == domain && !HasCompletedSecureExecution(x)).ToArray();
            if (records.Length != sources.Count || records.Any(x => !sources.ContainsKey(x.Request.Binding.Id)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Captured secure composition inventory changed.");
            KernelResult Next(int index)
            {
                if (index == records.Length) return commit();
                    SecureExecutionRecord execution = records[index]; SecureExecutionContext context = execution.Request.Context;
                if (!HasOriginalSecureExecution(execution) || execution.State != SecureExecutionState.Active || _secureExecutionDrainingOwners.Contains(owner) ||
                    _secureExecutionDrainingVirtual.Contains(domain) || _secureExecutionDrainingSecure.Contains(context.Secure))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Captured secure composition stopped.");
                return CommitSecureEffectSource(sources[execution.Request.Binding.Id], () =>
                    CapabilityAuthority.CommitSecureDomainEffect(execution.SecureCapability, context.Parent.Subject.DomainId,
                        owner.Generation, SecureComputeResourceIds.Domain(context.Secure.DomainId), () =>
                    CapabilityAuthority.CommitVirtualDomainEffect(execution.VirtualCapability, context.Parent.Subject.DomainId,
                        owner.Generation, CapabilityRights.Configure, VirtualizationResourceIds.Domain(domain.DomainId), () => Next(index + 1))));
            }
            return Next(0);
        }
    }

    private ComposedUse PinComposedDomain(ProcessHandle owner, VirtualDomainHandle? virtualDomain = null, SecureDomainHandle? secureDomain = null, bool allowDraining = false)
    {
        lock (_secureExecutionGate)
        {
            SecureExecutionRecord[] records = SecureExecutionInventory().Where(x => x.Owner == owner && !HasCompletedSecureExecution(x) &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain)).ToArray();
            bool participated = SecureExecutionInventory().Any(x => x.Owner == owner &&
                (virtualDomain is null || x.Request.Context.Virtual == virtualDomain) &&
                (secureDomain is null || x.Request.Context.Secure == secureDomain));
            if (records.Any(x => !HasOriginalSecureExecution(x) || x.State != SecureExecutionState.Active) ||
                participated && !allowDraining && (_secureExecutionDrainingOwners.Contains(owner) ||
                    virtualDomain is { } v && _secureExecutionDrainingVirtual.Contains(v) ||
                    secureDomain is { } s && _secureExecutionDrainingSecure.Contains(s)))
                return new(this, [], KernelResult.Fail(KernelError.PlatformFaulted, "Composition blocks new effects/publication."));
            foreach (SecureExecutionRecord? record in records) record.EffectUses++;
            return new(this, records, KernelResult.Ok());
        }
    }

    private void QuarantineSecureExecutionsForBackendReset()
    {
        lock (_secureExecutionGate)
            foreach (SecureExecutionRecord record in SecureExecutionInventory())
                if (!HasCompletedSecureExecution(record)) record.State = SecureExecutionState.Quarantined;
    }

    private bool HasPinnedSecureExecution(ProcessHandle owner)
    {
        lock (_secureExecutionGate)
            return SecureExecutionInventory().Any(x => x.Owner == owner && !HasCompletedSecureExecution(x));
    }
}
