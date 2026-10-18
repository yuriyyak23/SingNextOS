using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter
{
    private sealed class OriginalStartInvocation(NeutralChildExecutionStartRequest request,Artifact artifact,Child child,Mapping mapping,ExternalChildExecutionStartRequest external)
    {
        public OriginalStartInvocation? OwnerSelf { get; private set; }
        public void EstablishSelf() => OwnerSelf = this;
        public NeutralChildExecutionStartRequest Request { get; } = request;
        public Artifact Artifact { get; } = artifact;
        public Child Child { get; } = child;
        public Mapping Mapping { get; } = mapping;
        public long ChildRevision { get; } = child.StateRevision;
        public ExternalChildExecutionStartRequest ExternalRequest { get; } = external;
        public ExternalOriginalChildExecutionStartRequest OriginalExternalRequest { get; } = new(child.External, artifact.External, external);
        public ExternalOriginalChildExecutionStartObservation? OriginalObservation { get; set; }
        public ExternalOriginalChildExecutionStartObservation? ResolutionObservation { get; set; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool SourceChanged { get; set; }
        public ExternalChildResult<ExternalChildExecutionReceipt>? Observation { get; set; }
        public ExternalOriginalExecutionTerminalObservation? TerminalObservation { get; set; }
    }
    private static bool OriginalStartHistoryIntact(Artifact artifact) => (ulong)artifact.StartHistory.Count == artifact.StartsIssued &&
        artifact.StartHistory.All(fact => ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Artifact,artifact)) &&
        (artifact.OriginalStart is null ? artifact.StartsIssued == 0 : artifact.StartHistory.Count > 0 && ReferenceEquals(artifact.StartHistory[^1],artifact.OriginalStart));
    private bool CurrentOriginalStart(OriginalStartInvocation fact,HybridCpuExternalFeatureManifest source)
    {
        if (fact.Child.NestedParent is not null &&
            (fact.Child.StateRevisionExhausted || fact.Child.StateRevision != fact.ChildRevision))
            fact.SourceChanged = true; // Independent leaf ABA/exhaustion, before writing this invocation's quarantine.
        return
        MatchesOriginalStartManifest(fact,source) &&
        ReferenceEquals(fact.OwnerSelf,fact) && OriginalStartHistoryIntact(fact.Artifact) && ReferenceEquals(fact.Artifact.OriginalStart,fact) &&
        pendingChildStarts.Contains(fact.Child.Neutral.Handle) && !fact.Child.StateRevisionExhausted && fact.Child.StateRevision == fact.ChildRevision &&
        fact.Child.State == NeutralChildDomainState.Created && !fact.Child.GuestMapMayHaveEffect &&
        HasOriginalArtifactReleaseSources(fact.Artifact) && ReferenceEquals(fact.Artifact.BindChild,fact.Child) && ReferenceEquals(fact.Artifact.BindMapping,fact.Mapping) &&
        fact.Artifact.ReleaseRequest is null && !fact.Artifact.ReleaseInFlight && !fact.Mapping.ClosureMayHaveEffect && !fact.Mapping.ArtifactMayHaveEffect;
    }
    private bool MatchesOriginalStartManifest(OriginalStartInvocation fact,HybridCpuExternalFeatureManifest source)
    {
        if (!ExternalChildContractQualification.CanAdvertiseExecutable(source) || source.ContractVersion != externalFeatures.ContractVersion || source.Generation != externalFeatures.Generation)
            fact.SourceChanged = true; // Independently observed source drift cannot be repaired by an equal/ABA projection.
        return !fact.SourceChanged;
    }
}

