using System.Collections.Immutable;

namespace SingPlus.Runtime;

internal enum SipJobDagEdgeKind { ClosedCopy = 0, ReadOnlyBorrow, Move, Mutable }
internal enum SipJobDagJoinPolicy { AllSuccessByStageOrder = 0 }
internal enum SipJobDagOutcomeKind { Success = 0, Cancelled, Faulted }

internal sealed record SipJobDagNode(uint Version, string StageId);
internal sealed record SipJobDagEdge(
    uint Version, string EdgeId, string FromStageId, string ToStageId,
    SipJobDagEdgeKind Kind, string SchemaId, string LifetimeScopeId);
internal sealed record SipJobReadOnlyDagDescriptor(
    uint Version, ImmutableArray<SipJobDagNode> Nodes, ImmutableArray<SipJobDagEdge> Edges,
    SipJobDagJoinPolicy JoinPolicy, ImmutableArray<string> DeclaredGateSet);
internal readonly record struct SipJobDagOutcome(string StageId, SipJobDagOutcomeKind Kind, int ErrorCode);
internal readonly record struct SipJobDagJoinResult(SipJobDagOutcomeKind Kind, string? SelectedStageId, int ErrorCode);

internal enum SipJobReadOnlyDagError
{
    None = 0, UnknownVersion, Malformed, UnsupportedGate, Cycle, Orphan,
    UnsupportedEdge, MoveFanOut, MutableConflict, LifetimeConflict,
}

internal readonly record struct SipJobReadOnlyDagResult(
    ImmutableArray<string> TopologicalStageIds, SipJobReadOnlyDagError Error, string? Detail)
{
    internal bool IsSuccess => Error == SipJobReadOnlyDagError.None;
}

internal static class SipJobReadOnlyDagVerifier
{
    internal const uint Version = 1;
    internal const int MaxBranches = 8;
    internal static readonly ImmutableArray<string> ExactGates = ["FG-JOB-LINEAR", "FG-READONLY-DAG"];

    internal static SipJobReadOnlyDagResult Verify(SipJobReadOnlyDagDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Nodes.IsDefault || descriptor.Edges.IsDefault || descriptor.DeclaredGateSet.IsDefault ||
            descriptor.Nodes.Any(static node => node is null) || descriptor.Edges.Any(static edge => edge is null))
            return Fail(SipJobReadOnlyDagError.Malformed, "DAG collections cannot be default or contain null entries.");
        if (descriptor.Version != Version || descriptor.Nodes.Any(static n => n.Version != Version) || descriptor.Edges.Any(static e => e.Version != Version))
            return Fail(SipJobReadOnlyDagError.UnknownVersion, "Unknown DAG schema version.");
        if (!descriptor.DeclaredGateSet.SequenceEqual(ExactGates))
            return Fail(SipJobReadOnlyDagError.UnsupportedGate, "Read-only DAG requires the exact closed gate set.");
        if (!Enum.IsDefined(descriptor.JoinPolicy) || descriptor.Nodes.Length is < 2 or > MaxBranches ||
            descriptor.Nodes.Any(static n => !Canonical(n.StageId)) ||
            descriptor.Nodes.Select(static n => n.StageId).Distinct(StringComparer.Ordinal).Count() != descriptor.Nodes.Length ||
            descriptor.Edges.Any(static e => !Canonical(e.EdgeId) || !Canonical(e.FromStageId) || !Canonical(e.ToStageId) || !Canonical(e.SchemaId) || !Canonical(e.LifetimeScopeId)) ||
            descriptor.Edges.Select(static e => e.EdgeId).Distinct(StringComparer.Ordinal).Count() != descriptor.Edges.Length ||
            descriptor.Edges.Select(static e => (e.FromStageId, e.ToStageId)).Distinct().Count() != descriptor.Edges.Length)
            return Fail(SipJobReadOnlyDagError.Malformed, "DAG identities must be canonical, unique, and bounded.");

