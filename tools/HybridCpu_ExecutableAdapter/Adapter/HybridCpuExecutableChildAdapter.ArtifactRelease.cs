using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter
{
    public NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> ReleaseExecutableArtifact(NeutralArtifactReleaseRequest request)
        => ReleaseArtifactCore(request, false);
    public NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> ResolveExecutableArtifactRelease(NeutralArtifactReleaseRequest request)
        => ReleaseArtifactCore(request, true);

    private bool HasOriginalArtifactReleaseSources(Artifact artifact) =>
        OriginalStartHistoryIntact(artifact) &&
        !artifact.StartHistory.Any(start => start.SourceChanged) &&
        originalArtifacts.TryGetValue(artifact.Neutral.ArtifactHandle, out var original) && ReferenceEquals(original, artifact) &&
        artifacts.TryGetValue(artifact.Neutral.ArtifactHandle, out var route) && ReferenceEquals(route, artifact) &&
        artifact.BindChild is { } child && originalChildren.TryGetValue(child.Neutral.Handle, out var originalChild) && ReferenceEquals(originalChild, child) &&
        children.TryGetValue(child.Neutral.Handle, out var childRoute) && ReferenceEquals(childRoute, child) &&
        (child.NestedParent is null || NestedIdentity(child) && artifact.BindMapping?.ArtifactBind is { } bind && HasOriginalBindSources(bind)) &&
        artifact.BindMapping is { } mapping && originalMappings.TryGetValue(mapping.Neutral.Handle, out var originalMapping) && ReferenceEquals(originalMapping, mapping) &&
        mappings.TryGetValue(mapping.Neutral.Handle, out var mappingRoute) && ReferenceEquals(mappingRoute, mapping) &&
        parents.TryGetValue(child.Neutral.ParentLease, out var parent) && ReferenceEquals(parent, child.ParentSource) &&
        originalParents.TryGetValue(child.Neutral.ParentLease, out var originalParent) && ReferenceEquals(parent, originalParent) &&
        parent.External == child.Parent && parent.External == child.External.Parent;

    private NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> ReleaseArtifactCore(NeutralArtifactReleaseRequest request, bool resolutionOnly)
    {
        Artifact artifact;
        ExternalArtifactReleaseRequest externalRequest;
        bool resolve;
        Child[] lineage;
        lock (sync)
        {
            if (request.InvocationId == Guid.Empty || !originalArtifacts.TryGetValue(request.Artifact.ArtifactHandle, out artifact!) ||
                artifact.Neutral != request.Artifact)
                return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact release owner/request unavailable.");
            if (artifact.ReleaseRequest is { } prior && prior != request)
                return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Another original artifact release invocation is pinned.");
            if (!artifacts.TryGetValue(request.Artifact.ArtifactHandle, out var route) || !ReferenceEquals(route, artifact))
                return Fail<NeutralArtifactReleaseReceipt>(artifact.ReleaseInvoked || artifact.ReleaseInFlight ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked,
                    "Original artifact route changed; this release has not invoked the producer unless already retained.");
            if (artifact.Released)
                return artifact.ReleaseRequest is { } retained && retained == request
                    ? Ok(new NeutralArtifactReleaseReceipt(request, true))
                    : Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original completed release request history unavailable; no invocation adoption.");
            if (childRuntime is not IHybridCpuArtifactReleaseRuntimeV1)
                return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Unsupported, "Artifact release producer unavailable; backing remains pinned.");
            if (!HasOriginalArtifactReleaseSources(artifact) || artifact.ReleaseInFlight ||
                pendingChildStarts.Contains(artifact.BindChild!.Neutral.Handle) || pendingChildTransitions.Contains(artifact.BindChild.Neutral.Handle) ||
                pendingChildCloses.Contains(artifact.BindChild.Neutral.Handle) || pendingGuestUnmaps.Contains(artifact.BindMapping!.Neutral.Handle) ||
                pendingArtifactBinds.Contains(artifact.BindMapping.Neutral.Handle))
                return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact release source/dependencies remain pinned.");
            if (resolutionOnly && artifact.ExternalReleaseRequest is null)
                return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact release was not invoked.");
            if (artifact.ExternalReleaseRequest is null)
            {
                if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                    return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Release operation capacity exhausted before effect.");
                artifact.ReleaseRequest = request;
                artifact.ExternalReleaseRequest = new(artifact.BindChild.Neutral == artifact.BindMapping.Neutral.ChildLease
                    ? artifact.BindChild.External : default, artifact.External, Op());
            }
            externalRequest = artifact.ExternalReleaseRequest;
            resolve = artifact.ReleaseInvoked;
            lineage = artifact.BindChild!.NestedParent is null ? [] : GuestLineage(artifact.BindChild).ToArray();
            if (lineage.Any(source => pendingNestedSources.Contains(source) || pendingChildCloses.Contains(source.Neutral.Handle) || pendingChildTransitions.Contains(source.Neutral.Handle)))
                return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact ancestor release is in flight.");
            artifact.ReleaseInFlight = true;
            foreach (var source in lineage) pendingNestedSources.Add(source);
        }
        try
        {
            if (artifact.BindChild!.NestedParent is not null &&
                artifact.OriginalStart is { Invoked: true, NoEffectConsumed: false } start &&
                childRuntime is IHybridCpuOriginalChildExecutionStartRuntimeV1 originalStart)
            {
                var canonical = originalStart.ResolveOriginalChildExecutionStart(start.OriginalExternalRequest);
                lock (sync)
                {
                    start.ResolutionObservation = canonical; // Pure raw retained before subsequent source callbacks.
                    if (canonical is null)
                        return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                            "Original nested Start lookup unknown; release not invoked.");
                    if (!ReferenceEquals(canonical.Request, start.OriginalExternalRequest) ||
                        start.Observation is { } known && !ReferenceEquals(known, canonical.Raw) ||
                        start.OriginalObservation is { } observation && observation.NoEffect != canonical.NoEffect)
                    {
                        start.SourceChanged = true;
                        return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                            "Original nested Start raw/identity changed; independent uncertainty remains pinned.");
                    }
                }
            }
            var source = root.QueryFeatures(); // Callback outside adapter owner gate.
            lock (sync)
            {
                if (source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation ||
                    !HasOriginalArtifactReleaseSources(artifact))
                    return Fail<NeutralArtifactReleaseReceipt>(artifact.ReleaseInvoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked,
                        "Original release source changed before effect/lookup.");
                if (resolutionOnly && !resolve)
                    return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.NotInvoked, "Original release request has not invoked the producer.");
                if (!resolutionOnly && !resolve) artifact.ReleaseInvoked = true;
            }
            ExternalChildResult<ExternalArtifactReleaseReceipt> result;
            if (artifact.ReleaseObservation is { Outcome: ExternalRuntimeOutcome.Closed, Receipt: { NoFurtherUse: true } } retained) result = retained;
            else if (resolve || resolutionOnly)
            {
                var receipt = artifact.ReleaseResolutionObservation ?? ((IHybridCpuArtifactReleaseRuntimeV1)childRuntime).ResolveChildExecutableArtifactRelease(externalRequest);
                lock (sync) artifact.ReleaseResolutionObservation = receipt;
                result = receipt is null ? new(ExternalRuntimeOutcome.Faulted, null, "Original release outcome unknown.") :
                    new(ExternalRuntimeOutcome.Closed, receipt, string.Empty);
            }
            else result = ((IHybridCpuArtifactReleaseRuntimeV1)childRuntime).ReleaseChildExecutableArtifact(externalRequest);
            if (!resolve && !resolutionOnly) lock (sync) artifact.ReleaseObservation = result; // Raw before source callbacks.
            source = root.QueryFeatures();
            lock (sync)
            {
                if (source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation ||
                    !HasOriginalArtifactReleaseSources(artifact) || artifact.ReleaseRequest != request ||
                    result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is not { NoFurtherUse: true } receipt ||
                    !ReferenceEquals(receipt.Request, externalRequest) || !ReferenceEquals(receipt.Request.Artifact, artifact.External) ||
                    receipt.ContractVersion != externalFeatures.ContractVersion || receipt.ManifestGeneration != externalFeatures.Generation)
                    return Fail<NeutralArtifactReleaseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact release fact unresolved; backing remains pinned.");
                artifact.Released = true;
                if (!originalArtifacts.Values.Any(x => !x.Released && ReferenceEquals(x.BindMapping, artifact.BindMapping)))
                    artifact.BindMapping!.ArtifactMayHaveEffect = false;
                return Ok(new NeutralArtifactReleaseReceipt(request, true));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralArtifactReleaseReceipt>(artifact.ReleaseInvoked ? NeutralVirtualizationStatus.Ambiguous : NeutralVirtualizationStatus.NotInvoked,
            $"Original artifact release outcome retained/unknown: {exception.GetType().Name}"); }
        finally
        {
            lock (sync)
            {
                artifact.ReleaseInFlight = false;
                foreach (var source in lineage) pendingNestedSources.Remove(source);
            }
        }
    }
}

