using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SingPlus.Runtime;

internal enum SipJobAuthoritySourceClass
{
    CallerProvidedCapabilityReference = 0,
    SessionBoundCapabilityReference,
    PreviousStageDeclaredReturn,
    RegionAuthorityHandle,
    SealedObjectReference,
    ExternalOperationReference,
    ExistingProtocolStateOwnerReference,
}

internal enum SipJobEdgeKind { ClosedCopiedValue = 0, RegionBorrow, RegionMove, MaterializedSipBoundary, ExternalEffectBoundary }
internal enum SipJobEffectClass { None = 0, ExternalEffect }
internal enum SipJobExecutionClass { None = 0, ManagedDefault }
internal enum SipJobStageMode { Synchronous = 0, Asynchronous }
internal enum SipJobInvocationObservability { HiddenIntermediate = 0, FinalPublication }
internal enum SipJobBarrierClass { None = 0, ExternalEffect, Publication, OwnershipSettlement, AsyncWait, CrossRuntime, IndependentCancellation, ObservableInvocation, NativeIsolated, ConfidentialDomain, IrreversiblePrivateMutation, UnsupportedAuthorityCommit, ResourceConsumption }

internal readonly record struct SipJobAuthorityRequirement(
    uint Version,
    SipJobAuthoritySourceClass Source,
    string OwnerIdentity,
    string RequirementId);

internal sealed record SipJobStageDescriptor
{
    internal SipJobStageDescriptor(
        uint version, string stageId, string contractId, string contractDigest,
        string operationId, string requestSchemaId, string requestSchemaDigest,
        string responseSchemaId, string responseSchemaDigest, string generatedThunkId,
        string thunkDigest, IEnumerable<SipJobAuthorityRequirement> authorityRequirements,
        IEnumerable<string> ownershipUseRequirements, string protocolTransitionId,
        SipJobInvocationObservability invocationObservability, string cancellationPolicyId,
        SipJobEffectClass effectClass, SipJobExecutionClass executionClass, SipJobStageMode mode)
    {
        Version = version;
        StageId = stageId;
        ContractId = contractId;
        ContractDigest = contractDigest;
        OperationId = operationId;
        RequestSchemaId = requestSchemaId;
        RequestSchemaDigest = requestSchemaDigest;
        ResponseSchemaId = responseSchemaId;
        ResponseSchemaDigest = responseSchemaDigest;
        GeneratedThunkId = generatedThunkId;
        ThunkDigest = thunkDigest;
        AuthorityRequirements = authorityRequirements.ToImmutableArray();
        OwnershipUseRequirements = ownershipUseRequirements.ToImmutableArray();
        ProtocolTransitionId = protocolTransitionId;
        InvocationObservability = invocationObservability;
        CancellationPolicyId = cancellationPolicyId;
        EffectClass = effectClass;
        ExecutionClass = executionClass;
        Mode = mode;
    }

    internal uint Version { get; }
    internal string StageId { get; }
    internal string ContractId { get; }
    internal string ContractDigest { get; }
    internal string OperationId { get; }
    internal string RequestSchemaId { get; }
    internal string RequestSchemaDigest { get; }
    internal string ResponseSchemaId { get; }
    internal string ResponseSchemaDigest { get; }
    internal string GeneratedThunkId { get; }
    internal string ThunkDigest { get; }
    internal ImmutableArray<SipJobAuthorityRequirement> AuthorityRequirements { get; }
    internal ImmutableArray<string> OwnershipUseRequirements { get; }
    internal string ProtocolTransitionId { get; }
    internal SipJobInvocationObservability InvocationObservability { get; }
    internal string CancellationPolicyId { get; }
    internal SipJobEffectClass EffectClass { get; }
    internal SipJobExecutionClass ExecutionClass { get; }
    internal SipJobStageMode Mode { get; }
}

