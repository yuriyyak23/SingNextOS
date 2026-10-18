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
    internal KernelResult<PlatformDmaPostCompletionVisibilityEvidence> GetProvenDmaCopyVisibility(
        PlatformDmaSubmission submission, PlatformDmaCompletionEvidence completion, PlatformDomainIdentity subject) =>
        WithObservedDmaSubmissionIdentity(submission, subject, record =>
        {
            if (_dmaSubmissionFaultPins.Contains(submission.GrantId))
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted, "Copy visibility original effect is pinned.");
            var valid = ValidateExactDmaCompletionEvidence(submission, completion);
            if (!valid.IsSuccess) return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(valid.Error, valid.Message!);
            return record.CopySubmissionId is not null && record.CopyVisibilityEvidence is { IsSatisfied: true } evidence
                ? KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Ok(evidence)
                : KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformBindingDraining, "Copy leg visibility is not yet committed.");
        });

    internal KernelResult<PlatformDmaPostCompletionVisibilityEvidence> FinalizeDmaPostCompletionVisibility(
        PlatformDmaSubmission submission, PlatformDmaCompletionEvidence completionEvidence,
        PlatformDomainIdentity expectedSubject,
        Func<Func<KernelResult<PlatformDmaPostCompletionVisibilityEvidence>>, KernelResult<PlatformDmaPostCompletionVisibilityEvidence>> ownerCommit)
    {
        DmaSubmissionRecord record;
        DmaGrantRecord grantRecord;
        DmaVisibilityState visibilityState;
        PlatformBackendEpoch backendEpoch;
        PlatformProviderIncarnation expectedIncarnation;
        DmaVisibilityOwnerInvocation? visibilityInvocation = null;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
        var submissionValidation = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
        if (!submissionValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                submissionValidation.Error,
                submissionValidation.Message!);
        }

        record = _activeDmaSubmissions[submission.GrantId];
        grantRecord = _dmaGrants[submission.GrantId];
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

        if (!_dmaVisibilityStates.TryGetValue(submission.GrantId, out visibilityState) ||
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

        if (visibilityState.Acquired || record.CopyVisibilityEvidence is not null)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                KernelError.PlatformDenied,
                "Post-completion visibility for the exact DMA operation has already been consumed.");
        }

        backendEpoch = BackendEpoch;
        expectedIncarnation = grantRecord.ProviderIncarnation;
        grantRecord.AcquisitionInFlight = true;
        }
        try
        {
            var incarnation = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                if (!VisibilityTupleRemainsExact(incarnation)) return RefuseSource(incarnation);
            }
            if (submission.Direction == PlatformDmaDirection.DeviceReadsMemory)
                return CommitVisibility(incarnation, false);
            if (_provider is not IPlatformDmaVisibilityProvider visibilityProvider)
            {
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted, "Completed provider lost acquire primitive; mapping pinned.");
            }
            var providerGrant = grantRecord.ProviderGrant;
            PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> providerResult;
            bool hasKnown;
            lock (_dmaCompletionGate)
            {
                hasKnown = visibilityState.KnownAcquireEvidence is { } known && visibilityState.KnownAcquireEpoch == backendEpoch &&
                    PlatformDmaVisibilityContract.ValidateAcquireEvidence(providerGrant, visibilityState.ProviderCycle, known).IsSuccess;
                providerResult = hasKnown ? PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(visibilityState.KnownAcquireEvidence!.Value) : default;
            }
            if (!hasKnown)
            {
                var canContain = _provider is IPlatformDmaVisibilityClosureProvider closure && closure.CanCloseOriginalDmaVisibility(providerGrant);
                lock (_secureDomainLifecycleGate)
                lock (_dmaCompletionGate)
                {
                    if (!VisibilityTupleRemainsExact(incarnation)) return RefuseSource(incarnation);
                    if (!OriginalVisibilityHistoryIntact(grantRecord)) throw new InvalidOperationException("Original visibility history lost.");
                    if (grantRecord.VisibilityIssued == ulong.MaxValue) return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.CapacityExhausted,"Visibility history exhausted before effect.");
                    visibilityInvocation = new(grantRecord,true,backendEpoch,canContain);
                    grantRecord.VisibilityHistory.Add(visibilityInvocation); grantRecord.VisibilityIssued++; visibilityInvocation.Invoked = true;
                }
                providerResult = visibilityProvider.AcquireDmaGrantVisibility(providerGrant);
                lock (_dmaCompletionGate) visibilityInvocation.RawAcquire = providerResult; // Before late callbacks.
            }
            if (providerResult.IsSuccess)
            {
                var valid = PlatformDmaVisibilityContract.ValidateAcquireEvidence(providerGrant, visibilityState.ProviderCycle, providerResult.Value!);
                if (!valid.IsSuccess)
                {
                    lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
                    return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted, valid.Message ?? "Malformed completed acquire receipt.");
                }
                lock (_dmaCompletionGate)
                {
                    visibilityState.KnownAcquireEvidence = providerResult.Value!;
                    visibilityState.KnownAcquireEpoch = backendEpoch;
                }
            }
            else
            {
                lock (_dmaCompletionGate)
                    if (providerResult.Status is PlatformAuthorityStatus.Faulted or PlatformAuthorityStatus.Stale or PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.WrongDomain)
                    {
                        if (providerResult.Status == PlatformAuthorityStatus.Faulted) PinVisibilityUnknown();
                        else FaultPinDmaSubmissionLocked(submission.GrantId);
                        return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted, providerResult.Message ?? "Acquire lost exact provider identity.");
                    }
                return FromProviderFailure<PlatformDmaPostCompletionVisibilityEvidence>(providerResult.Status, providerResult.Message);
            }
            try { incarnation = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                    $"Known completed acquire receipt retained while source query unavailable: {exception.GetType().Name}.");
            }
            return CommitVisibility(incarnation, true);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_dmaCompletionGate) PinVisibilityUnknown();
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted, $"Completed visibility outcome unknown; mapping pinned: {exception.GetType().Name}.");
        }
        finally { lock (_dmaCompletionGate) grantRecord.AcquisitionInFlight = false; }

        void PinVisibilityUnknown()
        {
            PinOriginalDmaVisibilityLocked(grantRecord,visibilityInvocation);
            if (grantRecord.OwnVisibilityUncertain && grantRecord.OriginalCopy is { Invoked: true, LifetimeConsumed: false } original &&
                (ReferenceEquals(original.AcceptedSource,record) || ReferenceEquals(original.AcceptedDestination,record))) original.Uncertain = true;
        }

        bool VisibilityTupleRemainsExact(PlatformProviderIncarnation incarnation) =>
            ValidateDmaSubmissionIdentityLocked(submission, expectedSubject).IsSuccess &&
            !_dmaSubmissionFaultPins.Contains(submission.GrantId) && BackendEpoch == backendEpoch &&
            incarnation.Value != 0 && incarnation == expectedIncarnation && record.CompletionProven &&
            ValidateExactDmaCompletionEvidence(submission, completionEvidence).IsSuccess &&
            _activeDmaSubmissions.TryGetValue(submission.GrantId, out var currentRecord) && ReferenceEquals(currentRecord, record) &&
            _dmaGrants.TryGetValue(submission.GrantId, out var currentGrant) && ReferenceEquals(currentGrant, grantRecord) &&
            grantRecord.AcquisitionInFlight && _dmaVisibilityStates.TryGetValue(submission.GrantId, out var currentVisibility) &&
            ReferenceEquals(currentVisibility, visibilityState) && !visibilityState.Acquired && visibilityState.Consumed &&
            visibilityState.LocalCycle == submission.PreparedCycle;

        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> RefuseSource(PlatformProviderIncarnation incarnation)
        {
            FaultPinDmaSubmissionLocked(submission.GrantId, incarnation != expectedIncarnation ? incarnation : null);
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted, "Completed visibility lost exact source/cycle; mapping pinned.");
        }
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> CommitVisibility(PlatformProviderIncarnation incarnation, bool acquired) => ownerCommit(() =>
        {
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                if (!VisibilityTupleRemainsExact(incarnation)) return RefuseSource(incarnation);
                DmaSubmissionRecord? peer = null;
                PlatformDmaGrantId peerId = default;
                if (record.CopySubmissionId is { } copyId)
                {
                    if (grantRecord.CopyPeerGrantId is not { } exactPeer ||
                        !_activeDmaSubmissions.TryGetValue(exactPeer, out peer) ||
                        peer.CopySubmissionId != copyId || peer.CopyGeneration != record.CopyGeneration ||
                        !_dmaGrants.TryGetValue(exactPeer, out var peerGrant) || peerGrant.CopyPeerGrantId != submission.GrantId ||
                        _dmaSubmissionFaultPins.Contains(exactPeer))
                    {
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                        return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.PlatformFaulted,
                            "Copy peer lifetime/correlation lost; neither leg can authorize pair release.");
                    }
                    peerId = exactPeer;
                }
                if (acquired) visibilityState.Acquired = true;
                var evidence = new PlatformDmaPostCompletionVisibilityEvidence(submission.OperationId, submission.Generation,
                    submission.GrantId, submission.GrantGeneration, submission.PreparedCycle, submission.Direction,
                    acquired ? PlatformDmaPostCompletionVisibilityRequirement.AcquisitionFence : PlatformDmaPostCompletionVisibilityRequirement.None,
                    acquired ? PlatformDmaPostCompletionVisibilityOutcome.AcquisitionFenceSatisfied : PlatformDmaPostCompletionVisibilityOutcome.NotRequired);
                QueueDmaTraceLocked(record, SemanticTraceEventKindV1.Visible);
                CaptureVisibleDmaTraceLocked(record);
                if (peer is null) _activeDmaSubmissions.Remove(submission.GrantId);
                else
                {
                    record.CopyVisibilityEvidence = evidence;
                    // The original pair records own partial progress; keep both until both visibility commits.
                    if (peer.CopyVisibilityEvidence is { IsSatisfied: true })
                    {
                        _dmaGrants[peerId].CompletedCopySubmission = peer;
                        grantRecord.CompletedCopySubmission = record;
                        _activeDmaSubmissions.Remove(peerId);
                        _activeDmaSubmissions.Remove(submission.GrantId);
                    }
                }
                return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Ok(evidence);
            }
        });
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
