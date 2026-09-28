using System.Collections.Immutable;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class SipJobProtectedLabelFlowTests
{
    private const string SchemaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SchemaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void FusionAndMaterializationPreserveExactLabelSidecars()
    {
        var (plan, verified) = Plan(SipJobBarrierClass.None, SipJobBarrierClass.Publication);
        var result = SipJobProtectedLabelFlow.Verify(1, plan, verified,
        [
            Sidecar(plan, "edge-a", SchemaA, generation: 7),
            Sidecar(plan, "edge-b", SchemaB, generation: 8),
        ]);

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(SipJobBarrierDisposition.Fuse, result.Bindings[0].BoundaryDisposition);
        Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip, result.Bindings[1].BoundaryDisposition);
        Assert.All(result.Bindings, binding => Assert.Equal(Label(), binding.Label));
        Assert.All(result.Bindings, binding => Assert.False(
            new ProtectedValueLabelSidecarV1(1, plan.PlanDigest, binding.EdgeId,
                binding.EdgeId == "edge-a" ? SchemaA : SchemaB, binding.Label, binding.LabelGeneration).AuthorizesAccess));
    }

    [Fact]
    public void MissingDuplicateAndSchemaSubstitutionFailClosed()
    {
        var (plan, verified) = Plan();
        var first = Sidecar(plan, "edge-a", SchemaA);
        Assert.Equal(SipJobProtectedLabelError.MissingOrDuplicateSidecar,
            SipJobProtectedLabelFlow.Verify(1, plan, verified, [first]).Error);
        Assert.Equal(SipJobProtectedLabelError.MissingOrDuplicateSidecar,
            SipJobProtectedLabelFlow.Verify(1, plan, verified, [first, first]).Error);
        Assert.Equal(SipJobProtectedLabelError.SchemaMismatch,
            SipJobProtectedLabelFlow.Verify(1, plan, verified,
                [first, Sidecar(plan, "edge-b", SchemaA)]).Error);
    }

    [Fact]
    public void ImplicitLabelChangeAndGenerationRollbackFailClosed()
    {
        var (plan, verified) = Plan();
        var changed = Sidecar(plan, "edge-b", SchemaB) with
        {
            Label = new(1, ConfidentialityClassV1.Public, IntegrityClassV1.Trusted)
        };
        Assert.Equal(SipJobProtectedLabelError.LabelLaundering,
            SipJobProtectedLabelFlow.Verify(1, plan, verified,
                [Sidecar(plan, "edge-a", SchemaA), changed]).Error);
        Assert.Equal(SipJobProtectedLabelError.LabelLaundering,
            SipJobProtectedLabelFlow.Verify(1, plan, verified,
                [Sidecar(plan, "edge-a", SchemaA, 9), Sidecar(plan, "edge-b", SchemaB, 8)]).Error);
    }

    [Fact]
    public void ReplayedPlanAndUnsupportedConfidentialBoundaryFailClosed()
    {
        var (plan, verified) = Plan(SipJobBarrierClass.None, SipJobBarrierClass.ConfidentialDomain);
        Assert.Equal(SipJobProtectedLabelError.UnsupportedBoundary,
            SipJobProtectedLabelFlow.Verify(1, plan, verified,
                [Sidecar(plan, "edge-a", SchemaA), Sidecar(plan, "edge-b", SchemaB)]).Error);

        var wrong = Sidecar(plan, "edge-a", SchemaA) with { PlanDigest = new string('A', 64) };
        Assert.Equal(SipJobProtectedLabelError.PlanMismatch,
            SipJobProtectedLabelFlow.Verify(1, plan, verified,
                [wrong, Sidecar(plan, "edge-b", SchemaB)]).Error);
    }

    private static (SipJobPlanDescriptor Plan, VerifiedPlanMetadata Verified) Plan(
        SipJobBarrierClass first = SipJobBarrierClass.None,
        SipJobBarrierClass second = SipJobBarrierClass.None)
    {
        var stages = new[] { Stage("stage-a"), Stage("stage-b"), Stage("stage-c") };
        var edges = new[]
        {
            Edge("edge-a", "stage-a", "stage-b", SchemaA, first),
            Edge("edge-b", "stage-b", "stage-c", SchemaB, second),
        };
        var plan = new SipJobPlanDescriptor(1, stages, edges, ["FG-JOB-LINEAR"], string.Empty).WithDigest();
        return (plan, new(plan.PlanDigest, stages.Select(stage => stage.StageId).ToImmutableArray(), 1));
    }

    private static SipJobStageDescriptor Stage(string id) => new(1, id, "contract", SchemaA, "operation",
        "request", SchemaA, "response", SchemaB, "thunk", SchemaA, [], [], "transition",
        SipJobInvocationObservability.HiddenIntermediate, "cancel", SipJobEffectClass.None,
        SipJobExecutionClass.ManagedDefault, SipJobStageMode.Synchronous);

    private static SipJobEdgeDescriptor Edge(string id, string from, string to, string schema, SipJobBarrierClass barrier) =>
        new(1, id, from, to, SipJobEdgeKind.ClosedCopiedValue, "schema", schema, "copy", "managed",
            "hidden", barrier, "contour");

    private static ProtectedValueLabelSidecarV1 Sidecar(
        SipJobPlanDescriptor plan, string edge, string schema, ulong generation = 7) =>
        new(1, plan.PlanDigest, edge, schema, Label(), generation);

    private static DataLabelV1 Label() => new(1, ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated);
}