internal sealed record SipJobEdgeDescriptor(
    uint Version,
    string EdgeId,
    string ProducerStageId,
    string ConsumerStageId,
    SipJobEdgeKind Kind,
    string ValueSchemaId,
    string ValueSchemaDigest,
    string OwnershipUseSemanticsId,
    string IsolationSemanticsId,
    string PublicationSemanticsId,
    SipJobBarrierClass BarrierClass,
    string CancellationScopeId,
    SipJobRegionBorrowDescriptor? RegionBorrow = null,
    SipJobRegionMoveDescriptor? RegionMove = null);

internal sealed record SipJobRegionBorrowDescriptor(
    uint Version,
    string RegionSourceRole,
    string RequiredUseMode,
    string LifetimeScopeId,
    string ClosurePolicyId);

internal sealed record SipJobRegionMoveDescriptor(
    uint Version,
    string RegionSourceRole,
    string OwnershipTransitionSequenceId,
    string SettlementPolicyId);

internal sealed record SipJobPlanDescriptor
{
    internal SipJobPlanDescriptor(
        uint formatVersion,
        IEnumerable<SipJobStageDescriptor> stages,
        IEnumerable<SipJobEdgeDescriptor> edges,
        IEnumerable<string> declaredGateSet,
        string planDigest)
    {
        FormatVersion = formatVersion;
        Stages = stages.ToImmutableArray();
        Edges = edges.ToImmutableArray();
        DeclaredGateSet = declaredGateSet.ToImmutableArray();
        PlanDigest = planDigest;
    }

    internal uint FormatVersion { get; }
    internal ImmutableArray<SipJobStageDescriptor> Stages { get; }
    internal ImmutableArray<SipJobEdgeDescriptor> Edges { get; }
    internal ImmutableArray<string> DeclaredGateSet { get; }
    internal string PlanDigest { get; }

    internal SipJobPlanDescriptor WithDigest() =>
        new(FormatVersion, Stages, Edges, DeclaredGateSet, SipJobPlanDigest.Compute(this));
}

internal sealed record SipJobCatalogEntry(
    string ContractId,
    string ContractDigest,
    string OperationId,
    string RequestSchemaId,
    string RequestSchemaDigest,
    string ResponseSchemaId,
    string ResponseSchemaDigest,
    string GeneratedThunkId,
    string ThunkDigest,
    ImmutableHashSet<SipJobAuthoritySourceClass> AllowedAuthoritySources,
    bool AllowsPreviousStageAuthorityReturn = false);

internal sealed class SipJobClosedCatalog(IEnumerable<SipJobCatalogEntry> entries)
{
    private readonly ImmutableDictionary<(string ContractId, string OperationId), SipJobCatalogEntry> _entries =
        entries.ToImmutableDictionary(entry => (entry.ContractId, entry.OperationId));

    internal bool TryGet(string contractId, string operationId, out SipJobCatalogEntry entry) =>
        _entries.TryGetValue((contractId, operationId), out entry!);
}

internal enum SipJobPlanError
{
    None = 0,
    Malformed,
    UnknownVersion,
    UnsupportedGate,
    InvalidStageCount,
    DuplicateStage,
    DuplicateEdge,
    UnknownStageReference,
    NotLinear,
    CycleOrOrphan,
    UnknownOperation,
    CatalogMismatch,
    UnsupportedAuthoritySource,
    UnsupportedContour,
    SchemaMismatch,
    UnknownPolicy,
    DigestMismatch,
}

internal readonly record struct SipJobPlanFailure(SipJobPlanError Error, string Detail);
internal sealed record VerifiedPlanMetadata(string PlanDigest, ImmutableArray<string> OrderedStageIds, uint FormatVersion);
internal readonly record struct SipJobVerificationResult(VerifiedPlanMetadata? Metadata, SipJobPlanFailure? Failure)
{
    internal bool IsSuccess => Metadata is not null && Failure is null;
    internal static SipJobVerificationResult Success(VerifiedPlanMetadata metadata) => new(metadata, null);
    internal static SipJobVerificationResult Fail(SipJobPlanError error, string detail) => new(null, new(error, detail));
}

