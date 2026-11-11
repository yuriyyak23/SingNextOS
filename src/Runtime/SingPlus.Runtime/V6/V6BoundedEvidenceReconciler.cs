using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum V6EvidenceReconciliationStatus
{
    ExactEvidenceObserved = 1,
    QuarantinePendingExactEvidence,
    EvidenceRejected,
    UnsupportedPolicy,
}

/// <summary>
/// Non-authoritative result of a bounded evidence-query campaign. Exhaustion is
/// quarantine, never proof of closure or permission to release/reclaim resources.
/// </summary>
internal sealed record V6EvidenceReconciliationReceipt(
    ReconciliationPolicyDecisionV1 PolicyDecision,
    V6EvidenceReconciliationStatus Status,
    uint Attempts,
    ProviderHealthEvidenceV1? ExactEvidence,
    KernelError LastQueryError)
{
    internal bool ProvesClosure => false;
    internal bool AuthorizesRelease => false;
    internal bool AuthorizesReclaim => false;
    internal bool AuthorizesExecution => false;
}

/// <summary>
/// Executes only the transport/query portion of reconciliation. It owns no
/// Region, operation, device, or capability state and invokes provider code
/// without holding an authority lock.
/// </summary>
internal static class V6BoundedEvidenceReconciler
{
    internal static KernelResult<V6EvidenceReconciliationReceipt> Execute(
        ReconciliationPolicyV1 policy,
        FailureDomainScopeV1 expectedScope,
        ulong minimumObservationSequence,
        Func<uint, KernelResult<ProviderHealthEvidenceV1>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        try { expectedScope.Validate(); }
        catch (ArgumentException exception)
        {
            return KernelResult<V6EvidenceReconciliationReceipt>.Fail(
                KernelError.InvalidMessage, exception.Message);
        }
        if (minimumObservationSequence == 0)
            return KernelResult<V6EvidenceReconciliationReceipt>.Fail(
                KernelError.InvalidMessage, "Reconciliation requires a non-zero evidence high-watermark.");

        ReconciliationPolicyDecisionV1 decision = ReconciliationPolicyEvaluatorV1.Evaluate(policy);
        if (decision.Disposition == ReconciliationDispositionV1.UnsupportedTrustContour)
            return Ok(decision, V6EvidenceReconciliationStatus.UnsupportedPolicy, 0, null,
                KernelError.PlatformUnsupported);
        if (decision.Disposition == ReconciliationDispositionV1.RejectEvidenceAndQuarantine)
            return Ok(decision, V6EvidenceReconciliationStatus.EvidenceRejected, 0, null,
                KernelError.PlatformDenied);
        if (decision.Disposition == ReconciliationDispositionV1.QuarantinePendingExactEvidence)
            return Ok(decision, V6EvidenceReconciliationStatus.QuarantinePendingExactEvidence, 0, null,
                KernelError.Quarantined);

        KernelError lastError = KernelError.PlatformUnavailable;
        for (uint attempt = 1; attempt <= decision.MaximumEvidenceQueryAttempts; attempt++)
        {
            KernelResult<ProviderHealthEvidenceV1> observed;
            try { observed = query(attempt); }
            catch (Exception)
            {
                lastError = KernelError.PlatformUnavailable;
                continue;
            }
            if (!observed.IsSuccess)
            {
                lastError = observed.Error == KernelError.None
                    ? KernelError.PlatformUnavailable
                    : observed.Error;
                continue;
            }

            ProviderHealthEvidenceV1 evidence = observed.Value;
            try { evidence.Validate(); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Ok(decision, V6EvidenceReconciliationStatus.EvidenceRejected,
                    attempt, null, KernelError.InvalidMessage);
            }
            if (evidence.Scope != expectedScope || evidence.ObservationSequence < minimumObservationSequence)
                return Ok(decision, V6EvidenceReconciliationStatus.EvidenceRejected,
                    attempt, null, KernelError.StaleGeneration);
            return Ok(decision, V6EvidenceReconciliationStatus.ExactEvidenceObserved,
                attempt, evidence, KernelError.None);
        }

        return Ok(decision, V6EvidenceReconciliationStatus.QuarantinePendingExactEvidence,
            decision.MaximumEvidenceQueryAttempts, null, lastError);
    }

    private static KernelResult<V6EvidenceReconciliationReceipt> Ok(
        ReconciliationPolicyDecisionV1 decision,
        V6EvidenceReconciliationStatus status,
        uint attempts,
        ProviderHealthEvidenceV1? evidence,
        KernelError error) => KernelResult<V6EvidenceReconciliationReceipt>.Ok(
            new(decision, status, attempts, evidence, error));
}
