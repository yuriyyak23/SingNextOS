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

        var providerResult = submissionProvider.SubmitDma(request);
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
            new PlatformDmaOperationId(NextLocalDmaOperationId()),
            new PlatformDmaOperationGeneration(1),
            grant.GrantId,
            grant.Generation,
            visibilityState.LocalCycle,
            grant.Range,
            grant.Direction);
        visibilityState.Consumed = true;
        _activeDmaSubmissions.Add(
            grant.GrantId,
            new DmaSubmissionRecord(submission, providerSubmission));
        return KernelResult<PlatformDmaSubmission>.Ok(submission);
    }

    internal bool HasActiveDmaSubmission(PlatformDmaGrantId grantId) =>
        _activeDmaSubmissions.ContainsKey(grantId);

    internal bool HasPendingDmaSubmission(PlatformDmaGrantId grantId) =>
        _activeDmaSubmissions.TryGetValue(grantId, out var record) &&
        !record.CompletionProven;

    internal bool HasCompletedDmaSubmission(PlatformDmaGrantId grantId) =>
        _activeDmaSubmissions.TryGetValue(grantId, out var record) &&
        record.CompletionProven;

    internal bool HasFaultPinnedDmaSubmission(PlatformDmaGrantId grantId) =>
        _dmaSubmissionFaultPins.Contains(grantId);

    private ulong NextLocalDmaOperationId()
    {
        var value = _nextDmaOperationId;
        unchecked { _nextDmaOperationId++; }
        if (value == 0)
            throw new InvalidOperationException("Local DMA operation identity space is exhausted.");
        return value;
    }
}
