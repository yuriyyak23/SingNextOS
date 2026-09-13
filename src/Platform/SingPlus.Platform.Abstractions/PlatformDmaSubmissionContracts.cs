namespace SingPlus.Platform;

public readonly record struct PlatformProviderDmaSubmissionId(ulong Value);
public readonly record struct PlatformProviderDmaSubmissionGeneration(ulong Value);

public readonly record struct PlatformProviderDmaSubmitRequest(
    PlatformProviderDmaGrant Grant,
    PlatformProviderDmaVisibilityCycle PreparedCycle);

public readonly record struct PlatformProviderDmaSubmission(
    PlatformProviderDmaSubmissionId SubmissionId,
    PlatformProviderDmaSubmissionGeneration Generation,
    PlatformProviderDmaGrantId GrantId,
    PlatformProviderLeaseGeneration GrantGeneration,
    PlatformProviderDmaVisibilityCycle PreparedCycle,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction);

/// <summary>
/// Version 3 adds bounded DMA submission acceptance for the exact provider grant and
/// exact prepared visibility cycle. Submission means only that the bounded operation was
/// accepted into an external pending lifetime. It is not completion evidence and does not
/// authorize CPU reuse, post-write acquisition, grant closure, or mapping reclaim.
/// </summary>
public static class PlatformDmaSubmissionContract
{
    public const uint ContractVersion = 3;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformProviderDmaSubmitRequest request)
    {
        if (request.Grant.GrantId.Value == 0 ||
            request.Grant.Generation.Value == 0 ||
            request.PreparedCycle.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA submission requires materialized provider grant and visibility-cycle identities.");
        }

        if (request.Grant.Range.Offset < 0 || request.Grant.Range.Length <= 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA submission requires the positive bounded range already committed by the exact grant.");
        }

        if (!Enum.IsDefined(request.Grant.Direction))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA submission requires the defined direction already committed by the exact grant.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateSubmission(
        PlatformProviderDmaSubmitRequest request,
        PlatformProviderDmaSubmission submission)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (submission.SubmissionId.Value == 0 || submission.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider DMA submission identity must be materialized.");
        }

        if (submission.GrantId != request.Grant.GrantId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Provider DMA submission belongs to a different grant.");
        }

        if (submission.GrantGeneration != request.Grant.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "Provider DMA submission uses a stale grant generation.");
        }

        if (submission.PreparedCycle != request.PreparedCycle ||
            submission.Range != request.Grant.Range ||
            submission.Direction != request.Grant.Direction)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider DMA submission does not match the exact prepared cycle, bounded range, and direction.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDmaSubmissionProvider
{
    PlatformAuthorityResult<PlatformProviderDmaSubmission> SubmitDma(
        PlatformProviderDmaSubmitRequest request);
}
