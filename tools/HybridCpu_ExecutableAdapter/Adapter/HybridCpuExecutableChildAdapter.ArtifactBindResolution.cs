using HybridCPU.ExternalRuntime.Contracts;
using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalArtifactBindNoEffectProvider
{
    private sealed class OriginalArtifactBindFact(NeutralExecutableArtifactRequest request,
        ExternalChildArtifactBindRequest external, Child child, Mapping mapping, ulong identity)
    {
        public OriginalArtifactBindFact? OwnerSelf { get; private set; }
        public void EstablishSelf() => OwnerSelf = this;
        public NeutralExecutableArtifactRequest Request { get; } = request;
        public ExternalChildArtifactBindRequest External { get; } = external;
        public Child Child { get; } = child;
        public Mapping Mapping { get; } = mapping;
        public ulong Identity { get; } = identity;
        public string Digest { get; } = Convert.ToHexStringLower(SHA256.HashData(external.PackageBytes));
        public ExternalChildResult<ExternalChildArtifactBindReceipt>? Observation { get; set; }
        public ExternalChildArtifactBindReceipt? ResolutionObservation { get; set; }
        public bool ResolutionInFlight { get; set; }
        public ExternalOriginalArtifactBindDisposition? Disposition { get; set; }
        public bool NoEffectConsumed { get; set; }
        public NeutralChildDomainState ChildBefore { get; } = child.State;
        public long AdmissionRevision { get; } = child.StateRevision;
        public long FailureRevision { get; set; }
        public bool OwnFailure { get; set; }
        public bool SourceChanged { get; set; }
        public Dictionary<Child, long> Ancestors { get; } = [];
    }

    private bool HasOriginalBindSources(OriginalArtifactBindFact fact) =>
        ReferenceEquals(fact.OwnerSelf, fact) && !fact.SourceChanged &&
        originalChildren.TryGetValue(fact.Child.Neutral.Handle, out var child) && ReferenceEquals(child, fact.Child) &&
        children.TryGetValue(child.Neutral.Handle, out var childRoute) && ReferenceEquals(childRoute, child) &&
        (child.NestedParent is null || NestedIdentity(child) && fact.Ancestors.All(pair =>
            !pair.Key.StateRevisionExhausted && pair.Key.StateRevision == pair.Value)) &&
        originalMappings.TryGetValue(fact.Mapping.Neutral.Handle, out var mapping) && ReferenceEquals(mapping, fact.Mapping) &&
        mappings.TryGetValue(mapping.Neutral.Handle, out var mappingRoute) && ReferenceEquals(mappingRoute, mapping) &&
        ReferenceEquals(mapping.ArtifactBind, fact) && !mapping.ClosureMayHaveEffect &&
        parents.TryGetValue(child.Neutral.ParentLease, out var parent) && ReferenceEquals(parent, child.ParentSource) &&
        originalParents.TryGetValue(child.Neutral.ParentLease, out var originalParent) && ReferenceEquals(parent, originalParent) &&
        parent.External == child.Parent && parent.External == child.External.Parent;

    public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> ResolveExecutableArtifactBind(NeutralExecutableArtifactRequest originalRequest)
    {
        OriginalArtifactBindFact fact;
        Child[] lineage;
        lock (sync)
        {
            if (!originalMappings.TryGetValue(originalRequest.GuestMapping.Handle, out var mapping) ||
                mapping.ArtifactBind is not { } saved || saved.Request != originalRequest || !HasOriginalBindSources(saved) ||
                saved.ResolutionInFlight || pendingArtifactBinds.Contains(mapping.Neutral.Handle) || pendingGuestUnmaps.Contains(mapping.Neutral.Handle) ||
                pendingChildCloses.Contains(saved.Child.Neutral.Handle) || pendingChildStarts.Contains(saved.Child.Neutral.Handle) ||
                pendingChildTransitions.Contains(saved.Child.Neutral.Handle))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Bind invocation/source is unavailable or in flight.");
            fact = saved;
            if (fact.NoEffectConsumed) return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked, "Original Bind no-effect already consumed.");
            lineage = fact.Child.NestedParent is null ? [] : GuestLineage(fact.Child).ToArray();
            if (lineage.Any(source => pendingNestedSources.Contains(source) || pendingChildCloses.Contains(source.Neutral.Handle) ||
                pendingChildStarts.Contains(source.Neutral.Handle) || pendingChildTransitions.Contains(source.Neutral.Handle)))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Bind ancestor lookup is in flight.");
            fact.ResolutionInFlight = true;
            pendingArtifactBinds.Add(mapping.Neutral.Handle);
            foreach (var source in lineage) pendingNestedSources.Add(source);
        }
        try
        {
            var source = root.QueryFeatures();
            lock (sync) if (!MatchesOriginalBindManifest(fact, source) || !HasOriginalBindSources(fact))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Bind source changed before pure lookup.");
            var raw = fact.Child.NestedParent is not null
                ? (childRuntime as IHybridCpuOriginalArtifactBindRuntimeV1)?.ResolveChildExecutableArtifactBind(fact.Child.External, fact.External)
                : fact.Observation is { Outcome: ExternalRuntimeOutcome.Succeeded, Receipt: { } known } ? known :
                fact.ResolutionObservation ?? (childRuntime as IHybridCpuOriginalArtifactBindRuntimeV1)?.ResolveChildExecutableArtifactBind(fact.Child.External, fact.External);
            lock (sync) fact.ResolutionObservation = raw; // Before mutable source callback.
            if (fact.Child.NestedParent is not null && fact.Observation is { Outcome: ExternalRuntimeOutcome.Succeeded, Receipt: { } captured } &&
                !ReferenceEquals(captured, raw))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Captured nested Bind raw does not match the original native fact; backing stays pinned.");
            var disposition = raw is null ? fact.Disposition ??
                (childRuntime as IHybridCpuOriginalArtifactBindDispositionRuntimeV1)?.ResolveOriginalArtifactBindDisposition(fact.Child.External, fact.External) : null;
            lock (sync) fact.Disposition = disposition;
            if (raw is null && disposition is { NoEffect: false, CleanupArtifact: { } cleanup } &&
                ReferenceEquals(disposition.Request, fact.External) && disposition.Child == fact.Child.External &&
                disposition.ContractVersion == externalFeatures.ContractVersion && disposition.ManifestGeneration == externalFeatures.Generation &&
                disposition.PackageSha256 == fact.Digest && disposition.Raw.Outcome is ExternalRuntimeOutcome.Denied or ExternalRuntimeOutcome.Faulted &&
                disposition.Raw.Receipt is null)
            {
                raw = cleanup;
                lock (sync) fact.ResolutionObservation = raw; // Retained before fresh source callback; cleanup only.
            }
            source = root.QueryFeatures();
            lock (sync)
            {
                if (!MatchesOriginalBindManifest(fact, source)) return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Bind source drift remains pinned.");
                if (HasOriginalBindSources(fact) && source.ContractVersion == externalFeatures.ContractVersion && source.Generation == externalFeatures.Generation &&
                    disposition is { NoEffect: true, Raw: { Outcome: ExternalRuntimeOutcome.Denied, Receipt: null } } &&
                    ReferenceEquals(disposition.Request, fact.External) && disposition.Child == fact.Child.External &&
                    disposition.ContractVersion == externalFeatures.ContractVersion && disposition.ManifestGeneration == externalFeatures.Generation &&
                    disposition.PackageSha256 == fact.Digest &&
                    fact.OwnFailure && !fact.Child.StateRevisionExhausted && fact.Child.StateRevision == fact.FailureRevision &&
                    fact.Child.State == NeutralChildDomainState.Faulted && !fact.Child.GuestMapMayHaveEffect && !fact.Child.CloseMayHaveEffect)
                {
                    fact.NoEffectConsumed = true;
                    fact.Mapping.ArtifactMayHaveEffect = false;
                    fact.Child.State = fact.ChildBefore;
                    return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked, "Exact original Bind refused before native materialization.");
                }
                if (!HasOriginalBindSources(fact) || source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation ||
                    raw is null || raw.ChildHandle != fact.Child.External.Handle || raw.ChildEpoch != fact.Child.External.Epoch ||
                    raw.MappingHandle != fact.Mapping.External.Handle || raw.MappingEpoch != fact.Mapping.External.Epoch ||
                    raw.Parent != fact.Child.Parent || raw.Operation != fact.External.Operation || raw.ContractVersion != externalFeatures.ContractVersion ||
                    raw.ManifestGeneration != externalFeatures.Generation || raw.PackageSha256 != fact.Digest || raw.MaximumPipelineCycles != fact.External.MaximumPipelineCycles ||
                    raw.MaximumPrivateDataBytes != fact.External.MaximumPrivateDataBytes ||
                    raw.ArtifactHandle.Value == Guid.Empty || raw.ArtifactEpoch.Value == 0)
                    return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Bind outcome unknown/malformed; backing remains pinned.");
                var receipt = new NeutralExecutableArtifactReceipt(new(fact.Identity), new(1), fact.Child.Neutral.Handle, fact.Child.Neutral.Epoch,
                    fact.Mapping.Neutral.Handle, fact.Mapping.Neutral.Epoch, fact.Child.Neutral.ParentLease.Handle, fact.Child.Neutral.ParentLease.Epoch,
                    fact.Digest, fact.Request.MaximumExecutionSteps) { MaximumPrivateDataBytes = fact.Request.MaximumPrivateDataBytes };
                if (originalArtifacts.TryGetValue(receipt.ArtifactHandle, out var existing))
                {
                    if (!ReferenceEquals(existing.External, raw) || !artifacts.TryGetValue(receipt.ArtifactHandle, out var route) || !ReferenceEquals(route, existing))
                        return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Bind artifact owner changed.");
                    return Ok(existing.Neutral);
                }
                var artifact = new Artifact(receipt, raw) { AdmissionPublished = false, BindChild = fact.Child, BindMapping = fact.Mapping };
                originalArtifacts.Add(receipt.ArtifactHandle, artifact);
                artifacts.Add(receipt.ArtifactHandle, artifact);
                return Ok(receipt); // Cleanup only; Start requires AdmissionPublished.
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, $"Original Bind lookup retained/unknown: {exception.GetType().Name}"); }
        finally
        {
            lock (sync)
            {
                fact.ResolutionInFlight = false; pendingArtifactBinds.Remove(fact.Mapping.Neutral.Handle);
                foreach (var source in lineage) pendingNestedSources.Remove(source);
            }
        }
    }

    private bool MatchesOriginalBindManifest(OriginalArtifactBindFact fact, HybridCpuExternalFeatureManifest source)
    {
        if (source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation) fact.SourceChanged = true;
        return !fact.SourceChanged;
    }
}
