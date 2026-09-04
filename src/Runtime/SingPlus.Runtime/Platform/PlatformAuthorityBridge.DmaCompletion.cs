using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDmaCompletionEvidence(
    PlatformDmaOperationId OperationId,
    PlatformDmaOperationGeneration OperationGeneration,
    PlatformDmaGrantId GrantId,
    PlatformDmaGrantGeneration GrantGeneration,
    PlatformDmaVisibilityCycle PreparedCycle,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction)
{
    public bool IsSatisfied =>
        OperationId.Value != 0 &&
        OperationGeneration.Value != 0 &&
        GrantId.Value != 0 &&
        GrantGeneration.Value != 0 &&
        PreparedCycle.Value != 0;
}

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformDmaCompletionEvidence> ObserveDmaCompletion(
        PlatformDmaSubmission submission,
        PlatformDomainIdentity expectedSubject)
    {
        DmaSubmissionRecord record;
        IPlatformDmaCompletionProvider completionProvider;
        lock (_dmaCompletionGate)
        {
            var validation = ValidateDmaSubmissionIdentity(submission, expectedSubject);
            if (!validation.IsSuccess)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    validation.Error,
                    validation.Message!);
            }

            record = _activeDmaSubmissions[submission.GrantId];
            if (record.CompletionProven)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformDenied,
                    "Completion for this exact DMA operation has already been proven and cannot be replayed.");
            }

            if (HasFaultPinnedDmaSubmission(submission.GrantId))
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    "DMA completion state is fault-pinned and cannot produce reusable completion evidence.");
            }

            if (record.CompletionObservationInFlight)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformBindingDraining,
                    "Completion observation for this exact DMA operation is already in flight.");
            }

            if (!_featureManifest.Supports(
                    PlatformFeatureFamily.DmaMapping,
                    PlatformDmaCompletionContract.ContractVersion,
                    PlatformFeatureAvailability.RuntimeAdmission))
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformUnsupported,
                    "The platform provider does not advertise exact DMA completion contract v4.");
            }

            if (_provider is not IPlatformDmaCompletionProvider exactCompletionProvider)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformUnsupported,
                    "The platform provider does not expose exact DMA completion observation.");
            }

            record.CompletionObservationInFlight = true;
            completionProvider = exactCompletionProvider;
        }

        try
        {
            var providerResult = completionProvider.ObserveDmaCompletion(record.ProviderSubmission);
            if (!providerResult.IsSuccess)
            {
                if (providerResult.Status is PlatformAuthorityStatus.Faulted or
                    PlatformAuthorityStatus.Stale or
                    PlatformAuthorityStatus.Revoked or
                    PlatformAuthorityStatus.WrongDomain)
                {
                    _dmaSubmissionFaultPins.Add(submission.GrantId);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                        KernelError.PlatformFaulted,
                        providerResult.Message ?? "Provider DMA completion state became invalid while the operation remained locally pending.");
                }

                return FromProviderFailure<PlatformDmaCompletionEvidence>(
                    providerResult.Status,
                    providerResult.Message);
            }

            var providerEvidence = providerResult.Value!;
            var providerValidation = PlatformDmaCompletionContract.ValidateEvidence(
                record.ProviderSubmission,
                providerEvidence);
            if (!providerValidation.IsSuccess)
            {
                _dmaSubmissionFaultPins.Add(submission.GrantId);
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    providerValidation.Message ?? "The provider returned malformed DMA completion evidence.");
            }

            switch (providerEvidence.State)
            {
                case PlatformProviderDmaCompletionState.Pending:
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                        KernelError.PlatformBindingDraining,
                        "The exact DMA operation remains pending; completion has not been proven.");

                case PlatformProviderDmaCompletionState.Faulted:
                    _dmaSubmissionFaultPins.Add(submission.GrantId);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                        KernelError.PlatformFaulted,
                        "The provider reported the exact DMA operation faulted; lower authority remains pinned.");

                case PlatformProviderDmaCompletionState.Completed:
                    record.CompletionProven = true;
                    return KernelResult<PlatformDmaCompletionEvidence>.Ok(
                        new PlatformDmaCompletionEvidence(
                            submission.OperationId,
                            submission.Generation,
                            submission.GrantId,
                            submission.GrantGeneration,
                            submission.PreparedCycle,
                            submission.Range,
                            submission.Direction));

                default:
                    _dmaSubmissionFaultPins.Add(submission.GrantId);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                        KernelError.PlatformFaulted,
                        "The provider returned an undefined DMA completion state.");
            }
        }
        finally
        {
            lock (_dmaCompletionGate)
                record.CompletionObservationInFlight = false;
        }
    }

    internal KernelResult ValidateDmaSubmissionIdentity(
        PlatformDmaSubmission submission,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_activeDmaSubmissions.TryGetValue(submission.GrantId, out var record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The exact DMA submission is not tracked.");
        }

        if (!_dmaGrants.TryGetValue(submission.GrantId, out var grantRecord))
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The DMA submission exists without its exact grant authority.");
        }

        var grantIdentity = ValidateDmaGrantIdentity(grantRecord.Grant, expectedSubject);
        if (!grantIdentity.IsSuccess) return grantIdentity;

        if (record.Submission.OperationId != submission.OperationId)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA completion request belongs to a different local operation.");
        }

        if (record.Submission.Generation != submission.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "DMA completion request uses a stale local operation generation.");
        }

        if (record.Submission.GrantGeneration != submission.GrantGeneration)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "DMA completion request uses a stale local grant generation.");
        }

        if (record.Submission.PreparedCycle != submission.PreparedCycle)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA completion request belongs to a different prepared visibility cycle.");
        }

        if (record.Submission.GrantId != submission.GrantId ||
            record.Submission.Range != submission.Range ||
            record.Submission.Direction != submission.Direction)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The local DMA completion request is malformed.");
        }

        return KernelResult.Ok();
    }
}
