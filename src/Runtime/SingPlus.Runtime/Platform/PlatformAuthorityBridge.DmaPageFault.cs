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
    internal KernelResult<PlatformDmaGrant> ResolveDmaSubmissionGrant(
        PlatformDmaSubmission submission, PlatformDomainIdentity expectedSubject)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
                KernelResult identity = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
            return identity.IsSuccess
                ? KernelResult<PlatformDmaGrant>.Ok(_dmaGrants[submission.GrantId].Grant)
                : KernelResult<PlatformDmaGrant>.Fail(identity.Error, identity.Message!);
        }
    }

    internal KernelResult<PlatformDmaPageFaultResolutionEvidence> ResolveDmaPageFault(
        PlatformDmaSubmission submission,
        PlatformDmaRange faultRange,
        PlatformMemoryAccess requestedAccess,
        ulong faultSequence,
        PlatformDomainIdentity expectedSubject,
        Func<Func<KernelResult<PlatformDmaPageFaultResolutionEvidence>>, KernelResult<PlatformDmaPageFaultResolutionEvidence>> ownerCommit)
    {
        DmaSubmissionRecord record;
        PlatformProviderDmaPageFaultRequest request;
        IPlatformDmaPageFaultProvider pageFaultProvider;
        PlatformProviderIncarnation incarnation;
        PlatformBackendEpoch backendEpoch;
        DmaGrantRecord grantRecord;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
                KernelResult identity = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
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
                record.ResolvedPageFaults.Contains(faultSequence) || record.KnownPageFaultEvidence.ContainsKey(faultSequence))
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformDenied,
                    "DMA page-fault sequence is already in flight or resolved.");
            if (!_featureManifest.Supports(PlatformFeatureFamily.DmaMapping,
                    PlatformDmaPageFaultContract.ContractVersion,
                    PlatformFeatureAvailability.RuntimeAdmission) ||
                _provider is not IPlatformDmaPageFaultProvider exactProvider)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformUnsupported,
                    "The DMA provider does not expose generation-bound page-fault resolution.");
            grantRecord = _dmaGrants[submission.GrantId];
            request = new(record.ProviderSubmission, grantRecord.ProviderGrant,
                faultRange, requestedAccess, faultSequence);
                PlatformAuthorityResult requestValidation = PlatformDmaPageFaultContract.ValidateRequest(request);
            if (!requestValidation.IsSuccess)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(
                    requestValidation.Status == PlatformAuthorityStatus.Stale
                        ? KernelError.StaleGeneration : KernelError.PlatformDenied,
                    requestValidation.Message ?? "DMA page-fault request is invalid.");
            record.PageFaultsInFlight.Add(faultSequence);
            backendEpoch = BackendEpoch;
            pageFaultProvider = exactProvider;
        }

        try
        {
            incarnation = CurrentProviderIncarnation();
            KernelResult<PlatformDmaPageFaultResolutionEvidence> admitted = Commit(false);
            if (!admitted.IsSuccess) return admitted;
            PlatformAuthorityResult<PlatformProviderDmaPageFaultEvidence> reply = pageFaultProvider.ResolveDmaPageFault(request);
            if (!reply.IsSuccess)
            {
                lock (_dmaCompletionGate)
                    if (reply.Status is PlatformAuthorityStatus.Faulted or PlatformAuthorityStatus.Stale or
                        PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.WrongDomain)
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                return FromProviderFailure<PlatformDmaPageFaultResolutionEvidence>(reply.Status, reply.Message);
            }
            PlatformAuthorityResult valid = PlatformDmaPageFaultContract.ValidateEvidence(request, reply.Value!);
            if (!valid.IsSuccess)
            {
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                    valid.Message ?? "Malformed page-fault receipt; original effect remains pinned.");
            }
            // Exact known provider fact; not local publication or closure.
            lock (_dmaCompletionGate)
                record.KnownPageFaultEvidence[faultSequence] = (backendEpoch, reply.Value!);
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                if (_dmaSubmissionFaultPins.Contains(submission.GrantId) || BackendEpoch != backendEpoch)
                    return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                        "Known page-fault fact retained after original authority was fault-pinned.");
            }
            try { incarnation = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                    $"Known page-fault receipt retained; source unavailable: {exception.GetType().Name}.");
            }
            return Commit(true);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
            return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                $"Page-fault outcome unknown; original effect pinned: {exception.GetType().Name}.");
        }
        finally { lock (_dmaCompletionGate) record.PageFaultsInFlight.Remove(faultSequence); }

        KernelResult<PlatformDmaPageFaultResolutionEvidence> Commit(bool publish) => ownerCommit(() =>
        {
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                    KernelResult exact = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
                if (!exact.IsSuccess || BackendEpoch != backendEpoch || _dmaSubmissionFaultPins.Contains(submission.GrantId) ||
                    !_activeDmaSubmissions.TryGetValue(submission.GrantId, out DmaSubmissionRecord? current) || !ReferenceEquals(current, record) ||
                    !_dmaGrants.TryGetValue(submission.GrantId, out DmaGrantRecord? currentGrant) || !ReferenceEquals(currentGrant, grantRecord) ||
                    record.CompletionProven || record.CompletionObservationInFlight || !record.PageFaultsInFlight.Contains(faultSequence) ||
                    incarnation.Value == 0 || incarnation != grantRecord.ProviderIncarnation)
                {
                    FaultPinDmaSubmissionLocked(submission.GrantId,
                        incarnation != grantRecord.ProviderIncarnation ? incarnation : null);
                    return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.PlatformFaulted,
                        "Page-fault source/submission changed; original effect remains pinned.");
                }
                if (!publish) return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Ok(default);
                record.ResolvedPageFaults.Add(faultSequence);
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Ok(new(
                    submission.OperationId, submission.Generation, submission.GrantId, submission.GrantGeneration,
                    submission.PreparedCycle, faultRange, requestedAccess, faultSequence));
            }
        });
    }
}
