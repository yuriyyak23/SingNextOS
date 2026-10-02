using SingPlus.Contracts;
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
        PlatformBackendEpoch backendEpoch;
        PlatformProviderIncarnation incarnation;
        PlatformProviderIncarnation expectedIncarnation;
        lock (_dmaCompletionGate)
        {
            var validation = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject, observeProvider: false);
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

            if (record.CompletionObservationInFlight)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformBindingDraining,
                    "Completion observation for this exact DMA operation is already in flight.");
            }

            if (record.PageFaultsInFlight.Count != 0)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformBindingDraining,
                    "Completion cannot be observed while exact DMA page-fault resolution is in flight.");
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
            backendEpoch = BackendEpoch;
            expectedIncarnation = _dmaGrants[submission.GrantId].ProviderIncarnation;
            completionProvider = exactCompletionProvider;
        }

        try
        {
            lock (_dmaCompletionGate)
            {
                try { incarnation = CurrentProviderIncarnation(); }
                catch (Exception exception)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                        $"DMA completion generation read failed; the effect remains pinned: {exception.Message}");
                }
                var exact = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject, observeProvider: false);
                if (!exact.IsSuccess || BackendEpoch != backendEpoch ||
                    _dmaSubmissionFaultPins.Contains(submission.GrantId) || record.CompletionProven ||
                    record.PageFaultsInFlight.Count != 0 || incarnation.Value == 0 ||
                    incarnation != expectedIncarnation)
                {
                    // Actual generation observations remain trace evidence even for a pinned effect.
                    FaultPinDmaSubmissionLocked(submission.GrantId,
                        incarnation != expectedIncarnation ? incarnation : null);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                        "DMA authority changed during completion admission; the effect remains pinned.");
                }
            }
            PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> providerResult;
            try
            {
                providerResult = completionProvider.ObserveDmaCompletion(
                    record.ProviderSubmission);
            }
            catch (Exception exception)
            {
                lock (_dmaCompletionGate)
                    FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    $"The DMA provider threw while observing completion; the exact mapping remains pinned: {exception.Message}");
            }

            lock (_dmaCompletionGate)
            {
                if (_dmaSubmissionFaultPins.Contains(submission.GrantId))
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                        "DMA completion authority became fault-pinned during observation.");
                PlatformProviderIncarnation observedIncarnation;
                try { observedIncarnation = CurrentProviderIncarnation(); }
                catch (Exception exception)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                        $"DMA completion post-response generation read failed; the effect remains pinned: {exception.Message}");
                }
                var stillExact = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject, observeProvider: false);
                if (!stillExact.IsSuccess || BackendEpoch != backendEpoch ||
                    _dmaSubmissionFaultPins.Contains(submission.GrantId) || observedIncarnation != incarnation ||
                    record.CompletionProven || record.PageFaultsInFlight.Count != 0)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId,
                        observedIncarnation != incarnation ? observedIncarnation : null);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                        "DMA owner or provider incarnation changed during completion observation; the exact mapping remains pinned.");
                }
                if (!providerResult.IsSuccess)
                {
                    if (providerResult.Status is PlatformAuthorityStatus.Faulted or
                        PlatformAuthorityStatus.Stale or
                        PlatformAuthorityStatus.Revoked or
                        PlatformAuthorityStatus.WrongDomain)
                    {
                        FaultPinDmaSubmissionLocked(submission.GrantId);
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
                    FaultPinDmaSubmissionLocked(submission.GrantId);
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
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                        return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                            KernelError.PlatformFaulted,
                            "The provider reported the exact DMA operation faulted; lower authority remains pinned.");

                    case PlatformProviderDmaCompletionState.Completed:
                        var evidence = new PlatformDmaCompletionEvidence(
                            submission.OperationId,
                            submission.Generation,
                            submission.GrantId,
                            submission.GrantGeneration,
                            submission.PreparedCycle,
                            submission.Range,
                            submission.Direction);
                        record.CompletionProven = true;
                        record.CompletionEvidence = evidence;
                        QueueDmaTraceLocked(record, SemanticTraceEventKindV1.RetireOrComplete);
                        return KernelResult<PlatformDmaCompletionEvidence>.Ok(evidence);

                    default:
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                        return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                            KernelError.PlatformFaulted,
                            "The provider returned an undefined DMA completion state.");
                }
            }
        }
        finally
        {
            lock (_dmaCompletionGate)
                record.CompletionObservationInFlight = false;
        }
    }

    internal KernelResult<PlatformDmaCompletionEvidence> GetProvenDmaCompletion(
        PlatformDmaSubmission submission,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_dmaCompletionGate)
        {
            var validation = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
            if (!validation.IsSuccess)
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    validation.Error, validation.Message!);
            if (_dmaSubmissionFaultPins.Contains(submission.GrantId))
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    "DMA completion state is fault-pinned and cannot produce reusable completion evidence.");
            var record = _activeDmaSubmissions[submission.GrantId];
            return record.CompletionEvidence is { } evidence
                ? KernelResult<PlatformDmaCompletionEvidence>.Ok(evidence)
                : KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformBindingDraining,
                    "The exact DMA operation has not yet produced completion evidence.");
        }
    }

    internal KernelResult ValidateDmaSubmissionIdentity(
        PlatformDmaSubmission submission,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_dmaCompletionGate)
            return ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
    }

    private KernelResult ValidateDmaSubmissionIdentityLocked(
        PlatformDmaSubmission submission,
        PlatformDomainIdentity expectedSubject,
        bool observeProvider = true)
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

        if (observeProvider)
        {
            var backendEpoch = BackendEpoch;
            PlatformProviderIncarnation currentIncarnation;
            try { currentIncarnation = CurrentProviderIncarnation(); }
            catch (Exception exception)
            {
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    $"DMA identity generation read failed; possible effect remains pinned: {exception.Message}");
            }
            if (currentIncarnation.Value == 0 || currentIncarnation != grantRecord.ProviderIncarnation)
            {
                FaultPinDmaSubmissionLocked(submission.GrantId, currentIncarnation);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "The DMA provider incarnation changed while a possible submission remained pending.");
            }
            if (BackendEpoch != backendEpoch || _dmaSubmissionFaultPins.Contains(submission.GrantId))
            {
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "DMA identity continuity was lost during generation observation.");
            }
            var fresh = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject, observeProvider: false);
            if (!fresh.IsSuccess) return fresh;
            if (!ReferenceEquals(_activeDmaSubmissions[submission.GrantId], record) ||
                !ReferenceEquals(_dmaGrants[submission.GrantId], grantRecord))
            {
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "The exact DMA lifetime changed during generation observation.");
            }
        }
        return KernelResult.Ok();
    }
}
