using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal sealed record V6RestartBindingV1(
    ExternalOperationHandle Predecessor,
    ExternalOperationHandle Replacement,
    OperationDependencySnapshot ReplacementDependencies,
    PreemptionGuaranteeV1 Guarantee,
    ulong ProviderGeneration,
    ulong RuntimeGeneration,
    string PredecessorClosureDigest,
    BudgetReservationHandle? ReplacementBudget)
{
    internal bool AuthorizesExecution => false;
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<V6RestartBindingV1> CreateV6RestartBinding(
        ProcessHandle principal,
        ExternalOperationHandle predecessor,
        ExternalOperationHandle replacement,
        OperationDependencySnapshot replacementDependencies,
        PreemptionGuaranteeV1 guarantee,
        ulong providerGeneration,
        ulong runtimeGeneration,
        BudgetReservationHandle? replacementBudget = null)
    {
        try { guarantee.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6RestartBindingV1>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (guarantee.Class != PreemptionClassV1.RestartOnly ||
            guarantee.EffectSemantics != PreemptionEffectSemanticsV1.RequestOnly)
            return KernelResult<V6RestartBindingV1>.Fail(KernelError.PlatformUnsupported,
                "The first executable contour supports restart-only request semantics.");
        if (providerGeneration == 0 || runtimeGeneration == 0)
            return KernelResult<V6RestartBindingV1>.Fail(KernelError.InvalidMessage,
                "Restart dependency generations must be non-zero.");
        var process = Processes.Resolve(principal);
        if (!process.IsSuccess) return KernelResult<V6RestartBindingV1>.Fail(process.Error, process.Message!);
        var prior = QueryExternalOperation(principal, predecessor);
        if (!prior.IsSuccess) return KernelResult<V6RestartBindingV1>.Fail(prior.Error, prior.Message!);
        if (!ClosedUnpublishedStaged(prior.Value!))
            return KernelResult<V6RestartBindingV1>.Fail(KernelError.ExternalEffectUncontained,
                "Restart requires a released staged predecessor with explicit containment and no publication.");
        var next = QueryExternalOperation(principal, replacement);
        if (!next.IsSuccess) return KernelResult<V6RestartBindingV1>.Fail(next.Error, next.Message!);
        if (next.Value!.State != ExternalOperationState.Admitted ||
            next.Value.Admission?.Dependencies != replacementDependencies)
            return KernelResult<V6RestartBindingV1>.Fail(KernelError.InvalidTransition,
                "Replacement must have a fresh exact admitted dependency tuple.");
        if (replacementBudget is { } budget)
        {
            var budgetSnapshot = Budgets.Query(budget);
            if (!budgetSnapshot.IsSuccess || budgetSnapshot.Value!.Owner != principal ||
                budgetSnapshot.Value.Lifetime != BudgetReservationLifetime.ExternalEffect ||
                budgetSnapshot.Value.State != BudgetReservationState.Bound)
                return KernelResult<V6RestartBindingV1>.Fail(KernelError.InvalidTransition,
                    "Restart budget must be an exact bound external-effect reservation owned by the replacement process.");
        }
        return KernelResult<V6RestartBindingV1>.Ok(new(predecessor, replacement,
            replacementDependencies, guarantee, providerGeneration, runtimeGeneration,
            ClosureDigest(prior.Value!), replacementBudget));
    }

    internal KernelResult<RestartAdmissionReceiptV1> SubmitV6Restart(
        ProcessHandle principal,
        V6RestartBindingV1 binding,
        Func<KernelResult> singNextAdmission,
        Func<KernelResult> providerAdmission,
        Func<KernelResult> runtimeLegality,
        Func<ulong> currentProviderGeneration,
        Func<ulong> currentRuntimeGeneration,
        Func<KernelResult> providerSubmit)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(singNextAdmission);
        ArgumentNullException.ThrowIfNull(providerAdmission);
        ArgumentNullException.ThrowIfNull(runtimeLegality);
        ArgumentNullException.ThrowIfNull(currentProviderGeneration);
        ArgumentNullException.ThrowIfNull(currentRuntimeGeneration);
        ArgumentNullException.ThrowIfNull(providerSubmit);
        foreach (var gate in new[] { singNextAdmission, providerAdmission, runtimeLegality })
        {
            KernelResult decision;
            try { decision = gate(); }
            catch (Exception exception) { return KernelResult<RestartAdmissionReceiptV1>.Fail(KernelError.PlatformDenied, exception.Message); }
            if (!decision.IsSuccess)
                return KernelResult<RestartAdmissionReceiptV1>.Fail(decision.Error, decision.Message!);
        }
        var prior = QueryExternalOperation(principal, binding.Predecessor);
        var next = QueryExternalOperation(principal, binding.Replacement);
        ulong providerGeneration;
        ulong runtimeGeneration;
        try
        {
            providerGeneration = currentProviderGeneration();
            runtimeGeneration = currentRuntimeGeneration();
        }
        catch (Exception exception)
        {
            return KernelResult<RestartAdmissionReceiptV1>.Fail(
                KernelError.PlatformUnavailable, exception.Message);
        }
        if (!prior.IsSuccess || !next.IsSuccess || !ClosedUnpublishedStaged(prior.Value!) ||
            ClosureDigest(prior.Value!) != binding.PredecessorClosureDigest ||
            next.Value!.State != ExternalOperationState.Admitted ||
            next.Value.Admission?.Dependencies != binding.ReplacementDependencies ||
            providerGeneration != binding.ProviderGeneration ||
            runtimeGeneration != binding.RuntimeGeneration)
            return KernelResult<RestartAdmissionReceiptV1>.Fail(KernelError.StaleGeneration,
                "Restart binding changed before the replacement submit linearization point.");
        if (binding.ReplacementBudget is { } replacementBudget)
        {
            var budget = Budgets.Query(replacementBudget);
            if (!budget.IsSuccess || budget.Value!.Owner != principal ||
                budget.Value.Lifetime != BudgetReservationLifetime.ExternalEffect ||
                budget.Value.State != BudgetReservationState.Bound)
                return KernelResult<RestartAdmissionReceiptV1>.Fail(KernelError.StaleGeneration,
                    "Restart budget changed before the replacement submit linearization point.");
        }
        var submitted = RecordExternalOperationSubmission(principal, binding.Replacement,
            binding.ReplacementDependencies);
        if (!submitted.IsSuccess)
            return KernelResult<RestartAdmissionReceiptV1>.Fail(submitted.Error, submitted.Message!);
        if (binding.ReplacementBudget is { } consumingBudget)
        {
            var consuming = Budgets.BeginConsumption(principal, consumingBudget);
            if (!consuming.IsSuccess)
            {
                _ = RecordExternalOperationProviderLoss(principal, binding.Replacement);
                return KernelResult<RestartAdmissionReceiptV1>.Fail(consuming.Error, consuming.Message!);
            }
        }
        KernelResult providerResult;
        try { providerResult = providerSubmit(); }
        catch (Exception exception) { providerResult = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
        if (!providerResult.IsSuccess)
        {
            _ = RecordExternalOperationProviderLoss(principal, binding.Replacement);
            if (binding.ReplacementBudget is { } failedBudget)
            {
                var quarantined = Budgets.QuarantineLease(principal, failedBudget);
                if (!quarantined.IsSuccess)
                    return KernelResult<RestartAdmissionReceiptV1>.Fail(
                        KernelError.PlatformFaulted,
                        $"Provider loss was recorded but restart budget quarantine failed: {quarantined.Message}");
            }
            return KernelResult<RestartAdmissionReceiptV1>.Fail(providerResult.Error, providerResult.Message!);
        }
        var evidence = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{binding.PredecessorClosureDigest}|{binding.ProviderGeneration}|{binding.RuntimeGeneration}|" +
            $"{binding.ReplacementBudget?.ReservationId.Value ?? 0}|{binding.ReplacementBudget?.Generation.Value ?? 0}")));
        PreemptionLifecycleEventV1[] lifecycle =
        [new(1, 1, PreemptionLifecycleEventKindV1.Requested, evidence),
         new(1, 2, PreemptionLifecycleEventKindV1.Draining, evidence),
         new(1, 3, PreemptionLifecycleEventKindV1.Contained, evidence),
         new(1, 4, PreemptionLifecycleEventKindV1.Restarted, evidence)];
        return KernelResult<RestartAdmissionReceiptV1>.Ok(new(1, binding.Predecessor,
            binding.Replacement, binding.Guarantee, binding.ProviderGeneration,
            binding.RuntimeGeneration, Array.AsReadOnly(lifecycle), binding.ReplacementBudget,
            binding.ReplacementBudget is null ? null : BudgetReservationState.Consuming));
    }

    private static bool ClosedUnpublishedStaged(ExternalOperationSnapshot snapshot) =>
        snapshot.State == ExternalOperationState.Released &&
        snapshot.PublicationPolicy == ExternalPublicationPolicy.Staged &&
        snapshot.Transitions.All(static transition => transition.Event != "Published") &&
        snapshot.Transitions.Any(static transition => transition.Event == "ProviderLost") &&
        snapshot.Transitions.Any(static transition => transition.Event == "Released");

    private static string ClosureDigest(ExternalOperationSnapshot snapshot)
    {
        var payload = string.Join('|', snapshot.Transitions.Select(static transition =>
            $"{transition.Sequence}:{(byte)transition.From}:{(byte)transition.To}:{transition.Event}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
