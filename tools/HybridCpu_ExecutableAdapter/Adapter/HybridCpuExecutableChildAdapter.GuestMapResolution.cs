using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace YAKSys_Hybrid_CPU.ExecutableAdapter;
public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalGuestMapProvider
{
    public bool SupportsOriginalGuestMapResolution => childRuntime is IHybridCpuOriginalGuestMapRuntimeV1;
    private static bool GuestMapHistory(Child child) => (ulong)child.OriginalGuestMaps.Count == child.OriginalGuestMapsIssued &&
        child.OriginalGuestMaps.All(f => ReferenceEquals(f.OriginalOwner,f) && ReferenceEquals(f.Child,child)) &&
        child.OriginalGuestMaps.Distinct(ReferenceEqualityComparer.Instance).Count() == child.OriginalGuestMaps.Count &&
        child.OriginalGuestMaps.Select(f=>f.Request).Distinct(ReferenceEqualityComparer.Instance).Count() == child.OriginalGuestMaps.Count;
    private sealed class OriginalGuestMapInvocation
    {
        public OriginalGuestMapInvocation(Child child, NeutralOriginalGuestMapRequest request, ExternalOriginalGuestMapRequest external, ulong identity)
        { OriginalOwner = this; Child = child; Request = request; External = external; Identity = identity; State = child.State; Revision = child.StateRevision; }
        public OriginalGuestMapInvocation OriginalOwner { get; }
        public Child Child { get; }
        public NeutralOriginalGuestMapRequest Request { get; }
        public ExternalOriginalGuestMapRequest External { get; }
        public ulong Identity { get; }
        public NeutralChildDomainState State { get; }
        public long Revision { get; }
        public bool Invoked, OwnFault, Consumed, LocalNoEffect;
        public long FaultRevision;
        public ExternalOriginalGuestMapObservation? Raw, Resolution, Validated;
        public Mapping? Mapping;
        public Dictionary<Child, long> Ancestors { get; } = [];
    }
    public NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegion(NeutralGuestMappingRequest request) =>
        SupportsOriginalGuestMapResolution ? MapOriginalGuest(new(request)) : MapGuestRegionLegacy(request);
    public NeutralVirtualizationResult<NeutralGuestMappingLease> MapOriginalGuest(NeutralOriginalGuestMapRequest request) => GuestMapCore(request, false);
    public NeutralVirtualizationResult<NeutralGuestMappingLease> ResolveOriginalGuestMap(NeutralOriginalGuestMapRequest request) => GuestMapCore(request, true);
    private bool GuestMapSources(OriginalGuestMapInvocation fact) =>
        GuestMapHistory(fact.Child) && ReferenceEquals(fact.OriginalOwner, fact) && fact.Child.OriginalGuestMaps.Contains(fact) &&
        ReferenceEquals(fact.Child.CreateReceipt, fact.External.Admission) &&
        originalChildren.TryGetValue(fact.Child.Neutral.Handle, out Child? child) && ReferenceEquals(child, fact.Child) &&
        children.TryGetValue(child.Neutral.Handle, out Child? route) && ReferenceEquals(route, child) &&
        child.ParentSource is { } parent && HasOriginalParentClosureSources(parent) && !child.StateRevisionExhausted &&
        (child.NestedParent is null || NestedIdentity(child) && fact.Ancestors.All(pair =>
            !pair.Key.StateRevisionExhausted && pair.Key.StateRevision == pair.Value)) &&
        (fact.Consumed || child.State == fact.State && child.StateRevision == fact.Revision ||
         fact.OwnFault && child.State == NeutralChildDomainState.Faulted && child.StateRevision == fact.FaultRevision);
    private void PinOriginalGuestMap(OriginalGuestMapInvocation fact)
    {
        fact.Child.GuestMapMayHaveEffect = true;
        if (!fact.Child.StateRevisionExhausted && fact.Child.State == fact.State && fact.Child.StateRevision == fact.Revision)
        { fact.Child.State = NeutralChildDomainState.Faulted; fact.OwnFault = true; fact.FaultRevision = fact.Child.StateRevision; }
    }
    private NeutralVirtualizationResult<NeutralGuestMappingLease> GuestMapCore(NeutralOriginalGuestMapRequest original, bool resolve)
    {
        if (original is null || childRuntime is not IHybridCpuOriginalGuestMapRuntimeV1 native)
            return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Original guest Map capability unavailable.");
        NeutralGuestMappingRequest request = original.MappingRequest;
        OriginalGuestMapInvocation fact; Child child;
        lock (sync)
        {
            if (!originalChildren.TryGetValue(request.ChildLease.Handle, out child!) || child.Neutral != request.ChildLease || !GuestMapHistory(child))
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Stale, "Original guest Map child absent.");
            OriginalGuestMapInvocation? prior = child.OriginalGuestMaps.SingleOrDefault(f => ReferenceEquals(f.Request, original));
            if (prior is { Consumed: true } && ReferenceEquals(prior.OriginalOwner, prior) && ReferenceEquals(prior.Child, child) &&
                (prior.LocalNoEffect || prior.Validated is { NoEffect: true }))
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked, "Completed exact original Map no-effect.");
            if (child.NestedParent is not null && !NestedIdentity(child))
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Original nested guest factory/history unavailable.");
            if (pendingGuestMaps.ContainsKey(child.Neutral.Handle) || pendingChildStarts.Contains(child.Neutral.Handle) || pendingChildTransitions.Contains(child.Neutral.Handle) ||
                GuestLineage(child).Any(pendingNestedSources.Contains))
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Original guest Map interlock pending.");
            if (prior is null)
            {
                if (resolve || child.GuestMapMayHaveEffect || !TryChild(request.ChildLease, out _, out _) ||
                    child.State != NeutralChildDomainState.Created || child.CreateReceipt is null ||
                    child.OriginalGuestMapsIssued == ulong.MaxValue || nextIdentity == 0 || nextIdentity == ulong.MaxValue || nextOperation == 0 || nextOperation == ulong.MaxValue)
                    return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Original guest Map request/source unavailable.");
                NeutralVirtualizationResult valid = NeutralGuestMemoryContract.ValidateMap(request, child.State);
                if (!valid.IsSuccess) return Fail<NeutralGuestMappingLease>(valid);
                fact = new(child, original, new(child.CreateReceipt, new(request.GuestRange.Offset, checked((ulong)request.GuestRange.Length), Op())), Next());
                foreach (Child? ancestor in GuestLineage(child).Skip(1))
                {
                    if (ancestor.StateRevisionExhausted || ancestor.State is not (NeutralChildDomainState.Created or NeutralChildDomainState.Running or NeutralChildDomainState.Parked) ||
                        pendingChildStarts.Contains(ancestor.Neutral.Handle) || pendingChildTransitions.Contains(ancestor.Neutral.Handle) ||
                        pendingChildCloses.Contains(ancestor.Neutral.Handle) || HasPendingGuestAdmission(ancestor.Neutral))
                        return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked, "Nested guest ancestor is unavailable or pending.");
                    fact.Ancestors.Add(ancestor, ancestor.StateRevision);
                }
                child.OriginalGuestMaps.Add(fact);
                child.OriginalGuestMapsIssued++;
            }
            else fact = prior;
            if (!GuestMapSources(fact)) return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Original guest Map sources changed.");
            if (fact.Consumed && (fact.LocalNoEffect || fact.Validated is { NoEffect: true }))
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked, "Exact original Map no-effect already consumed.");
            if (fact.Mapping is { } adopted)
            {
                if (!originalMappings.TryGetValue(adopted.Neutral.Handle,out Mapping? owner) || !ReferenceEquals(owner,adopted))
                    return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,"Original Map acquisition owner changed; equal lease is not continuity.");
                return resolve ? Ok(adopted.Neutral) : Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Stale, "Original Map request already consumed.");
            }
            pendingGuestMaps.Add(child.Neutral.Handle, 1);
            foreach (Child source in GuestLineage(child)) pendingNestedSources.Add(source);
        }
        try
        {
            HybridCpuExternalFeatureManifest features = root.QueryFeatures();
            lock (sync)
                if (!GuestMapSources(fact) || features.ContractVersion != externalFeatures.ContractVersion || features.Generation != externalFeatures.Generation)
                    return Fail<NeutralGuestMappingLease>(fact.Invoked || resolve ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original guest Map source changed before effect.");
            ExternalOriginalGuestMapObservation? observation = fact.Validated;
            if (observation is null)
            {
                if (!fact.Invoked && !resolve)
                {
                    lock (sync)
                    {
                        if (!GuestMapSources(fact)) return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked, "Original Map source changed at invocation commit.");
                        fact.Invoked = true;
                    }
                    observation = native.MapOriginalGuest(fact.External);
                    lock (sync) fact.Raw = observation;
                }
                else observation = native.ResolveOriginalGuestMap(fact.External);
                lock (sync) fact.Resolution = observation;
                ExternalOriginalGuestMapObservation? canonical = native.ResolveOriginalGuestMap(fact.External);
                lock (sync) fact.Raw ??= observation;
                if (observation is null || !ReferenceEquals(observation, canonical) || !ReferenceEquals(observation, fact.Raw) || !ReferenceEquals(observation.Request, fact.External))
                { lock (sync) PinOriginalGuestMap(fact); return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Original guest Map outcome unknown/foreign."); }
                lock (sync) fact.Validated = observation;
            }
            features = root.QueryFeatures();
            lock (sync)
            {
                if (!GuestMapSources(fact) || features.ContractVersion != externalFeatures.ContractVersion || features.Generation != externalFeatures.Generation)
                { PinOriginalGuestMap(fact); return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Known guest Map source changed before adoption."); }
                if (observation.NoEffect && observation.Raw.Receipt is null)
                {
                    if (fact.OwnFault) child.State = fact.State;
                    child.GuestMapMayHaveEffect = false; fact.Consumed = true;
                    return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked, "Exact original guest Map no-effect consumed.");
                }
                ExternalGuestMemoryMapReceipt? receipt = observation.Raw.Receipt;
                if (observation.NoEffect || observation.Raw.Outcome != ExternalRuntimeOutcome.Succeeded || receipt is null ||
                    receipt.Mapping.Handle.Value == Guid.Empty || receipt.Mapping.Epoch.Value == 0 || receipt.Mapping.Child != child.External ||
                    receipt.Operation != fact.External.MappingRequest.Operation || receipt.ChildOffsetBytes != fact.External.MappingRequest.ChildOffsetBytes ||
                    receipt.LengthBytes != fact.External.MappingRequest.LengthBytes || receipt.ContractVersion != externalFeatures.ContractVersion || receipt.ManifestGeneration != externalFeatures.Generation)
                { PinOriginalGuestMap(fact); return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous, "Original guest Map receipt malformed."); }
                NeutralGuestMappingLease lease = new NeutralGuestMappingLease(request.ChildLease, request.ParentMapping, request.GuestRange, request.Access, new(fact.Identity), new(1));
                Mapping mapped = new Mapping(lease, receipt.Mapping) { OriginalAdmission = receipt, AdmissionPublished = !resolve };
                originalMappings.Add(lease.Handle, mapped); mappings.Add(lease.Handle, mapped); fact.Mapping = mapped;
                if (fact.OwnFault) child.State = fact.State;
                child.GuestMapMayHaveEffect = false; fact.Consumed = true;
                return Ok(lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { lock (sync) if (fact.Invoked) PinOriginalGuestMap(fact); return Fail<NeutralGuestMappingLease>(fact.Invoked || resolve ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original guest Map unavailable: " + exception.Message); }
        finally
        {
            lock (sync)
            {
                pendingGuestMaps.Remove(child.Neutral.Handle);
                foreach (Child source in GuestLineage(child)) pendingNestedSources.Remove(source);
                if (!fact.Invoked) { fact.LocalNoEffect = true; fact.Consumed = true; }
            }
        }
    }
    private static IEnumerable<Child> GuestLineage(Child child)
    { for (Child? current = child; current is not null; current = current.NestedParent) yield return current; }
}
