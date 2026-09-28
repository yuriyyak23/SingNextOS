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
    internal KernelResult<PlatformDmaCopyClosureObservation> ObserveDmaCopyClosure(
        PlatformDmaGrant sourceGrant, PlatformDomainIdentity sourceSubject,
        PlatformDmaGrant destinationGrant, PlatformDomainIdentity destinationSubject)
    {
        lock (_dmaCompletionGate)
        {
            var sourceIdentity = ValidateDmaGrantIdentity(sourceGrant, sourceSubject);
            if (!sourceIdentity.IsSuccess)
                return KernelResult<PlatformDmaCopyClosureObservation>.Fail(
                    sourceIdentity.Error, sourceIdentity.Message!);
            var destinationIdentity = ValidateDmaGrantIdentity(destinationGrant, destinationSubject);
            if (!destinationIdentity.IsSuccess)
                return KernelResult<PlatformDmaCopyClosureObservation>.Fail(
                    destinationIdentity.Error, destinationIdentity.Message!);
            var source = _dmaGrants[sourceGrant.GrantId];
            var destination = _dmaGrants[destinationGrant.GrantId];
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
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
        {
            var sourceValidation = ValidateDmaGrant(sourceGrant, sourceSubject);
            if (!sourceValidation.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(
                    sourceValidation.Error, sourceValidation.Message!);
            var destinationValidation = ValidateDmaGrant(destinationGrant, destinationSubject);
            if (!destinationValidation.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(
                    destinationValidation.Error, destinationValidation.Message!);
            if (!_activeDmaSubmissions.TryGetValue(sourceGrant.GrantId, out var source) ||
                !_activeDmaSubmissions.TryGetValue(destinationGrant.GrantId, out var destination))
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
        PlatformDmaGrant sourceGrant,
        PlatformDmaPrepareEvidence sourcePrepare,
        PlatformDomainIdentity sourceSubject,
        ulong sourceMutationGeneration,
        PlatformDmaGrant destinationGrant,
        PlatformDmaPrepareEvidence destinationPrepare,
        PlatformDomainIdentity destinationSubject,
        ulong destinationMutationGeneration)
    {
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
        {
            var source = ValidateCopyLeg(sourceGrant, sourcePrepare, sourceSubject,
                sourceMutationGeneration, PlatformDmaDirection.DeviceReadsMemory);
            if (!source.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(source.Error, source.Message!);
            var destination = ValidateCopyLeg(destinationGrant, destinationPrepare,
                destinationSubject, destinationMutationGeneration, PlatformDmaDirection.DeviceWritesMemory);
            if (!destination.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(destination.Error, destination.Message!);
            if (sourceGrant.GrantId == destinationGrant.GrantId ||
                sourceGrant.Range.Length != destinationGrant.Range.Length)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformDenied,
                    "DMA copy requires distinct equal-length source and destination grants.");
            if (!_featureManifest.Supports(PlatformFeatureFamily.DmaMapping,
                    PlatformDmaCopySubmissionContract.ContractVersion,
                    PlatformFeatureAvailability.RuntimeAdmission) ||
                _provider is not IPlatformDmaCopySubmissionProvider copyProvider)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformUnsupported,
                    "The platform provider does not advertise atomic bounded DMA copy submission.");
            if (_provider is not IPlatformProviderIncarnationSource)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformUnsupported,
                    "Bound DMA copy requires a live provider incarnation source; the legacy fallback cannot validate generation drift.");
            if (_nextDmaOperationId is 0 or ulong.MaxValue)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.CapacityExhausted,
                    "Two local DMA operation identities are not available for atomic copy tracking.");
            try
            {
                _activeDmaSubmissions.EnsureCapacity(_activeDmaSubmissions.Count + 2);
                _dmaSubmissionFaultPins.EnsureCapacity(_dmaSubmissionFaultPins.Count + 2);
            }
            catch (OutOfMemoryException)
            {
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.CapacityExhausted,
                    "Atomic DMA copy tracking capacity is exhausted before provider admission.");
            }

            var sourceMapping = ValidateDmaMappingUseAdmissionLocked(sourceGrant);
            if (!sourceMapping.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(sourceMapping.Error, sourceMapping.Message!);
            var destinationMapping = ValidateDmaMappingUseAdmissionLocked(destinationGrant);
            if (!destinationMapping.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(destinationMapping.Error, destinationMapping.Message!);

            var incarnation = CurrentProviderIncarnation();
            var backendEpoch = BackendEpoch;
            var sourceRecord = _dmaGrants[sourceGrant.GrantId];
            var destinationRecord = _dmaGrants[destinationGrant.GrantId];
            if (incarnation.Value == 0 || sourceRecord.ProviderIncarnation != incarnation ||
                destinationRecord.ProviderIncarnation != incarnation)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.StaleGeneration,
                    "The DMA provider restarted after one of the exact copy grants was admitted.");
            var sourceBinding = CreateDmaExecutionBinding(sourceGrant, sourceSubject,
                sourceMutationGeneration, incarnation, DmaEffectStateV1.Admitted);
            var destinationBinding = CreateDmaExecutionBinding(destinationGrant, destinationSubject,
                destinationMutationGeneration, incarnation, DmaEffectStateV1.Admitted);
            var request = new PlatformProviderDmaCopySubmitRequest(
                new(sourceRecord.ProviderGrant, source.Value!.ProviderCycle),
                new(destinationRecord.ProviderGrant, destination.Value!.ProviderCycle),
                sourceBinding, destinationBinding);
            var requestValidation = PlatformDmaCopySubmissionContract.ValidateRequest(request);
            if (!requestValidation.IsSuccess)
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformFaulted,
                    requestValidation.Message ?? "The bridge constructed an invalid atomic DMA copy request.");

            PlatformAuthorityResult<PlatformProviderDmaCopySubmission> providerResult;
            try { providerResult = copyProvider.SubmitDmaCopy(request); }
            catch (Exception exception)
            {
                PinBoth();
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformFaulted,
                    $"The atomic DMA copy provider threw; both exact mappings remain pinned: {exception.Message}");
            }
            if (CurrentProviderIncarnation() != incarnation || BackendEpoch != backendEpoch)
            {
                PinBoth();
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformFaulted,
                    "The DMA provider incarnation or local backend epoch changed during atomic copy acceptance; both mappings remain pinned.");
            }
            if (!providerResult.IsSuccess)
            {
                if (providerResult.Status != PlatformAuthorityStatus.NotAccepted) PinBoth();
                return FromProviderFailure<PlatformDmaCopyPairSubmission>(
                    providerResult.Status, providerResult.Message);
            }
            var providerSubmission = providerResult.Value!;
            var providerValidation = PlatformDmaCopySubmissionContract.ValidateSubmission(request, providerSubmission);
            if (!providerValidation.IsSuccess)
            {
                PinBoth();
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformFaulted,
                    providerValidation.Message ?? "The provider returned malformed atomic DMA copy evidence.");
            }

            var sourceSubmission = LocalSubmission(sourceGrant, sourcePrepare);
            var destinationSubmission = LocalSubmission(destinationGrant, destinationPrepare);
            try
            {
                _activeDmaSubmissions.Add(sourceGrant.GrantId, new(sourceSubmission,
                    providerSubmission.SourceSubmission,
                    sourceBinding with { EffectState = DmaEffectStateV1.EffectPossible },
                    providerSubmission.CopySubmissionId, providerSubmission.Generation));
                _activeDmaSubmissions.Add(destinationGrant.GrantId, new(destinationSubmission,
                    providerSubmission.DestinationSubmission,
                    destinationBinding with { EffectState = DmaEffectStateV1.EffectPossible },
                    providerSubmission.CopySubmissionId, providerSubmission.Generation));
                sourceRecord.CopySubmissionId = destinationRecord.CopySubmissionId =
                    providerSubmission.CopySubmissionId;
                sourceRecord.CopyGeneration = destinationRecord.CopyGeneration =
                    providerSubmission.Generation;
                sourceRecord.CopyPeerGrantId = destinationGrant.GrantId;
                destinationRecord.CopyPeerGrantId = sourceGrant.GrantId;
                source.Value!.Visibility.Consumed = true;
                destination.Value!.Visibility.Consumed = true;
            }
            catch (Exception exception) when (exception is OutOfMemoryException or InvalidOperationException)
            {
                PinBoth();
                return KernelResult<PlatformDmaCopyPairSubmission>.Fail(KernelError.PlatformFaulted,
                    $"Atomic provider acceptance could not be tracked locally; both mappings remain pinned: {exception.Message}");
            }
            return KernelResult<PlatformDmaCopyPairSubmission>.Ok(new(sourceSubmission,
                destinationSubmission,
                sourceBinding with { EffectState = DmaEffectStateV1.EffectPossible },
                destinationBinding with { EffectState = DmaEffectStateV1.EffectPossible }));

            void PinBoth()
            {
                _dmaSubmissionFaultPins.Add(sourceGrant.GrantId);
                _dmaSubmissionFaultPins.Add(destinationGrant.GrantId);
            }
        }
    }

    private KernelResult<CopyLegState> ValidateCopyLeg(
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepare,
        PlatformDomainIdentity subject,
        ulong mutationGeneration,
        PlatformDmaDirection requiredDirection)
    {
        var validation = ValidateDmaGrant(grant, subject);
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
            !_dmaVisibilityStates.TryGetValue(grant.GrantId, out var visibility) ||
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
