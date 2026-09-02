namespace SingPlus.Platform;

public readonly record struct PlatformRegionVisibilityRequest(
    PlatformProviderRegionMappingLease Mapping,
    PlatformRegionSlice Slice,
    PlatformMemoryConsumerClass Consumer,
    PlatformMemoryVisibilityRequirement Requirement);

public readonly record struct PlatformRegionVisibilityResult(
    PlatformProviderRegionMappingId MappingId,
    PlatformProviderLeaseGeneration MappingGeneration,
    PlatformRegionSlice Slice,
    PlatformMemoryConsumerClass Consumer,
    PlatformMemoryVisibilityRequirement Requirement,
    PlatformMemoryVisibilityOutcome Outcome)
{
    public bool IsSatisfied =>
        PlatformMemoryVisibilityContract.IsSatisfied(Requirement, Outcome);
}

public static class PlatformRegionVisibilityContract
{
    public const uint ContractVersion = 2;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformRegionVisibilityRequest request)
    {
        var sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(request.Slice);
        if (!sliceValidation.IsSuccess) return sliceValidation;

        if (request.Mapping.MappingId.Value == 0 ||
            request.Mapping.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-visibility requests require a materialized provider mapping identity.");
        }

        if (request.Mapping.Region != request.Slice.Region ||
            request.Mapping.Access != request.Slice.Access)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-visibility requests must bind the exact provider mapping to the exact region slice.");
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

    public static PlatformAuthorityResult ValidateResult(
        PlatformRegionVisibilityRequest request,
        PlatformRegionVisibilityResult result)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (result.MappingId != request.Mapping.MappingId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Region-visibility evidence belongs to a different provider mapping.");
        }

        if (result.MappingGeneration != request.Mapping.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "Region-visibility evidence uses a stale provider mapping generation.");
        }

        if (result.Slice != request.Slice ||
            result.Consumer != request.Consumer ||
            result.Requirement != request.Requirement)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-visibility evidence does not match the exact request.");
        }

        if (!Enum.IsDefined(result.Outcome))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Region-visibility evidence contains an undefined outcome.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformRegionVisibilityProvider
{
    PlatformAuthorityResult<PlatformRegionVisibilityResult> PrepareRegionMappingForConsumer(
        PlatformRegionVisibilityRequest request);
}
