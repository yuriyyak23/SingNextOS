using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;
public sealed partial class PlatformAuthorityBridge
{
    private bool OriginalChildCreateIdentitySources(ChildCreateInvocationFact fact, PlatformProviderIncarnation source) =>
        ReferenceEquals(fact.OriginalOwner, fact) && fact.Source.ChildCreateInvocations.Contains(fact) &&
        TryGetCanonicalRootRecord(fact.Source.Binding.BindingId, out var root) && ReferenceEquals(root, fact.Source) &&
        !_backendEpochExhausted && root.BackendEpoch == BackendEpoch && source.Value != 0 && source == root.ProviderIncarnation &&
        !root.ParentRevokeInFlight && !root.ParentRevokeMayHaveEffect &&
        ValidateDomainIdentity(root.Binding, root.Binding.Subject).IsSuccess;
    private bool OriginalChildCreateSources(ChildCreateInvocationFact fact, PlatformProviderIncarnation source) =>
        OriginalChildCreateIdentitySources(fact, source) && !fact.Source.AuthorityRevisionExhausted &&
        (fact.Source.AuthorityState == DomainAuthorityState.Active && fact.Source.AuthorityRevision == fact.Revision ||
         fact.OwnFault && fact.Source.AuthorityState == DomainAuthorityState.Quarantined && fact.Source.AuthorityRevision == fact.FaultRevision);
    private KernelResult ResolveOriginalChildCreatesForRootCleanup(PlatformDomainBinding binding, PlatformDomainIdentity subject)
    {
        ChildCreateInvocationFact[] facts;
        lock (_secureDomainLifecycleGate)
        {
            if (!_domains.TryGetValue(binding.BindingId, out var root) || root.Binding != binding || binding.Subject != subject) return KernelResult.Ok();
            facts = root.ChildCreateInvocations.ToArray();
            if (facts.Length != 0 && (root.PendingChildCreates != 0 || facts.Any(f => f.InFlight)))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original child Create is still pending.");
        }
        if (facts.Length == 0 || _provider is not IPlatformOriginalChildCreateProvider { SupportsOriginalChildCreateResolution: true } provider) return KernelResult.Ok();
        foreach (var fact in facts)
        {
            bool pending = false;
            try
            {
                var source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (OriginalChildCreateIdentitySources(fact, source) && fact.Child is { State: PlatformChildDomainState.Closed } completed &&
                        HasCurrentChildCreateSources(completed) && HasValidatedChildClosureFact(completed))
                    { fact.Source.ChildCreateInvocations.Remove(fact); fact.Source.ChildCreateMayHaveEffect = fact.Source.ChildCreateInvocations.Count != 0; continue; }
                    if (!OriginalChildCreateSources(fact, source) || fact.Source.PendingChildCreates != 0 || fact.InFlight)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original child Create source changed before cleanup.");
                    fact.InFlight = true; fact.Source.PendingChildCreates++; pending = true;
                }
                if (fact.Child is null)
                {
                    var observation = provider.ResolveOriginalChildCreate(fact.OriginalRequest);
                    lock (_secureDomainLifecycleGate) fact.Resolution = observation;
                    source = CurrentProviderIncarnation();
                    lock (_secureDomainLifecycleGate)
                    {
                        if (!OriginalChildCreateSources(fact, source)) return KernelResult.Fail(KernelError.PlatformFaulted, "Known child Create source changed before adoption.");
                        if (observation.Status == PlatformAuthorityStatus.NotAccepted)
                        { fact.Source.ChildCreateInvocations.Remove(fact); fact.Source.ChildCreateMayHaveEffect = fact.Source.ChildCreateInvocations.Count != 0; continue; }
                        if (!observation.IsSuccess || !PlatformChildDomainContract.ValidateLease(fact.Source.ProviderLease, fact.Intent, observation.Value!).IsSuccess)
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original child Create outcome unknown/malformed.");
                        var childBinding = new PlatformChildBinding(fact.ReservedIdentity, new(1), fact.Source.Binding, fact.Source.Binding.Subject.Process);
                        var child = new ChildBindingRecord(childBinding, observation.Value!)
                            { CreationPublished = false, CreateRoot = fact.Source, State = PlatformChildDomainState.Draining, CreateObservation = observation };
                        _childBindings.Add(childBinding.BindingId, child); _originalChildOwners.Add(childBinding.BindingId, child); fact.Child = child;
                    }
                }
                // Draining is local cleanup intent; it is neither provider closure nor new execution admission.
                var cleanup = CloseChildDomain(fact.Child.Binding);
                if (!cleanup.IsSuccess) return cleanup;
                source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalChildCreateIdentitySources(fact, source) || !HasCurrentChildCreateSources(fact.Child) || fact.Child.State != PlatformChildDomainState.Closed || !HasValidatedChildClosureFact(fact.Child))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original created child closure not consumed on exact source.");
                    fact.Source.ChildCreateInvocations.Remove(fact); fact.Source.ChildCreateMayHaveEffect = fact.Source.ChildCreateInvocations.Count != 0;
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Original child Create cleanup unavailable: " + exception.Message); }
            finally { if (pending) lock (_secureDomainLifecycleGate) { fact.InFlight = false; fact.Source.PendingChildCreates--; } }
        }
        return KernelResult.Ok();
    }
}
