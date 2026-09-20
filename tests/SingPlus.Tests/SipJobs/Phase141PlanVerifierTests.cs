using System.Collections.Immutable;
using System.Reflection;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase141PlanVerifierTests
{
    [Fact]
    public void CanonicalTwoStagePlanVerifiesButDoesNotAuthorizeExecution()
    {
        var (plan, catalog) = ValidPlan();

        var result = SipJobPlanVerifier.Verify(plan, catalog);

        Assert.True(result.IsSuccess);
        Assert.True(result.Metadata!.OrderedStageIds.SequenceEqual(["s1", "s2"]));
        Assert.DoesNotContain(result.Metadata.GetType().GetProperties(), property =>
            property.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) || property.PropertyType == typeof(bool));
        Assert.False(SipJobFeatureGates.IsEnabled(SipJobPlanVerifier.LinearGate));
    }

    [Fact]
    public void CanonicalDigestChangesForEverySemanticFieldFamily()
    {
        var (plan, _) = ValidPlan();
        var baseline = SipJobPlanDigest.Compute(plan);
        var stage = plan.Stages[0];
        var edge = plan.Edges[0];

        var mutations = new SipJobPlanDescriptor[]
        {
            NewPlan(plan, formatVersion: 2),
            NewPlan(plan, gates: ["FG-DIRECT-SENTRY"]),
            NewPlan(plan, stages: [Copy(stage, stageId: "changed"), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, contractDigest: Hash("contract-mutated")), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, operationId: "changed-op"), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, requestSchemaDigest: Hash("request-mutated")), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, responseSchemaDigest: Hash("response-mutated")), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, thunkDigest: Hash("thunk-mutated")), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, authorities: [new(1, SipJobAuthoritySourceClass.SessionBoundCapabilityReference, "session:2", "invoke")]), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, protocol: "protocol:changed"), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, cancellation: "cancel:changed"), plan.Stages[1]]),
            NewPlan(plan, stages: [Copy(stage, execution: SipJobExecutionClass.None), plan.Stages[1]]),
            NewPlan(plan, edges: [edge with { IsolationSemanticsId = "Changed" }]),
            NewPlan(plan, edges: [edge with { BarrierClass = SipJobBarrierClass.Publication }]),
            NewPlan(plan, edges: [edge with { PublicationSemanticsId = "Changed" }]),
        };

        Assert.All(mutations, mutation => Assert.NotEqual(baseline, SipJobPlanDigest.Compute(mutation)));
    }

    public static TheoryData<string, string, int> InvalidPlans => new()
    {
        { "unknown plan version", "plan-version", (int)SipJobPlanError.UnknownVersion },
        { "unknown descriptor version", "descriptor-version", (int)SipJobPlanError.UnknownVersion },
        { "wrong stage count", "stage-count", (int)SipJobPlanError.InvalidStageCount },
        { "duplicate stage", "duplicate-stage", (int)SipJobPlanError.DuplicateStage },
        { "duplicate edge", "duplicate-edge", (int)SipJobPlanError.DuplicateEdge },
        { "cycle", "cycle", (int)SipJobPlanError.CycleOrOrphan },
        { "orphan", "orphan", (int)SipJobPlanError.UnknownStageReference },
        { "region edge", "region", (int)SipJobPlanError.UnsupportedContour },
        { "external effect", "external", (int)SipJobPlanError.UnsupportedContour },
        { "async", "async", (int)SipJobPlanError.UnsupportedContour },
        { "observable intermediate", "observable", (int)SipJobPlanError.UnsupportedContour },
        { "unknown authority", "authority", (int)SipJobPlanError.UnsupportedAuthoritySource },
        { "schema mismatch", "schema", (int)SipJobPlanError.SchemaMismatch },
        { "unknown barrier", "barrier", (int)SipJobPlanError.UnsupportedContour },
        { "extra gate", "gate", (int)SipJobPlanError.UnsupportedGate },
    };

    [Theory]
    [MemberData(nameof(InvalidPlans))]
    public void InvalidPlanFailsBeforeAnyExecutionBinding(
        string _, string mutation, int expected)
    {
        var (plan, catalog) = ValidPlan();
        var invalid = mutation switch
        {
            "plan-version" => NewPlan(plan, formatVersion: 2).WithDigest(),
            "descriptor-version" => NewPlan(plan, stages: [Copy(plan.Stages[0], version: 2), plan.Stages[1]]).WithDigest(),
            "stage-count" => NewPlan(plan, stages: [plan.Stages[0]], edges: []).WithDigest(),
            "duplicate-stage" => NewPlan(plan, stages: [plan.Stages[0], Copy(plan.Stages[1], stageId: "s1")]).WithDigest(),
            "duplicate-edge" => NewPlan(plan, stages: [plan.Stages[0], plan.Stages[1], Copy(plan.Stages[1], stageId: "s3")], edges: [plan.Edges[0], plan.Edges[0]]).WithDigest(),
            "cycle" => NewPlan(plan, edges: [plan.Edges[0] with { ProducerStageId = "s2", ConsumerStageId = "s1" }]).WithDigest(),
            "orphan" => NewPlan(plan, stages: [plan.Stages[0], Copy(plan.Stages[1], stageId: "s3")]).WithDigest(),
            "region" => NewPlan(plan, edges: [plan.Edges[0] with { Kind = SipJobEdgeKind.RegionMove }]).WithDigest(),
            "external" => NewPlan(plan, stages: [Copy(plan.Stages[0], effect: SipJobEffectClass.ExternalEffect), plan.Stages[1]]).WithDigest(),
            "async" => NewPlan(plan, stages: [Copy(plan.Stages[0], mode: SipJobStageMode.Asynchronous), plan.Stages[1]]).WithDigest(),
            "observable" => NewPlan(plan, stages: [Copy(plan.Stages[0], observability: SipJobInvocationObservability.FinalPublication), plan.Stages[1]]).WithDigest(),
            "authority" => NewPlan(plan, stages: [Copy(plan.Stages[0], authorities: [new(1, SipJobAuthoritySourceClass.RegionAuthorityHandle, "region:1", "read")]), plan.Stages[1]]).WithDigest(),
            "schema" => NewPlan(plan, edges: [plan.Edges[0] with { ValueSchemaDigest = Hash("bad") }]).WithDigest(),
            "barrier" => NewPlan(plan, edges: [plan.Edges[0] with { BarrierClass = SipJobBarrierClass.Publication }]).WithDigest(),
            "gate" => NewPlan(plan, gates: [SipJobPlanVerifier.LinearGate, "FG-DIRECT-SENTRY"]).WithDigest(),
            _ => throw new InvalidOperationException(mutation),
        };
        var result = SipJobPlanVerifier.Verify(invalid, catalog);
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, (int)result.Failure!.Value.Error);
    }

    [Fact]
    public void DigestMutationWithoutRebindingIsRejected()
    {
        var (plan, catalog) = ValidPlan();
        var changed = NewPlan(plan, stages: [Copy(plan.Stages[0], protocol: "protocol:other"), plan.Stages[1]]);
        var result = SipJobPlanVerifier.Verify(changed, catalog);
        Assert.Equal(SipJobPlanError.DigestMismatch, result.Failure!.Value.Error);
    }

    [Fact]
    public void MissingDigestFailsClosedWithoutThrowing()
    {
        var (plan, catalog) = ValidPlan();
        var malformed = new SipJobPlanDescriptor(
            plan.FormatVersion,
            plan.Stages,
            plan.Edges,
            plan.DeclaredGateSet,
            null!);

        var result = SipJobPlanVerifier.Verify(malformed, catalog);

        Assert.False(result.IsSuccess);
        Assert.Equal(SipJobPlanError.DigestMismatch, result.Failure!.Value.Error);
    }

    [Fact]
    public void ReadOnlyRegionBorrowDescriptorIsCanonicalButDoesNotEnableRuntimeGate()
    {
        var (closedPlan, catalog) = ValidPlan();
        var edge = closedPlan.Edges[0] with
        {
            Kind = SipJobEdgeKind.RegionBorrow,
            OwnershipUseSemanticsId = "BorrowRead",
            IsolationSemanticsId = "OwnerQualifiedView",
            RegionBorrow = new(1, "ProducerResponse", "ReadOnly", "LinearConsumer", "ReleaseUseThenReturnLoan")
        };
        var plan = NewPlan(closedPlan, edges: [edge], gates: [SipJobPlanVerifier.LinearGate, SipJobPlanVerifier.RegionBorrowGate]).WithDigest();

        var result = SipJobPlanVerifier.Verify(plan, catalog);

        Assert.True(result.IsSuccess);
        Assert.False(SipJobFeatureGates.IsEnabled(SipJobPlanVerifier.RegionBorrowGate));
        Assert.DoesNotContain(result.Metadata!.GetType().GetProperties(), property =>
            property.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(SipJobPlanDigest.Compute(closedPlan), SipJobPlanDigest.Compute(plan));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("source")]
    [InlineData("lifetime")]
    [InlineData("closure")]
    [InlineData("version")]
    public void RegionBorrowSemanticMutationFailsClosed(string mutation)
    {
        var (closedPlan, catalog) = ValidPlan();
        var descriptor = new SipJobRegionBorrowDescriptor(1, "ProducerResponse", "ReadOnly", "LinearConsumer", "ReleaseUseThenReturnLoan");
        descriptor = mutation switch
        {
            "mode" => descriptor with { RequiredUseMode = "Write" },
            "source" => descriptor with { RegionSourceRole = "CachedOwner" },
            "lifetime" => descriptor with { LifetimeScopeId = "Unbounded" },
            "closure" => descriptor with { ClosurePolicyId = "Implicit" },
            "version" => descriptor with { Version = 2 },
            _ => descriptor
        };
        var edge = closedPlan.Edges[0] with
        {
            Kind = SipJobEdgeKind.RegionBorrow,
            OwnershipUseSemanticsId = "BorrowRead",
            IsolationSemanticsId = "OwnerQualifiedView",
            RegionBorrow = descriptor
        };
        var plan = NewPlan(closedPlan, edges: [edge], gates: [SipJobPlanVerifier.LinearGate, SipJobPlanVerifier.RegionBorrowGate]).WithDigest();

        var result = SipJobPlanVerifier.Verify(plan, catalog);

        Assert.False(result.IsSuccess);
        Assert.Equal(mutation == "version" ? SipJobPlanError.UnknownVersion : SipJobPlanError.UnsupportedContour, result.Failure!.Value.Error);
    }

    [Fact]
    public void RegionBorrowGateWithoutExactBorrowEdgeFailsClosed()
    {
        var (plan, catalog) = ValidPlan();
        var widened = NewPlan(plan, gates: [SipJobPlanVerifier.LinearGate, SipJobPlanVerifier.RegionBorrowGate]).WithDigest();

        var result = SipJobPlanVerifier.Verify(widened, catalog);

        Assert.False(result.IsSuccess);
        Assert.Equal(SipJobPlanError.UnsupportedGate, result.Failure!.Value.Error);
    }

    [Fact]
    public void RegionMoveDescriptorPinsOrdinaryTwoTransferSequenceWithGateOff()
    {
        var (closedPlan, catalog) = ValidPlan();
        var edge = closedPlan.Edges[0] with
        {
            Kind = SipJobEdgeKind.RegionMove,
            OwnershipUseSemanticsId = "MoveExclusive",
            IsolationSemanticsId = "OwnerTokenRematerialization",
            RegionMove = new(1, "ProducerResponse", "ResponderToCallerThenCallerToConsumer", "NoImplicitInverseMove")
        };
        var plan = NewPlan(closedPlan, edges: [edge],
            gates: [SipJobPlanVerifier.LinearGate, SipJobPlanVerifier.RegionMoveGate]).WithDigest();

        var result = SipJobPlanVerifier.Verify(plan, catalog);

        Assert.True(result.IsSuccess);
        Assert.False(SipJobFeatureGates.IsEnabled(SipJobPlanVerifier.RegionMoveGate));
        Assert.NotEqual(SipJobPlanDigest.Compute(closedPlan), SipJobPlanDigest.Compute(plan));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("shortcut")]
    [InlineData("inverse")]
    [InlineData("version")]
    [InlineData("borrow-sidecar")]
    public void RegionMoveSemanticMutationOrHiddenSidecarFailsClosed(string mutation)
    {
        var (closedPlan, catalog) = ValidPlan();
        var descriptor = new SipJobRegionMoveDescriptor(
            1, "ProducerResponse", "ResponderToCallerThenCallerToConsumer", "NoImplicitInverseMove");
        descriptor = mutation switch
        {
            "source" => descriptor with { RegionSourceRole = "CachedOwner" },
            "shortcut" => descriptor with { OwnershipTransitionSequenceId = "ResponderToConsumer" },
            "inverse" => descriptor with { SettlementPolicyId = "RollbackToResponder" },
            "version" => descriptor with { Version = 2 },
            _ => descriptor
        };
        var edge = closedPlan.Edges[0] with
        {
            Kind = SipJobEdgeKind.RegionMove,
            OwnershipUseSemanticsId = "MoveExclusive",
            IsolationSemanticsId = "OwnerTokenRematerialization",
            RegionMove = descriptor,
            RegionBorrow = mutation == "borrow-sidecar"
                ? new(1, "ProducerResponse", "ReadOnly", "LinearConsumer", "ReleaseUseThenReturnLoan")
                : null
        };
        var plan = NewPlan(closedPlan, edges: [edge],
            gates: [SipJobPlanVerifier.LinearGate, SipJobPlanVerifier.RegionMoveGate]).WithDigest();

        var result = SipJobPlanVerifier.Verify(plan, catalog);

        Assert.False(result.IsSuccess);
        Assert.Equal(mutation == "version" ? SipJobPlanError.UnknownVersion : SipJobPlanError.UnsupportedContour,
            result.Failure!.Value.Error);
    }

    [Fact]
    public void RegionMoveGateWithoutExactMoveEdgeFailsClosed()
    {
        var (plan, catalog) = ValidPlan();
        var widened = NewPlan(plan,
            gates: [SipJobPlanVerifier.LinearGate, SipJobPlanVerifier.RegionMoveGate]).WithDigest();

        var result = SipJobPlanVerifier.Verify(widened, catalog);

        Assert.False(result.IsSuccess);
        Assert.Equal(SipJobPlanError.UnsupportedGate, result.Failure!.Value.Error);
    }

    [Theory]
    [InlineData("Extra")]
    [InlineData("formatVersion")]
    public void ParserRejectsUnknownOrWrongCaseField(string field)
    {
        var json = $$"""
        { "FormatVersion": 1, "Stages": [], "Edges": [], "DeclaredGateSet": [], "PlanDigest": "{{new string('0', 64)}}", "{{field}}": 1 }
        """;
        var parsed = SipJobPlanJsonParser.Parse(json);
        Assert.Null(parsed.Plan);
        Assert.Equal(SipJobPlanError.Malformed, parsed.Failure!.Value.Error);
    }

    [Fact]
    public void ParserRejectsMissingSemanticEnumInsteadOfUsingClrDefault()
    {
        var json = $$"""
        {
          "FormatVersion": 1,
          "Stages": [{
            "Version": 1, "StageId": "s1", "ContractId": "c", "ContractDigest": "d",
            "OperationId": "op", "RequestSchemaId": "schema:req", "RequestSchemaDigest": "rd",
            "ResponseSchemaId": "schema:res", "ResponseSchemaDigest": "sd",
            "GeneratedThunkId": "thunk:op", "ThunkDigest": "td",
            "AuthorityRequirements": [], "OwnershipUseRequirements": [],
            "ProtocolTransitionId": "protocol:none", "InvocationObservability": "HiddenIntermediate",
            "CancellationPolicyId": "cancel:job", "EffectClass": "None", "ExecutionClass": "ManagedDefault"
          }],
          "Edges": [], "DeclaredGateSet": ["FG-JOB-LINEAR"], "PlanDigest": "{{new string('0', 64)}}"
        }
        """;

        var parsed = SipJobPlanJsonParser.Parse(json);

        Assert.Null(parsed.Plan);
        Assert.Equal(SipJobPlanError.Malformed, parsed.Failure!.Value.Error);
    }

    [Theory]
    [InlineData("Stages", "Edges")]
    [InlineData("Edges", "Stages")]
    public void ParserRejectsNullDescriptorArrayElementsWithoutThrowing(string nullArray, string emptyArray)
    {
        var json = $$"""
        {
          "FormatVersion": 1,
          "{{nullArray}}": [null], "{{emptyArray}}": [],
          "DeclaredGateSet": ["FG-JOB-LINEAR"], "PlanDigest": "{{new string('0', 64)}}"
        }
        """;

        var parsed = SipJobPlanJsonParser.Parse(json);

        Assert.Null(parsed.Plan);
        Assert.Equal(SipJobPlanError.Malformed, parsed.Failure!.Value.Error);
    }

    [Fact]
    public void ParserRejectsNullAuthorityRequirementWithoutThrowing()
    {
        var json = $$"""
        {
          "FormatVersion": 1,
          "Stages": [{
            "Version": 1, "StageId": "s1", "ContractId": "c", "ContractDigest": "d",
            "OperationId": "op", "RequestSchemaId": "schema:req", "RequestSchemaDigest": "rd",
            "ResponseSchemaId": "schema:res", "ResponseSchemaDigest": "sd",
            "GeneratedThunkId": "thunk:op", "ThunkDigest": "td",
            "AuthorityRequirements": [null], "OwnershipUseRequirements": [],
            "ProtocolTransitionId": "protocol:none", "InvocationObservability": "HiddenIntermediate",
            "CancellationPolicyId": "cancel:job", "EffectClass": "None", "ExecutionClass": "ManagedDefault",
            "Mode": "Synchronous"
          }],
          "Edges": [], "DeclaredGateSet": ["FG-JOB-LINEAR"], "PlanDigest": "{{new string('0', 64)}}"
        }
        """;

        var parsed = SipJobPlanJsonParser.Parse(json);

        Assert.Null(parsed.Plan);
        Assert.Equal(SipJobPlanError.Malformed, parsed.Failure!.Value.Error);
    }

    [Fact]
    public void DescriptorSurfaceCannotCarryRawRuntimeReferences()
    {
        var forbidden = new[] { typeof(object), typeof(Type), typeof(Delegate), typeof(Task), typeof(ValueTask), typeof(IServiceProvider) };
        var descriptorTypes = new[] { typeof(SipJobPlanDescriptor), typeof(SipJobStageDescriptor), typeof(SipJobEdgeDescriptor), typeof(SipJobRegionBorrowDescriptor), typeof(SipJobRegionMoveDescriptor), typeof(SipJobAuthorityRequirement), typeof(VerifiedPlanMetadata) };
        foreach (var property in descriptorTypes
                     .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                     .Where(property => property.Name != "EqualityContract"))
            Assert.DoesNotContain(property.PropertyType, forbidden);
    }

    private static (SipJobPlanDescriptor Plan, SipJobClosedCatalog Catalog) ValidPlan()
    {
        var sharedSchema = Hash("shared-schema");
        var first = Stage("s1", "contract-a", "op-a", Hash("request-a"), sharedSchema, SipJobInvocationObservability.HiddenIntermediate);
        var second = Stage("s2", "contract-b", "op-b", sharedSchema, Hash("response-b"), SipJobInvocationObservability.FinalPublication);
        var edge = new SipJobEdgeDescriptor(1, "e1", "s1", "s2", SipJobEdgeKind.ClosedCopiedValue,
            "schema:shared", sharedSchema, "None", "ClosedCopy", "InternalOnly", SipJobBarrierClass.None, "JobRun");
        var plan = new SipJobPlanDescriptor(1, [first, second], [edge], [SipJobPlanVerifier.LinearGate], "").WithDigest();
        var sources = ImmutableHashSet.Create(SipJobAuthoritySourceClass.CallerProvidedCapabilityReference, SipJobAuthoritySourceClass.SessionBoundCapabilityReference);
        var catalog = new SipJobClosedCatalog(new[] { Entry(first, sources), Entry(second, sources) });
        return (plan, catalog);
    }

    private static SipJobStageDescriptor Stage(string id, string contract, string operation, string requestDigest, string responseDigest, SipJobInvocationObservability observability) =>
        new(1, id, contract, Hash(contract), operation,
            id == "s1" ? "schema:request-a" : "schema:shared", requestDigest,
            id == "s1" ? "schema:shared" : "schema:response-b", responseDigest,
            $"thunk:{operation}", Hash($"thunk:{operation}"),
            [new(1, SipJobAuthoritySourceClass.CallerProvidedCapabilityReference, "capability:opaque", "invoke")],
            [], "protocol:none", observability, "cancel:job", SipJobEffectClass.None,
            SipJobExecutionClass.ManagedDefault, SipJobStageMode.Synchronous);

    private static SipJobCatalogEntry Entry(SipJobStageDescriptor stage, ImmutableHashSet<SipJobAuthoritySourceClass> sources) =>
        new(stage.ContractId, stage.ContractDigest, stage.OperationId, stage.RequestSchemaId, stage.RequestSchemaDigest,
            stage.ResponseSchemaId, stage.ResponseSchemaDigest, stage.GeneratedThunkId, stage.ThunkDigest, sources);

    private static SipJobStageDescriptor Copy(SipJobStageDescriptor value, uint? version = null, string? stageId = null,
        string? contractDigest = null, string? operationId = null, string? requestSchemaDigest = null,
        string? responseSchemaDigest = null, string? thunkDigest = null, IEnumerable<SipJobAuthorityRequirement>? authorities = null,
        string? protocol = null, string? cancellation = null, SipJobEffectClass? effect = null,
        SipJobExecutionClass? execution = null, SipJobStageMode? mode = null, SipJobInvocationObservability? observability = null) =>
        new(version ?? value.Version, stageId ?? value.StageId, value.ContractId, contractDigest ?? value.ContractDigest,
            operationId ?? value.OperationId, value.RequestSchemaId, requestSchemaDigest ?? value.RequestSchemaDigest,
            value.ResponseSchemaId, responseSchemaDigest ?? value.ResponseSchemaDigest, value.GeneratedThunkId,
            thunkDigest ?? value.ThunkDigest, authorities ?? value.AuthorityRequirements, value.OwnershipUseRequirements,
            protocol ?? value.ProtocolTransitionId, observability ?? value.InvocationObservability,
            cancellation ?? value.CancellationPolicyId, effect ?? value.EffectClass, execution ?? value.ExecutionClass, mode ?? value.Mode);

    private static SipJobPlanDescriptor NewPlan(SipJobPlanDescriptor value, uint? formatVersion = null,
        IEnumerable<SipJobStageDescriptor>? stages = null, IEnumerable<SipJobEdgeDescriptor>? edges = null,
        IEnumerable<string>? gates = null) =>
        new(formatVersion ?? value.FormatVersion, stages ?? value.Stages, edges ?? value.Edges, gates ?? value.DeclaredGateSet, value.PlanDigest);

    private static string Hash(string value) => Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(value)));
}
