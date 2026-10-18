using System.Collections.Immutable;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum SipJobProtectedLabelError
{
    None = 0,
    Malformed,
    PlanMismatch,
    MissingOrDuplicateSidecar,
    SchemaMismatch,
    LabelLaundering,
    UnsupportedBoundary,
}

internal readonly record struct SipJobProtectedLabelBinding(
    string EdgeId,
    DataLabelV1 Label,
    ulong LabelGeneration,
    SipJobBarrierDisposition BoundaryDisposition);

internal readonly record struct SipJobProtectedLabelResult(
    ImmutableArray<SipJobProtectedLabelBinding> Bindings,
    SipJobProtectedLabelError Error,
    string? Detail)
{
    internal bool IsSuccess => Error == SipJobProtectedLabelError.None;
}

/// <summary>
/// Static admission for the named protected linear SipJob contour. This is not
/// an authority owner: it only rejects missing, stale, or laundering sidecars.
/// </summary>
internal static class SipJobProtectedLabelFlow
{
    internal const uint Version = 1;

    internal static SipJobProtectedLabelResult Verify(
        uint version,
        SipJobPlanDescriptor plan,
        VerifiedPlanMetadata verifiedPlan,
        IEnumerable<ProtectedValueLabelSidecarV1> sidecars)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sidecars);
        if (version != Version)
            return Fail(SipJobProtectedLabelError.Malformed, "Unknown protected label-flow version.");
        if (plan.FormatVersion != SipJobPlanVerifier.FormatVersion || verifiedPlan.FormatVersion != plan.FormatVersion ||
            !string.Equals(plan.PlanDigest, SipJobPlanDigest.Compute(plan), StringComparison.Ordinal) ||
            !string.Equals(verifiedPlan.PlanDigest, plan.PlanDigest, StringComparison.Ordinal) ||
            !verifiedPlan.OrderedStageIds.SequenceEqual(plan.Stages.Select(static stage => stage.StageId)))
            return Fail(SipJobProtectedLabelError.PlanMismatch, "The protected sidecars require an exact canonical plan digest.");

        ProtectedValueLabelSidecarV1[] labels;
        try
        {
            labels = sidecars.ToArray();
            foreach (var label in labels) label.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Fail(SipJobProtectedLabelError.Malformed, exception.Message);
        }

        if (labels.Length != plan.Edges.Length ||
            labels.GroupBy(static label => label.ValueId, StringComparer.Ordinal).Any(static group => group.Count() != 1))
            return Fail(SipJobProtectedLabelError.MissingOrDuplicateSidecar, "Every value edge requires exactly one label sidecar.");

        var byEdge = labels.ToDictionary(static label => label.ValueId, StringComparer.Ordinal);
        var bindings = ImmutableArray.CreateBuilder<SipJobProtectedLabelBinding>(plan.Edges.Length);
        DataLabelV1? contourLabel = null;
        ulong previousGeneration = 0;
        foreach (var edge in plan.Edges)
        {
            if (!byEdge.TryGetValue(edge.EdgeId, out var sidecar))
                return Fail(SipJobProtectedLabelError.MissingOrDuplicateSidecar, $"Value edge '{edge.EdgeId}' has no label sidecar.");
            if (!string.Equals(sidecar.PlanDigest, plan.PlanDigest, StringComparison.Ordinal))
                return Fail(SipJobProtectedLabelError.PlanMismatch, $"Value edge '{edge.EdgeId}' is bound to another plan.");
            if (!string.Equals(sidecar.ValueSchemaDigest, edge.ValueSchemaDigest, StringComparison.Ordinal))
                return Fail(SipJobProtectedLabelError.SchemaMismatch, $"Value edge '{edge.EdgeId}' schema binding does not match.");
            if (contourLabel is { } expected && sidecar.Label != expected)
                return Fail(SipJobProtectedLabelError.LabelLaundering, $"Value edge '{edge.EdgeId}' changes its label without an authorized transition.");
            if (sidecar.LabelGeneration < previousGeneration)
                return Fail(SipJobProtectedLabelError.LabelLaundering, $"Value edge '{edge.EdgeId}' rolls label generation backward.");

            var boundary = SipJobBarrierPlanner.Classify(SipJobBarrierPlanner.Version, edge.BarrierClass);
            if (boundary.Disposition == SipJobBarrierDisposition.RejectFutureGated)
                return Fail(SipJobProtectedLabelError.UnsupportedBoundary, $"Value edge '{edge.EdgeId}' crosses an unsupported protected boundary.");
            contourLabel ??= sidecar.Label;
            previousGeneration = sidecar.LabelGeneration;
            bindings.Add(new(edge.EdgeId, sidecar.Label, sidecar.LabelGeneration, boundary.Disposition));
        }

        return new(bindings.MoveToImmutable(), SipJobProtectedLabelError.None, null);
    }

    private static SipJobProtectedLabelResult Fail(SipJobProtectedLabelError error, string detail) =>
        new([], error, detail);
}
