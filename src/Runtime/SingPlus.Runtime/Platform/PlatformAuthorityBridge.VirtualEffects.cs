using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    // Original child-owned observation, never permission or validated closure truth.
    // Null response after the invocation preserves ambiguity rather than no-effect.
    private sealed class ChildDeliveryInvocation<TRequest, TOutcome>(
        TRequest request, DomainRecord root, PlatformBackendEpoch epoch)
    {
        public TRequest Request { get; } = request;
        public DomainRecord OriginalRoot { get; } = root;
        public PlatformBackendEpoch AdmissionEpoch { get; } = epoch;
        public PlatformAuthorityResult<TOutcome>? Observation { get; set; }
    }

    internal KernelResult<PlatformVirtualEventReceipt> InjectChildEvent(
        PlatformChildBinding child, PlatformVirtualEventClass eventClass, string sourceResourceId,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        var resolved = ResolveChild(child);
        if (!resolved.IsSuccess) return KernelResult<PlatformVirtualEventReceipt>.Fail(resolved.Error, resolved.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildEventDelivery, PlatformVirtualEventContract.ContractVersion) ||
            _provider is not IPlatformVirtualEventProvider provider)
            return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformUnsupported, "Virtual-event provider is unavailable.");
        ChildBindingRecord record = resolved.Value!;
        var request = new PlatformVirtualEventRequest(record.ProviderLease, eventClass, sourceResourceId);
        var requestValidation = PlatformVirtualEventContract.ValidateRequest(request, record.State);
        if (!requestValidation.IsSuccess)
            return FromProviderFailure<PlatformVirtualEventReceipt>(requestValidation.Status, requestValidation.Message);
        PlatformBackendEpoch backendEpoch = default;
        DomainRecord originalRoot = null!;
        KernelResult Begin()
        {
          lock (_secureDomainLifecycleGate)
          {
            var lineage = ValidateActiveChildLineage(record);
            if (!lineage.IsSuccess) return lineage;
            if (record.TransitionInFlight || !PlatformVirtualEventContract.ValidateRequest(request, record.State).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Child lifecycle changed before virtual-event injection.");
            KernelResult CommitAdmission()
            {
                if (record.PendingChildEffects == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Child event interlock capacity is exhausted.");
                originalRoot = _domains[child.ParentBinding.BindingId];
                record.PendingChildEffects++;
                backendEpoch = BackendEpoch;
                return KernelResult.Ok();
            }
            return sourceCommitConsumer is null ? CommitAdmission() : sourceCommitConsumer(CommitAdmission);
          }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformVirtualEventReceipt>.Fail(admitted.Error, admitted.Message!);
        try
        {
            KernelResult ValidateSource(PlatformProviderIncarnation source)
            {
                if (source.Value == 0 || source != originalRoot.ProviderIncarnation ||
                    BackendEpoch != backendEpoch || originalRoot.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                    !_domains.TryGetValue(child.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, originalRoot) ||
                    root.Binding != child.ParentBinding || root.ProviderLease != record.ProviderLease.ParentDomainLease ||
                    !_childBindings.TryGetValue(child.BindingId, out var exact) || !ReferenceEquals(exact, record) ||
                    record.Binding != child || record.ProviderLease != request.ChildLease)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    QuarantineDomain(originalRoot);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original event source changed; child remains pinned.");
                }
                var lineage = ValidateActiveChildLineage(record);
                if (!lineage.IsSuccess) return lineage;
                return !record.TransitionInFlight && PlatformVirtualEventContract.ValidateRequest(request, record.State).IsSuccess
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformBindingActive, "Event admission changed after source observation.");
            }
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted, "Event source unavailable before invocation."); }
            KernelResult FinalAdmission()
            {
                lock (_secureDomainLifecycleGate)
                {
                    var exact = ValidateSource(source);
                    if (!exact.IsSuccess) return exact;
                    return sourceCommitConsumer is null ? KernelResult.Ok() : sourceCommitConsumer(KernelResult.Ok);
                }
            }
            var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
            if (!final.IsSuccess) return KernelResult<PlatformVirtualEventReceipt>.Fail(final.Error, final.Message!);
            PlatformAuthorityResult<PlatformVirtualEventReceipt> result;
            var invocation = new ChildDeliveryInvocation<PlatformVirtualEventRequest, PlatformVirtualEventReceipt>(request, originalRoot, backendEpoch);
            lock (_secureDomainLifecycleGate) record.EventInvocations.Add(invocation);
            try { result = provider.InjectVirtualEvent(invocation.Request); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) record.State = PlatformChildDomainState.Faulted;
                return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted,
                    $"Virtual-event injection may have taken effect without a receipt: {exception.Message}");
            }
            PlatformVirtualEventReceipt evidence;
            lock (_secureDomainLifecycleGate)
            {
                invocation.Observation = result;
                if (!result.IsSuccess)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return FromProviderFailure<PlatformVirtualEventReceipt>(result.Status, result.Message);
                }
                evidence = result.Value!;
                var validation = PlatformVirtualEventContract.ValidateReceipt(request, evidence);
                if (!validation.IsSuccess || evidence.Sequence <= record.LastEventSequence)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted,
                        validation.IsSuccess ? "Virtual-event evidence sequence is stale or replayed." : validation.Message!);
                }
                record.LastEventSequence = evidence.Sequence;
                // A valid receipt is a cleanup fact even when mutable publication permission is gone.
                if (BackendEpoch != backendEpoch || record.State == PlatformChildDomainState.Faulted)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted,
                        "Backend or child changed during virtual-event injection.");
                }
                var lineage = ValidateActiveChildLineage(record);
                if (!lineage.IsSuccess) return KernelResult<PlatformVirtualEventReceipt>.Fail(lineage.Error, lineage.Message!);
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted, "Known event source unavailable; sequence retained without publication."); }
            bool publicationCommitted = false;
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var exactSource = ValidateSource(source);
                    if (!exactSource.IsSuccess) return exactSource;
                    var current = ValidateActiveChildLineage(record);
                    if (!current.IsSuccess) return current;
                    if (BackendEpoch != backendEpoch || record.TransitionInFlight ||
                        !PlatformVirtualEventContract.ValidateRequest(request, record.State).IsSuccess)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Event owner changed before local publication.");
                    var committed = commit();
                    if (committed.IsSuccess) publicationCommitted = true;
                    return committed;
                }
            }
            var published = completionConsumer is null ? CommitCurrent(() => KernelResult.Ok()) : completionConsumer(CommitCurrent);
            if (!published.IsSuccess) return KernelResult<PlatformVirtualEventReceipt>.Fail(published.Error, published.Message!);
            if (!publicationCommitted)
                return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted, "Original delivery was not committed by the publication consumer.");
            // The successful publication consumer owns its result. Keep only pending or
            // unpublished provider outcomes here; ordinary traffic is not a history ledger.
            lock (_secureDomainLifecycleGate) record.EventInvocations.Remove(invocation);
            return KernelResult<PlatformVirtualEventReceipt>.Ok(evidence);
        }
        finally { lock (_secureDomainLifecycleGate) record.PendingChildEffects--; }
    }

    internal KernelResult<PlatformVirtualTrapEvidence> ObserveChildTrap(PlatformChildBinding child,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        var resolved = ResolveChild(child);
        if (!resolved.IsSuccess) return KernelResult<PlatformVirtualTrapEvidence>.Fail(resolved.Error, resolved.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildTrapDelivery, PlatformVirtualTrapContract.ContractVersion) ||
            _provider is not IPlatformVirtualTrapProvider provider)
            return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformUnsupported, "Neutral trap provider is unavailable.");
        ChildBindingRecord record = resolved.Value!;
        var requestValidation = PlatformVirtualTrapContract.ValidateObservationRequest(record.ProviderLease, record.State);
        if (!requestValidation.IsSuccess)
            return FromProviderFailure<PlatformVirtualTrapEvidence>(requestValidation.Status, requestValidation.Message);
        PlatformBackendEpoch backendEpoch = default;
        DomainRecord originalRoot = null!;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                var lineage = ValidateActiveChildLineage(record);
                if (!lineage.IsSuccess) return lineage;
                if (record.TransitionInFlight ||
                    !PlatformVirtualTrapContract.ValidateObservationRequest(record.ProviderLease, record.State).IsSuccess)
                    return KernelResult.Fail(KernelError.PlatformBindingActive,
                        "Child lifecycle changed before trap observation.");
                KernelResult CommitAdmission()
                {
                    if (record.PendingChildEffects == int.MaxValue)
                        return KernelResult.Fail(KernelError.CapacityExhausted, "Child trap interlock capacity is exhausted.");
                    originalRoot = _domains[child.ParentBinding.BindingId];
                    record.PendingChildEffects++;
                    backendEpoch = BackendEpoch;
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? CommitAdmission() : sourceCommitConsumer(CommitAdmission);
            }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformVirtualTrapEvidence>.Fail(admitted.Error, admitted.Message!);
        try
        {
            KernelResult ValidateSource(PlatformProviderIncarnation source)
            {
                if (source.Value == 0 || source != originalRoot.ProviderIncarnation ||
                    BackendEpoch != backendEpoch || originalRoot.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                    !_domains.TryGetValue(child.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, originalRoot) ||
                    root.Binding != child.ParentBinding || root.ProviderLease != record.ProviderLease.ParentDomainLease ||
                    !_childBindings.TryGetValue(child.BindingId, out var exact) || !ReferenceEquals(exact, record) ||
                    record.Binding != child || record.ProviderLease.ParentDomainLease != root.ProviderLease)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    QuarantineDomain(originalRoot);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original trap source changed; child remains pinned.");
                }
                var lineage = ValidateActiveChildLineage(record);
                if (!lineage.IsSuccess) return lineage;
                return !record.TransitionInFlight && PlatformVirtualTrapContract.ValidateObservationRequest(record.ProviderLease, record.State).IsSuccess
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformBindingActive, "Trap admission changed after source observation.");
            }
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted, "Trap source unavailable before invocation."); }
            KernelResult FinalAdmission()
            {
                lock (_secureDomainLifecycleGate)
                {
                    var exact = ValidateSource(source);
                    if (!exact.IsSuccess) return exact;
                    return sourceCommitConsumer is null ? KernelResult.Ok() : sourceCommitConsumer(KernelResult.Ok);
                }
            }
            var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
            if (!final.IsSuccess) return KernelResult<PlatformVirtualTrapEvidence>.Fail(final.Error, final.Message!);
            PlatformVirtualTrapEvidence evidence;
            PlatformAuthorityResult<PlatformVirtualTrapEvidence> result;
            var invocation = new ChildDeliveryInvocation<PlatformProviderChildDomainLease, PlatformVirtualTrapEvidence>(record.ProviderLease, originalRoot, backendEpoch);
            lock (_secureDomainLifecycleGate) record.TrapInvocations.Add(invocation);
            try { result = provider.ObserveVirtualTrap(invocation.Request); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) record.State = PlatformChildDomainState.Faulted;
                return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted,
                    $"Trap observation lost its provider result: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                invocation.Observation = result;
                if (!result.IsSuccess)
                {
                    var continuityLost = BackendEpoch != backendEpoch || record.State == PlatformChildDomainState.Faulted;
                    record.State = PlatformChildDomainState.Faulted;
                    if (continuityLost)
                        return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted,
                            "Backend or child changed during trap observation.");
                    return FromProviderFailure<PlatformVirtualTrapEvidence>(result.Status, result.Message);
                }
                evidence = result.Value!;
                var validation = PlatformVirtualTrapContract.ValidateEvidence(record.ProviderLease, evidence);
                if (!validation.IsSuccess || evidence.Sequence <= record.LastTrapSequence)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted,
                        validation.IsSuccess ? "Trap evidence sequence is stale or replayed." : validation.Message!);
                }
                record.LastTrapSequence = evidence.Sequence;
                // Valid observation is a cleanup fact even after continuity loss.
                if (BackendEpoch != backendEpoch || record.State == PlatformChildDomainState.Faulted)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted,
                        "Backend or child changed during trap observation.");
                }
                var lineage = ValidateActiveChildLineage(record);
                if (!lineage.IsSuccess) return KernelResult<PlatformVirtualTrapEvidence>.Fail(lineage.Error, lineage.Message!);
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted, "Known trap source unavailable; sequence retained without publication."); }
            bool publicationCommitted = false;
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var exactSource = ValidateSource(source);
                    if (!exactSource.IsSuccess) return exactSource;
                    var current = ValidateActiveChildLineage(record);
                    if (!current.IsSuccess) return current;
                    if (BackendEpoch != backendEpoch || record.TransitionInFlight ||
                        !PlatformVirtualTrapContract.ValidateObservationRequest(record.ProviderLease, record.State).IsSuccess)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Trap owner changed before local publication.");
                    var committed = commit();
                    if (committed.IsSuccess) publicationCommitted = true;
                    return committed;
                }
            }
            var published = completionConsumer is null ? CommitCurrent(() => KernelResult.Ok()) : completionConsumer(CommitCurrent);
            if (!published.IsSuccess) return KernelResult<PlatformVirtualTrapEvidence>.Fail(published.Error, published.Message!);
            if (!publicationCommitted)
                return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted, "Original delivery was not committed by the publication consumer.");
            // The successful publication consumer owns its result. Keep only pending or
            // unpublished provider outcomes here; ordinary traffic is not a history ledger.
            lock (_secureDomainLifecycleGate) record.TrapInvocations.Remove(invocation);
            return KernelResult<PlatformVirtualTrapEvidence>.Ok(evidence);
        }
        finally { lock (_secureDomainLifecycleGate) record.PendingChildEffects--; }
    }
}



