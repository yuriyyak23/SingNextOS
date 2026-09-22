namespace SingPlus.Contracts;

public enum ProviderFaultClassV1 : byte
{
    CrashStop = 1,
    Recovery = 2,
    Omission = 3,
    Duplication = 4,
    Reordering = 5,
    Partition = 6,
    CorruptEvidence = 7,
    MaliciousProvider = 8,
}

public enum ProviderTrustClassV1 : byte
{
    LocalTrustedFallible = 1,
    RemoteAuthenticated = 2,
    Attested = 3,
    ByzantineTolerant = 4,
}

public enum ReconciliationDispositionV1 : byte
{
    RetryEvidenceQuery = 1,
    QuarantinePendingExactEvidence = 2,
    RejectEvidenceAndQuarantine = 3,
    UnsupportedTrustContour = 4,
}

/// <summary>
/// Immutable policy input. Attempts limit transport work only; exhaustion never
/// proves provider closure and never authorizes release, refund, reclaim or retry execution.
/// </summary>
public readonly record struct ReconciliationPolicyV1(
    ushort Version,
    ProviderTrustClassV1 TrustClass,
    ProviderFaultClassV1 FaultClass,
    uint MaximumEvidenceQueryAttempts)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesRelease => false;
}

public readonly record struct ReconciliationPolicyDecisionV1(
    ushort Version,
    ReconciliationDispositionV1 Disposition,
    uint MaximumEvidenceQueryAttempts,
    bool RequiresExactGenerationEvidence)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool ProvesClosure => false;
    public bool AuthorizesRelease => false;
}

public static class ReconciliationPolicyEvaluatorV1
{
    private const uint MaximumBoundedAttempts = 64;

    public static ReconciliationPolicyDecisionV1 Evaluate(ReconciliationPolicyV1 policy)
    {
        if (policy.Version != ReconciliationPolicyV1.CurrentVersion ||
            !Enum.IsDefined(policy.TrustClass) || !Enum.IsDefined(policy.FaultClass) ||
            policy.MaximumEvidenceQueryAttempts > MaximumBoundedAttempts)
            return Unsupported();

        if (policy.TrustClass != ProviderTrustClassV1.LocalTrustedFallible)
            return new(1, ReconciliationDispositionV1.UnsupportedTrustContour, 0, true);

        return policy.FaultClass switch
        {
            ProviderFaultClassV1.Omission or ProviderFaultClassV1.Reordering or ProviderFaultClassV1.Recovery
                when policy.MaximumEvidenceQueryAttempts > 0 =>
                new(1, ReconciliationDispositionV1.RetryEvidenceQuery,
                    policy.MaximumEvidenceQueryAttempts, true),
            ProviderFaultClassV1.CorruptEvidence or ProviderFaultClassV1.MaliciousProvider =>
                new(1, ReconciliationDispositionV1.RejectEvidenceAndQuarantine, 0, true),
            _ => new(1, ReconciliationDispositionV1.QuarantinePendingExactEvidence, 0, true),
        };
    }

    private static ReconciliationPolicyDecisionV1 Unsupported() =>
        new(1, ReconciliationDispositionV1.UnsupportedTrustContour, 0, true);
}
