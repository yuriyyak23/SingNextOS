namespace SingPlus.Platform;

public enum PlatformProviderDmaCompletionState
{
    Pending = 0,
    Completed,
    Faulted,
}

public readonly record struct PlatformProviderDmaCompletionEvidence(
    PlatformProviderDmaSubmissionId SubmissionId,
    PlatformProviderDmaSubmissionGeneration SubmissionGeneration,
    PlatformProviderDmaGrantId GrantId,
    PlatformProviderLeaseGeneration GrantGeneration,
    PlatformProviderDmaVisibilityCycle PreparedCycle,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction,
    PlatformProviderDmaCompletionState State);

/// <summary>
/// Version 4 adds exact completion observation for the exact pending DMA submission and
/// exact prepared visibility cycle. Completion proof is evidence only: it does not perform
/// post-write CPU acquire/maintenance and does not authorize grant closure or memory reclaim.
/// </summary>
public static class PlatformDmaCompletionContract
{
    public const uint ContractVersion = 4;

    public static PlatformAuthorityResult ValidateEvidence(
        PlatformProviderDmaSubmission expectedSubmission,
        PlatformProviderDmaCompletionEvidence evidence)
    {
        if (expectedSubmission.SubmissionId.Value == 0 ||
            expectedSubmission.Generation.Value == 0 ||
            expectedSubmission.GrantId.Value == 0 ||
            expectedSubmission.GrantGeneration.Value == 0 ||
            expectedSubmission.PreparedCycle.Value == 0 ||
            evidence.SubmissionId.Value == 0 ||
            evidence.SubmissionGeneration.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA completion evidence requires materialized submission, grant, and visibility-cycle identities.");
        }

        if (!Enum.IsDefined(evidence.State))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA completion evidence contains an undefined state.");
        }

        if (evidence.SubmissionId != expectedSubmission.SubmissionId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA completion evidence belongs to a different provider submission.");
        }

        if (evidence.SubmissionGeneration != expectedSubmission.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "DMA completion evidence uses a stale provider submission generation.");
        }

        if (evidence.GrantId != expectedSubmission.GrantId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA completion evidence belongs to a different provider grant.");
        }

        if (evidence.GrantGeneration != expectedSubmission.GrantGeneration)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "DMA completion evidence uses a stale provider grant generation.");
        }

        if (evidence.PreparedCycle != expectedSubmission.PreparedCycle)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA completion evidence belongs to a different prepared visibility cycle.");
        }

        if (evidence.Range != expectedSubmission.Range ||
            evidence.Direction != expectedSubmission.Direction)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA completion evidence does not match the exact bounded range and direction of the submitted operation.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDmaCompletionProvider
{
    PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> ObserveDmaCompletion(
        PlatformProviderDmaSubmission submission);
}