internal static class SipJobPlanVerifier
{
    internal const uint FormatVersion = 1;
    internal const uint DescriptorVersion = 1;
    internal const string LinearGate = "FG-JOB-LINEAR";
    internal const string RegionBorrowGate = "FG-REGION-BORROW";
    internal const string RegionMoveGate = "FG-REGION-MOVE";

    internal static SipJobVerificationResult Verify(SipJobPlanDescriptor plan, SipJobClosedCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);

        if (plan.Stages.Any(static stage => stage is null) || plan.Edges.Any(static edge => edge is null))
            return Fail(SipJobPlanError.Malformed, "Plan descriptor collections cannot contain null entries.");
        if (plan.FormatVersion != FormatVersion)
            return Fail(SipJobPlanError.UnknownVersion, "Unsupported plan format version.");
        if (plan.Stages.Length is < 2 or > 4)
            return Fail(SipJobPlanError.InvalidStageCount, "Linear MVP requires two through four stages.");
        var closedCopyGateSet = plan.DeclaredGateSet is [LinearGate];
        var regionBorrowGateSet = plan.DeclaredGateSet is [LinearGate, RegionBorrowGate];
        var regionMoveGateSet = plan.DeclaredGateSet is [LinearGate, RegionMoveGate];
        if (!closedCopyGateSet && !regionBorrowGateSet && !regionMoveGateSet)
            return Fail(SipJobPlanError.UnsupportedGate, "The admitted gate set must be exactly FG-JOB-LINEAR followed by at most one qualified Region edge gate.");
        if (plan.Stages.Any(stage => stage.Version != DescriptorVersion) || plan.Edges.Any(edge => edge.Version != DescriptorVersion) ||
            plan.Stages.SelectMany(stage => stage.AuthorityRequirements).Any(requirement => requirement.Version != DescriptorVersion) ||
            plan.Edges.Any(edge => edge.RegionBorrow is { Version: not DescriptorVersion } ||
                                   edge.RegionMove is { Version: not DescriptorVersion }))
            return Fail(SipJobPlanError.UnknownVersion, "Unsupported descriptor version.");
        if (plan.Stages.Any(stage => !ValidId(stage.StageId)) || plan.Edges.Any(edge => !ValidId(edge.EdgeId)))
            return Fail(SipJobPlanError.Malformed, "Identifiers must be non-empty canonical values.");
        if (plan.Stages.Select(stage => stage.StageId).Distinct(StringComparer.Ordinal).Count() != plan.Stages.Length)
            return Fail(SipJobPlanError.DuplicateStage, "Stage IDs must be unique.");
        if (plan.Edges.Select(edge => edge.EdgeId).Distinct(StringComparer.Ordinal).Count() != plan.Edges.Length ||
            plan.Edges.Select(edge => (edge.ProducerStageId, edge.ConsumerStageId)).Distinct().Count() != plan.Edges.Length)
            return Fail(SipJobPlanError.DuplicateEdge, "Edges must be unique.");
        if (plan.Edges.Length != plan.Stages.Length - 1)
            return Fail(SipJobPlanError.NotLinear, "Linear MVP requires exactly stage-count minus one edges.");
        var regionBorrowEdgeCount = plan.Edges.Count(edge => edge.Kind == SipJobEdgeKind.RegionBorrow);
        if (regionBorrowGateSet && regionBorrowEdgeCount != 1)
            return Fail(SipJobPlanError.UnsupportedGate, "FG-REGION-BORROW must correspond to exactly one linear Region BORROW edge.");
        var regionMoveEdgeCount = plan.Edges.Count(edge => edge.Kind == SipJobEdgeKind.RegionMove);
        if (regionMoveGateSet && regionMoveEdgeCount != 1)
            return Fail(SipJobPlanError.UnsupportedGate, "FG-REGION-MOVE must correspond to exactly one linear Region MOVE edge.");

