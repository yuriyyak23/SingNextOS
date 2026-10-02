using SingPlus.Contracts;
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
        (Direction switch
        {
            PlatformDmaDirection.DeviceReadsMemory =>
                Requirement == PlatformDmaPostCompletionVisibilityRequirement.None &&
                Outcome == PlatformDmaPostCompletionVisibilityOutcome.NotRequired,
            PlatformDmaDirection.DeviceWritesMemory or PlatformDmaDirection.Bidirectional =>
                Requirement == PlatformDmaPostCompletionVisibilityRequirement.AcquisitionFence &&
                Outcome == PlatformDmaPostCompletionVisibilityOutcome.AcquisitionFenceSatisfied,
            _ => false,
        });
}

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformDmaPostCompletionVisibilityEvidence> FinalizeDmaPostCompletionVisibility(
        PlatformDmaSubmission submission,
        PlatformDmaCompletionEvidence completionEvidence,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_dmaCompletionGate)
        {
            return FinalizeDmaPostCompletionVisibilityLocked(
                submission,
                completionEvidence,
                expectedSubject);
        }
    }

    private KernelResult<PlatformDmaPostCompletionVisibilityEvidence> FinalizeDmaPostCompletionVisibilityLocked(
        PlatformDmaSubmission submission,
        PlatformDmaCompletionEvidence completionEvidence,
        PlatformDomainIdentity expectedSubject)
    {
        var submissionValidation = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject, observeProvider: false);
        if (!submissionValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                submissionValidation.Error,
                submissionValidation.Message!);
        }

        var record = _activeDmaSubmissions[submission.GrantId];
        var grantRecord = _dmaGrants[submission.GrantId];
        if (grantRecord.AcquisitionInFlight)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformBindingDraining,
                "The exact DMA visibility acquire is already in flight.");
        }
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
            FaultPinDmaSubmissionLocked(submission.GrantId);
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformFaulted,
                "The completed DMA operation lost its exact prepared visibility-cycle state.");
        }

        if (visibilityState.LocalCycle != submission.PreparedCycle || !visibilityState.Consumed)
        {
            FaultPinDmaSubmissionLocked(submission.GrantId);
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

        var backendEpoch = BackendEpoch;
        var expectedIncarnation = grantRecord.ProviderIncarnation;
        grantRecord.AcquisitionInFlight = true;
        try { return FinalizePreparedVisibility(); }
        finally { grantRecord.AcquisitionInFlight = false; }

        bool VisibilityTupleRemainsExact(PlatformProviderIncarnation incarnation) =>
            ValidateDmaSubmissionIdentityLocked(submission, expectedSubject, observeProvider: false).IsSuccess &&
            !_dmaSubmissionFaultPins.Contains(submission.GrantId) && BackendEpoch == backendEpoch &&
            incarnation.Value != 0 && incarnation == expectedIncarnation && record.CompletionProven &&
            _activeDmaSubmissions.TryGetValue(submission.GrantId, out var currentRecord) &&
            ReferenceEquals(currentRecord, record) &&
            _dmaVisibilityStates.TryGetValue(submission.GrantId, out var currentVisibility) &&
            ReferenceEquals(currentVisibility, visibilityState) && !visibilityState.Acquired &&
            visibilityState.Consumed && visibilityState.LocalCycle == submission.PreparedCycle;

        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> FinalizePreparedVisibility()
        {
            PlatformProviderIncarnation incarnation;
            try { incarnation = CurrentProviderIncarnation(); }
            catch (Exception exception)
            {
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                    $"DMA visibility generation read failed; the effect remains pinned: {exception.Message}");
            }
            if (!VisibilityTupleRemainsExact(incarnation))
            {
                FaultPinDmaSubmissionLocked(submission.GrantId,
                    incarnation != expectedIncarnation ? incarnation : null);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                    "DMA visibility authority changed during generation admission; the effect remains pinned.");
            }
            if (submission.Direction == PlatformDmaDirection.DeviceReadsMemory)
            {
                QueueDmaTraceLocked(record, SemanticTraceEventKindV1.Visible);
                CaptureVisibleDmaTraceLocked(record);
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
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    "The v5 DMA provider no longer exposes the acquire primitive required for post-completion visibility.");
            }

            var providerGrant = _dmaGrants[submission.GrantId].ProviderGrant;
            PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> providerResult;
            try
            {
                providerResult = visibilityProvider.AcquireDmaGrantVisibility(providerGrant);
                incarnation = CurrentProviderIncarnation();
            }
            catch (Exception exception)
            {
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    $"The DMA provider threw during post-completion acquire; the exact mapping remains pinned: {exception.Message}");
            }

            if (!VisibilityTupleRemainsExact(incarnation))
            {
                FaultPinDmaSubmissionLocked(submission.GrantId,
                    incarnation != expectedIncarnation ? incarnation : null);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                    "DMA visibility authority changed during acquire; the effect remains pinned.");
            }

            if (!providerResult.IsSuccess)
            {
                if (providerResult.Status is PlatformAuthorityStatus.Faulted or
                    PlatformAuthorityStatus.Stale or
                    PlatformAuthorityStatus.Revoked or
                    PlatformAuthorityStatus.WrongDomain)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId);
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
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    providerValidation.Message ?? "The provider returned malformed post-completion DMA acquire evidence.");
            }

            visibilityState.Acquired = true;
            QueueDmaTraceLocked(record, SemanticTraceEventKindV1.Visible);
            CaptureVisibleDmaTraceLocked(record);
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
