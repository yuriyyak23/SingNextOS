using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter
{
    private sealed class Parent
    {
        public Parent(NeutralDomainBindingLease neutral, ExternalDomainBindRequest bindRequest)
        { OriginalOwner = this; Neutral = neutral; BindRequest = bindRequest; }
        public Parent OriginalOwner { get; }
        public List<OriginalChildCreateInvocation> OriginalChildCreates { get; } = [];
        public NeutralDomainBindingLease Neutral { get; }
        public ExternalDomainBindRequest BindRequest { get; }
        public ExternalDomainBindResult? BindObservation { get; set; }
        public ExternalOriginalDomainBindRequest? NativeBindRequest { get; set; }
        public ExternalOriginalDomainBindObservation? NativeBindRaw { get; set; }
        public ExternalOriginalDomainBindObservation? NativeBindResolution { get; set; }
        public ExternalOriginalDomainBindObservation? NativeBindValidated { get; set; }
        public ExternalOriginalDomainBindObservation? BindRetirementRaw { get; set; }
        public bool BindInvoked, BindInFlight, BindNoEffectConsumed, BindLocalNoEffect, PrepareInvoked, BindRetirementInvoked;
        public ExternalDomainBindReceipt? Admission { get; set; }
        public ExternalDomainLease External => Admission?.Lease ?? default;
        public NeutralExternalParentCloseRequest? CloseRequest { get; set; }
        public ExternalOriginalDomainCloseRequest? NativeCloseRequest { get; set; }
        public ExternalOriginalDomainCloseObservation? CloseObservation { get; set; }
        public ExternalOriginalDomainCloseObservation? ResolutionObservation { get; set; }
        public ExternalOriginalDomainCloseObservation? ValidatedObservation { get; set; }
        public bool CloseInvoked { get; set; }
        public bool CloseInFlight { get; set; }
        public bool Closed { get; set; }
        public NeutralExternalParentCloseRequest? NoEffectConsumed { get; set; }
    }

    private bool HasOriginalParentClosureSources(Parent parent) =>
        ReferenceEquals(parent.OriginalOwner, parent) &&
        originalParents.TryGetValue(parent.Neutral, out var original) && ReferenceEquals(original, parent) &&
        parents.TryGetValue(parent.Neutral, out var route) && ReferenceEquals(route, parent) && parent.Admission is { } admission &&
        ReferenceEquals(parent.BindObservation?.Receipt, admission) && parent.BindObservation?.Outcome == ExternalRuntimeOutcome.Bound &&
        admission.Operation == parent.BindRequest.Operation && admission.ContractVersion == externalFeatures.ContractVersion &&
        admission.ManifestGeneration == externalFeatures.Generation && !parentCreateMayHaveEffect.Contains(parent.Neutral.Handle) &&
        !pendingParentBinds.Contains(parent.Neutral.Handle) && !parent.OriginalChildCreates.Any(f => !f.Consumed || f.InFlight);

    private bool HasExternalParentUsers(Parent parent) =>
        originalChildren.Values.Where(x => ReferenceEquals(x.ParentSource, parent)).Any(child =>
            child.State != NeutralChildDomainState.Closed || child.CloseReceipt is not { IsTerminal: true } closed ||
            closed.Lease != child.External || closed.Operation != child.CloseOperation || closed.ResultingState != ExternalChildDomainState.Closed ||
            closed.ContractVersion != externalFeatures.ContractVersion || closed.ManifestGeneration != externalFeatures.Generation ||
            pendingChildCloses.Contains(child.Neutral.Handle) || pendingChildTransitions.Contains(child.Neutral.Handle) || HasPendingGuestAdmission(child.Neutral) || HasOriginalIoPins(child) || HasOriginalEventPins(child)) ||
        originalMappings.Values.Any(mapping => mapping.Neutral.ChildLease.ParentLease == parent.Neutral &&
            mapping.ClosureReceipt is not { IsTerminal: true }) ||
        originalArtifacts.Values.Any(artifact => ReferenceEquals(artifact.BindChild?.ParentSource, parent) && !artifact.Released) ||
        ioBindings.Values.Any(io => io.Neutral.ChildLease.ParentLease == parent.Neutral);

    public NeutralVirtualizationResult<NeutralExternalParentCloseReceipt> CloseExternalParent(NeutralExternalParentCloseRequest request)
        => CloseExternalParentCore(request, false);
    public NeutralVirtualizationResult<NeutralExternalParentCloseReceipt> ResolveExternalParentClose(NeutralExternalParentCloseRequest originalRequest)
        => CloseExternalParentCore(originalRequest, true);

    private NeutralVirtualizationResult<NeutralExternalParentCloseReceipt> CloseExternalParentCore(NeutralExternalParentCloseRequest request, bool resolutionOnly)
    {
        Parent parent;
        ExternalOriginalDomainCloseRequest nativeRequest;
        bool resolve;
        lock (sync)
        {
            if (request.InvocationId == Guid.Empty || !originalParents.TryGetValue(request.Parent, out parent!) || !ReferenceEquals(parent.OriginalOwner, parent) ||
                parent.CloseRequest is { } saved && saved != request)
                return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original external parent/request unavailable; absence is not closure.");
            if (parent.Closed && parent.CloseRequest == request)
                return Ok(new NeutralExternalParentCloseReceipt(request, true)); // Already consumed same original lifetime.
            if (!HasOriginalParentClosureSources(parent) || parent.CloseInFlight || HasExternalParentUsers(parent))
                return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original external parent source/users remain pinned.");
            if (childRuntime is not IHybridCpuOriginalDomainCloseRuntimeV1)
                return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.Unsupported, "Original external parent Close producer unavailable.");
            if (resolutionOnly && parent.NativeCloseRequest is null)
                return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original parent Close invocation is absent.");
            if (parent.NativeCloseRequest is null)
            {
                if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                    return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Parent Close operation capacity exhausted before effect.");
                parent.CloseRequest = request;
                parent.NativeCloseRequest = new(parent.Admission!, Op());
            }
            nativeRequest = parent.NativeCloseRequest;
            resolve = parent.CloseInvoked;
            parent.CloseInFlight = true;
        }
        try
        {
            var source = root.QueryFeatures();
            lock (sync)
            {
                if (!HasOriginalParentClosureSources(parent) || HasExternalParentUsers(parent) ||
                    source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation)
                    return Fail<NeutralExternalParentCloseReceipt>(parent.CloseInvoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked,
                        "Original external parent source changed before Close/lookup.");
                if (!resolve && !resolutionOnly) parent.CloseInvoked = true;
            }
            var producer = (IHybridCpuOriginalDomainCloseRuntimeV1)childRuntime;
            var raw = resolve || resolutionOnly ? parent.ValidatedObservation ?? producer.ResolveOriginalDomainClose(nativeRequest) : producer.CloseOriginalDomain(nativeRequest);
            lock (sync)
            {
                if (resolve || resolutionOnly) parent.ResolutionObservation = raw;
                else parent.CloseObservation = raw; // Original raw before queries/callbacks.
            }
            // Pure producing-owner canonical fact; an equal tuple/copy is not continuity.
            var canonical = ReferenceEquals(raw, parent.ValidatedObservation) && raw is not null ? raw : producer.ResolveOriginalDomainClose(nativeRequest);
            lock (sync)
            {
                parent.ResolutionObservation = canonical;
                if (IsExactProducingParentObservation(parent, nativeRequest, raw, canonical)) parent.ValidatedObservation = canonical;
            }
            source = root.QueryFeatures();
            lock (sync)
            {
                if (!HasOriginalParentClosureSources(parent) || HasExternalParentUsers(parent) ||
                    !ReferenceEquals(parent.NativeCloseRequest, nativeRequest) || source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation ||
                    raw is null || !ReferenceEquals(raw, canonical) || !ReferenceEquals(raw.Request, nativeRequest) ||
                    !ReferenceEquals(raw.Request.Admission, parent.Admission))
                    return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original external parent outcome unknown/foreign; pins retained.");
                if (raw.NoEffect && !raw.NoFurtherUse && raw.Raw.Receipt is null)
                {
                    parent.NativeCloseRequest = null; parent.CloseRequest = null; parent.CloseInvoked = false;
                    parent.ValidatedObservation = null;
                    return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Exact original parent Close no-effect consumed; no closure.");
                }
                var closed = raw.Raw.Receipt;
                if (!raw.NoFurtherUse || raw.NoEffect || raw.Raw.Outcome != ExternalRuntimeOutcome.Closed || closed is null || !closed.IsTerminal ||
                    closed.ResultingState != ExternalDomainState.Closed || closed.Lease != parent.External || closed.Operation != nativeRequest.Operation ||
                    closed.ContractVersion != externalFeatures.ContractVersion || closed.ManifestGeneration != externalFeatures.Generation)
                    return Fail<NeutralExternalParentCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original external parent lacks exact terminal no-further-use proof.");
                parent.Closed = true;
                return Ok(new NeutralExternalParentCloseReceipt(request, true));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralExternalParentCloseReceipt>(parent.CloseInvoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked,
            $"Original external parent Close retained/unknown: {exception.GetType().Name}"); }
        finally
        {
            lock (sync)
            {
                if (!parent.CloseInvoked && ReferenceEquals(parent.NativeCloseRequest, nativeRequest))
                {
                    // This original component never entered its producer. Release only
                    // its staged intent; the composed root is still partially closed.
                    parent.NoEffectConsumed = request;
                    parent.NativeCloseRequest = null; parent.CloseRequest = null;
                }
                parent.CloseInFlight = false;
            }
        }
    }

    private bool IsExactProducingParentObservation(Parent parent, ExternalOriginalDomainCloseRequest request,
        ExternalOriginalDomainCloseObservation? raw, ExternalOriginalDomainCloseObservation? canonical)
    {
        if (raw is null || !ReferenceEquals(raw, canonical) || !ReferenceEquals(raw.Request, request) || !ReferenceEquals(request.Admission, parent.Admission)) return false;
        if (raw.NoEffect) return !raw.NoFurtherUse && raw.Raw.Outcome == ExternalRuntimeOutcome.Denied && raw.Raw.Receipt is null;
        return raw.NoFurtherUse && raw.Raw.Outcome == ExternalRuntimeOutcome.Closed && raw.Raw.Receipt is { IsTerminal: true } closed &&
            closed.ResultingState == ExternalDomainState.Closed && closed.Lease == parent.External && closed.Operation == request.Operation &&
            closed.ContractVersion == externalFeatures.ContractVersion && closed.ManifestGeneration == externalFeatures.Generation;
    }
}