        var stageIds = plan.Stages.Select(stage => stage.StageId).ToHashSet(StringComparer.Ordinal);
        if (plan.Edges.Any(edge => !stageIds.Contains(edge.ProducerStageId) || !stageIds.Contains(edge.ConsumerStageId) || edge.ProducerStageId == edge.ConsumerStageId))
            return Fail(SipJobPlanError.UnknownStageReference, "Every edge must connect two distinct declared stages.");

        for (var index = 0; index < plan.Stages.Length - 1; index++)
        {
            var edge = plan.Edges[index];
            if (edge.ProducerStageId != plan.Stages[index].StageId || edge.ConsumerStageId != plan.Stages[index + 1].StageId)
                return Fail(SipJobPlanError.CycleOrOrphan, "Edges must exactly follow declared stage order without side edges.");
        }

        for (var index = 0; index < plan.Stages.Length; index++)
        {
            var stage = plan.Stages[index];
            if (!catalog.TryGet(stage.ContractId, stage.OperationId, out var admitted))
                return Fail(SipJobPlanError.UnknownOperation, $"Stage '{stage.StageId}' is not in the closed catalog.");
            if (!CatalogMatches(stage, admitted))
                return Fail(SipJobPlanError.CatalogMismatch, $"Stage '{stage.StageId}' does not match its catalog entry.");
            if (stage.AuthorityRequirements.Any(requirement =>
                    !Enum.IsDefined(requirement.Source) || !admitted.AllowedAuthoritySources.Contains(requirement.Source) ||
                    requirement.Source == SipJobAuthoritySourceClass.PreviousStageDeclaredReturn && !admitted.AllowsPreviousStageAuthorityReturn))
                return Fail(SipJobPlanError.UnsupportedAuthoritySource, $"Stage '{stage.StageId}' uses an unsupported authority source.");
            if (stage.Mode != SipJobStageMode.Synchronous || stage.EffectClass != SipJobEffectClass.None ||
                stage.ExecutionClass is not (SipJobExecutionClass.None or SipJobExecutionClass.ManagedDefault) ||
                stage.OwnershipUseRequirements.Length != 0)
                return Fail(SipJobPlanError.UnsupportedContour, $"Stage '{stage.StageId}' is outside the synchronous closed-value MVP.");
            var expectedObservability = index == plan.Stages.Length - 1
                ? SipJobInvocationObservability.FinalPublication
                : SipJobInvocationObservability.HiddenIntermediate;
            if (stage.InvocationObservability != expectedObservability)
                return Fail(SipJobPlanError.UnsupportedContour, "Only the final stage may publish.");
            if (!ValidId(stage.ProtocolTransitionId) || !ValidId(stage.CancellationPolicyId) ||
                stage.AuthorityRequirements.Any(requirement => !ValidId(requirement.OwnerIdentity) || !ValidId(requirement.RequirementId)))
                return Fail(SipJobPlanError.UnknownPolicy, "Protocol, cancellation and authority policy IDs must be explicit.");
        }

