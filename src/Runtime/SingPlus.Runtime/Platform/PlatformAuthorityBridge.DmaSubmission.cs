using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDmaOperationId(ulong Value);
public readonly record struct PlatformDmaOperationGeneration(ulong Value);

public readonly record struct PlatformDmaSubmission(
    PlatformDmaOperationId OperationId,
    PlatformDmaOperationGeneration Generation,
    PlatformDmaGrantId GrantId,
    PlatformDmaGrantGeneration GrantGeneration,
    PlatformDmaVisibilityCycle PreparedCycle,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction);

public sealed partial class PlatformAuthorityBridge
{
    private sealed class DmaSubmissionRecord(
        PlatformDmaSubmission submission,
        PlatformProviderDmaSubmission providerSubmission)
    {
        public PlatformDmaSubmission Submission { get; } = submission;
        public PlatformProviderDmaSubmission ProviderSubmission { get; } = providerSubmission;
        public bool CompletionObservationInFlight { get; set; }
        public bool CompletionProven { get; set; }
    }

    private readonly object _dmaCompletionGate = new();
    private readonly Dictionary<PlatformDmaGrantId, DmaSubmissionRecord> _activeDmaSubmissions = [];
    private readonly HashSet<PlatformDmaGrantId> _dmaSubmissionFaultPins = [];
    private ulong _nextDmaOperationId = 1;

    internal KernelResult<PlatformDmaSubmission> SubmitDmaGrant(
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence,
        PlatformDomainIdentity expectedSubject)
    {
        // Keep the bridge lock order stable: DSC1 state precedes DMA state.
        // RuntimeKernel holds its platform-memory-use gate outside both.
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
            return SubmitDmaGrantLocked(grant, prepareEvidence, expectedSubject);
    }

    private KernelResult<PlatformDmaSubmission> SubmitDmaGrantLocked(
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDmaGrant(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                validation.Error,
                validation.Message!);
        }

        if (HasFaultPinnedDmaSubmission(grant.GrantId))
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformFaulted,
                "DMA submission state is fault-pinned because an external effect may have been accepted ambiguously.");
        }

        if (HasActiveDmaSubmission(grant.GrantId))
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformBindingActive,
                "The exact DMA grant already has a submitted operation whose post-submit lifecycle is not closed.");
        }

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaSubmissionContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission))
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise bounded DMA submission contract v3.");
        }

        if (_provider is not IPlatformDmaSubmissionProvider submissionProvider)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose bounded DMA submission.");
        }

        if (!prepareEvidence.IsSatisfied)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformDenied,
                "DMA submission requires satisfied publication evidence for the exact prepared cycle.");
        }

        if (prepareEvidence.GrantId != grant.GrantId)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformDenied,
                "DMA prepare evidence belongs to a different local grant.");
        }

        if (prepareEvidence.GrantGeneration != grant.Generation)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.StaleGeneration,
                "DMA prepare evidence uses a stale local grant generation.");
        }

        if (prepareEvidence.Direction != grant.Direction)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformDenied,
                "DMA prepare evidence direction does not match the exact grant.");
        }

        if (!_dmaVisibilityStates.TryGetValue(grant.GrantId, out var visibilityState) ||
            visibilityState.LocalCycle.Value == 0)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformDenied,
                "The exact DMA grant has no prepared visibility cycle to submit.");
        }

        if (visibilityState.LocalCycle != prepareEvidence.Cycle)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformDenied,
                "DMA prepare evidence does not match the current exact visibility cycle.");
        }

        if (visibilityState.Acquired || visibilityState.Consumed)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformDenied,
                "The current DMA visibility cycle was already consumed and cannot be submitted.");
        }

        var providerGrant = _dmaGrants[grant.GrantId].ProviderGrant;
        var request = new PlatformProviderDmaSubmitRequest(
            providerGrant,
            visibilityState.ProviderCycle);
        var requestValidation = PlatformDmaSubmissionContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformFaulted,
                requestValidation.Message ?? "The bridge constructed malformed provider DMA submission state.");
        }

        if (_nextDmaOperationId == 0)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.CapacityExhausted,
                "Local DMA operation identity space is exhausted.");
        }

        try
        {
            _activeDmaSubmissions.EnsureCapacity(_activeDmaSubmissions.Count + 1);
            _dmaSubmissionFaultPins.EnsureCapacity(_dmaSubmissionFaultPins.Count + 1);
        }
        catch (OutOfMemoryException)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.CapacityExhausted,
                "Local DMA submission tracking capacity is exhausted before provider admission.");
        }

        // All exact grant/evidence checks precede the cross-mechanism query so
        // forged inputs cannot use the interlock as a mapping-use oracle.
        var mappingUse = ValidateDmaMappingUseAdmissionLocked(grant);
        if (!mappingUse.IsSuccess)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                mappingUse.Error,
                mappingUse.Message!);
        }

        var operationId = new PlatformDmaOperationId(NextLocalDmaOperationId());
        PlatformAuthorityResult<PlatformProviderDmaSubmission> providerResult;
        try
        {
            providerResult = submissionProvider.SubmitDma(request);
        }
        catch (Exception exception)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformFaulted,
                $"The DMA provider threw during submission; acceptance is ambiguous and the exact mapping remains pinned: {exception.Message}");
        }

        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status == PlatformAuthorityStatus.Faulted)
                _dmaSubmissionFaultPins.Add(grant.GrantId);

            return FromProviderFailure<PlatformDmaSubmission>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerSubmission = providerResult.Value!;
        var providerValidation = PlatformDmaSubmissionContract.ValidateSubmission(
            request,
            providerSubmission);
        if (!providerValidation.IsSuccess)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed DMA submission evidence.");
        }

        var submission = new PlatformDmaSubmission(
            operationId,
            new PlatformDmaOperationGeneration(1),
            grant.GrantId,
            grant.Generation,
            visibilityState.LocalCycle,
            grant.Range,
            grant.Direction);
        try
        {
            var record = new DmaSubmissionRecord(submission, providerSubmission);
            _activeDmaSubmissions.Add(grant.GrantId, record);
            visibilityState.Consumed = true;
        }
        catch (Exception exception) when (
            exception is OutOfMemoryException or InvalidOperationException)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformFaulted,
                $"The provider accepted DMA but exact local tracking failed; the mapping remains fault-pinned: {exception.Message}");
        }

        return KernelResult<PlatformDmaSubmission>.Ok(submission);
    }

    internal bool HasActiveDmaSubmission(PlatformDmaGrantId grantId)
    {
        lock (_dmaCompletionGate)
            return _activeDmaSubmissions.ContainsKey(grantId);
    }

    internal bool HasPendingDmaSubmission(PlatformDmaGrantId grantId)
    {
        lock (_dmaCompletionGate)
        {
            return _activeDmaSubmissions.TryGetValue(grantId, out var record) &&
                   !record.CompletionProven;
        }
    }

    internal bool HasCompletedDmaSubmission(PlatformDmaGrantId grantId)
    {
        lock (_dmaCompletionGate)
        {
            return _activeDmaSubmissions.TryGetValue(grantId, out var record) &&
                   record.CompletionProven;
        }
    }

    internal bool HasFaultPinnedDmaSubmission(PlatformDmaGrantId grantId)
    {
        lock (_dmaCompletionGate)
            return _dmaSubmissionFaultPins.Contains(grantId);
    }

    private ulong NextLocalDmaOperationId()
    {
        var value = _nextDmaOperationId;
        unchecked { _nextDmaOperationId++; }
        if (value == 0)
            throw new InvalidOperationException("Local DMA operation identity space is exhausted.");
        return value;
    }
}
