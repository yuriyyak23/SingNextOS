namespace SingPlus.Platform;

public enum PlatformMemoryAcquireRequirement
{
    AcquisitionFence = 0,
}

public enum PlatformMemoryAcquireOutcome
{
    AcquisitionFenceSatisfied = 0,
    Unsupported,
}

public readonly record struct PlatformRegionAcquireRequest(
    PlatformProviderRegionMappingLease Mapping,
    PlatformRegionSlice Slice,
    PlatformMemoryConsumerClass Producer,
    PlatformMemoryAcquireRequirement Requirement);

public readonly record struct PlatformRegionAcquireResult(
    PlatformProviderRegionMappingId MappingId,
    PlatformProviderLeaseGeneration MappingGeneration,
    PlatformRegionSlice Slice,
    PlatformMemoryConsumerClass Producer,
    PlatformMemoryAcquireRequirement Requirement,
    PlatformMemoryAcquireOutcome Outcome)
{
    public bool IsSatisfied =>
        Requirement == PlatformMemoryAcquireRequirement.AcquisitionFence &&
        Outcome == PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied;
}

public static class PlatformRegionAcquireContract
{
    public const uint ContractVersion = 3;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformRegionAcquireRequest request)
    {
        var sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(request.Slice);
        if (!sliceValidation.IsSuccess) return sliceValidation;

        if (request.Mapping.MappingId.Value == 0 ||
            request.Mapping.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-acquire requests require a materialized provider mapping identity.");
        }

        if (request.Mapping.Region != request.Slice.Region ||
            request.Mapping.Access != request.Slice.Access)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-acquire requests must bind the exact provider mapping to the exact region slice.");
        }

        if (!Enum.IsDefined(request.Producer))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The memory producer class is undefined.");
        }

        if (!Enum.IsDefined(request.Requirement))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The memory-acquire requirement is undefined.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateResult(
        PlatformRegionAcquireRequest request,
        PlatformRegionAcquireResult result)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (result.MappingId != request.Mapping.MappingId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Region-acquire evidence belongs to a different provider mapping.");
        }

        if (result.MappingGeneration != request.Mapping.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "Region-acquire evidence uses a stale provider mapping generation.");
        }

        if (result.Slice != request.Slice ||
            result.Producer != request.Producer ||
            result.Requirement != request.Requirement)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-acquire evidence does not match the exact request.");
        }

        if (!Enum.IsDefined(result.Outcome))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-acquire evidence contains an undefined outcome.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformRegionAcquireProvider
{
    PlatformAuthorityResult<PlatformRegionAcquireResult> AcquireRegionMappingFromConsumer(
        PlatformRegionAcquireRequest request);
}
