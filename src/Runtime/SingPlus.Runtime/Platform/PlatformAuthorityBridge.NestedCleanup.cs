using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private KernelResult ContainOriginalNestedForDrain(PlatformChildBinding binding)
    {
        ChildBindingRecord owner;
        NestedCreateInvocationFact[] facts;
        lock (_secureDomainLifecycleGate)
        {
            if (!_originalChildOwners.TryGetValue(binding.BindingId, out owner!) || owner.Binding != binding) return KernelResult.Ok();
            if (!HasOriginalNestedHistory(owner)) return KernelResult.Fail(KernelError.PlatformFaulted, "Original nested history unavailable.");
            facts = owner.NestedCreateInvocations.ToArray();
            if (facts.Length == 0) return KernelResult.Ok();
            if (owner.PendingChildEffects != 0 || owner.CloseInFlight || owner.TransitionInFlight || facts.Any(f => f.CleanupInFlight))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original nested invocation remains in flight.");
        }
        var provider=_provider as IPlatformOriginalNestedDomainProvider;
        if (facts.Any(f=>f.Invoked) && provider is not { SupportsOriginalNestedDomains:true })
            return KernelResult.Fail(KernelError.PlatformFaulted, "V1 nested unknown has no original cleanup producer.");
        foreach (var fact in facts)
        {
            bool pending = false;
            try
            {
                var before = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalNestedCleanupSources(owner, fact, before)) return KernelResult.Fail(KernelError.PlatformFaulted, "Original nested cleanup source unavailable.");
                    fact.CleanupInFlight = true; owner.PendingChildEffects++; pending = true;
                }
                var raw = fact.CleanupRaw;
                if (fact.Invoked && raw is null)
                {
                    var result = provider!.ResolveOriginalNestedDomainCleanup(fact.OriginalRequest);
                    if (!result.IsSuccess) result = provider.ContainOriginalNestedDomain(fact.OriginalRequest);
                    if (result.IsSuccess) raw = result.Value;
                }
                lock (_secureDomainLifecycleGate) fact.CleanupRaw ??= raw; // Raw before any subsequent query/source callback.
                var canonical = fact.Invoked ? provider!.ResolveOriginalNestedDomainCleanup(fact.OriginalRequest) : default;
                var after = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalNestedCleanupSources(owner, fact, after) || after != before || fact.Invoked &&
                        (raw is null || !canonical.IsSuccess || !ReferenceEquals(canonical.Value, raw) || !ReferenceEquals(raw.Request, fact.OriginalRequest) ||
                        !raw.NoFurtherAdmission || !raw.NoFurtherChildUse))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original nested closure unknown/foreign; parent/backing remains pinned.");
                }
                var consumed=fact.Invoked ? fact.ClosureConsumer?.Invoke(fact) ?? KernelResult.Ok() :
                    fact.NeverInvokedConsumer?.Invoke(fact) ?? KernelResult.Ok();
                if(!consumed.IsSuccess)return consumed;
                after=CurrentProviderIncarnation();
                lock(_secureDomainLifecycleGate)
                {
                    if(!OriginalNestedCleanupSources(owner,fact,after) || after!=before ||
                        fact.Invoked && !ReferenceEquals(fact.CleanupRaw,raw))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original nested cleanup changed after invocation consumption.");
                    owner.NestedCreateInvocations.Remove(fact); owner.NestedCreatesConsumed++;
                    owner.UnpublishedNestedCreates.RemoveAll(f => ReferenceEquals(f.Invocation, fact));
                    owner.NestedCreateMayHaveEffect = owner.NestedCreateInvocations.Count != 0;
                    if (!owner.NestedCreateMayHaveEffect && owner.State == PlatformChildDomainState.Faulted &&
                        fact.OwnFaultRevision is { } faultRevision && !owner.LifecycleRevisionExhausted && owner.LifecycleRevision == faultRevision)
                    {
                        // Exact own failure can proceed to drain. This is local cleanup
                        // continuity only; native state/permission is never resurrected.
                        owner.State = fact.AdmittedState; owner.NestedCleanupOnly = true;
                    }
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Original nested cleanup query/source unavailable: " + exception.GetType().Name); }
            finally { if (pending) lock (_secureDomainLifecycleGate) { fact.CleanupInFlight = false; owner.PendingChildEffects--; } }
        }
        return KernelResult.Ok();
    }

    private KernelResult ConsumeKnownNestedCreationClosure(PlatformChildBinding binding)
    {
        ChildBindingRecord child;
        NestedCreateInvocationFact? fact;
        lock(_secureDomainLifecycleGate)
        {
            if(!_originalChildOwners.TryGetValue(binding.BindingId,out child!) || child.Binding!=binding)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original closed nested child absent/foreign.");
            fact=child.OriginalNestedCreation;
            if(fact is null)return child.ParentChild is null ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.PlatformFaulted,"Original nested creation history absent.");
        }
        try
        {
            var before=CurrentProviderIncarnation();
            bool Exact(PlatformProviderIncarnation source) =>
                ReferenceEquals(fact.OriginalOwner,fact) && fact.Invoked && ReferenceEquals(fact.Child,child) &&
                ReferenceEquals(child.OriginalNestedCreation,fact) && ReferenceEquals(child.CreateParentChild,fact.Source) &&
                HasOriginalNestedHistory(fact.Source) && _childBindings.TryGetValue(binding.BindingId,out var route) && ReferenceEquals(route,child) &&
                _originalChildOwners.TryGetValue(binding.BindingId,out var original) && ReferenceEquals(original,child) &&
                child.State==PlatformChildDomainState.Closed && !child.CloseInFlight && !child.TransitionInFlight &&
                child.PendingChildEffects==0 && !HasUnclosedGuestDependency(child) && HasValidatedChildClosureFact(child) &&
                HasCurrentChildCreateSources(child) && ReferenceEquals(child.CreateRoot,fact.OriginalRoot) &&
                fact.OriginalRoot is { } root && root.BackendEpoch==fact.AdmissionEpoch && BackendEpoch==fact.AdmissionEpoch &&
                !_backendEpochExhausted && source.Value!=0 && root.ProviderIncarnation==source;
            lock(_secureDomainLifecycleGate)
                if(!Exact(before))return KernelResult.Fail(KernelError.PlatformFaulted,"Original nested child closure source changed.");
            var consumed=fact.ClosureConsumer?.Invoke(fact) ?? KernelResult.Ok();
            if(!consumed.IsSuccess)return consumed;
            var after=CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
                return after==before && Exact(after) ? KernelResult.Ok() :
                    KernelResult.Fail(KernelError.PlatformFaulted,"Original nested closure changed after invocation consumption.");
        }
        catch(Exception exception) when(exception is not StackOverflowException)
        {return KernelResult.Fail(KernelError.PlatformFaulted,"Original nested closure delivery unavailable; retained for retry.");}
    }

    private bool OriginalNestedCleanupSources(ChildBindingRecord owner, NestedCreateInvocationFact fact, PlatformProviderIncarnation source) =>
        ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, owner) && HasOriginalNestedHistory(owner) &&
        owner.NestedCreateInvocations.Contains(fact) && _childBindings.TryGetValue(owner.Binding.BindingId, out var route) && ReferenceEquals(route, owner) &&
        _originalChildOwners.TryGetValue(owner.Binding.BindingId, out var original) && ReferenceEquals(original, owner) &&
        HasCurrentChildCreateSources(owner) && fact.OriginalRoot is { } root && ReferenceEquals(owner.CreateRoot, root) &&
        TryGetCanonicalRootRecord(root.Binding.BindingId, out var currentRoot) && ReferenceEquals(currentRoot, root) &&
        !_backendEpochExhausted && BackendEpoch == fact.AdmissionEpoch && root.BackendEpoch == fact.AdmissionEpoch &&
        source.Value != 0 && source == root.ProviderIncarnation && !root.ParentRevokeMayHaveEffect && !root.ParentRevokeInFlight;
}
