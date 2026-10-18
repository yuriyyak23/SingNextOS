using System.Collections.Immutable;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase145CReadOnlyDagTests
{
    [Fact]
    public void ClosedCopyAndReadOnlyBorrowFanOutAreAdmittedWithDeterministicOrder()
    {
        var result = SipJobReadOnlyDagVerifier.Verify(Dag(
            Edge("e1", "root", "a", SipJobDagEdgeKind.ReadOnlyBorrow, "region:r", "scope:all"),
            Edge("e2", "root", "b", SipJobDagEdgeKind.ReadOnlyBorrow, "region:r", "scope:all"),
            Edge("e3", "a", "join", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "scope:a"),
            Edge("e4", "b", "join", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "scope:b")));
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "root", "a", "b", "join" }, result.TopologicalStageIds.ToArray());
        Assert.False(SipJobFeatureGates.IsEnabled("FG-READONLY-DAG"));
    }

    [Theory]
    [InlineData((int)SipJobDagEdgeKind.Move, (int)SipJobReadOnlyDagError.MoveFanOut)]
    [InlineData((int)SipJobDagEdgeKind.Mutable, (int)SipJobReadOnlyDagError.MutableConflict)]
    public void MoveFanOutAndMutableEdgesFailClosed(int kindValue, int errorValue)
    {
        var kind = (SipJobDagEdgeKind)kindValue;
        var error = (SipJobReadOnlyDagError)errorValue;
        var result = SipJobReadOnlyDagVerifier.Verify(Dag(
            Edge("e1", "root", "a", kind, "region:r", "scope:all"),
            Edge("e2", "root", "b", kind, "region:r", "scope:all"),
            Edge("e3", "a", "join", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "scope:a"),
            Edge("e4", "b", "join", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "scope:b")));
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void SingleLinearMoveIsOutsideReadOnlyDagContour()
    {
        var descriptor = new SipJobReadOnlyDagDescriptor(
            1,
            [new(1, "root"), new(1, "leaf")],
            [Edge("e1", "root", "leaf", SipJobDagEdgeKind.Move, "region:r", "scope:linear")],
            SipJobDagJoinPolicy.AllSuccessByStageOrder,
            SipJobReadOnlyDagVerifier.ExactGates);

        var result = SipJobReadOnlyDagVerifier.Verify(descriptor);

        Assert.Equal(SipJobReadOnlyDagError.MoveFanOut, result.Error);
    }

    [Fact]
    public void DuplicateSemanticEdgeWithDifferentIdentityFailsClosed()
    {
        var descriptor = new SipJobReadOnlyDagDescriptor(
            1,
            [new(1, "root"), new(1, "leaf")],
            [
                Edge("e1", "root", "leaf", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "scope:one"),
                Edge("e2", "root", "leaf", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "scope:two"),
            ],
            SipJobDagJoinPolicy.AllSuccessByStageOrder,
            SipJobReadOnlyDagVerifier.ExactGates);

        Assert.Equal(SipJobReadOnlyDagError.Malformed,
            SipJobReadOnlyDagVerifier.Verify(descriptor).Error);
    }

    [Fact]
    public void DefaultCollectionsAndNullGraphEntriesFailClosedBeforeTopologyAnalysis()
    {
        var defaultNodes = new SipJobReadOnlyDagDescriptor(
            1, default, [], SipJobDagJoinPolicy.AllSuccessByStageOrder, SipJobReadOnlyDagVerifier.ExactGates);
        Assert.Equal(SipJobReadOnlyDagError.Malformed, SipJobReadOnlyDagVerifier.Verify(defaultNodes).Error);

        var nullNode = new SipJobReadOnlyDagDescriptor(
            1, [null!, new(1, "leaf")], [], SipJobDagJoinPolicy.AllSuccessByStageOrder, SipJobReadOnlyDagVerifier.ExactGates);
        Assert.Equal(SipJobReadOnlyDagError.Malformed, SipJobReadOnlyDagVerifier.Verify(nullNode).Error);

        var nullEdge = new SipJobReadOnlyDagDescriptor(
            1, [new(1, "root"), new(1, "leaf")], [null!], SipJobDagJoinPolicy.AllSuccessByStageOrder, SipJobReadOnlyDagVerifier.ExactGates);
        Assert.Equal(SipJobReadOnlyDagError.Malformed, SipJobReadOnlyDagVerifier.Verify(nullEdge).Error);
    }

    [Theory]
    [InlineData((int)SipJobDagEdgeKind.ClosedCopy, "object")]
    [InlineData((int)SipJobDagEdgeKind.ClosedCopy, "schema:")]
    [InlineData((int)SipJobDagEdgeKind.ReadOnlyBorrow, "schema:v1")]
    [InlineData((int)SipJobDagEdgeKind.ReadOnlyBorrow, "region:")]
    public void EdgeKindCannotClaimAnOpenOrMismatchedSchema(int kindValue, string schemaId)
    {
        var descriptor = new SipJobReadOnlyDagDescriptor(
            1,
            [new(1, "root"), new(1, "leaf")],
            [Edge("e1", "root", "leaf", (SipJobDagEdgeKind)kindValue, schemaId, "scope:one")],
            SipJobDagJoinPolicy.AllSuccessByStageOrder,
            SipJobReadOnlyDagVerifier.ExactGates);

        Assert.Equal(SipJobReadOnlyDagError.UnsupportedEdge,
            SipJobReadOnlyDagVerifier.Verify(descriptor).Error);
    }

    [Fact]
    public void CycleUnknownKindAndBorrowLifetimeMismatchFailClosed()
    {
        Assert.Equal(SipJobReadOnlyDagError.Cycle, SipJobReadOnlyDagVerifier.Verify(new(1,
            [new(1, "a"), new(1, "b")],
            [Edge("e1", "a", "b", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "s"), Edge("e2", "b", "a", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "s")],
            SipJobDagJoinPolicy.AllSuccessByStageOrder, SipJobReadOnlyDagVerifier.ExactGates)).Error);
        var mismatch = Dag(
            Edge("e1", "root", "a", SipJobDagEdgeKind.ReadOnlyBorrow, "region:r", "scope:1"),
            Edge("e2", "root", "b", SipJobDagEdgeKind.ReadOnlyBorrow, "region:r", "scope:2"),
            Edge("e3", "a", "join", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "a"),
            Edge("e4", "b", "join", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "b"));
        Assert.Equal(SipJobReadOnlyDagError.LifetimeConflict, SipJobReadOnlyDagVerifier.Verify(mismatch).Error);
        Assert.Equal(SipJobReadOnlyDagError.UnsupportedEdge, SipJobReadOnlyDagVerifier.Verify(Dag(
            Edge("e1", "root", "a", (SipJobDagEdgeKind)999, "schema:v1", "s"),
            Edge("e2", "a", "b", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "s"),
            Edge("e3", "b", "join", SipJobDagEdgeKind.ClosedCopy, "schema:v1", "s"))).Error);
    }

    [Fact]
    public void JoinSelectionIsIndependentOfCompletionOrder()
    {
        var values = new[] { new SipJobDagOutcome("b", SipJobDagOutcomeKind.Cancelled, 2), new SipJobDagOutcome("a", SipJobDagOutcomeKind.Faulted, 1), new SipJobDagOutcome("c", SipJobDagOutcomeKind.Faulted, 3) };
        foreach (var permutation in Permute(values))
            Assert.Equal(new SipJobDagJoinResult(SipJobDagOutcomeKind.Faulted, "a", 1, SipJobDagJoinError.None),
                SipJobReadOnlyDagVerifier.Join(permutation.ToImmutableArray()));
    }

    [Fact]
    public void MalformedOrUnknownJoinOutcomesCannotBecomeSuccess()
    {
        Assert.Equal(SipJobDagJoinError.Malformed,
            SipJobReadOnlyDagVerifier.Join(default).Error);
        Assert.Equal(SipJobDagJoinError.Malformed,
            SipJobReadOnlyDagVerifier.Join([
                new("branch", SipJobDagOutcomeKind.Success, 0),
                new("branch", SipJobDagOutcomeKind.Faulted, 1),
            ]).Error);
        Assert.Equal(SipJobDagJoinError.Malformed,
            SipJobReadOnlyDagVerifier.Join([new(" branch ", SipJobDagOutcomeKind.Success, 0)]).Error);
        Assert.Equal(SipJobDagJoinError.UnsupportedOutcome,
            SipJobReadOnlyDagVerifier.Join([new("branch", (SipJobDagOutcomeKind)999, 0)]).Error);
    }

    private static SipJobReadOnlyDagDescriptor Dag(params SipJobDagEdge[] edges) => new(1,
        [new(1, "root"), new(1, "a"), new(1, "b"), new(1, "join")], edges.ToImmutableArray(),
        SipJobDagJoinPolicy.AllSuccessByStageOrder, SipJobReadOnlyDagVerifier.ExactGates);
    private static SipJobDagEdge Edge(string id, string from, string to, SipJobDagEdgeKind kind, string schema, string scope) => new(1, id, from, to, kind, schema, scope);
    private static IEnumerable<SipJobDagOutcome[]> Permute(SipJobDagOutcome[] values) =>
        values.SelectMany((first, i) => values.Where((_, j) => j != i).SelectMany((second, j) => values.Where((_, k) => k != i && k != (j >= i ? j + 1 : j)).Select(third => new[] { first, second, third })));
}
