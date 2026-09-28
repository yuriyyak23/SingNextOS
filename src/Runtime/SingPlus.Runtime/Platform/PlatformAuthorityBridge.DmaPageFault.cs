using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDmaPageFaultResolutionEvidence(
    PlatformDmaOperationId OperationId,
    PlatformDmaOperationGeneration OperationGeneration,
    PlatformDmaGrantId GrantId,
    PlatformDmaGrantGeneration GrantGeneration,
    PlatformDmaVisibilityCycle PreparedCycle,
    PlatformDmaRange FaultRange,
    PlatformMemoryAccess RequestedAccess,
    ulong FaultSequence)
{
    public bool AuthorizesMapping => false;
    public bool AuthorizesMemoryAccess => false;
}

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformDmaPageFaultResolutionEvidence> ResolveDmaPageFault(
        PlatformDmaSubmission submission,
        PlatformDmaRange faultRange,
        PlatformMemoryAccess requestedAccess,
        ulong faultSequence,
        PlatformDomainIdentity expectedSubject)
    {
        DmaSubmissionRecord record;
        PlatformProviderDmaPageFaultRequest request;
        IPlatformDmaPageFaultProvider pageFaultProvider;
        PlatformProviderIncarnation incarnation;
        lock (_dmaCompletionGate)
        {
            var identity = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
            if (!identity.IsSuccess)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(identity.Error, identity.Message!);
            record = _activeDmaSubmissions[submission.GrantId];
            if (record.CompletionProven)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.InvalidTransition,
                    "A completed DMA operation cannot resolve a new device page fault.");
            if (record.CompletionObservationInFlight)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformBindingDraining,
                    "DMA page-fault resolution cannot start while completion observation is in flight.");
            if (_dmaSubmissionFaultPins.Contains(submission.GrantId))
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                    "DMA page-fault state is fault-pinned.");
            if (record.PageFaultsInFlight.Contains(faultSequence) ||
                record.ResolvedPageFaults.Contains(faultSequence))
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformDenied,
                    "DMA page-fault sequence is already in flight or resolved.");
            if (!_featureManifest.Supports(PlatformFeatureFamily.DmaMapping,
                    PlatformDmaPageFaultContract.ContractVersion,
                    PlatformFeatureAvailability.RuntimeAdmission) ||
                _provider is not IPlatformDmaPageFaultProvider exactProvider)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformUnsupported,
                    "The DMA provider does not expose generation-bound page-fault resolution.");
            var grantRecord = _dmaGrants[submission.GrantId];
            incarnation = CurrentProviderIncarnation();
            if (incarnation.Value == 0 || incarnation != grantRecord.ProviderIncarnation)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.StaleGeneration,
                    "The DMA provider incarnation changed before page-fault resolution.");
            request = new(record.ProviderSubmission, grantRecord.ProviderGrant,
                faultRange, requestedAccess, faultSequence);
            var requestValidation = PlatformDmaPageFaultContract.ValidateRequest(request);
            if (!requestValidation.IsSuccess)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(
                    requestValidation.Status == PlatformAuthorityStatus.Stale
                        ? KernelError.StaleGeneration : KernelError.PlatformDenied,
                    requestValidation.Message ?? "DMA page-fault request is invalid.");
            record.PageFaultsInFlight.Add(faultSequence);
            pageFaultProvider = exactProvider;
        }

        try
        {
            PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence> providerResult;
            try { providerResult = pageFaultProvider.ResolveDmaPageFault(request); }
            catch (Exception exception)
            {
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                    $"The provider threw during DMA page-fault resolution; the mapping remains pinned: {exception.Message}");
            }

            lock (_dmaCompletionGate)
            {
                var stillExact = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
                if (!stillExact.IsSuccess)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId);
                    return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                        "DMA authority changed during page-fault resolution.");
                }
                var observedIncarnation = CurrentProviderIncarnation();
                if (observedIncarnation != incarnation)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId, observedIncarnation);
                    return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                        "DMA provider incarnation changed during page-fault resolution.");
                }
                if (record.CompletionObservationInFlight || record.CompletionProven)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId);
                    return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                        "DMA completion raced with page-fault resolution; the mapping remains pinned.");
                }
                if (!providerResult.IsSuccess)
                {
                    if (providerResult.Status is PlatformAuthorityStatus.Faulted or
                        PlatformAuthorityStatus.Stale or PlatformAuthorityStatus.Revoked or
                        PlatformAuthorityStatus.WrongDomain)
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                    return FromProviderFailure<PlatformDmaPageFaultResolutionEvidence>(
                        providerResult.Status, providerResult.Message);
                }
                var evidenceValidation = PlatformDmaPageFaultContract.ValidateEvidence(
                    request, providerResult.Value!);
                if (!evidenceValidation.IsSuccess)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId);
                    return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                        evidenceValidation.Message ?? "Provider DMA page-fault evidence is malformed.");
                }
                record.ResolvedPageFaults.Add(faultSequence);
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Ok(new(
                    submission.OperationId, submission.Generation, submission.GrantId,
                    submission.GrantGeneration, submission.PreparedCycle, faultRange,
                    requestedAccess, faultSequence));
            }
        }
        finally
        {
            lock (_dmaCompletionGate) record.PageFaultsInFlight.Remove(faultSequence);
        }
    }
}
