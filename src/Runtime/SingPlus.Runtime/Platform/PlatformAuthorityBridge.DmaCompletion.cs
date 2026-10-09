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
        DmaGrantRecord grantRecord;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
            var validation = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
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
            grantRecord = _dmaGrants[submission.GrantId];
            expectedIncarnation = grantRecord.ProviderIncarnation;
            completionProvider = exactCompletionProvider;
        }

        try
        {
            incarnation = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                if (!StillExact(incarnation)) return Refuse(incarnation);
            }
            PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> reply;
            bool hasKnown;
            lock (_dmaCompletionGate)
            {
                hasKnown = record.KnownCompletionEvidence is { } known && record.KnownCompletionEpoch == backendEpoch &&
                    PlatformDmaCompletionContract.ValidateEvidence(record.ProviderSubmission, known).IsSuccess &&
                    known.State == PlatformProviderDmaCompletionState.Completed;
                reply = hasKnown ? PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Ok(record.KnownCompletionEvidence!.Value) : default;
            }
            if (!hasKnown)
                reply = completionProvider.ObserveDmaCompletion(record.ProviderSubmission);
            if (reply.IsSuccess)
            {
                var valid = PlatformDmaCompletionContract.ValidateEvidence(record.ProviderSubmission, reply.Value!);
                if (!valid.IsSuccess)
                {
                    lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                        valid.Message ?? "Malformed completion receipt; original effect pinned.");
                }
                if (reply.Value!.State == PlatformProviderDmaCompletionState.Completed)
                {
                    lock (_dmaCompletionGate)
                    {
                        record.KnownCompletionEvidence = reply.Value!;
                        record.KnownCompletionEpoch = backendEpoch;
                    }
                }
            }
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
                if (_dmaSubmissionFaultPins.Contains(submission.GrantId) || BackendEpoch != backendEpoch)
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                        "Completion original lifetime became pinned; known receipt retained.");
            PlatformProviderIncarnation observed;
            try { observed = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_dmaCompletionGate)
                    if (record.KnownCompletionEvidence is null || record.KnownCompletionEpoch != backendEpoch)
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                    $"Completion source query unavailable; original effect/known receipt retained: {exception.GetType().Name}.");
            }
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                if (!StillExact(observed)) return Refuse(observed);
                if (!reply.IsSuccess)
                {
                    if (reply.Status is PlatformAuthorityStatus.Faulted or PlatformAuthorityStatus.Stale or
                        PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.WrongDomain)
                    {
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                        return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                            reply.Message ?? "Provider completion lost exact lifetime.");
                    }
                    return FromProviderFailure<PlatformDmaCompletionEvidence>(reply.Status, reply.Message);
                }
                switch (reply.Value!.State)
                {
                    case PlatformProviderDmaCompletionState.Pending:
                        return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformBindingDraining,
                            "Exact DMA operation remains pending; completion not proven.");
                    case PlatformProviderDmaCompletionState.Completed:
                        var evidence = new PlatformDmaCompletionEvidence(submission.OperationId, submission.Generation,
                            submission.GrantId, submission.GrantGeneration, submission.PreparedCycle, submission.Range, submission.Direction);
                        record.CompletionProven = true;
                        record.CompletionEvidence = evidence;
                        QueueDmaTraceLocked(record, SemanticTraceEventKindV1.RetireOrComplete);
                        return KernelResult<PlatformDmaCompletionEvidence>.Ok(evidence);
                    default:
                        FaultPinDmaSubmissionLocked(submission.GrantId);
                        return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                            "Faulted/undefined exact completion state; lower owners remain pinned.");
                }
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                $"Completion outcome unknown; mapping pinned: {exception.GetType().Name}.");
        }
        finally { lock (_dmaCompletionGate) record.CompletionObservationInFlight = false; }

        bool StillExact(PlatformProviderIncarnation observed) =>
            ValidateDmaSubmissionIdentityLocked(submission, expectedSubject).IsSuccess &&
            BackendEpoch == backendEpoch && !_dmaSubmissionFaultPins.Contains(submission.GrantId) &&
            _activeDmaSubmissions.TryGetValue(submission.GrantId, out var current) && ReferenceEquals(current, record) &&
            _dmaGrants.TryGetValue(submission.GrantId, out var currentGrant) && ReferenceEquals(currentGrant, grantRecord) &&
            record.CompletionObservationInFlight && !record.CompletionProven && record.PageFaultsInFlight.Count == 0 &&
            observed.Value != 0 && observed == expectedIncarnation;

        KernelResult<PlatformDmaCompletionEvidence> Refuse(PlatformProviderIncarnation observed)
        {
            FaultPinDmaSubmissionLocked(submission.GrantId, observed != expectedIncarnation ? observed : null);
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                "Completion original tuple/source changed; effect remains pinned.");
        }
    }
    internal KernelResult<PlatformDmaCompletionEvidence> GetProvenDmaCompletion(
        PlatformDmaSubmission submission, PlatformDomainIdentity expectedSubject) =>
        WithObservedDmaSubmissionIdentity(submission, expectedSubject, record =>
        {
            if (_dmaSubmissionFaultPins.Contains(submission.GrantId))
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformFaulted,
                    "Fault-pinned DMA completion cannot produce reusable evidence.");
            return record.CompletionEvidence is { } evidence
                ? KernelResult<PlatformDmaCompletionEvidence>.Ok(evidence)
                : KernelResult<PlatformDmaCompletionEvidence>.Fail(KernelError.PlatformBindingDraining,
                    "Exact operation has not produced local completion evidence.");
        });

    internal KernelResult ValidateDmaSubmissionIdentity(
        PlatformDmaSubmission submission, PlatformDomainIdentity expectedSubject)
    {
        var validated = WithObservedDmaSubmissionIdentity(submission, expectedSubject,
            _ => KernelResult<bool>.Ok(true));
        return validated.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(validated.Error, validated.Message!);
    }

    // Only trusted local projection runs under the existing owner gates.
    private KernelResult<T> WithObservedDmaSubmissionIdentity<T>(
        PlatformDmaSubmission submission, PlatformDomainIdentity expectedSubject,
        Func<DmaSubmissionRecord, KernelResult<T>> project)
    {
        DmaSubmissionRecord record;
        DmaGrantRecord grantRecord;
        PlatformBackendEpoch epoch;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
            var valid = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
            if (!valid.IsSuccess) return KernelResult<T>.Fail(valid.Error, valid.Message!);
            record = _activeDmaSubmissions[submission.GrantId];
            grantRecord = _dmaGrants[submission.GrantId];
            epoch = BackendEpoch;
        }
        PlatformProviderIncarnation observed;
        try { observed = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(submission.GrantId);
            return KernelResult<T>.Fail(KernelError.PlatformFaulted,
                $"DMA identity source unavailable; original effect retained: {exception.GetType().Name}.");
        }
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
            if (observed.Value == 0 || observed != grantRecord.ProviderIncarnation)
            {
                FaultPinDmaSubmissionLocked(submission.GrantId, observed);
                return KernelResult<T>.Fail(KernelError.PlatformFaulted, "DMA identity provider source changed.");
            }
            if (BackendEpoch != epoch || _dmaSubmissionFaultPins.Contains(submission.GrantId))
            {
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<T>.Fail(KernelError.PlatformFaulted, "DMA identity source continuity lost.");
            }
            var fresh = ValidateDmaSubmissionIdentityLocked(submission, expectedSubject);
            if (!fresh.IsSuccess) return KernelResult<T>.Fail(fresh.Error, fresh.Message!);
            if (!ReferenceEquals(_activeDmaSubmissions[submission.GrantId], record) ||
                !ReferenceEquals(_dmaGrants[submission.GrantId], grantRecord))
            {
                FaultPinDmaSubmissionLocked(submission.GrantId);
                return KernelResult<T>.Fail(KernelError.PlatformFaulted, "Original DMA identity lifetime changed.");
            }
            return project(record);
        }
    }
    private KernelResult ValidateDmaSubmissionIdentityLocked(
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

        if (grantRecord.SubmissionInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "DMA submission final publication is still in flight.");
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