        var ids = descriptor.Nodes.Select(static n => n.StageId).ToHashSet(StringComparer.Ordinal);
        if (descriptor.Edges.Any(e => !ids.Contains(e.FromStageId) || !ids.Contains(e.ToStageId) || e.FromStageId == e.ToStageId))
            return Fail(SipJobReadOnlyDagError.Orphan, "Every edge must join distinct declared stages.");
        if (descriptor.Edges.Any(static e => !Enum.IsDefined(e.Kind)))
            return Fail(SipJobReadOnlyDagError.UnsupportedEdge, "Unknown edge kind.");
        if (descriptor.Edges.Any(static e => e.Kind switch
            {
                SipJobDagEdgeKind.ClosedCopy => !QualifiedIdentity(e.SchemaId, "schema:"),
                SipJobDagEdgeKind.ReadOnlyBorrow => !QualifiedIdentity(e.SchemaId, "region:"),
                _ => false,
            }))
            return Fail(SipJobReadOnlyDagError.UnsupportedEdge,
                "Closed-copy edges require a closed schema identity and read-only borrows require a Region identity.");
        if (descriptor.Edges.Any(static e => e.Kind == SipJobDagEdgeKind.Mutable))
            return Fail(SipJobReadOnlyDagError.MutableConflict, "Shared mutable DAG edges are not admitted.");
        if (descriptor.Edges.Any(static e => e.Kind == SipJobDagEdgeKind.Move))
            return Fail(SipJobReadOnlyDagError.MoveFanOut,
                "MOVE is outside the read-only DAG contour; linear MOVE remains a separately qualified P14-3 contour.");
        if (descriptor.Edges.Where(static e => e.Kind == SipJobDagEdgeKind.ReadOnlyBorrow)
            .GroupBy(static e => (e.FromStageId, e.SchemaId))
            .Any(group => group.Select(static e => e.LifetimeScopeId).Distinct(StringComparer.Ordinal).Count() != 1))
            return Fail(SipJobReadOnlyDagError.LifetimeConflict, "Read-only BORROW fan-out requires one dominating lifetime scope.");

        var incoming = ids.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        var outgoing = ids.ToDictionary(static id => id, static _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in descriptor.Edges) { incoming[edge.ToStageId]++; outgoing[edge.FromStageId].Add(edge.ToStageId); }
        var rootCount = incoming.Count(static pair => pair.Value == 0);
        var ready = new SortedSet<string>(incoming.Where(static pair => pair.Value == 0).Select(static pair => pair.Key), StringComparer.Ordinal);
        var order = ImmutableArray.CreateBuilder<string>(ids.Count);
        while (ready.Count != 0)
        {
            var id = ready.Min!; ready.Remove(id); order.Add(id);
            foreach (var next in outgoing[id].Order(StringComparer.Ordinal)) if (--incoming[next] == 0) ready.Add(next);
        }
        if (order.Count != ids.Count)
            return Fail(SipJobReadOnlyDagError.Cycle, "DAG contains a cycle or unreachable cycle.");
        if (rootCount != 1)
            return Fail(SipJobReadOnlyDagError.Orphan, "DAG must have one logical entry.");
        return new(order.MoveToImmutable(), SipJobReadOnlyDagError.None, null);
    }

    internal static SipJobDagJoinResult Join(IEnumerable<SipJobDagOutcome> outcomes)
    {
        var ordered = outcomes.OrderBy(static x => x.StageId, StringComparer.Ordinal).ToArray();
        var selected = ordered.FirstOrDefault(static x => x.Kind == SipJobDagOutcomeKind.Faulted);
        if (selected.Kind != SipJobDagOutcomeKind.Faulted)
            selected = ordered.FirstOrDefault(static x => x.Kind == SipJobDagOutcomeKind.Cancelled);
        return selected.Kind is SipJobDagOutcomeKind.Faulted or SipJobDagOutcomeKind.Cancelled
            ? new(selected.Kind, selected.StageId, selected.ErrorCode)
            : new(SipJobDagOutcomeKind.Success, null, 0);
    }

    private static bool Canonical(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();
    private static bool QualifiedIdentity(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length;
    private static SipJobReadOnlyDagResult Fail(SipJobReadOnlyDagError error, string detail) => new([], error, detail);
}
