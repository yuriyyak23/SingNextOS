using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter
{
    public NeutralVirtualizationResult<NeutralExecutionTerminalObservation> ResolveExecutionTerminal(
        NeutralChildExecutionStartRequest originalRequest)
    {
        if (childRuntime is not IHybridCpuOriginalExecutionTerminalRuntimeV1 producer)
            return Fail<NeutralExecutionTerminalObservation>(NeutralVirtualizationStatus.Unsupported, "Typed original terminal producer is unavailable.");
        var before = root.QueryFeatures(); // External callbacks outside the adapter owner gate.
        OriginalStartInvocation fact;
        lock (sync)
        {
            if (!artifacts.TryGetValue(originalRequest.Artifact.ArtifactHandle, out var artifact) ||
                !OriginalStartHistoryIntact(artifact) || artifact.OriginalStart is not { Invoked: true, NoEffectConsumed: false } original ||
                original.Request != originalRequest || !OriginalTerminalSources(original, before))
                return Fail<NeutralExecutionTerminalObservation>(NeutralVirtualizationStatus.Ambiguous, "Original terminal owner/source is unavailable.");
            fact = original;
        }
        var raw = producer.ResolveOriginalExecutionTerminal(fact.OriginalExternalRequest);
        lock (sync) fact.TerminalObservation ??= raw; // Retain first raw before subsequent source callback/validation.
        var after = root.QueryFeatures();
        var nativeCurrent = raw is not null && producer.ValidateCurrentOriginalExecutionTerminal(fact.OriginalExternalRequest, raw);
        lock (sync)
        {
            if (!OriginalTerminalSources(fact, after) || !nativeCurrent || raw is null || !ReferenceEquals(fact.TerminalObservation, raw) ||
                !ReferenceEquals(raw.Request, fact.OriginalExternalRequest) ||
                fact.Observation is not { Outcome: ExternalRuntimeOutcome.Succeeded, Receipt: { } receipt } ||
                !ReferenceEquals(raw.Receipt, receipt) || !ReferenceEquals(receipt, fact.Artifact.TupleMatchedExecutionObservation) ||
                fact.Artifact.ExecutionReceipt is not { } neutral || !Enum.IsDefined(raw.Kind) ||
                !NeutralChildExecutionContract.ValidateExecutionReceipt(originalRequest, neutral).IsSuccess)
                return Fail<NeutralExecutionTerminalObservation>(NeutralVirtualizationStatus.Ambiguous, "Typed terminal fact is unknown or foreign; original Start remains retained.");
            return Ok(new NeutralExecutionTerminalObservation(originalRequest, neutral, (NeutralExecutionTerminalKind)(byte)raw.Kind)
            { ProcessExitCode = raw.ProcessExitCode });
        }
    }

    private bool OriginalTerminalSources(OriginalStartInvocation fact, HybridCpuExternalFeatureManifest source)
    {
        if (fact.Child.StateRevisionExhausted || fact.Child.StateRevision != fact.ChildRevision)
            fact.SourceChanged = true; // Independent source loss/ABA, not repairable by an equal current projection.
        return
        ReferenceEquals(fact.OwnerSelf, fact) && OriginalStartHistoryIntact(fact.Artifact) &&
        ReferenceEquals(fact.Artifact.OriginalStart, fact) && MatchesOriginalStartManifest(fact, source) &&
        !fact.Child.StateRevisionExhausted && fact.Child.StateRevision == fact.ChildRevision &&
        fact.Child.State == NeutralChildDomainState.Created &&
        HasOriginalArtifactReleaseSources(fact.Artifact) && ReferenceEquals(fact.Artifact.BindChild, fact.Child) &&
        ReferenceEquals(fact.Artifact.BindMapping, fact.Mapping) && fact.Artifact.ReleaseRequest is null &&
        !fact.Artifact.ReleaseInFlight && !fact.Mapping.ClosureMayHaveEffect &&
        !pendingChildStarts.Contains(fact.Child.Neutral.Handle);
    }
}
