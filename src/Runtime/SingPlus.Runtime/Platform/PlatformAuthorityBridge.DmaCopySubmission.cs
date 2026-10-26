using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal readonly record struct PlatformDmaCopyPairSubmission(
    PlatformDmaSubmission Source,
    PlatformDmaSubmission Destination,
    DmaExecutionBindingV1 SourceBinding,
    DmaExecutionBindingV1 DestinationBinding);

internal readonly record struct PlatformDmaCopyClosureObservation(
    PlatformProviderDmaCopySubmissionId CopySubmissionId,
    PlatformProviderDmaCopySubmissionGeneration CopyGeneration,
    PlatformDmaGrant SourceGrant,
    PlatformDmaGrant DestinationGrant,
    bool SourceGrantClosed,
    bool DestinationGrantClosed,
    bool SourceFaultPinned,
    bool DestinationFaultPinned)
{
    internal bool AuthorizesSourceRegionRelease => false;
    internal bool ProvesProviderEffectClosure => false;
}

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<bool> HasCompletedDmaCopyPairForCleanup(
        PlatformDmaGrant source, PlatformDmaPrepareEvidence sourcePrepare, PlatformDomainIdentity sourceSubject,
        PlatformDmaGrant destination, PlatformDmaPrepareEvidence destinationPrepare, PlatformDomainIdentity destinationSubject,
        ulong sourceMutation)
    {
        DmaGrantRecord sourceGrant, destinationGrant;
        DmaSubmissionRecord sourceRecord, destinationRecord;
        PlatformBackendEpoch epoch;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
                KernelResult valid = ValidateDmaGrantIdentity(source, sourceSubject);
            if (!valid.IsSuccess) return KernelResult<bool>.Fail(valid.Error, valid.Message!);
            valid = ValidateDmaGrantIdentity(destination, destinationSubject);
            if (!valid.IsSuccess) return KernelResult<bool>.Fail(valid.Error, valid.Message!);
            sourceGrant = _dmaGrants[source.GrantId]; destinationGrant = _dmaGrants[destination.GrantId];
            if (sourceGrant.CompletedCopySubmission is null && destinationGrant.CompletedCopySubmission is null)
                return KernelResult<bool>.Ok(false);
            if (sourceGrant.CompletedCopySubmission is not { } a || destinationGrant.CompletedCopySubmission is not { } b)
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Completed copy lost one original leg.");
            sourceRecord = a; destinationRecord = b; epoch = BackendEpoch;
        }
        PlatformProviderIncarnation observed;
        try { observed = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<bool>.Fail(KernelError.PlatformFaulted,
                $"Completed copy facts retained; cleanup source unavailable: {exception.GetType().Name}.");
        }
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
            if (BackendEpoch != epoch || observed.Value == 0 || observed != sourceGrant.ProviderIncarnation || observed != destinationGrant.ProviderIncarnation)
            {
                if (!sourceGrant.PlatformClosed) FaultPinDmaSubmissionLocked(source.GrantId);
                if (!destinationGrant.PlatformClosed) FaultPinDmaSubmissionLocked(destination.GrantId);
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Completed copy cleanup lost original source continuity.");
            }
            if (!_dmaGrants.TryGetValue(source.GrantId, out DmaGrantRecord? currentSource) || !ReferenceEquals(currentSource, sourceGrant) ||
                !_dmaGrants.TryGetValue(destination.GrantId, out DmaGrantRecord? currentDestination) || !ReferenceEquals(currentDestination, destinationGrant) ||
                !ReferenceEquals(sourceGrant.CompletedCopySubmission, sourceRecord) || !ReferenceEquals(destinationGrant.CompletedCopySubmission, destinationRecord) ||
                HasFaultPinnedDmaSubmission(source.GrantId) || HasFaultPinnedDmaSubmission(destination.GrantId))
                return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original completed copy lifetime is pinned or changed.");
            if (sourceGrant.PreparationInFlight || destinationGrant.PreparationInFlight || sourceGrant.SubmissionInFlight || destinationGrant.SubmissionInFlight ||
                HasActiveDmaSubmission(source.GrantId) || HasActiveDmaSubmission(destination.GrantId))
                return KernelResult<bool>.Fail(KernelError.PlatformBindingDraining, "A new copy grant cycle is in flight.");
                KernelResult exact = ValidateDmaGrantIdentity(source, sourceSubject);
            if (!exact.IsSuccess) return KernelResult<bool>.Fail(exact.Error, exact.Message!);
            exact = ValidateDmaGrantIdentity(destination, destinationSubject);
            if (!exact.IsSuccess) return KernelResult<bool>.Fail(exact.Error, exact.Message!);
            if (source.GrantId == destination.GrantId || sourceGrant.CopyPeerGrantId != destination.GrantId || destinationGrant.CopyPeerGrantId != source.GrantId ||
                sourceRecord.CopySubmissionId is not { Value: > 0 } copy || destinationRecord.CopySubmissionId != copy ||
                sourceRecord.CopyGeneration is not { Value: > 0 } generation || destinationRecord.CopyGeneration != generation ||
                sourceRecord.CopyVisibilityEvidence is not { IsSatisfied: true } || destinationRecord.CopyVisibilityEvidence is not { IsSatisfied: true } ||
                sourceRecord.KnownCompletionEpoch != epoch || destinationRecord.KnownCompletionEpoch != epoch ||
                sourceRecord.SemanticBinding is not { } binding || binding.MutationGeneration != sourceMutation ||
                !ExactPrepared(source, sourcePrepare, sourceRecord, sourceGrant) || !ExactPrepared(destination, destinationPrepare, destinationRecord, destinationGrant))
                return KernelResult<bool>.Fail(KernelError.StaleGeneration, "Completed copy cleanup requires the original pair/cycles/mutation/source tuple.");
            return KernelResult<bool>.Ok(true); // Cleanup fact, never new effect admission.
        }
        bool ExactPrepared(PlatformDmaGrant grant, PlatformDmaPrepareEvidence prepare, DmaSubmissionRecord record, DmaGrantRecord owner) =>
            prepare.IsSatisfied && prepare.GrantId == grant.GrantId && prepare.GrantGeneration == grant.Generation &&
            prepare.Direction == grant.Direction && prepare.Cycle == record.Submission.PreparedCycle &&
            record.Submission.GrantId == grant.GrantId && record.Submission.GrantGeneration == grant.Generation &&
            (owner.PlatformClosed || _dmaVisibilityStates.TryGetValue(grant.GrantId, out DmaVisibilityState? cycle) && cycle.LocalCycle == prepare.Cycle && cycle.Consumed);
    }
    internal KernelResult<PlatformDmaCopyClosureObservation> ObserveDmaCopyClosure(
        PlatformDmaGrant sourceGrant, PlatformDomainIdentity sourceSubject,
        PlatformDmaGrant destinationGrant, PlatformDomainIdentity destinationSubject)
    {
        lock (_dmaCompletionGate)
        {
            KernelResult sourceIdentity = ValidateDmaGrantIdentity(sourceGrant, sourceSubject);
            if (!sourceIdentity.IsSuccess)
                return KernelResult<PlatformDmaCopyClosureObservation>.Fail(
                    sourceIdentity.Error, sourceIdentity.Message!);
            KernelResult destinationIdentity = ValidateDmaGrantIdentity(destinationGrant, destinationSubject);
            if (!destinationIdentity.IsSuccess)
                return KernelResult<PlatformDmaCopyClosureObservation>.Fail(
                    destinationIdentity.Error, destinationIdentity.Message!);
            DmaGrantRecord source = _dmaGrants[sourceGrant.GrantId];
            DmaGrantRecord destination = _dmaGrants[destinationGrant.GrantId];
            if (sourceGrant.GrantId == destinationGrant.GrantId ||
                sourceGrant.Direction != PlatformDmaDirection.DeviceReadsMemory ||
                destinationGrant.Direction != PlatformDmaDirection.DeviceWritesMemory ||
                source.CopySubmissionId is not { Value: > 0 } copyId ||
                source.CopyGeneration is not { Value: > 0 } copyGeneration ||
                destination.CopySubmissionId != copyId ||
                destination.CopyGeneration != copyGeneration ||
                source.CopyPeerGrantId != destinationGrant.GrantId ||
                destination.CopyPeerGrantId != sourceGrant.GrantId)
                return KernelResult<PlatformDmaCopyClosureObservation>.Fail(KernelError.PlatformBindingNotFound,
                    "The exact grants have no retained atomic-copy pair correlation.");
            return KernelResult<PlatformDmaCopyClosureObservation>.Ok(new(copyId,
                copyGeneration, sourceGrant, destinationGrant, source.PlatformClosed,
                destination.PlatformClosed, _dmaSubmissionFaultPins.Contains(sourceGrant.GrantId),
                _dmaSubmissionFaultPins.Contains(destinationGrant.GrantId)));
        }
    }

    internal KernelResult<PlatformDmaCopyPairSubmission> GetActiveDmaCopyPairBound(
        PlatformDmaGrant sourceGrant,
        PlatformDomainIdentity sourceSubject,
        PlatformDmaGrant destinationGrant,
        PlatformDomainIdentity destinationSubject)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
        {
                    KernelResult sourceValidation = ValidateDmaGrant(sourceGrant, sourceSubject);
            if (!sourceValidation.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(
                    sourceValidation.Error, sourceValidation.Message!);
                    KernelResult destinationValidation = ValidateDmaGrant(destinationGrant, destinationSubject);
            if (!destinationValidation.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(
                    destinationValidation.Error, destinationValidation.Message!);
            if (!_activeDmaSubmissions.TryGetValue(sourceGrant.GrantId, out DmaSubmissionRecord? source) ||
                !_activeDmaSubmissions.TryGetValue(destinationGrant.GrantId, out DmaSubmissionRecord? destination))
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(
                    KernelError.PlatformBindingNotFound,
                    "Both exact DMA copy legs must remain active to resume movement.");
            if (source.Submission.Direction != PlatformDmaDirection.DeviceReadsMemory ||
                destination.Submission.Direction != PlatformDmaDirection.DeviceWritesMemory ||
                source.Submission.Range.Length != destination.Submission.Range.Length ||
                source.CopySubmissionId is not { Value: > 0 } sourceCopyId ||
                destination.CopySubmissionId != sourceCopyId ||
                source.CopyGeneration is not { Value: > 0 } sourceCopyGeneration ||
                destination.CopyGeneration != sourceCopyGeneration ||
                source.SemanticBinding is not { } sourceBinding ||
                destination.SemanticBinding is not { } destinationBinding)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(
                    KernelError.PlatformFaulted,
                    "Active DMA state is not the exact bound read/write copy pair.");
            return KernelResult<PlatformDmaCopyPairSubmission>.Ok(new(
                source.Submission, destination.Submission, sourceBinding, destinationBinding));
        }
    }

    internal KernelResult<PlatformDmaCopyPairSubmission> SubmitDmaCopyPairBound(
        PlatformDmaGrant sourceGrant, PlatformDmaPrepareEvidence sourcePrepare,
        PlatformDomainIdentity sourceSubject, ulong sourceMutationGeneration,
        PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare,
        PlatformDomainIdentity destinationSubject, ulong destinationMutationGeneration,
        Func<Func<KernelResult<PlatformDmaCopyPairSubmission>>, KernelResult<PlatformDmaCopyPairSubmission>> ownerCommit,
        ISemanticTraceSinkV1? sourceSink, ISemanticTraceSinkV1? destinationSink,
        out DmaTraceObserver? sourceTrace, out DmaTraceObserver? destinationTrace,
        PlatformDmaCopyBacking? backing = null, Action? effectBoundary = null, Action? noEffectBoundary = null)
    {
        sourceTrace = destinationTrace = null;
        DmaTraceObserver? sourceObservation = null, destinationObservation = null;
        bool invoked = false;
        PlatformProviderIncarnation observedSource = default(PlatformProviderIncarnation);
        DmaGrantRecord sourceRecord, destinationRecord;
        CopyLegState sourceState, destinationState;
        IPlatformDmaCopySubmissionProvider provider;
        PlatformBackendEpoch epoch;
        PlatformProviderDmaCopySubmitRequest request = default;
        PlatformDmaCopyPairSubmission pair = default;
        DmaSubmissionRecord? acceptedSource = null, acceptedDestination = null;
        OriginalCopyInvocation? originalInvocation = null;
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
        {
                    KernelResult<CopyLegState> source = ValidateCopyLeg(sourceGrant, sourcePrepare, sourceSubject,
                sourceMutationGeneration, PlatformDmaDirection.DeviceReadsMemory);
            if (!source.IsSuccess) return Fail(source.Error, source.Message!);
                    KernelResult<CopyLegState> destination = ValidateCopyLeg(destinationGrant, destinationPrepare, destinationSubject,
                destinationMutationGeneration, PlatformDmaDirection.DeviceWritesMemory);
            if (!destination.IsSuccess) return Fail(destination.Error, destination.Message!);
            if (sourceGrant.GrantId == destinationGrant.GrantId || sourceGrant.Range.Length != destinationGrant.Range.Length)
                return Fail(KernelError.PlatformDenied, "DMA copy requires distinct equal-length source and destination grants.");
            if (!_featureManifest.Supports(PlatformFeatureFamily.DmaMapping,
                PlatformDmaCopySubmissionContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission) ||
                _provider is not IPlatformDmaCopySubmissionProvider copyProvider)
                return Fail(KernelError.PlatformUnsupported, "The platform provider does not advertise atomic bounded DMA copy submission.");
            if (_provider is not IPlatformProviderIncarnationSource)
                return Fail(KernelError.PlatformUnsupported, "Bound copy requires a live provider incarnation source.");
            sourceRecord = _dmaGrants[sourceGrant.GrantId];
            destinationRecord = _dmaGrants[destinationGrant.GrantId];
            sourceState = source.Value!;
            destinationState = destination.Value!;
            provider = copyProvider;
            if (backing is not null && (provider is not IPlatformDmaCopyBackingProvider || backing.Source.Length != sourceGrant.Range.Length || backing.Destination.Length != destinationGrant.Range.Length))
                return Fail(KernelError.PlatformUnsupported,"DMA copy provider does not admit the exact reserved backing.");
            epoch = BackendEpoch;
            sourceRecord.SubmissionInFlight = destinationRecord.SubmissionInFlight = true;
        }
        try
        {
            PlatformProviderIncarnation incarnation = CurrentProviderIncarnation(); // External source, outside owner locks.
            KernelResult<PlatformDmaCopyPairSubmission> admission = ownerCommit(() =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dsc1Gate)
                lock (_dmaCompletionGate)
                {
                            KernelResult exact = ValidateOriginal(incarnation, false);
                    if (!exact.IsSuccess) return Fail(exact.Error, exact.Message!);
                            KernelResult sourceMapping = ValidateDmaMappingUseAdmissionLocked(sourceGrant);
                    if (!sourceMapping.IsSuccess) return Fail(sourceMapping.Error, sourceMapping.Message!);
                            KernelResult destinationMapping = ValidateDmaMappingUseAdmissionLocked(destinationGrant);
                    if (!destinationMapping.IsSuccess) return Fail(destinationMapping.Error, destinationMapping.Message!);
                    if (_nextDmaOperationId is 0 or ulong.MaxValue)
                        return Fail(KernelError.CapacityExhausted, "Two local DMA operation identities are unavailable.");
                    try
                    {
                        _activeDmaSubmissions.EnsureCapacity(_activeDmaSubmissions.Count + 2);
                        _dmaSubmissionFaultPins.EnsureCapacity(_dmaSubmissionFaultPins.Count + 2);
                    }
                    catch (OutOfMemoryException)
                    {
                        return Fail(KernelError.CapacityExhausted, "Atomic copy tracking capacity exhausted before invocation.");
                    }
                            DmaExecutionBindingV1 sourceBinding = CreateDmaExecutionBinding(sourceGrant, sourceSubject,
                        sourceMutationGeneration, incarnation, DmaEffectStateV1.Admitted);
                            DmaExecutionBindingV1 destinationBinding = CreateDmaExecutionBinding(destinationGrant, destinationSubject,
                        destinationMutationGeneration, incarnation, DmaEffectStateV1.Admitted);
                    request = new(new(sourceRecord.ProviderGrant, sourceState.ProviderCycle),
                        new(destinationRecord.ProviderGrant, destinationState.ProviderCycle), sourceBinding, destinationBinding);
                            PlatformAuthorityResult valid = PlatformDmaCopySubmissionContract.ValidateRequest(request);
                    if (!valid.IsSuccess) return Fail(KernelError.PlatformFaulted, valid.Message!);
                    if (backing is not null && provider is IPlatformOriginalDmaCopyProvider)
                    {
                        if (sourceRecord.CopiesIssued == ulong.MaxValue || destinationRecord.CopiesIssued == ulong.MaxValue)
                            return Fail(KernelError.CapacityExhausted,"Original copy history exhausted before effect.");
                        sourceRecord.CopyHistory.EnsureCapacity(sourceRecord.CopyHistory.Count + 1); destinationRecord.CopyHistory.EnsureCapacity(destinationRecord.CopyHistory.Count + 1);
                        originalInvocation = new(new(request,backing),sourceRecord,destinationRecord) { Epoch = epoch,SourceCycle = sourceState.Visibility,DestinationCycle = destinationState.Visibility };
                        originalInvocation.EstablishSelf();
                        sourceRecord.CopyHistory.Add(originalInvocation); destinationRecord.CopyHistory.Add(originalInvocation); sourceRecord.CopiesIssued++; destinationRecord.CopiesIssued++;
                        sourceRecord.OriginalCopy = destinationRecord.OriginalCopy = originalInvocation;
                    }
                    pair = new(LocalSubmission(sourceGrant, sourcePrepare), LocalSubmission(destinationGrant, destinationPrepare),
                        sourceBinding with { EffectState = DmaEffectStateV1.EffectPossible },
                        destinationBinding with { EffectState = DmaEffectStateV1.EffectPossible });
                    return KernelResult<PlatformDmaCopyPairSubmission>.Ok(pair);
                }
            });
            if (!admission.IsSuccess) return admission;
            effectBoundary?.Invoke(); // Trusted runtime retention, outside commit locks.
            invoked = true;
            PlatformAuthorityResult<PlatformProviderDmaCopySubmission> reply;
            if (originalInvocation is { } original && provider is IPlatformOriginalDmaCopyProvider originalProvider)
            {
                original.Invoked = true;
                try { reply = originalProvider.SubmitDmaCopy(original.Request); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { reply = PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.Faulted,"Original copy reply lost."); }
                original.Raw = reply; // Retain before pure lookup/source callbacks.
                if (!reply.IsSuccess && reply.Status == PlatformAuthorityStatus.Faulted)
                {
                    PlatformAuthorityResult<PlatformOriginalDmaCopyResolution> resolution = originalProvider.ResolveDmaCopy(original.Request);
                    original.Resolution = resolution;
                    if (resolution.IsSuccess && ReferenceEquals(resolution.Value.Request,original.Request) &&
                        resolution.Value.NoEffectGuaranteed != resolution.Value.Submission.HasValue)
                        reply = resolution.Value.NoEffectGuaranteed ? PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.NotAccepted,"Exact original copy no-effect.") :
                            PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Ok(resolution.Value.Submission!.Value);
                }
            }
            else reply = backing is null ? provider.SubmitDmaCopy(request) :
                ((IPlatformDmaCopyBackingProvider)provider).SubmitDmaCopy(request,backing); // Provider effect boundary.
            if (!reply.IsSuccess)
            {
                PlatformProviderIncarnation refusedSource = CurrentProviderIncarnation();
                observedSource = refusedSource;
                lock (_secureDomainLifecycleGate)
                lock (_dmaCompletionGate)
                {
                    if (BackendEpoch != epoch || refusedSource.Value == 0 || refusedSource != incarnation)
                    {
                        PinBoth();
                        return Fail(KernelError.PlatformFaulted, "Copy refusal lost original source continuity; both mappings pinned.");
                    }
                    if (reply.Status != PlatformAuthorityStatus.NotAccepted) PinBoth();
                }
                if (reply.Status == PlatformAuthorityStatus.NotAccepted) noEffectBoundary?.Invoke();
                return FromProviderFailure<PlatformDmaCopyPairSubmission>(reply.Status, reply.Message);
            }
            PlatformProviderDmaCopySubmission providerSubmission = reply.Value!;
            PlatformAuthorityResult validation = PlatformDmaCopySubmissionContract.ValidateSubmission(request, providerSubmission);
            if (!validation.IsSuccess)
            {
                lock (_dmaCompletionGate) PinBoth();
                return Fail(KernelError.PlatformFaulted, validation.Message!);
            }
            // Retain the exact accepted pair BEFORE late source query or local publication refusal.
            lock (_dmaCompletionGate)
            {
                acceptedSource = new(pair.Source, providerSubmission.SourceSubmission, pair.SourceBinding,
                    providerSubmission.CopySubmissionId, providerSubmission.Generation);
                acceptedDestination = new(pair.Destination, providerSubmission.DestinationSubmission, pair.DestinationBinding,
                    providerSubmission.CopySubmissionId, providerSubmission.Generation);
                acceptedSource.EstablishSelf(); acceptedDestination.EstablishSelf();
                if (originalInvocation is { } acceptedOriginal) { acceptedOriginal.AcceptedSource = acceptedSource; acceptedOriginal.AcceptedDestination = acceptedDestination; }
                _activeDmaSubmissions.Add(sourceGrant.GrantId, acceptedSource);
                _activeDmaSubmissions.Add(destinationGrant.GrantId, acceptedDestination);
                sourceRecord.CopySubmissionId = destinationRecord.CopySubmissionId = providerSubmission.CopySubmissionId;
                sourceRecord.CopyGeneration = destinationRecord.CopyGeneration = providerSubmission.Generation;
                sourceRecord.CopyPeerGrantId = destinationGrant.GrantId;
                destinationRecord.CopyPeerGrantId = sourceGrant.GrantId;
                sourceState.Visibility.Consumed = destinationState.Visibility.Consumed = true;
                if (sourceSink is not null) RegisterDmaTraceLocked(acceptedSource, sourceSink, epoch);
                if (destinationSink is not null) RegisterDmaTraceLocked(acceptedDestination, destinationSink, epoch);
                sourceObservation = acceptedSource.Trace;
                destinationObservation = acceptedDestination.Trace;
            }
            PlatformProviderIncarnation observed = CurrentProviderIncarnation();
            observedSource = observed;
            KernelResult<PlatformDmaCopyPairSubmission> publication = ownerCommit(() =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dsc1Gate)
                lock (_dmaCompletionGate)
                {
                            KernelResult exact = ValidateOriginal(observed, true);
                    if (exact.IsSuccess && originalInvocation is { } publishedOriginal) publishedOriginal.Published = true;
                    return exact.IsSuccess ? KernelResult<PlatformDmaCopyPairSubmission>.Ok(pair) : Fail(exact.Error, exact.Message!);
                }
            });
            if (!publication.IsSuccess) lock (_dmaCompletionGate) PinBoth();
            return publication;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_dmaCompletionGate) PinBoth();
            return Fail(KernelError.PlatformFaulted, $"Copy source/effect/publication failed; both original mappings pinned: {exception.GetType().Name}.");
        }
        finally
        {
            lock (_dmaCompletionGate)
            {
                if (invoked && (HasFaultPinnedDmaSubmission(sourceGrant.GrantId) || HasFaultPinnedDmaSubmission(destinationGrant.GrantId)))
                {
                    sourceObservation = ObserveRefusal(acceptedSource, sourceSink, pair.SourceBinding, pair.Source.OperationId, sourceRecord.ProviderIncarnation);
                    destinationObservation = ObserveRefusal(acceptedDestination, destinationSink, pair.DestinationBinding, pair.Destination.OperationId, destinationRecord.ProviderIncarnation);
                }
                sourceRecord.SubmissionInFlight = destinationRecord.SubmissionInFlight = false;
                sourceTrace = sourceObservation;
                destinationTrace = destinationObservation;
            }
        }

        DmaTraceObserver? ObserveRefusal(DmaSubmissionRecord? accepted, ISemanticTraceSinkV1? sink,
            DmaExecutionBindingV1 binding, PlatformDmaOperationId operationId, PlatformProviderIncarnation original)
        {
            if (sink is null) return null;
            if (accepted?.Trace is { } existing)
            {
                PlatformProviderIncarnation? sourceChange = observedSource.Value > 0 && observedSource.Value != existing.ProviderGeneration ? observedSource : null;
                PlatformBackendEpoch? epochChange = BackendEpoch.Value != existing.BackendEpoch ? BackendEpoch : null;
                if (sourceChange is not null || epochChange is not null)
                    QueueDmaTraceLocked(accepted, SemanticTraceEventKindV1.GenerationChanged, sourceChange, epochChange);
                if (!existing.QuarantineQueued) QueueDmaTraceLocked(accepted, SemanticTraceEventKindV1.Quarantined);
                return existing;
            }
            DmaTraceObserver? observer = CreateAmbiguousDmaSubmitTraceLocked(sink, binding, operationId,
                original, epoch, observedSource.Value > 0 ? observedSource : original, BackendEpoch);
            if (accepted is not null) accepted.Trace = observer;
            return observer;
        }
        KernelResult ValidateOriginal(PlatformProviderIncarnation incarnation, bool publishing)
        {
            if (BackendEpoch != epoch || HasFaultPinnedDmaSubmission(sourceGrant.GrantId) || HasFaultPinnedDmaSubmission(destinationGrant.GrantId))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original copy backend/effect continuity lost.");
            if (incarnation.Value == 0 || sourceRecord.ProviderIncarnation != incarnation || destinationRecord.ProviderIncarnation != incarnation)
                return KernelResult.Fail(publishing ? KernelError.PlatformFaulted : KernelError.StaleGeneration, "Original copy provider source changed.");
            KernelResult source = ValidateLeg(sourceGrant, sourceSubject, sourceRecord, sourceState, acceptedSource, publishing);
            return source.IsSuccess ? ValidateLeg(destinationGrant, destinationSubject, destinationRecord, destinationState, acceptedDestination, publishing) : source;
        }
        KernelResult ValidateLeg(PlatformDmaGrant grant, PlatformDomainIdentity subject, DmaGrantRecord original,
            CopyLegState state, DmaSubmissionRecord? accepted, bool publishing)
        {
            if (!_dmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? current) || !ReferenceEquals(current, original) ||
                !original.SubmissionInFlight || original.PlatformClosed ||
                !_dmaVisibilityStates.TryGetValue(grant.GrantId, out DmaVisibilityState? visibility) || !ReferenceEquals(visibility, state.Visibility) ||
                visibility.Acquired || visibility.Consumed != publishing ||
                (publishing ? !_activeDmaSubmissions.TryGetValue(grant.GrantId, out DmaSubmissionRecord? active) || !ReferenceEquals(active, accepted)
                    : HasActiveDmaSubmission(grant.GrantId)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original copy grant/cycle/lifetime changed.");
            KernelResult device = ValidateDeviceLease(grant.DeviceLease, subject);
            return device.IsSuccess ? ValidateExactMapping(grant.Mapping, subject) : device;
        }
        void PinBoth()
        {
            if (originalInvocation is { Invoked: true } original &&
                (acceptedSource is null && acceptedDestination is null || ReferenceEquals(original.AcceptedSource,acceptedSource) && ReferenceEquals(original.AcceptedDestination,acceptedDestination)) &&
                BackendEpoch == epoch && sourceRecord.SourceRevision == original.SourceRevision && destinationRecord.SourceRevision == original.DestinationRevision)
            { original.Uncertain = true; return; }
            _dmaSubmissionFaultPins.Add(sourceGrant.GrantId);
            _dmaSubmissionFaultPins.Add(destinationGrant.GrantId);
        }
        static KernelResult<PlatformDmaCopyPairSubmission> Fail(KernelError error, string message) =>
            KernelResult<PlatformDmaCopyPairSubmission>.Fail(error, message);
    }
    private KernelResult<CopyLegState> ValidateCopyLeg(
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepare,
        PlatformDomainIdentity subject,
        ulong mutationGeneration,
        PlatformDmaDirection requiredDirection)
    {

        KernelResult validation = ValidateDmaGrant(grant, subject);
        if (!validation.IsSuccess) return KernelResult<CopyLegState>.Fail(validation.Error, validation.Message!);
        if (grant.Direction != requiredDirection || mutationGeneration == 0)
            return KernelResult<CopyLegState>.Fail(KernelError.PlatformDenied,
                "DMA copy leg direction or Region mutation generation is invalid.");
        if (HasFaultPinnedDmaSubmission(grant.GrantId))
            return KernelResult<CopyLegState>.Fail(KernelError.PlatformFaulted,
                "The exact DMA copy grant is fault-pinned by an ambiguous external effect.");
        if (HasActiveDmaSubmission(grant.GrantId))
            return KernelResult<CopyLegState>.Fail(KernelError.PlatformBindingActive,
                "The exact DMA copy grant already has active submission state.");
        if (!prepare.IsSatisfied || prepare.GrantId != grant.GrantId ||
            prepare.GrantGeneration != grant.Generation || prepare.Direction != grant.Direction ||
            !_dmaVisibilityStates.TryGetValue(grant.GrantId, out DmaVisibilityState? visibility) ||
            visibility.LocalCycle != prepare.Cycle || visibility.Acquired || visibility.Consumed)
            return KernelResult<CopyLegState>.Fail(KernelError.PlatformDenied,
                "DMA copy requires the exact current prepared and unconsumed visibility cycle.");
        return KernelResult<CopyLegState>.Ok(new(visibility, visibility.ProviderCycle));
    }

    private PlatformDmaSubmission LocalSubmission(
        PlatformDmaGrant grant, PlatformDmaPrepareEvidence prepare) => new(
            new(NextLocalDmaOperationId()), new(1), grant.GrantId, grant.Generation,
            prepare.Cycle, grant.Range, grant.Direction);

    private sealed record CopyLegState(
        DmaVisibilityState Visibility,
        PlatformProviderDmaVisibilityCycle ProviderCycle);
}
