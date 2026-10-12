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
    }

    public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease lease) =>
        childRuntime is IHybridCpuOriginalChildCloseRuntimeV1 ? CloseOriginalChildCore(lease, false) : CloseChildDomainLegacy(lease);

    public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> ResolveOriginalChildClose(NeutralChildDomainLease originalChild) =>
        CloseOriginalChildCore(originalChild, true);

    private bool HasOriginalChildCloseSources(Child child, OriginalChildCloseInvocation invocation) =>
        ReferenceEquals(invocation.OriginalOwner, invocation) && ReferenceEquals(invocation.Source, child) &&
        ReferenceEquals(child.OriginalCloseInvocation, invocation) && child.Neutral == invocation.Lease &&
        ReferenceEquals(child.CreateReceipt, invocation.Request.Admission) && child.External == invocation.Request.Admission.Lease &&
        originalChildren.TryGetValue(child.Neutral.Handle, out var original) && ReferenceEquals(original, child) &&
        children.TryGetValue(child.Neutral.Handle, out var route) && ReferenceEquals(route, child) &&
        ReferenceEquals(child.ParentSource, invocation.Parent) && HasOriginalParentClosureSources(invocation.Parent) &&
        !child.StateRevisionExhausted &&
        (child.State == invocation.State && child.StateRevision == invocation.Revision ||
         invocation.OwnFaultPinned && child.State == NeutralChildDomainState.Faulted && child.StateRevision == invocation.FaultRevision);

    private bool HasOriginalChildCloseUsers(Child child) =>
        HasOriginalNestedPins(child) ||
        child.GuestMapMayHaveEffect || pendingGuestMaps.ContainsKey(child.Neutral.Handle) || pendingChildTransitions.Contains(child.Neutral.Handle) ||
        pendingChildStarts.Contains(child.Neutral.Handle) ||
        originalMappings.Values.Any(mapping => mapping.Neutral.ChildLease == child.Neutral && mapping.ClosureReceipt is not { IsTerminal: true }) ||
        originalArtifacts.Values.Any(artifact => ReferenceEquals(artifact.BindChild, child) && !artifact.Released) ||
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
        Child child; OriginalChildCloseInvocation invocation; bool resolve;
        lock (sync)
        {
            if (childRuntime is not IHybridCpuOriginalChildCloseRuntimeV1 ||
                !originalChildren.TryGetValue(lease.Handle, out child!) || child.Neutral != lease)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original child Close producer/owner unavailable; absence is not closure.");
            if (child.State == NeutralChildDomainState.Closed && child.CloseReceipt is { IsTerminal: true } &&
                child.OriginalCloseInvocation is { Validated.NoFurtherUse: true } consumed && ReferenceEquals(consumed.OriginalOwner, consumed) && ReferenceEquals(consumed.Source, child))
                return resolutionOnly ? ClosedChildReceipt(lease) : Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Stale, "Child already closed.");
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
            pendingChildCloses.Add(lease.Handle);
        }
        try
        {
            var source = root.QueryFeatures();
            lock (sync)
            {
                if (!HasOriginalChildCloseSources(child, invocation) || HasOriginalChildCloseUsers(child) ||
                    source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation)
                    return Fail<NeutralChildDomainCloseReceipt>(invocation.Invoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked, "Original child Close source changed before producing effect/lookup.");
                if (!resolve && !resolutionOnly) invocation.Invoked = true;
            }
            var producer = (IHybridCpuOriginalChildCloseRuntimeV1)childRuntime;
            var raw = resolve || resolutionOnly ? invocation.Validated ?? producer.ResolveOriginalChildClose(invocation.Request) : producer.CloseOriginalChild(invocation.Request);
            lock (sync) { if (resolve || resolutionOnly) invocation.Resolution = raw; else invocation.Observation = raw; }
            var canonical = raw is not null && ReferenceEquals(raw, invocation.Validated) ? raw : producer.ResolveOriginalChildClose(invocation.Request);
            lock (sync)
            {
                invocation.Resolution = canonical;
                if (IsExactChildCloseObservation(invocation, raw, canonical)) invocation.Validated = canonical;
            }
            source = root.QueryFeatures();
            lock (sync)
            {
                if (!HasOriginalChildCloseSources(child, invocation) || HasOriginalChildCloseUsers(child) ||
                    source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation ||
                    !IsExactChildCloseObservation(invocation, raw, canonical))
                { PinOriginalChildClose(child, invocation); return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original child Close unknown/foreign; pins retained."); }
                if (raw!.NoEffect)
                {
                    if (invocation.OwnFaultPinned) child.State = invocation.State;
                    invocation.NoEffectConsumed = true; child.CloseMayHaveEffect = false;
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Exact original child no-effect consumed separately from closure.");
                }
                child.CloseReceipt = raw.Raw.Receipt; child.CloseOperation = invocation.Request.Operation; child.CloseLease = lease;
                child.State = NeutralChildDomainState.Closed;
                invocation.OwnFaultPinned = false;
                return ClosedChildReceipt(lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (sync) if (invocation.Invoked) PinOriginalChildClose(child, invocation);
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
