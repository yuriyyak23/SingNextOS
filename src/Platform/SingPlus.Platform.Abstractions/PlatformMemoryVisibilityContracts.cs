namespace SingPlus.Platform;

public enum PlatformMemoryConsumerClass
{
    CpuExecution = 0,
    ExternalExecutionDomain,
    IoDevice,
    Accelerator
}

public enum PlatformMemoryVisibilityRequirement
{
    CoherentAccess = 0,
    PublicationFence,
    CacheMaintenance
}

public enum PlatformMemoryVisibilityOutcome
{
    Coherent = 0,
    PublicationFenceSatisfied,
    CacheMaintenanceSatisfied,
    Unsupported
}

public readonly record struct PlatformMemoryVisibilityRequest(
    PlatformOperationIdentity Operation,
    PlatformMemoryConsumerClass Consumer,
    PlatformMemoryVisibilityRequirement Requirement);

public readonly record struct PlatformMemoryVisibilityResult(
    PlatformMemoryConsumerClass Consumer,
    PlatformMemoryVisibilityRequirement Requirement,
    PlatformMemoryVisibilityOutcome Outcome)
{
    public bool IsSatisfied =>
        PlatformMemoryVisibilityContract.IsSatisfied(Requirement, Outcome);
}

public static class PlatformMemoryVisibilityContract
{
    public static PlatformAuthorityResult ValidateRequest(
        PlatformMemoryVisibilityRequest request)
    {
        if (request.Operation.OperationId.Value == 0 ||
            request.Operation.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Memory-visibility requests require a non-zero platform operation identity.");
        }

        if (!Enum.IsDefined(request.Consumer))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The memory consumer class is undefined.");
        }

        if (!Enum.IsDefined(request.Requirement))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The memory-visibility requirement is undefined.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static bool IsSatisfied(
        PlatformMemoryVisibilityRequirement requirement,
        PlatformMemoryVisibilityOutcome outcome) =>
        (requirement, outcome) switch
        {
            (PlatformMemoryVisibilityRequirement.CoherentAccess,
                PlatformMemoryVisibilityOutcome.Coherent) => true,
            (PlatformMemoryVisibilityRequirement.PublicationFence,
                PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied) => true,
            (PlatformMemoryVisibilityRequirement.CacheMaintenance,
                PlatformMemoryVisibilityOutcome.CacheMaintenanceSatisfied) => true,
            _ => false
        };
}

public interface IPlatformMemoryVisibilityProvider
{
    PlatformAuthorityResult<PlatformMemoryVisibilityResult> EnsureMemoryVisibility(
        PlatformMemoryVisibilityRequest request);
}
