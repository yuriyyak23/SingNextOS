using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalGuestUnmapProvider
{
    public bool SupportsOriginalGuestUnmapResolution => childRuntime is IHybridCpuOriginalGuestUnmapRuntimeV1;
    private sealed class OriginalGuestUnmapInvocation
    {
        public OriginalGuestUnmapInvocation(Mapping mapping, Child child, ExternalOriginalGuestUnmapRequest request)
        { OriginalOwner = this; Mapping = mapping; Child = child; Request = request; State = child.State; Revision = child.StateRevision; }
        public OriginalGuestUnmapInvocation OriginalOwner { get; }
        public Mapping Mapping { get; }
        public Child Child { get; }
        public ExternalOriginalGuestUnmapRequest Request { get; }
        public NeutralChildDomainState State { get; }
        public long Revision { get; }
        public long FaultRevision;
        public bool Invoked, OwnFault;
        public ExternalOriginalGuestUnmapObservation? Raw, Resolution, Validated;
        public ExternalOriginalGuestUnmapObservation? OriginalCanonical;
    }
    public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegion(NeutralGuestMappingLease lease) =>
        SupportsOriginalGuestUnmapResolution ? OriginalGuestUnmapCore(lease, false) : UnmapGuestRegionLegacy(lease);
    public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> ResolveOriginalGuestUnmap(NeutralGuestMappingLease lease) => OriginalGuestUnmapCore(lease, true);
    private bool HasIntactGuestUnmapCanonical(OriginalGuestUnmapInvocation fact) =>
        ReferenceEquals(fact.OriginalOwner,fact) && ReferenceEquals(fact.Mapping.OriginalUnmap,fact) && fact.OriginalCanonical is { } original &&
        ReferenceEquals(fact.Validated,original) && (ReferenceEquals(fact.Raw,original) || ReferenceEquals(fact.Resolution,original)) &&
        ReferenceEquals(original.Request,fact.Request) && ReferenceEquals(fact.Request.Admission,fact.Mapping.OriginalAdmission);
    private bool HasRetainedOriginalGuestClosure(Mapping mapping) =>
        originalMappings.TryGetValue(mapping.Neutral.Handle,out Mapping? original) && ReferenceEquals(original,mapping) &&
        mapping.OriginalUnmap is { Invoked:true, OriginalCanonical: { NoFurtherUse:true, NoEffect:false } raw } fact &&
        HasIntactGuestUnmapCanonical(fact) && ReferenceEquals(fact.Mapping,mapping) && fact.Child.Neutral == mapping.Neutral.ChildLease &&
        originalChildren.TryGetValue(fact.Child.Neutral.Handle,out Child? child) && ReferenceEquals(child,fact.Child) &&
        children.TryGetValue(fact.Child.Neutral.Handle,out Child? childRoute) && ReferenceEquals(childRoute,child) &&
        child.ParentSource is { } parent && HasOriginalParentClosureSources(parent) &&
        mapping.ClosureReceipt is { IsTerminal:true } closed && ReferenceEquals(raw.Raw.Receipt,closed) && raw.Raw.Outcome == ExternalRuntimeOutcome.Closed &&
        closed.Mapping == mapping.External && closed.Operation == fact.Request.Operation &&
        closed.ContractVersion == externalFeatures.ContractVersion && closed.ManifestGeneration == externalFeatures.Generation &&
        mapping.ClosureOperation == fact.Request.Operation && mapping.ClosureLease == mapping.Neutral;
    private bool GuestUnmapSources(OriginalGuestUnmapInvocation fact) =>
        ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Mapping.OriginalUnmap, fact) &&
        ReferenceEquals(fact.Mapping.OriginalAdmission, fact.Request.Admission) &&
        originalMappings.TryGetValue(fact.Mapping.Neutral.Handle, out Mapping? original) && ReferenceEquals(original, fact.Mapping) &&
        mappings.TryGetValue(fact.Mapping.Neutral.Handle, out Mapping? route) && ReferenceEquals(route, fact.Mapping) &&
        originalChildren.TryGetValue(fact.Child.Neutral.Handle, out Child? child) && ReferenceEquals(child, fact.Child) &&
        children.TryGetValue(fact.Child.Neutral.Handle, out Child? current) && ReferenceEquals(current, child) &&
        fact.Child.ParentSource is { } parent && HasOriginalParentClosureSources(parent) &&
        !fact.Child.StateRevisionExhausted &&
        (fact.Child.State == fact.State && fact.Child.StateRevision == fact.Revision ||
         fact.OwnFault && fact.Child.State == NeutralChildDomainState.Faulted && fact.Child.StateRevision == fact.FaultRevision);
    private void PinOriginalGuestUnmap(OriginalGuestUnmapInvocation fact)
    {
        fact.Mapping.ClosureMayHaveEffect = true;
        if (!fact.Child.StateRevisionExhausted && fact.Child.State == fact.State && fact.Child.StateRevision == fact.Revision)
        { fact.Child.State = NeutralChildDomainState.Faulted; fact.OwnFault = true; fact.FaultRevision = fact.Child.StateRevision; }
    }
    private NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> OriginalGuestUnmapCore(NeutralGuestMappingLease lease, bool resolve)
    {
        if (childRuntime is not IHybridCpuOriginalGuestUnmapRuntimeV1 native)
            return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original guest resolution is unavailable.");
        Mapping mapping; OriginalGuestUnmapInvocation fact; bool consumedClosure;
        lock (sync)
        {
            if (!originalMappings.TryGetValue(lease.Handle, out mapping!) || mapping.Neutral != lease)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Stale, "Original guest mapping is absent.");
            if (originalChildren.TryGetValue(lease.ChildLease.Handle, out var sourceChild) && HasSoftwareMemoryPins(sourceChild))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original private software memory source remains pinned.");
            if (pendingGuestUnmaps.Contains(lease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original guest closure lookup is in flight.");
            consumedClosure=HasRetainedOriginalGuestClosure(mapping);
            if(consumedClosure) fact=mapping.OriginalUnmap!;
            else
            {
            if (mapping.ClosureReceipt is not null || mapping.OriginalUnmap is { OriginalCanonical:not null } retained && !HasIntactGuestUnmapCanonical(retained))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original guest closure custody unavailable; no new effect.");
            if (pendingGuestUnmaps.Contains(lease.Handle) || pendingArtifactBinds.Contains(lease.Handle) ||
                pendingChildStarts.Contains(lease.ChildLease.Handle) || pendingChildTransitions.Contains(lease.ChildLease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original guest dependents are pending.");
            if (mapping.ArtifactMayHaveEffect || originalArtifacts.Values.Any(a => !HasRetainedArtifactRelease(a) && a.Neutral.MappingHandle == lease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(resolve ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.Denied, "Original artifact backing requires exact release before Unmap.");
            if (mapping.OriginalUnmap is not { } existing)
            {
                if (resolve || mapping.ClosureMayHaveEffect || mapping.OriginalAdmission is null ||
                    !originalChildren.TryGetValue(lease.ChildLease.Handle, out Child? child) || nextOperation == 0 || nextOperation == ulong.MaxValue)
                    return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original guest request is unavailable.");
                mapping.OriginalUnmap = fact = new(mapping, child, new(mapping.OriginalAdmission, Op()));
            }
            else fact = existing;
            if (!GuestUnmapSources(fact)) return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original guest sources changed.");
            }
            pendingGuestUnmaps.Add(lease.Handle);
        }
        try
        {
            HybridCpuExternalFeatureManifest features = root.QueryFeatures();
            if(consumedClosure)
            {
                lock(sync)
                    return ReferenceEquals(mapping.OriginalUnmap,fact) && HasRetainedOriginalGuestClosure(mapping) &&
                        features.ContractVersion == externalFeatures.ContractVersion && features.Generation == externalFeatures.Generation ?
                        Ok(new NeutralGuestMappingCloseReceipt(lease.Handle,lease.Epoch,lease.ChildLease.Handle,lease.ChildLease.Epoch,lease.ChildLease.ParentLease.Handle,lease.ChildLease.ParentLease.Epoch,true)) :
                        Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Consumed original guest closure source/evidence changed.");
            }
            lock (sync)
                if (!GuestUnmapSources(fact) || features.ContractVersion != externalFeatures.ContractVersion || features.Generation != externalFeatures.Generation)
                    return Fail<NeutralGuestMappingCloseReceipt>(fact.Invoked || resolve ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original guest source changed before effect.");
            ExternalOriginalGuestUnmapObservation? observation = fact.Validated;
            if (observation is null)
            {
                if (!fact.Invoked && !resolve)
                {
                    lock (sync)
                    {
                        if (!GuestUnmapSources(fact)) return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Original guest source changed at invocation commit.");
                        fact.Invoked = true;
                    }
                    observation = native.UnmapOriginalGuest(fact.Request);
                    lock (sync) fact.Raw = observation;
                }
                else observation = native.ResolveOriginalGuestUnmap(fact.Request);
                lock (sync) fact.Resolution = observation;
                ExternalOriginalGuestUnmapObservation? canonical = native.ResolveOriginalGuestUnmap(fact.Request);
                if (observation is null || !ReferenceEquals(observation, canonical) || !ReferenceEquals(observation.Request, fact.Request))
                { lock (sync) PinOriginalGuestUnmap(fact); return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original guest outcome is unknown or foreign."); }
                lock (sync) { fact.Validated = observation; fact.OriginalCanonical ??= observation; }
            }
            features = root.QueryFeatures();
            lock (sync)
            {
                if (!GuestUnmapSources(fact) || !HasIntactGuestUnmapCanonical(fact) || !ReferenceEquals(fact.OriginalCanonical,observation) ||
                    features.ContractVersion != externalFeatures.ContractVersion || features.Generation != externalFeatures.Generation)
                { PinOriginalGuestUnmap(fact); return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Known original guest source changed before consumption."); }
                if (observation.NoEffect && !observation.NoFurtherUse && observation.Raw.Receipt is null)
                {
                    if (fact.OwnFault) fact.Child.State = fact.State;
                    mapping.ClosureMayHaveEffect = false; mapping.OriginalUnmap = null;
                    return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Exact original guest no-effect consumed.");
                }
                ExternalGuestMemoryUnmapReceipt? receipt = observation.Raw.Receipt;
                if (!observation.NoFurtherUse || observation.NoEffect || observation.Raw.Outcome != ExternalRuntimeOutcome.Closed || receipt is null ||
                    receipt.Mapping != mapping.External || receipt.Operation != fact.Request.Operation || !receipt.IsTerminal ||
                    receipt.ContractVersion != externalFeatures.ContractVersion || receipt.ManifestGeneration != externalFeatures.Generation)
                { PinOriginalGuestUnmap(fact); return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original guest closure is malformed."); }
                mapping.ClosureReceipt = receipt; mapping.ClosureOperation = fact.Request.Operation; mapping.ClosureLease = lease;
                mapping.ClosureMayHaveEffect = false; // Exact original consumption settles only this mapping's pending effect.
                if (fact.OwnFault) fact.Child.State = fact.State;
                mappings.Remove(lease.Handle);
                return Ok(new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch, lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { lock (sync) if (fact.Invoked && !consumedClosure) PinOriginalGuestUnmap(fact); return Fail<NeutralGuestMappingCloseReceipt>(fact.Invoked || resolve ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original guest outcome/source unavailable: " + exception.Message); }
        finally
        {
            lock (sync)
            {
                pendingGuestUnmaps.Remove(lease.Handle);
                if (!fact.Invoked && ReferenceEquals(mapping.OriginalUnmap, fact)) mapping.OriginalUnmap = null;
            }
        }
    }
}