        for (var index = 0; index < plan.Edges.Length; index++)
        {
            var edge = plan.Edges[index];
            var producer = plan.Stages[index];
            var consumer = plan.Stages[index + 1];
            var closedCopy = edge.Kind == SipJobEdgeKind.ClosedCopiedValue && edge.RegionBorrow is null && edge.RegionMove is null &&
                edge.BarrierClass == SipJobBarrierClass.None && edge.OwnershipUseSemanticsId == "None" &&
                edge.IsolationSemanticsId == "ClosedCopy" && edge.PublicationSemanticsId == "InternalOnly" &&
                edge.CancellationScopeId == "JobRun";
            var borrow = regionBorrowGateSet && edge.Kind == SipJobEdgeKind.RegionBorrow &&
                edge.BarrierClass == SipJobBarrierClass.None && edge.OwnershipUseSemanticsId == "BorrowRead" &&
                edge.IsolationSemanticsId == "OwnerQualifiedView" && edge.PublicationSemanticsId == "InternalOnly" &&
                edge.CancellationScopeId == "JobRun" && edge.RegionBorrow is
                {
                    RegionSourceRole: "ProducerResponse",
                    RequiredUseMode: "ReadOnly",
                    LifetimeScopeId: "LinearConsumer",
                    ClosurePolicyId: "ReleaseUseThenReturnLoan"
                } && edge.RegionMove is null;
            var move = regionMoveGateSet && edge.Kind == SipJobEdgeKind.RegionMove && edge.RegionBorrow is null &&
                edge.BarrierClass == SipJobBarrierClass.None && edge.OwnershipUseSemanticsId == "MoveExclusive" &&
                edge.IsolationSemanticsId == "OwnerTokenRematerialization" && edge.PublicationSemanticsId == "InternalOnly" &&
                edge.CancellationScopeId == "JobRun" && edge.RegionMove is
                {
                    RegionSourceRole: "ProducerResponse",
                    OwnershipTransitionSequenceId: "ResponderToCallerThenCallerToConsumer",
                    SettlementPolicyId: "NoImplicitInverseMove"
                };
            if (!closedCopy && !borrow && !move)
                return Fail(SipJobPlanError.UnsupportedContour, $"Edge '{edge.EdgeId}' is outside the admitted linear contours.");
            if (edge.ValueSchemaId != producer.ResponseSchemaId || edge.ValueSchemaDigest != producer.ResponseSchemaDigest ||
                edge.ValueSchemaId != consumer.RequestSchemaId || edge.ValueSchemaDigest != consumer.RequestSchemaDigest)
                return Fail(SipJobPlanError.SchemaMismatch, $"Edge '{edge.EdgeId}' schema is incompatible.");
        }

        var computed = SipJobPlanDigest.Compute(plan);
        if (!FixedDigestEquals(computed, plan.PlanDigest))
            return Fail(SipJobPlanError.DigestMismatch, "Plan digest does not match canonical semantics.");

        return SipJobVerificationResult.Success(new(plan.PlanDigest, plan.Stages.Select(stage => stage.StageId).ToImmutableArray(), plan.FormatVersion));
    }

    private static bool CatalogMatches(SipJobStageDescriptor stage, SipJobCatalogEntry entry) =>
        stage.ContractDigest == entry.ContractDigest && stage.RequestSchemaId == entry.RequestSchemaId &&
        stage.RequestSchemaDigest == entry.RequestSchemaDigest && stage.ResponseSchemaId == entry.ResponseSchemaId &&
        stage.ResponseSchemaDigest == entry.ResponseSchemaDigest && stage.GeneratedThunkId == entry.GeneratedThunkId &&
        stage.ThunkDigest == entry.ThunkDigest;

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();
    private static bool FixedDigestEquals(string expected, string? supplied) =>
        expected.Length == 64 && supplied is { Length: 64 } &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));
    private static SipJobVerificationResult Fail(SipJobPlanError error, string detail) => SipJobVerificationResult.Fail(error, detail);
}

