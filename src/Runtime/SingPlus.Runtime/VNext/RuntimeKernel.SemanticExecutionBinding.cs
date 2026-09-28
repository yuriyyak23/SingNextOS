using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult<SemanticExecutionBindingV1> CreateSemanticExecutionBindingV1(
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        ProcessHandle budgetOwner,
        BudgetReservationHandle lease,
        ResourceEnvelopeV1 envelope,
        string providerIdentity,
        Hc.ExternalRequestCorrelation providerRequestCorrelation,
        Hc.ExternalGenerationSet providerGenerations,
        string measurementContractIdentity = "unsupported")
    {
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(guarantees);
        ArgumentNullException.ThrowIfNull(providerGenerations);
        var live = RevalidateOperationObligationsV1(obligations);
        if (!live.IsSuccess) return KernelResult<SemanticExecutionBindingV1>.Fail(live.Error, live.Message!);

        OperationObligationsV1 exactObligations;
        ExecutionGuaranteesV1 exactGuarantees;
        ResourceEnvelopeV1 exactEnvelope;
        string generationDigest;
        try
        {
            exactObligations = obligations.Canonicalize();
            exactGuarantees = guarantees.Canonicalize();
            exactEnvelope = envelope.Canonicalize();
            generationDigest = HybridCpu114SemanticCompatibility.GenerationDigest(providerGenerations);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<SemanticExecutionBindingV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }

        if (exactGuarantees.ProviderContract != new ProviderContractIdentityV1(
                HybridCpu114SemanticCompatibility.PackageId,
                HybridCpu114SemanticCompatibility.PackageVersion,
                HybridCpu114SemanticCompatibility.SchemaIdentity))
            return KernelResult<SemanticExecutionBindingV1>.Fail(KernelError.InvalidMessage,
                "Guarantees do not belong to the exact qualified HybridCPU contract tuple.");
        if (!exactObligations.ResourceRequirements.Contains(exactEnvelope))
            return KernelResult<SemanticExecutionBindingV1>.Fail(KernelError.InvalidMessage,
                "Bound resource envelope is absent from exact obligations.");
        var budgetAmounts = ResourceEnvelopeBudgetMapping.Map([exactEnvelope]);
        if (!budgetAmounts.IsSuccess)
            return KernelResult<SemanticExecutionBindingV1>.Fail(budgetAmounts.Error, budgetAmounts.Message!);
        var reservation = Budgets.Query(lease);
        if (!reservation.IsSuccess) return KernelResult<SemanticExecutionBindingV1>.Fail(reservation.Error, reservation.Message!);
        if (reservation.Value!.Owner != budgetOwner ||
            reservation.Value.State is not (BudgetReservationState.Reserved or BudgetReservationState.Bound) ||
            !reservation.Value.Amounts.SequenceEqual(budgetAmounts.Value!))
            return KernelResult<SemanticExecutionBindingV1>.Fail(KernelError.StaleGeneration,
                "Budget lease is not the exact live lease for the bound envelope.");

        try
        {
            return KernelResult<SemanticExecutionBindingV1>.Ok(new SemanticExecutionBindingV1(
                SemanticExecutionBindingV1.CurrentVersion,
                exactObligations.Operation,
                exactObligations.Digest,
                exactGuarantees.Digest,
                budgetOwner,
                lease,
                exactEnvelope,
                providerIdentity,
                providerRequestCorrelation,
                generationDigest,
                providerGenerations,
                exactGuarantees.ProviderContract,
                measurementContractIdentity,
                exactObligations.VisibilityRequirement,
                exactObligations.PublicationPolicy,
                default).Canonicalize());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<SemanticExecutionBindingV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
    }

    internal KernelResult RevalidateSemanticExecutionBindingV1(
        SemanticExecutionBindingV1 binding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        string currentProviderIdentity,
        Hc.ExternalGenerationSet currentProviderGenerations)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(currentProviderGenerations);
        SemanticExecutionBindingV1 exact;
        try { exact = binding.Canonicalize(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return KernelResult.Fail(KernelError.InvalidMessage, exception.Message); }

        var obligationsLive = RevalidateOperationObligationsV1(obligations);
        if (!obligationsLive.IsSuccess) return obligationsLive;
        OperationObligationsV1 exactObligations;
        ExecutionGuaranteesV1 exactGuarantees;
        try
        {
            exactObligations = obligations.Canonicalize();
            exactGuarantees = guarantees.Canonicalize();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return KernelResult.Fail(KernelError.InvalidMessage, exception.Message); }
        if (exact.Operation != exactObligations.Operation || exact.ObligationsDigest != exactObligations.Digest ||
            exact.GuaranteesDigest != exactGuarantees.Digest ||
            !string.Equals(exact.ProviderIdentity, currentProviderIdentity, StringComparison.Ordinal) ||
            !exact.ProviderGenerations.Equals(currentProviderGenerations) ||
            exact.ProviderGenerationDigest != HybridCpu114SemanticCompatibility.GenerationDigest(currentProviderGenerations))
            return KernelResult.Fail(KernelError.StaleGeneration,
                "Semantic execution binding no longer matches exact obligations, guarantees, operation, or provider generation.");
        var lease = Budgets.Query(exact.Lease);
        if (!lease.IsSuccess || lease.Value!.Owner != exact.BudgetOwner ||
            lease.Value.State is not (BudgetReservationState.Reserved or BudgetReservationState.Bound))
            return KernelResult.Fail(KernelError.StaleGeneration, "Semantic execution binding lease is no longer live and exact.");
        return KernelResult.Ok();
    }
}
