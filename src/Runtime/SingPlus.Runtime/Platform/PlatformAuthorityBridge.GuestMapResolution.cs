using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;
public sealed partial class PlatformAuthorityBridge
{
    internal sealed record GuestMapCleanupProjection(GuestRegionMappingHandle Local, PlatformGuestMapping? Guest, PlatformRegionMapping Parent);
    internal KernelResult<IReadOnlyList<GuestMapCleanupProjection>> ResolveGuestMapCleanupForLocalOwner(
        PlatformChildBinding binding, VirtualDomainAuthority.Record localOwner, IReadOnlyCollection<GuestRegionMappingHandle> localMappings)
    {
        if (localMappings.Count == 0) return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Ok([]);
        GuestMapInvocationFact[] facts;
        lock (_secureDomainLifecycleGate)
        {
            if (!_originalChildOwners.TryGetValue(binding.BindingId, out var child) || child.Binding != binding ||
                !_childBindings.TryGetValue(binding.BindingId, out var route) || !ReferenceEquals(route, child))
                return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Fail(KernelError.StaleGeneration, "Original guest cleanup child unavailable.");
            facts = child.GuestMapInvocations.Where(f => ReferenceEquals(f.Source.LocalMappingOwner, localOwner) &&
                f.Source.LocalMapping is { } local && localMappings.Contains(local)).ToArray();
        }
        if (facts.Length == 0) return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Ok([]);
        var resolved = ResolveOriginalGuestMapsForDrain(binding); // Original lookup/Unmap, never a second Map.
        if (!resolved.IsSuccess) return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Fail(resolved.Error, resolved.Message!);
        PlatformProviderIncarnation source = default;
        try { if (facts.Any(f => f.KnownMapping is not { Closure: PlatformExternalClosureState.Closed })) source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Fail(KernelError.PlatformFaulted, "Guest cleanup source unavailable."); }
        lock (_secureDomainLifecycleGate)
        {
            var projections = new List<GuestMapCleanupProjection>();
            foreach (var fact in facts)
            {
                if (fact.Unresolved || !ReferenceEquals(fact.OriginalOwner, fact) || !fact.Source.Child.GuestMapInvocations.Contains(fact) ||
                    !ReferenceEquals(fact.Source.LocalMappingOwner, localOwner) || fact.Source.LocalMapping is not { } local)
                    return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Fail(KernelError.PlatformFaulted, "Original local guest cleanup correlation changed.");
                if (fact.KnownMapping is { } known)
                {
                    if (!HasCurrentOriginalGuestOwner(known) || !ReferenceEquals(known.AdmissionSource, fact.Source) ||
                        known.Closure != PlatformExternalClosureState.Closed || !HasValidatedGuestClosureFact(known))
                        return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Fail(KernelError.PlatformFaulted, "Original guest closure not proven.");
                    projections.Add(new(local, known.Mapping, fact.Source.Parent.Mapping));
                }
                else if (OriginalGuestMapIdentitySources(fact, source) &&
                    (fact.Observation?.Status == PlatformAuthorityStatus.NotAccepted || fact.Resolution?.Status == PlatformAuthorityStatus.NotAccepted))
                    projections.Add(new(local, null, fact.Source.Parent.Mapping));
                else return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Fail(KernelError.PlatformFaulted, "No exact guest closure/no-effect fact.");
            }
            return KernelResult<IReadOnlyList<GuestMapCleanupProjection>>.Ok(projections);
        }
    }
    private void PinOriginalGuestMap(GuestMapInvocationFact fact)
    {
        var child = fact.Source.Child; var parent = fact.Source.Parent;
        if (!child.LifecycleRevisionExhausted && child.State == fact.ChildState && child.LifecycleRevision == fact.ChildRevision)
        { child.State = PlatformChildDomainState.Faulted; fact.OwnChildFault = true; fact.ChildFaultRevision = child.LifecycleRevision; }
        if (!parent.LifecycleRevisionExhausted && parent.ClosureState == fact.ParentState && parent.LifecycleRevision == fact.ParentRevision)
        { parent.ClosureState = PlatformExternalClosureState.Faulted; fact.OwnParentFault = true; fact.ParentFaultRevision = parent.LifecycleRevision; }
        if (fact.KnownMapping is { Closure: PlatformExternalClosureState.Active, LifecycleRevision: 0, LifecycleRevisionExhausted: false } guest)
        { guest.Closure = PlatformExternalClosureState.Faulted; fact.OwnGuestFault = true; fact.GuestFaultRevision = guest.LifecycleRevision; }
    }
    private bool OriginalGuestMapIdentitySources(GuestMapInvocationFact fact, PlatformProviderIncarnation source) =>
        ReferenceEquals(fact.OriginalOwner, fact) && fact.Source.Child.GuestMapInvocations.Contains(fact) &&
        _originalChildOwners.TryGetValue(fact.Source.Child.Binding.BindingId, out var original) && ReferenceEquals(original, fact.Source.Child) &&
        _childBindings.TryGetValue(original.Binding.BindingId, out var child) && ReferenceEquals(child, original) &&
        _mappings.TryGetValue(fact.Source.Parent.Mapping.MappingId, out var parent) && ReferenceEquals(parent, fact.Source.Parent) &&
        _domains.TryGetValue(child.Binding.ParentBinding.BindingId, out var root) && ReferenceEquals(root, fact.Source.Root) &&
        HasCurrentChildCreateSources(child) && !_backendEpochExhausted && BackendEpoch == fact.Epoch && parent.BackendEpoch == fact.Epoch &&
        fact.Lineage.Keys.All(x => HasCurrentChildCreateSources(x) && HasOriginalNestedHistory(x)) &&
        source.Value != 0 && source == parent.ProviderIncarnation && !parent.LocalReservationReleased;
    private bool OriginalGuestMapSources(GuestMapInvocationFact fact, PlatformProviderIncarnation source) =>
        OriginalGuestMapIdentitySources(fact, source) && !fact.Source.Child.LifecycleRevisionExhausted && !fact.Source.Parent.LifecycleRevisionExhausted &&
        fact.Lineage.All(pair => ReferenceEquals(pair.Key, fact.Source.Child) ||
            !pair.Key.LifecycleRevisionExhausted && pair.Key.LifecycleRevision == pair.Value) &&
        (fact.Source.Child.State == fact.ChildState && fact.Source.Child.LifecycleRevision == fact.ChildRevision ||
         fact.OwnChildFault && fact.Source.Child.State == PlatformChildDomainState.Faulted && fact.Source.Child.LifecycleRevision == fact.ChildFaultRevision) &&
        (fact.Source.Parent.ClosureState == fact.ParentState && fact.Source.Parent.LifecycleRevision == fact.ParentRevision ||
         fact.OwnParentFault && fact.Source.Parent.ClosureState == PlatformExternalClosureState.Faulted && fact.Source.Parent.LifecycleRevision == fact.ParentFaultRevision ||
         fact.Source.Parent.LocalAuthorizationRevoked && fact.Source.Parent.ClosureState == PlatformExternalClosureState.Draining);
    private void ConsumeOwnGuestMapFault(GuestMapInvocationFact fact)
    {
        if (fact.OwnChildFault) { fact.Source.Child.State = fact.ChildState; fact.ChildRevision = fact.Source.Child.LifecycleRevision; fact.OwnChildFault = false; }
        if (fact.OwnParentFault)
        {
            if (fact.Source.Parent.ClosureState == PlatformExternalClosureState.Faulted && fact.Source.Parent.LifecycleRevision == fact.ParentFaultRevision)
                fact.Source.Parent.ClosureState = fact.ParentState;
            fact.ParentRevision = fact.Source.Parent.LifecycleRevision; fact.OwnParentFault = false;
        }
    }
    private KernelResult ResolveOriginalGuestMapsForDrain(PlatformChildBinding binding)
    {
        GuestMapInvocationFact[] facts;
        lock (_secureDomainLifecycleGate)
        {
            if (!_originalChildOwners.TryGetValue(binding.BindingId, out var child) || child.Binding != binding) return KernelResult.Ok();
            facts = child.GuestMapInvocations.Where(f => f.Unresolved || f.KnownMapping is { AdmissionPublished: false, Closure: not PlatformExternalClosureState.Closed }).ToArray();
            if (facts.Length != 0 && (child.PendingChildEffects != 0 || child.TransitionInFlight || child.CloseInFlight ||
                facts.Any(f => f.ResolutionInFlight || f.Source.Parent.PendingGuestMaps != 0)))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original guest Map or cleanup is still in flight.");
        }
        if (facts.Length == 0) return KernelResult.Ok();
        if (_provider is not IPlatformOriginalGuestMapProvider { SupportsOriginalGuestMapResolution: true } provider)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest Map resolution unavailable; drain remains pinned.");
        foreach (var fact in facts)
        {
            bool pending = false;
            try
            {
                var source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalGuestMapSources(fact, source) || fact.ResolutionInFlight || fact.Source.Child.TransitionInFlight ||
                        fact.Source.Child.CloseInFlight || fact.Lineage.Keys.Any(x => x.PendingChildEffects != 0 || x.TransitionInFlight || x.CloseInFlight) || fact.Source.Parent.PendingGuestMaps != 0)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest Map source/pending owner changed.");
                    fact.ResolutionInFlight = true; foreach (var owner in fact.Lineage.Keys) owner.PendingChildEffects++; pending = true;
                }
                if (fact.KnownMapping is null)
                {
                    var outcome = provider.ResolveOriginalGuestMap(fact.OriginalRequest);
                    lock (_secureDomainLifecycleGate) fact.Resolution = outcome;
                    source = CurrentProviderIncarnation();
                    lock (_secureDomainLifecycleGate)
                    {
                        if (!OriginalGuestMapSources(fact, source)) return KernelResult.Fail(KernelError.PlatformFaulted, "Known original Map source changed before cleanup adoption.");
                        if (outcome.Status == PlatformAuthorityStatus.NotAccepted)
                        { ConsumeOwnGuestMapFault(fact); fact.Unresolved = false; continue; }
                        if (!outcome.IsSuccess || !PlatformGuestMemoryContract.ValidateLease(fact.Request, outcome.Value!).IsSuccess)
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest Map outcome unknown/malformed.");
                        var mapping = new PlatformGuestMapping(fact.ReservedIdentity, new(1), fact.Source.Child.Binding, fact.Source.Parent.Mapping);
                        var record = new GuestBindingRecord(mapping, outcome.Value!) { AdmissionSource = fact.Source, AdmissionPublished = false };
                        _guestBindings.Add(mapping.MappingId, record); _originalGuestOwners.Add(mapping.MappingId, record); fact.KnownMapping = record;
                        ConsumeOwnGuestMapFault(fact);
                    }
                }
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalGuestMapSources(fact, source)) return KernelResult.Fail(KernelError.PlatformFaulted, "Original Map source changed before cleanup use.");
                    ConsumeOwnGuestMapFault(fact);
                    if (fact.OwnGuestFault && fact.KnownMapping is { } guest)
                    {
                        if (!HasCurrentOriginalGuestOwner(guest) || guest.LifecycleRevisionExhausted || guest.Closure != PlatformExternalClosureState.Faulted || guest.LifecycleRevision != fact.GuestFaultRevision)
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Independent guest fault cannot be consumed as original Map failure.");
                        guest.Closure = PlatformExternalClosureState.Active; fact.OwnGuestFault = false;
                    }
                }
                var cleanup = UnmapChildGuestRegion(fact.KnownMapping.Mapping, allowCommittedClosure: true);
                if (!cleanup.IsSuccess) return cleanup;
                source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalGuestMapIdentitySources(fact, source) || !HasCurrentOriginalGuestOwner(fact.KnownMapping) ||
                        fact.KnownMapping.Closure != PlatformExternalClosureState.Closed || !HasValidatedGuestClosureFact(fact.KnownMapping))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest cleanup closure not consumed on exact source.");
                    fact.Unresolved = false;
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            { lock (_secureDomainLifecycleGate) PinOriginalGuestMap(fact); return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest Map cleanup unavailable: " + exception.Message); }
            finally
            {
                if (pending) lock (_secureDomainLifecycleGate)
                { fact.ResolutionInFlight = false; foreach (var owner in fact.Lineage.Keys) owner.PendingChildEffects--; }
            }
        }
        return KernelResult.Ok();
    }
}