internal static class SipJobPlanDigest
{
    internal static string Compute(SipJobPlanDescriptor plan)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", plan.FormatVersion);
            writer.WriteStartArray("declaredGateSet");
            foreach (var gate in plan.DeclaredGateSet) writer.WriteStringValue(gate);
            writer.WriteEndArray();
            writer.WriteStartArray("stages");
            foreach (var stage in plan.Stages) WriteStage(writer, stage);
            writer.WriteEndArray();
            writer.WriteStartArray("edges");
            foreach (var edge in plan.Edges) WriteEdge(writer, edge);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteStage(Utf8JsonWriter writer, SipJobStageDescriptor stage)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", stage.Version); writer.WriteString("stageId", stage.StageId);
        writer.WriteString("contractId", stage.ContractId); writer.WriteString("contractDigest", stage.ContractDigest);
        writer.WriteString("operationId", stage.OperationId); writer.WriteString("requestSchemaId", stage.RequestSchemaId);
        writer.WriteString("requestSchemaDigest", stage.RequestSchemaDigest); writer.WriteString("responseSchemaId", stage.ResponseSchemaId);
        writer.WriteString("responseSchemaDigest", stage.ResponseSchemaDigest); writer.WriteString("generatedThunkId", stage.GeneratedThunkId);
        writer.WriteString("thunkDigest", stage.ThunkDigest);
        writer.WriteStartArray("authorityRequirements");
        foreach (var requirement in stage.AuthorityRequirements)
        {
            writer.WriteStartObject(); writer.WriteNumber("version", requirement.Version);
            writer.WriteString("source", requirement.Source.ToString()); writer.WriteString("ownerIdentity", requirement.OwnerIdentity);
            writer.WriteString("requirementId", requirement.RequirementId); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("ownershipUseRequirements"); foreach (var item in stage.OwnershipUseRequirements) writer.WriteStringValue(item); writer.WriteEndArray();
        writer.WriteString("protocolTransitionId", stage.ProtocolTransitionId);
        writer.WriteString("invocationObservability", stage.InvocationObservability.ToString());
        writer.WriteString("cancellationPolicyId", stage.CancellationPolicyId); writer.WriteString("effectClass", stage.EffectClass.ToString());
        writer.WriteString("executionClass", stage.ExecutionClass.ToString()); writer.WriteString("mode", stage.Mode.ToString());
        writer.WriteEndObject();
    }

