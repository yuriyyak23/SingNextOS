namespace SingPlus.Platform;

/// <summary>
/// Aggregate provider-accounted execution time requested for one replenishment period.
/// This is policy intent, not a deadline, WCET statement, reservation, or real-time proof.
/// </summary>
public readonly record struct ExecutionBudget(
    TimeSpan MaximumExecutionTime,
    TimeSpan ReplenishmentPeriod);

/// <summary>
/// Semantic OS priority class. Numeric enum values are not scheduler weights.
/// </summary>
public enum PriorityClass : byte
{
    Normal = 0,
    Background,
    Interactive,
}

public enum LatencyHint : byte
{
    Balanced = 0,
    PreferLowLatency,
}

public enum ThroughputHint : byte
{
    Balanced = 0,
    PreferThroughput,
}

/// <summary>
/// SingNextOS-owned execution-policy intent. Placement and enforcement remain provider-owned.
/// </summary>
public readonly record struct PlatformExecutionPolicy(
    ExecutionBudget Budget,
    PriorityClass Priority,
    LatencyHint Latency,
    ThroughputHint Throughput);

/// <summary>
/// Exact provider response for an execution-policy request. The provider lease remains behind
/// the Platform Authority Bridge and is never an application or SIP authority token.
/// </summary>
public readonly record struct PlatformExecutionPolicyResult(
    PlatformProviderDomainLease DomainLease,
    PlatformExecutionPolicy Policy);

public static class PlatformExecutionPolicyContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidatePolicy(PlatformExecutionPolicy policy)
    {
        if (policy.Budget.MaximumExecutionTime <= TimeSpan.Zero ||
            policy.Budget.ReplenishmentPeriod <= TimeSpan.Zero)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Execution budget time and replenishment period must both be positive.");
        }

        if (!Enum.IsDefined(policy.Priority) ||
            !Enum.IsDefined(policy.Latency) ||
            !Enum.IsDefined(policy.Throughput))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Execution policy priority and performance hints must be defined values.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static bool IsSupportedAvailability(PlatformFeatureAvailability availability) =>
        availability is PlatformFeatureAvailability.ModelOnly or
            PlatformFeatureAvailability.RuntimeAdmission or
            PlatformFeatureAvailability.Executable;

    public static PlatformAuthorityResult ValidateResult(
        PlatformProviderDomainLease expectedLease,
        PlatformExecutionPolicy expectedPolicy,
        PlatformExecutionPolicyResult result)
    {
        var policyValidation = ValidatePolicy(expectedPolicy);
        if (!policyValidation.IsSuccess) return policyValidation;

        if (result.DomainLease.LeaseId != expectedLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The execution-policy result belongs to a different provider domain lease.");
        }

        if (result.DomainLease.Generation != expectedLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The execution-policy result carries a stale provider domain generation.");
        }

        if (result.DomainLease.Subject != expectedLease.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The execution-policy result belongs to a different local process subject.");
        }

        if (result.Policy != expectedPolicy)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The platform provider acknowledged a different execution policy.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformExecutionPolicyProvider
{
    PlatformAuthorityResult<PlatformExecutionPolicyResult> ConfigureExecutionPolicy(
        PlatformProviderDomainLease domainLease,
        PlatformExecutionPolicy policy);
}
