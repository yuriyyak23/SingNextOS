using SingPlus.Contracts;
using Hc = HybridCPU.ExternalRuntime.Contracts;

namespace SingPlus.Runtime;

/// <summary>Non-authoritative composition of V1 binding and v6 memory sidecars.</summary>
internal sealed record V6MemorySemanticBindingV1(
    ushort Version,
    SemanticExecutionBindingDigestV1 BaseBindingDigest,
    OperationSemanticExtensionsV1 Requirements,
    ExecutionGuaranteeExtensionsV1 Guarantees,
    SemanticBindingExtensionSetV1 ExtensionBinding,
    MemorySemanticsV1 RequiredMemory,
    MemorySemanticsV1 ProvidedMemory)
{
    internal const ushort CurrentVersion = 1;
    internal bool AuthorizesExecution => false;
    internal bool AuthorizesEffect => false;
    internal bool AuthorizesPublication => false;
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<V6MemorySemanticBindingV1> CreateV6MemorySemanticBinding(
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
            return KernelResult<V6MemorySemanticBindingV1>.Fail(KernelError.InvalidMessage,
                "Provider generation is required for v6 memory binding.");

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
            return KernelResult<V6MemorySemanticBindingV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        if (exactBase.Operation != exactObligations.Operation ||
            exactBase.ObligationsDigest != exactObligations.Digest ||
            exactBase.GuaranteesDigest != exactGuarantees.Digest)
            return KernelResult<V6MemorySemanticBindingV1>.Fail(KernelError.StaleGeneration,
                "V1 objects do not match the exact base semantic binding.");

        var memory = ValidateMemoryContour(exactObligations, requirements, extensionGuarantees);
        if (!memory.IsSuccess)
            return KernelResult<V6MemorySemanticBindingV1>.Fail(memory.Error, memory.Message!);

        SemanticBindingExtensionSetV1 extensionBinding;
        try
        {
            extensionBinding = SemanticBindingExtensionSetV1.Create(exactObligations.Digest.Value,
                exactGuarantees.Digest.Value, requirements, extensionGuarantees,
                GenerationVector(exactObligations, providerGeneration));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<V6MemorySemanticBindingV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        return KernelResult<V6MemorySemanticBindingV1>.Ok(new(
            V6MemorySemanticBindingV1.CurrentVersion, exactBase.Digest, requirements, extensionGuarantees,
            extensionBinding, memory.Value.Required, memory.Value.Provided));
    }

    internal KernelResult RevalidateV6MemorySemanticBinding(
        V6MemorySemanticBindingV1 binding,
        SemanticExecutionBindingV1 baseBinding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        ulong currentProviderGeneration)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Version != V6MemorySemanticBindingV1.CurrentVersion ||
            binding.BaseBindingDigest != baseBinding.Digest || currentProviderGeneration == 0)
            return KernelResult.Fail(KernelError.StaleGeneration, "v6 memory binding identity is stale.");
        var live = RevalidateOperationObligationsV1(obligations);
        if (!live.IsSuccess) return live;
        return RevalidateMemorySidecar(binding, baseBinding, obligations, guarantees, currentProviderGeneration);
    }

    private KernelResult RevalidateMemorySidecar(
        V6MemorySemanticBindingV1 binding,
        SemanticExecutionBindingV1 baseBinding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        ulong currentProviderGeneration)
    {
        if (binding.Version != V6MemorySemanticBindingV1.CurrentVersion ||
            binding.BaseBindingDigest != baseBinding.Digest || currentProviderGeneration == 0)
            return KernelResult.Fail(KernelError.StaleGeneration, "v6 memory binding identity is stale.");
        var rebuilt = CreateV6MemorySemanticBinding(baseBinding, obligations, guarantees,
            binding.Requirements, binding.Guarantees, currentProviderGeneration);
        if (!rebuilt.IsSuccess)
            return KernelResult.Fail(rebuilt.Error, rebuilt.Message!);
        var current = rebuilt.Value!;
        if (current.ExtensionBinding.Digest != binding.ExtensionBinding.Digest ||
            current.RequiredMemory != binding.RequiredMemory || current.ProvidedMemory != binding.ProvidedMemory)
            return KernelResult.Fail(KernelError.StaleGeneration,
                "v6 memory extensions or live owner generation vector changed before submit.");
        return KernelResult.Ok();
    }

    internal KernelResult<OperationBinding> SubmitV6MemorySemanticResourceExternalAdmission(
        ResourceAdmissionCommit commit,
        OperationDependencySnapshot dependencies,
        SemanticExecutionBindingV1 baseBinding,
        V6MemorySemanticBindingV1 memoryBinding,
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
        return SubmitV6MemorySemanticResourceExternalAdmissionWithBinding(commit, dependencies,
            baseBinding, memoryBinding, obligations, guarantees, refinement, currentProviderIdentity,
            currentProviderGeneration, currentProviderGenerations, providerAdmission, runtimeLegality,
            _ => providerSubmit(), traceSink);
    }

    internal KernelResult<OperationBinding> SubmitV6MemorySemanticResourceExternalAdmissionWithBinding(
        ResourceAdmissionCommit commit,
        OperationDependencySnapshot dependencies,
        SemanticExecutionBindingV1 baseBinding,
        V6MemorySemanticBindingV1 memoryBinding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        SemanticRefinementProofV1 refinement,
        string currentProviderIdentity,
        Func<ulong> currentProviderGeneration,
        Hc.ExternalGenerationSet currentProviderGenerations,
        ISemanticProviderAdmissionService providerAdmission,
        IRuntimeLegalityService runtimeLegality,
        Func<OperationBinding, KernelResult> providerSubmit,
        ISemanticTraceSinkV1? traceSink = null)
    {
        ArgumentNullException.ThrowIfNull(currentProviderGeneration);
        ArgumentNullException.ThrowIfNull(providerSubmit);
        var submitted = SubmitSemanticResourceExternalAdmissionWithBinding(commit, dependencies, baseBinding, obligations, guarantees,
            refinement, currentProviderIdentity, currentProviderGenerations, providerAdmission, runtimeLegality,
            operationBinding =>
            {
                var fresh = RevalidateMemorySidecar(memoryBinding, baseBinding, obligations,
                    guarantees, currentProviderGeneration());
                if (!fresh.IsSuccess) return fresh;
                var owners = RevalidateResourceDispatch(commit, operationBinding);
                return owners.IsSuccess ? providerSubmit(operationBinding) : owners;
            }, () =>
            {
                var revalidated = RevalidateV6MemorySemanticBinding(memoryBinding, baseBinding, obligations,
                    guarantees, currentProviderGeneration());
                return revalidated;
            }, () =>
            {
                if (traceSink is not null)
                    RegisterExternalOperationTrace(commit.Operation, memoryBinding.ExtensionBinding.Digest.Value, traceSink);
            }, () => ObserveFailedPreSubmitTrace(commit, baseBinding, obligations,
                memoryBinding.ExtensionBinding, traceSink, generation =>
                    RevalidateMemorySidecar(memoryBinding, baseBinding, obligations, guarantees, generation)));
        if (traceSink is not null)
        {
            EmitExternalOperationTrace(commit.Operation);
            if (!submitted.IsSuccess)
                DiscardExternalOperationTraceIfPreSubmit(commit.Operation);
        }
        return submitted;
    }

    private static KernelResult<(MemorySemanticsV1 Required, MemorySemanticsV1 Provided)> ValidateMemoryContour(
        OperationObligationsV1 obligations,
        OperationSemanticExtensionsV1 requirements,
        ExecutionGuaranteeExtensionsV1 guarantees)
    {
        var requiredClause = requirements.Clauses.SingleOrDefault(clause =>
            clause.ClassId == MemorySemanticsV1.ExtensionClassId);
        var providedClause = guarantees.Clauses.SingleOrDefault(clause =>
            clause.ClassId == MemorySemanticsV1.ExtensionClassId);
        if (requiredClause is null || providedClause is null ||
            requiredClause.Requirement != SemanticExtensionRequirement.Mandatory)
            return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Fail(KernelError.PlatformDenied,
                "The safe v6 memory contour requires one mandatory memory clause and one guarantee.");

        if (requiredClause.SchemaId != "singnext.memory-semantics/1" ||
            providedClause.SchemaId != "singnext.memory-semantics/1" ||
            requiredClause.SchemaVersion != MemorySemanticsV1.CurrentVersion ||
            providedClause.SchemaVersion != MemorySemanticsV1.CurrentVersion)
            return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Fail(KernelError.PlatformDenied,
                "The memory contour does not support this exact extension schema tuple.");

        MemorySemanticsV1 required;
        MemorySemanticsV1 provided;
        try
        {
            required = MemorySemanticsV1.ParseCanonical(requiredClause.Payload.Span);
            provided = MemorySemanticsV1.ParseCanonical(providedClause.Payload.Span);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException)
        {
            return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Fail(KernelError.InvalidMessage,
                exception.Message);
        }
        var supported = new HashSet<SemanticExtensionClassId> { MemorySemanticsV1.ExtensionClassId };
        var decision = SemanticExtensionRefinementV1.Evaluate(requirements, guarantees, supported,
            (providedExtension, requiredExtension) =>
            {
                try
                {
                    return MemorySemanticPartialOrderV1.Refines(
                        MemorySemanticsV1.ParseCanonical(providedExtension.Payload.Span),
                        MemorySemanticsV1.ParseCanonical(requiredExtension.Payload.Span));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException)
                {
                    return false;
                }
            }, optional => optional.Requirement == SemanticExtensionRequirement.Optional);
        if (!decision.IsAccepted)
            return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Fail(KernelError.PlatformDenied,
                $"Memory extension refinement denied: {decision.Status}/{decision.ClassId}.");

        var expected = SafeStagedMemoryContour();
        if (required != expected || obligations.VisibilityRequirement != ExternalVisibilityRequirement.PublicationFence ||
            obligations.PublicationPolicy != ExternalPublicationPolicy.Staged ||
            obligations.EffectPolicy.EffectClass != ExternalEffectClass.StagedReversibleUntilPublish)
            return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Fail(KernelError.PlatformDenied,
                "Only read-only input plus exclusive staged output is admitted by the first v6 memory contour.");
        var inputs = obligations.RegionUses.Where(region => region.Mode == RegionUseMode.ReadOnly).ToArray();
        var outputs = obligations.RegionUses.Where(region => region.Mode == RegionUseMode.StagedOutput).ToArray();
        if (inputs.Length == 0 || outputs.Length == 0 || inputs.Length + outputs.Length != obligations.RegionUses.Count)
            return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Fail(KernelError.InvalidRegionState,
                "The first v6 memory contour requires at least one read-only input and one staged output.");
        for (var left = 0; left < obligations.RegionUses.Count; left++)
            for (var right = left + 1; right < obligations.RegionUses.Count; right++)
                if ((obligations.RegionUses[left].Mode == RegionUseMode.StagedOutput ||
                     obligations.RegionUses[right].Mode == RegionUseMode.StagedOutput) &&
                    obligations.RegionUses[left].Region.RegionId == obligations.RegionUses[right].Region.RegionId &&
                    Overlaps(obligations.RegionUses[left].Range, obligations.RegionUses[right].Range))
                    return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Fail(KernelError.InvalidRegionState,
                        "Staged output aliases another operation Region use.");
        return KernelResult<(MemorySemanticsV1, MemorySemanticsV1)>.Ok((required, provided));
    }

    private static MemorySemanticsV1 SafeStagedMemoryContour() => new MemorySemanticsV1(1,
        MemoryOwnershipClassV1.SharedReadOnlyInputAndExclusiveOutput,
        MemoryAccessClassV1.ReadOnlyInputAndExclusiveStagedOutput,
        MemoryOrderClassV1.CompletionBeforeVisibilityFence,
        MemoryAtomicityClassV1.None,
        MemoryCoherenceAssumptionV1.ExplicitFenceOnly,
        MemoryVisibilityClassV1.ConsumerVisibleAfterFence,
        MemoryPublicationModeV1.StagedWithheldUntilVisible).Validate();

    private static IReadOnlyList<SemanticGenerationSnapshotV1> GenerationVector(
        OperationObligationsV1 obligations, ulong providerGeneration)
    {
        var values = new List<SemanticGenerationSnapshotV1>
        {
            new("process", obligations.Principal.Generation),
            new("external-operation", obligations.Operation.Generation.Value),
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

    private static bool Overlaps(RegionUseRange left, RegionUseRange right)
    {
        var leftEnd = checked(left.Offset + left.Length);
        var rightEnd = checked(right.Offset + right.Length);
        return left.Offset < rightEnd && right.Offset < leftEnd;
    }
}