    private static void WriteEdge(Utf8JsonWriter writer, SipJobEdgeDescriptor edge)
    {
        writer.WriteStartObject(); writer.WriteNumber("version", edge.Version); writer.WriteString("edgeId", edge.EdgeId);
        writer.WriteString("producerStageId", edge.ProducerStageId); writer.WriteString("consumerStageId", edge.ConsumerStageId);
        writer.WriteString("kind", edge.Kind.ToString()); writer.WriteString("valueSchemaId", edge.ValueSchemaId);
        writer.WriteString("valueSchemaDigest", edge.ValueSchemaDigest); writer.WriteString("ownershipUseSemanticsId", edge.OwnershipUseSemanticsId);
        writer.WriteString("isolationSemanticsId", edge.IsolationSemanticsId); writer.WriteString("publicationSemanticsId", edge.PublicationSemanticsId);
        writer.WriteString("barrierClass", edge.BarrierClass.ToString()); writer.WriteString("cancellationScopeId", edge.CancellationScopeId);
        writer.WritePropertyName("regionBorrow");
        if (edge.RegionBorrow is not { } borrow) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject(); writer.WriteNumber("version", borrow.Version);
            writer.WriteString("regionSourceRole", borrow.RegionSourceRole); writer.WriteString("requiredUseMode", borrow.RequiredUseMode);
            writer.WriteString("lifetimeScopeId", borrow.LifetimeScopeId); writer.WriteString("closurePolicyId", borrow.ClosurePolicyId);
            writer.WriteEndObject();
        }
        writer.WritePropertyName("regionMove");
        if (edge.RegionMove is not { } move) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject(); writer.WriteNumber("version", move.Version);
            writer.WriteString("regionSourceRole", move.RegionSourceRole);
            writer.WriteString("ownershipTransitionSequenceId", move.OwnershipTransitionSequenceId);
            writer.WriteString("settlementPolicyId", move.SettlementPolicyId);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

internal static class SipJobPlanJsonParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    internal static (SipJobPlanDescriptor? Plan, SipJobPlanFailure? Failure) Parse(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<PlanDto>(json, Options) ?? throw new JsonException("Plan is null.");
            var stages = (dto.Stages ?? throw new JsonException("Stages are required."))
                .Select(stage => stage ?? throw new JsonException("Stage entries cannot be null."))
                .Select(stage => new SipJobStageDescriptor(
                stage.Version, Required(stage.StageId), Required(stage.ContractId), Required(stage.ContractDigest), Required(stage.OperationId),
                Required(stage.RequestSchemaId), Required(stage.RequestSchemaDigest), Required(stage.ResponseSchemaId), Required(stage.ResponseSchemaDigest),
                Required(stage.GeneratedThunkId), Required(stage.ThunkDigest),
                (stage.AuthorityRequirements ?? [])
                    .Select(item => item ?? throw new JsonException("Authority requirement entries cannot be null."))
                    .Select(item => new SipJobAuthorityRequirement(item.Version, item.Source, Required(item.OwnerIdentity), Required(item.RequirementId))),
                stage.OwnershipUseRequirements ?? [], Required(stage.ProtocolTransitionId), stage.InvocationObservability,
                Required(stage.CancellationPolicyId), stage.EffectClass, stage.ExecutionClass, stage.Mode));
            var edges = (dto.Edges ?? throw new JsonException("Edges are required."))
                .Select(edge => edge ?? throw new JsonException("Edge entries cannot be null."))
                .Select(edge => new SipJobEdgeDescriptor(
                edge.Version, Required(edge.EdgeId), Required(edge.ProducerStageId), Required(edge.ConsumerStageId), edge.Kind,
                Required(edge.ValueSchemaId), Required(edge.ValueSchemaDigest), Required(edge.OwnershipUseSemanticsId),
                Required(edge.IsolationSemanticsId), Required(edge.PublicationSemanticsId), edge.BarrierClass, Required(edge.CancellationScopeId),
                edge.RegionBorrow is null ? null : new SipJobRegionBorrowDescriptor(edge.RegionBorrow.Version,
                    Required(edge.RegionBorrow.RegionSourceRole), Required(edge.RegionBorrow.RequiredUseMode),
                    Required(edge.RegionBorrow.LifetimeScopeId), Required(edge.RegionBorrow.ClosurePolicyId)),
                edge.RegionMove is null ? null : new SipJobRegionMoveDescriptor(edge.RegionMove.Version,
                    Required(edge.RegionMove.RegionSourceRole), Required(edge.RegionMove.OwnershipTransitionSequenceId),
                    Required(edge.RegionMove.SettlementPolicyId))));
            return (new(dto.FormatVersion, stages, edges, dto.DeclaredGateSet ?? [], Required(dto.PlanDigest)), null);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return (null, new(SipJobPlanError.Malformed, exception.Message));
        }
    }

    private static string Required(string? value) => value ?? throw new JsonException("Required string is missing.");

    private sealed record PlanDto(uint FormatVersion, StageDto?[]? Stages, EdgeDto?[]? Edges, string[]? DeclaredGateSet, string? PlanDigest);
    private sealed record AuthorityDto(uint Version, SipJobAuthoritySourceClass Source, string? OwnerIdentity, string? RequirementId);
    private sealed record StageDto(uint Version, string? StageId, string? ContractId, string? ContractDigest, string? OperationId,
        string? RequestSchemaId, string? RequestSchemaDigest, string? ResponseSchemaId, string? ResponseSchemaDigest,
        string? GeneratedThunkId, string? ThunkDigest, AuthorityDto?[]? AuthorityRequirements, string[]? OwnershipUseRequirements,
        string? ProtocolTransitionId, SipJobInvocationObservability InvocationObservability, string? CancellationPolicyId,
        SipJobEffectClass EffectClass, SipJobExecutionClass ExecutionClass, SipJobStageMode Mode);
    private sealed record EdgeDto(uint Version, string? EdgeId, string? ProducerStageId, string? ConsumerStageId, SipJobEdgeKind Kind,
        string? ValueSchemaId, string? ValueSchemaDigest, string? OwnershipUseSemanticsId, string? IsolationSemanticsId,
        string? PublicationSemanticsId, SipJobBarrierClass BarrierClass, string? CancellationScopeId,
        RegionBorrowDto? RegionBorrow, RegionMoveDto? RegionMove);
    private sealed record RegionBorrowDto(uint Version, string? RegionSourceRole, string? RequiredUseMode,
        string? LifetimeScopeId, string? ClosurePolicyId);
    private sealed record RegionMoveDto(uint Version, string? RegionSourceRole,
        string? OwnershipTransitionSequenceId, string? SettlementPolicyId);
}
