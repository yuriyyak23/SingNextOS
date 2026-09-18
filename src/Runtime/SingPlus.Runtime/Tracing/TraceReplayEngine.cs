using SingPlus.Contracts;

namespace SingPlus.Runtime;

public static class TraceReplayEngine
{
    public static TraceReplayReport ReplayDiagnostic(TraceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var divergences = Validate(snapshot).ToList();
        if (!snapshot.Complete)
            divergences.Add(new(TraceDivergenceKind.IncompleteTrace, null, null, "Trace overflow prevents a completeness claim."));
        return new(TraceReplayMode.Diagnostic, divergences.Count == 0, divergences);
    }

    public static TraceReplayReport ReplayDeterministicModel(
        TraceSnapshot snapshot,
        Func<SemanticTraceEvent, string> model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var divergences = Validate(snapshot).ToList();
        foreach (var item in snapshot.Events.Where(static item => item.Kind != TraceEventKind.BufferOverflow))
        {
            var actual = model(item);
            if (!string.Equals(actual, item.Data.Outcome, StringComparison.Ordinal))
                divergences.Add(new(TraceDivergenceKind.ProviderSemanticMismatch, item.Producer, item.Sequence,
                    $"Model outcome '{actual}' differs from recorded semantic outcome '{item.Data.Outcome}'."));
        }
        return new(TraceReplayMode.DeterministicModel, divergences.Count == 0, divergences);
    }

    public static TraceReplayReport CorrelateExternalRuntimeEvidence(
        TraceSnapshot snapshot,
        CausalCorrelationId correlation,
        string evidenceDigest)
    {
        if (string.IsNullOrWhiteSpace(evidenceDigest))
            return new(TraceReplayMode.ExternalRuntimeCorrelated, false,
                [new(TraceDivergenceKind.NondeterministicInputMissing, null, null, "External-runtime evidence digest is required.")]);
        var matches = snapshot.Events.Any(item => item.Correlation == correlation);
        return matches
            ? new(TraceReplayMode.ExternalRuntimeCorrelated, true, [], evidenceDigest)
            : new(TraceReplayMode.ExternalRuntimeCorrelated, false,
                [new(TraceDivergenceKind.DependencyOrderMismatch, null, null, "No OS semantic event has the requested external-runtime correlation.")], evidenceDigest);
    }

    private static IEnumerable<TraceDivergence> Validate(TraceSnapshot snapshot)
    {
        foreach (var group in snapshot.Events.GroupBy(static item => item.Producer))
        {
            ulong prior = 0;
            foreach (var item in group)
            {
                if (item.Sequence.Value <= prior)
                    yield return new(TraceDivergenceKind.DependencyOrderMismatch, item.Producer, item.Sequence, "Producer sequence is not strictly monotonic.");
                if (item.Producer.Session != snapshot.Session.Session)
                    yield return new(TraceDivergenceKind.GenerationMismatch, item.Producer, item.Sequence, "Event belongs to another trace session generation.");
                prior = item.Sequence.Value;
            }
        }
    }
}
