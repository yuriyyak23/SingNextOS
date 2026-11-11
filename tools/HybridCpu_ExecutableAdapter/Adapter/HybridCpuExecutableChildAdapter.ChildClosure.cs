using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalChildCloseProvider
{
    public bool SupportsOriginalChildCloseResolution => childRuntime is IHybridCpuOriginalChildCloseRuntimeV1;
    private sealed class OriginalChildCloseInvocation
    {
        public OriginalChildCloseInvocation(Child source, Parent parent, ExternalOriginalChildCloseRequest request)
        { OriginalOwner = this; Source = source; Parent = parent; Request = request; Lease = source.Neutral; State = source.State; Revision = source.StateRevision; }
        public OriginalChildCloseInvocation OriginalOwner { get; }
        public Child Source { get; }
        public Parent Parent { get; }
        public ExternalOriginalChildCloseRequest Request { get; }
        public NeutralChildDomainLease Lease { get; }
        public NeutralChildDomainState State { get; }
        public long Revision { get; }
        public bool Invoked, OwnFaultPinned, NoEffectConsumed;
        public long FaultRevision;
        public ExternalOriginalChildCloseObservation? Observation, Resolution, Validated;
        public ExternalOriginalChildCloseObservation? OriginalCanonical;
    }

    public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease lease) =>
        childRuntime is IHybridCpuOriginalChildCloseRuntimeV1 ? CloseOriginalChildCore(lease, false) : CloseChildDomainLegacy(lease);

    public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> ResolveOriginalChildClose(NeutralChildDomainLease originalChild) =>
        CloseOriginalChildCore(originalChild, true);

    private bool HasOriginalChildCloseSources(Child child, OriginalChildCloseInvocation invocation) =>
        ReferenceEquals(invocation.OriginalOwner, invocation) && ReferenceEquals(invocation.Source, child) &&
        ReferenceEquals(child.OriginalCloseInvocation, invocation) && child.Neutral == invocation.Lease &&
        ReferenceEquals(child.CreateReceipt, invocation.Request.Admission) && child.External == invocation.Request.Admission.Lease &&
        originalChildren.TryGetValue(child.Neutral.Handle, out Child? original) && ReferenceEquals(original, child) &&
        children.TryGetValue(child.Neutral.Handle, out Child? route) && ReferenceEquals(route, child) &&
        ReferenceEquals(child.ParentSource, invocation.Parent) && HasOriginalParentClosureSources(invocation.Parent) &&
        !child.StateRevisionExhausted &&
        (child.State == invocation.State && child.StateRevision == invocation.Revision ||
         invocation.OwnFaultPinned && child.State == NeutralChildDomainState.Faulted && child.StateRevision == invocation.FaultRevision);

    private bool HasOriginalChildCloseUsers(Child child) =>
        HasSoftwareAdmissionPins(child) ||
        !GuestMapHistory(child) || HasOriginalNestedPins(child) || HasOriginalIoPins(child) ||
        child.GuestMapMayHaveEffect || pendingGuestMaps.ContainsKey(child.Neutral.Handle) || pendingChildTransitions.Contains(child.Neutral.Handle) ||
        pendingChildStarts.Contains(child.Neutral.Handle) ||
        originalMappings.Values.Any(mapping => mapping.Neutral.ChildLease == child.Neutral &&
            (mapping.ClosureReceipt is not { IsTerminal: true } || SupportsOriginalGuestUnmapResolution && !HasRetainedOriginalGuestClosure(mapping))) ||
        originalArtifacts.Values.Any(artifact => ReferenceEquals(artifact.BindChild, child) && !HasRetainedArtifactRelease(artifact)) ||
        ioBindings.Values.Any(io => io.Neutral.ChildLease == child.Neutral);

    private void PinOriginalChildClose(Child child, OriginalChildCloseInvocation invocation)
    {
        child.CloseMayHaveEffect = true;
        if (ReferenceEquals(child.OriginalCloseInvocation, invocation) && !child.StateRevisionExhausted &&
            child.State == invocation.State && child.StateRevision == invocation.Revision)
        {
            child.State = NeutralChildDomainState.Faulted;
            invocation.OwnFaultPinned = true; invocation.FaultRevision = child.StateRevision;
        }
    }

    private NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseOriginalChildCore(NeutralChildDomainLease lease, bool resolutionOnly)
    {
        Child child; OriginalChildCloseInvocation invocation; bool resolve; bool consumedClosure;
        lock (sync)
        {
            if (childRuntime is not IHybridCpuOriginalChildCloseRuntimeV1 ||
                !originalChildren.TryGetValue(lease.Handle, out child!) || child.Neutral != lease ||
                !children.TryGetValue(lease.Handle,out Child? route) || !ReferenceEquals(route,child))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original child Close producer/owner unavailable; absence is not closure.");
            if(pendingChildCloses.Contains(lease.Handle))return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original child closure lookup in flight.");
            consumedClosure=HasRetainedOriginalChildClosure(child);
            if(consumedClosure){invocation=child.OriginalCloseInvocation!;resolve=true;}
            else
            {
            if(child.OriginalCloseInvocation is {NoEffectConsumed:false,OriginalCanonical:not null} retained && !HasIntactChildCloseCanonical(retained))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original child canonical custody lost; no replacement effect/query.");
            if (pendingChildCloses.Contains(lease.Handle) || HasOriginalChildCloseUsers(child))
                return Fail<NeutralChildDomainCloseReceipt>(resolutionOnly || child.OriginalCloseInvocation is { Invoked: true }
                    ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original child users/interlock refuse Close/lookup before effect.");
            if (child.OriginalCloseInvocation is null || child.OriginalCloseInvocation.NoEffectConsumed)
            {
                if (resolutionOnly || child.CloseMayHaveEffect || child.CreateReceipt is null || child.ParentSource is null)
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original child Close request is absent/unknown.");
                if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Original child Close operation capacity exhausted.");
                child.OriginalCloseInvocation = new(child, child.ParentSource, new(child.CreateReceipt, Op()));
            }
            invocation = child.OriginalCloseInvocation;
            if (!HasOriginalChildCloseSources(child, invocation))
                return Fail<NeutralChildDomainCloseReceipt>(invocation.Invoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original child Close source continuity unavailable.");
            resolve = invocation.Invoked;
            }
            pendingChildCloses.Add(lease.Handle);
        }
        try
        {
            HybridCpuExternalFeatureManifest source = root.QueryFeatures();
            if(consumedClosure)
            {
                lock(sync)
                    return ReferenceEquals(child.OriginalCloseInvocation,invocation) && HasRetainedOriginalChildClosure(child) &&
                        source.ContractVersion == externalFeatures.ContractVersion && source.Generation == externalFeatures.Generation ?
                        ClosedChildReceipt(lease) : Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Consumed original child closure source/evidence changed.");
            }
            lock (sync)
            {
                if (!HasOriginalChildCloseSources(child, invocation) || HasOriginalChildCloseUsers(child) ||
                    source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation)
                    return Fail<NeutralChildDomainCloseReceipt>(invocation.Invoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original child Close source changed before producing effect/lookup.");
                if (!resolve && !resolutionOnly) invocation.Invoked = true;
            }
            IHybridCpuOriginalChildCloseRuntimeV1 producer = (IHybridCpuOriginalChildCloseRuntimeV1)childRuntime;
            ExternalOriginalChildCloseObservation? raw = resolve || resolutionOnly ? invocation.Validated ?? producer.ResolveOriginalChildClose(invocation.Request) : producer.CloseOriginalChild(invocation.Request);
            lock (sync) { if (resolve || resolutionOnly) invocation.Resolution = raw; else invocation.Observation = raw; }
            ExternalOriginalChildCloseObservation? canonical = raw is not null && ReferenceEquals(raw, invocation.Validated) ? raw : producer.ResolveOriginalChildClose(invocation.Request);
            lock (sync)
            {
                invocation.Resolution = canonical;
                if (IsExactChildCloseObservation(invocation, raw, canonical)) {invocation.Validated = canonical;invocation.OriginalCanonical ??= canonical;}
            }
            source = root.QueryFeatures();
            lock (sync)
            {
                if (!HasOriginalChildCloseSources(child, invocation) || HasOriginalChildCloseUsers(child) ||
                    source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation ||
                    !IsExactChildCloseObservation(invocation, raw, canonical) || !HasIntactChildCloseCanonical(invocation) || !ReferenceEquals(invocation.OriginalCanonical,canonical))
                { PinOriginalChildClose(child, invocation); return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original child Close unknown/foreign; pins retained."); }
                if (raw!.NoEffect)
                {
                    if (invocation.OwnFaultPinned) child.State = invocation.State;
                    invocation.NoEffectConsumed = true; child.CloseMayHaveEffect = false;
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Exact original child no-effect consumed separately from closure.");
                }
                child.CloseReceipt = raw.Raw.Receipt; child.CloseOperation = invocation.Request.Operation; child.CloseLease = lease;
                child.State = NeutralChildDomainState.Closed;
                child.CloseMayHaveEffect = false;
                invocation.OwnFaultPinned = false;
                return ClosedChildReceipt(lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (sync) if (invocation.Invoked && !consumedClosure) PinOriginalChildClose(child, invocation);
            return Fail<NeutralChildDomainCloseReceipt>(invocation.Invoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked,
                $"Original child Close retained/unknown: {exception.GetType().Name}");
        }
        finally
        {
            lock (sync)
            {
                if (!invocation.Invoked) invocation.NoEffectConsumed = true;
                pendingChildCloses.Remove(lease.Handle);
            }
        }
    }

    private bool HasRetainedOriginalChildClosure(Child child) =>
        child.State == NeutralChildDomainState.Closed && child.OriginalCloseInvocation is { Invoked: true, NoEffectConsumed: false, Validated: { NoFurtherUse: true } raw } fact &&
        ReferenceEquals(fact.OriginalOwner,fact) && ReferenceEquals(fact.Source,child) &&
        HasIntactChildCloseCanonical(fact) && ReferenceEquals(child.ParentSource,fact.Parent) && HasOriginalParentClosureSources(fact.Parent) &&
        originalChildren.TryGetValue(child.Neutral.Handle,out Child? original) && ReferenceEquals(original,child) &&
        children.TryGetValue(child.Neutral.Handle,out Child? route) && ReferenceEquals(route,child) && !HasOriginalChildCloseUsers(child) &&
        (ReferenceEquals(raw,fact.Observation) || ReferenceEquals(raw,fact.Resolution)) &&
        IsExactChildCloseObservation(fact,raw,raw) && ReferenceEquals(child.CloseReceipt,raw.Raw.Receipt) &&
        child.CloseOperation == fact.Request.Operation && child.CloseLease == child.Neutral;

    private bool HasIntactChildCloseCanonical(OriginalChildCloseInvocation fact) => ReferenceEquals(fact.OriginalOwner,fact) &&
        ReferenceEquals(fact.Source.OriginalCloseInvocation,fact) && fact.OriginalCanonical is { } original &&
        ReferenceEquals(fact.Validated,original) && (ReferenceEquals(fact.Observation,original) || ReferenceEquals(fact.Resolution,original)) &&
        IsExactChildCloseObservation(fact,original,original);

    private bool IsExactChildCloseObservation(OriginalChildCloseInvocation invocation, ExternalOriginalChildCloseObservation? raw, ExternalOriginalChildCloseObservation? canonical)
    {
        if (raw is null || !ReferenceEquals(raw, canonical) || !ReferenceEquals(raw.Request, invocation.Request) ||
            !ReferenceEquals(raw.Request.Admission, invocation.Source.CreateReceipt)) return false;
        if (raw.NoEffect) return !raw.NoFurtherUse && raw.Raw.Outcome == ExternalRuntimeOutcome.Denied && raw.Raw.Receipt is null;
        return raw.NoFurtherUse && raw.Raw.Outcome == ExternalRuntimeOutcome.Closed && raw.Raw.Receipt is { IsTerminal: true } closed &&
            closed.Lease == invocation.Source.External && closed.Operation == invocation.Request.Operation && closed.ResultingState == ExternalChildDomainState.Closed &&
            closed.ContractVersion == externalFeatures.ContractVersion && closed.ManifestGeneration == externalFeatures.Generation;
    }

    private NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> ClosedChildReceipt(NeutralChildDomainLease lease) =>
        Ok(new NeutralChildDomainCloseReceipt(lease.Handle, lease.Epoch, lease.ParentLease.Handle, lease.ParentLease.Epoch, NeutralChildDomainState.Closed, true));
}
