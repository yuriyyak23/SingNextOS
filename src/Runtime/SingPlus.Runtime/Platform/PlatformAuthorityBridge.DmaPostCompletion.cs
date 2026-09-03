using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDmaPostCompletionVisibilityEvidence(
    PlatformDmaOperationId OperationId,
    PlatformDmaOperationGeneration OperationGeneration,
    PlatformDmaGrantId GrantId,
    PlatformDmaGrantGeneration GrantGeneration,
    PlatformDmaVisibilityCycle PreparedCycle,
    PlatformDmaDirection Direction,
    PlatformDmaPostCompletionVisibilityRequirement Requirement,
    PlatformDmaPostCompletionVisibilityOutcome Outcome)
{
    public bool IsSatisfied =>
        OperationId.Value != 0 &&
        OperationGeneration.Value != 0 &&
        GrantId.Value != 0 &&
        GrantGeneration.Value != 0 &&
        PreparedCycle.Value != 0 &&
        Direction switch
        {
            PlatformDmaDirection.DeviceReadsMemory =>
                Requirement == PlatformDmaPostCompletionVisibilityRequirement.None &&
                Outcome == PlatformDmaPostCompletionVisibilityOutcome.NotRequired,
            PlatformDmaDirection.DeviceWritesMemory or PlatformDmaDirection.Bidirectional =>
                Requirement == PlatformDmaPostCompletionVisibilityRequirement.AcquisitionFence &&
                Outcome == PlatformDmaPostCompletionVisibilityOutcome.AcquisitionFenceSatisfied,
            _ => false,
        };
}

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformDmaPostCompletionVisibilityEvidence> FinalizeDmaPostCompletionVisibility(
        PlatformDmaSubmission submission,
        PlatformDmaCompletionEvidence completionEvidence,
        PlatformDomainIdentity expectedSubject)
    {
        var submissionValidation = ValidateDmaSubmissionIdentity(submission, expectedSubject);
        if (!submissionValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                submissionValidation.Error,
                submissionValidation.Message!);
        }

        if (HasFaultPinnedDmaSubmission(submission.GrantId))
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                "DMA post-completion visibility is fault-pinned and lower authority must remain closed to reclaim.");
        }

        var record = _activeDmaSubmissions[submission.GrantId];
        if (!record.CompletionProven)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformBindingDraining,
                "Exact DMA completion must be proven before post-completion visibility can advance.");
        }

        var completionValidation = ValidateExactDmaCompletionEvidence(submission, completionEvidence);
        if (!completionValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                completionValidation.Error,
                completionValidation.Message!);
        }

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaLifecycleContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission))
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise DMA post-completion lifecycle contract v5.");
        }

        if (!_dmaVisibilityStates.TryGetValue(submission.GrantId, out var visibilityState) ||
            visibilityState.LocalCycle.Value == 0 ||
            visibilityState.ProviderCycle.Value == 0)
        {
            _dmaSubmissionFaultPins.Add(submission.GrantId);
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                "The completed DMA operation lost its exact prepared visibility-cycle state.");
        }

        if (visibilityState.LocalCycle != submission.PreparedCycle || !visibilityState.Consumed)
        {
            _dmaSubmissionFaultPins.Add(submission.GrantId);
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                "The completed DMA operation no longer matches the exact consumed prepared cycle.");
        }

        if (visibilityState.Acquired)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformDenied,
                "Post-completion visibility for the exact DMA operation has already been consumed.");
        }

        if (submission.Direction == PlatformDmaDirection.DeviceReadsMemory)
        {
            _activeDmaSubmissions.Remove(submission.GrantId);
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Ok(
                new PlatformDmaPostCompletionVisibilityEvidence(
                    submission.OperationId,
                    submission.Generation,
                    submission.GrantId,
                    submission.GrantGeneration,
                    submission.PreparedCycle,
                    submission.Direction,
                    PlatformDmaPostCompletionVisibilityRequirement.None,
                    PlatformDmaPostCompletionVisibilityOutcome.NotRequired));
        }

        if (_provider is not IPlatformDmaVisibilityProvider visibilityProvider)
        {
            _dmaSubmissionFaultPins.Add(submission.GrantId);
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                "The v5 DMA provider no longer exposes the acquire primitive required for post-completion visibility.");
        }

        var providerGrant = _dmaGrants[submission.GrantId].ProviderGrant;
        var providerResult = visibilityProvider.AcquireDmaGrantVisibility(providerGrant);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status is PlatformAuthorityStatus.Faulted or
                PlatformAuthorityStatus.Stale or
                PlatformAuthorityStatus.Revoked or
                PlatformAuthorityStatus.WrongDomain)
            {
                _dmaSubmissionFaultPins.Add(submission.GrantId);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    providerResult.Message ?? "Post-completion DMA acquire lost exact provider lifetime identity.");
            }

            return FromProviderFailure<PlatformDmaPostCompletionVisibilityEvidence>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerEvidence = providerResult.Value!;
        var providerValidation = PlatformDmaVisibilityContract.ValidateAcquireEvidence(
            providerGrant,
            visibilityState.ProviderCycle,
            providerEvidence);
        if (!providerValidation.IsSuccess)
        {
            _dmaSubmissionFaultPins.Add(submission.GrantId);
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed post-completion DMA acquire evidence.");
        }

        visibilityState.Acquired = true;
        _activeDmaSubmissions.Remove(submission.GrantId);
        return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Ok(
            new PlatformDmaPostCompletionVisibilityEvidence(
                submission.OperationId,
                submission.Generation,
                submission.GrantId,
                submission.GrantGeneration,
                submission.PreparedCycle,
                submission.Direction,
                PlatformDmaPostCompletionVisibilityRequirement.AcquisitionFence,
                PlatformDmaPostCompletionVisibilityOutcome.AcquisitionFenceSatisfied));
    }

    private static KernelResult ValidateExactDmaCompletionEvidence(
        PlatformDmaSubmission submission,
        PlatformDmaCompletionEvidence evidence)
    {
        if (!evidence.IsSatisfied)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "Post-completion visibility requires satisfied exact completion evidence.");
        }

        if (evidence.OperationId != submission.OperationId)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA completion evidence belongs to a different local operation.");
        }

        if (evidence.OperationGeneration != submission.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "DMA completion evidence uses a stale local operation generation.");
        }

        if (evidence.GrantId != submission.GrantId)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA completion evidence belongs to a different local grant.");
        }

        if (evidence.GrantGeneration != submission.GrantGeneration)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "DMA completion evidence uses a stale local grant generation.");
        }

        if (evidence.PreparedCycle != submission.PreparedCycle)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA completion evidence belongs to a different prepared visibility cycle.");
        }

        if (evidence.Range != submission.Range || evidence.Direction != submission.Direction)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "DMA completion evidence does not match the exact submitted range and direction.");
        }

        return KernelResult.Ok();
    }
}
