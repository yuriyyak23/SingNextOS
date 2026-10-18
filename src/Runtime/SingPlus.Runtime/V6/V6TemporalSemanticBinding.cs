using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>Non-authoritative exact correlation for the first temporal upper-bound contour.</summary>
internal sealed record V6TemporalSemanticBindingV1(
    ushort Version,
    SemanticExecutionBindingDigestV1 BaseBindingDigest,
    OperationSemanticExtensionsV1 Requirements,
    ExecutionGuaranteeExtensionsV1 Guarantees,
    SemanticBindingExtensionSetV1 ExtensionBinding,
    TemporalSemanticsV1 RequiredTemporal,
    TemporalSemanticsV1 ProvidedTemporal)
{
    internal const ushort CurrentVersion = 1;
    internal bool AuthorizesExecution => false;
    internal bool ReservesCapacity => false;
    internal bool GuaranteesDeadline => false;
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<V6TemporalSemanticBindingV1> CreateV6TemporalSemanticBinding(
        SemanticExecutionBindingV1 baseBinding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        OperationSemanticExtensionsV1 requirements,
        ExecutionGuaranteeExtensionsV1 extensionGuarantees,
        ulong providerGeneration)
    {
        ArgumentNullException.ThrowIfNull(baseBinding);
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(guarantees);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(extensionGuarantees);
        if (providerGeneration == 0)
            return KernelResult<V6TemporalSemanticBindingV1>.Fail(KernelError.InvalidMessage,
                "Provider generation is required for a temporal binding.");

        SemanticExecutionBindingV1 exactBase;
        OperationObligationsV1 exactObligations;
        ExecutionGuaranteesV1 exactGuarantees;
        try
        {
            exactBase = baseBinding.Canonicalize();
            exactObligations = obligations.Canonicalize();
            exactGuarantees = guarantees.Canonicalize();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<V6TemporalSemanticBindingV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        if (exactBase.Operation != exactObligations.Operation ||
            exactBase.ObligationsDigest != exactObligations.Digest ||
            exactBase.GuaranteesDigest != exactGuarantees.Digest)
            return KernelResult<V6TemporalSemanticBindingV1>.Fail(KernelError.StaleGeneration,
                "V1 objects do not match the exact base semantic binding.");

        var temporal = ValidateTemporalContour(exactBase, exactObligations, requirements, extensionGuarantees);
        if (!temporal.IsSuccess)
            return KernelResult<V6TemporalSemanticBindingV1>.Fail(temporal.Error, temporal.Message!);

        SemanticBindingExtensionSetV1 extensionBinding;
        try
        {
            extensionBinding = SemanticBindingExtensionSetV1.Create(exactObligations.Digest.Value,
                exactGuarantees.Digest.Value, requirements, extensionGuarantees,
                TemporalGenerationVector(exactBase, exactObligations, providerGeneration));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<V6TemporalSemanticBindingV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        return KernelResult<V6TemporalSemanticBindingV1>.Ok(new(
            V6TemporalSemanticBindingV1.CurrentVersion, exactBase.Digest, requirements, extensionGuarantees,
            extensionBinding, temporal.Value.Required, temporal.Value.Provided));
    }

    internal KernelResult RevalidateV6TemporalSemanticBinding(
        V6TemporalSemanticBindingV1 binding,
        ResourceAdmissionCommit commit,
        SemanticExecutionBindingV1 baseBinding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        ulong currentProviderGeneration)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(commit);
        if (binding.Version != V6TemporalSemanticBindingV1.CurrentVersion ||
            binding.BaseBindingDigest != baseBinding.Digest || currentProviderGeneration == 0 ||
            commit.Operation != baseBinding.Operation || commit.Lease != baseBinding.Lease ||
            commit.BudgetOwner != baseBinding.BudgetOwner ||
            commit.Envelope != binding.RequiredTemporal.ComputeEnvelope)
            return KernelResult.Fail(KernelError.StaleGeneration,
                "Temporal binding identity, budget lease, or resource envelope is stale.");

        var rebuilt = CreateV6TemporalSemanticBinding(baseBinding, obligations, guarantees,
            binding.Requirements, binding.Guarantees, currentProviderGeneration);
        if (!rebuilt.IsSuccess)
            return KernelResult.Fail(rebuilt.Error, rebuilt.Message!);
        var current = rebuilt.Value!;
        if (current.ExtensionBinding.Digest != binding.ExtensionBinding.Digest ||
            current.RequiredTemporal != binding.RequiredTemporal ||
            current.ProvidedTemporal != binding.ProvidedTemporal)
            return KernelResult.Fail(KernelError.StaleGeneration,
                "Temporal extensions or live owner generation vector changed before submit.");
        return KernelResult.Ok();
    }

    internal KernelResult<OperationBinding> SubmitV6TemporalSemanticResourceExternalAdmission(
        ResourceAdmissionCommit commit,
        OperationDependencySnapshot dependencies,
        SemanticExecutionBindingV1 baseBinding,
        V6TemporalSemanticBindingV1 temporalBinding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        SemanticRefinementProofV1 refinement,
        string currentProviderIdentity,
        Func<ulong> currentProviderGeneration,
        Hc.ExternalGenerationSet currentProviderGenerations,
        ISemanticProviderAdmissionService providerAdmission,
        IRuntimeLegalityService runtimeLegality,
        Func<KernelResult> providerSubmit,
        ISemanticTraceSinkV1? traceSink = null)
    {
        ArgumentNullException.ThrowIfNull(providerSubmit);
        ArgumentNullException.ThrowIfNull(currentProviderGeneration);
        var submitted = SubmitSemanticResourceExternalAdmissionWithBinding(commit, dependencies, baseBinding, obligations, guarantees,
            refinement, currentProviderIdentity, currentProviderGenerations, providerAdmission, runtimeLegality,
            operationBinding =>
            {
                var fresh = RevalidateV6TemporalSemanticBinding(temporalBinding, commit, baseBinding,
                    obligations, guarantees, currentProviderGeneration());
                if (!fresh.IsSuccess) return fresh;
                var owners = RevalidateResourceDispatch(commit, operationBinding);
                return owners.IsSuccess ? providerSubmit() : owners;
            }, () => RevalidateV6TemporalSemanticBinding(temporalBinding, commit, baseBinding,
                obligations, guarantees, currentProviderGeneration()), () =>
                {
                    if (traceSink is not null)
                    {
                        RegisterExternalOperationTrace(commit.Operation, temporalBinding.ExtensionBinding.Digest.Value, traceSink);
                        EmitExternalOperationTrace(commit.Operation);
                    }
                }, () => ObserveFailedPreSubmitTrace(commit, baseBinding, obligations,
                    temporalBinding.ExtensionBinding, traceSink, generation =>
                        RevalidateV6TemporalSemanticBinding(temporalBinding, commit, baseBinding,
                            obligations, guarantees, generation)));
        if (traceSink is not null)
        {
            EmitExternalOperationTrace(commit.Operation);
            if (!submitted.IsSuccess) DiscardExternalOperationTraceIfPreSubmit(commit.Operation);
        }
        return submitted;
    }

    private static KernelResult<(TemporalSemanticsV1 Required, TemporalSemanticsV1 Provided)>
        ValidateTemporalContour(
            SemanticExecutionBindingV1 baseBinding,
            OperationObligationsV1 obligations,
            OperationSemanticExtensionsV1 requirements,
            ExecutionGuaranteeExtensionsV1 guarantees)
    {
        var requiredClause = requirements.Clauses.SingleOrDefault(clause =>
            clause.ClassId == TemporalSemanticsV1.ExtensionClassId);
        var providedClause = guarantees.Clauses.SingleOrDefault(clause =>
            clause.ClassId == TemporalSemanticsV1.ExtensionClassId);
        if (requiredClause is null || providedClause is null ||
            requiredClause.Requirement != SemanticExtensionRequirement.Mandatory)
            return KernelResult<(TemporalSemanticsV1, TemporalSemanticsV1)>.Fail(KernelError.PlatformDenied,
                "The temporal upper-bound contour requires one mandatory clause and one guarantee.");

        if (requiredClause.SchemaId != "singnext.temporal-semantics/1" ||
            providedClause.SchemaId != "singnext.temporal-semantics/1" ||
            requiredClause.SchemaVersion != TemporalSemanticsV1.CurrentVersion ||
            providedClause.SchemaVersion != TemporalSemanticsV1.CurrentVersion)
            return KernelResult<(TemporalSemanticsV1, TemporalSemanticsV1)>.Fail(KernelError.PlatformDenied,
                "The temporal contour does not support this exact extension schema tuple.");

        TemporalSemanticsV1 required;
        TemporalSemanticsV1 provided;
        try
        {
            required = TemporalSemanticsV1.ParseCanonical(requiredClause.Payload.Span);
            provided = TemporalSemanticsV1.ParseCanonical(providedClause.Payload.Span);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException)
        {
            return KernelResult<(TemporalSemanticsV1, TemporalSemanticsV1)>.Fail(KernelError.InvalidMessage,
                exception.Message);
        }
        var supported = new HashSet<SemanticExtensionClassId> { TemporalSemanticsV1.ExtensionClassId };
        var decision = SemanticExtensionRefinementV1.Evaluate(requirements, guarantees, supported,
            (providedExtension, requiredExtension) =>
            {
                try
                {
                    return TemporalSemanticPartialOrderV1.Refines(
                        TemporalSemanticsV1.ParseCanonical(providedExtension.Payload.Span),
                        TemporalSemanticsV1.ParseCanonical(requiredExtension.Payload.Span));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException)
                { return false; }
            }, optional => optional.Requirement == SemanticExtensionRequirement.Optional);
        if (!decision.IsAccepted)
            return KernelResult<(TemporalSemanticsV1, TemporalSemanticsV1)>.Fail(KernelError.PlatformDenied,
                $"Temporal extension refinement denied: {decision.Status}/{decision.ClassId}.");

        if (required.Assurance != ResourceAssuranceV1.EnforcedUpperBound ||
            required.DeadlineSemantics != TemporalDeadlineSemanticsV1.None ||
            provided.DeadlineSemantics != TemporalDeadlineSemanticsV1.None ||
            baseBinding.ResourceEnvelope != required.ComputeEnvelope ||
            !obligations.ResourceRequirements.Contains(required.ComputeEnvelope))
            return KernelResult<(TemporalSemanticsV1, TemporalSemanticsV1)>.Fail(KernelError.PlatformDenied,
                "Only the exact ComputeTime enforced-upper-bound contour is enabled in the first temporal slice.");
        return KernelResult<(TemporalSemanticsV1, TemporalSemanticsV1)>.Ok((required, provided));
    }

    private static IReadOnlyList<SemanticGenerationSnapshotV1> TemporalGenerationVector(
        SemanticExecutionBindingV1 binding, OperationObligationsV1 obligations, ulong providerGeneration)
    {
        var values = new List<SemanticGenerationSnapshotV1>
        {
            new("process", obligations.Principal.Generation),
            new("external-operation", obligations.Operation.Generation.Value),
            new("budget-reservation", binding.Lease.Generation.Value),
            new("provider", providerGeneration),
        };
        if (obligations.Session is { } session)
            values.Add(new("session", session.Generation.Value));
        for (var index = 0; index < obligations.RegionUses.Count; index++)
        {
            values.Add(new($"region:{index}", obligations.RegionUses[index].Region.Generation.Value));
            values.Add(new($"region-mutation:{index}", obligations.RegionUses[index].MutationEpoch.Value));
        }
        return values;
    }
}
