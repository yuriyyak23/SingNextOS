using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;
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
        PlatformProviderDmaSubmission providerSubmission,
        DmaExecutionBindingV1? semanticBinding = null,
        PlatformProviderDmaCopySubmissionId? copySubmissionId = null,
        PlatformProviderDmaCopySubmissionGeneration? copyGeneration = null)
    {
        public PlatformDmaSubmission Submission { get; } = submission;
        public PlatformProviderDmaSubmission ProviderSubmission { get; } = providerSubmission;
        public DmaExecutionBindingV1? SemanticBinding { get; } = semanticBinding;
        public PlatformProviderDmaCopySubmissionId? CopySubmissionId { get; } = copySubmissionId;
        public PlatformProviderDmaCopySubmissionGeneration? CopyGeneration { get; } = copyGeneration;
        public bool CompletionObservationInFlight { get; set; }
        public bool CompletionProven { get; set; }
        public PlatformDmaCompletionEvidence? CompletionEvidence { get; set; }
        public HashSet<ulong> PageFaultsInFlight { get; } = [];
        public HashSet<ulong> ResolvedPageFaults { get; } = [];
        public DmaTraceObserver? Trace { get; set; }
    }

    private readonly object _dmaCompletionGate = new();
    private readonly Dictionary<PlatformDmaGrantId, DmaSubmissionRecord> _activeDmaSubmissions = [];
    private readonly HashSet<PlatformDmaGrantId> _dmaSubmissionFaultPins = [];
    private ulong _nextDmaOperationId = 1;

    private void FaultPinDmaSubmissionLocked(
        PlatformDmaGrantId grantId, PlatformProviderIncarnation? changedIncarnation = null,
        PlatformBackendEpoch? backendEpoch = null)
    {
        // Grant closure can fail after exact completion/visibility removed the
        // pending submission. The grant still owns an ambiguous provider effect.
        var newlyPinned = _dmaSubmissionFaultPins.Add(grantId);
        if (!_activeDmaSubmissions.TryGetValue(grantId, out var record))
            return;
        if (backendEpoch is { Value: > 0 } epoch &&
            record.Trace is { } backendTrace && backendTrace.BackendEpoch != epoch.Value)
            QueueDmaTraceLocked(record, SemanticTraceEventKindV1.GenerationChanged,
                backendEpoch: epoch);
        else if (changedIncarnation is { Value: > 0 } incarnation &&
            record.Trace is { } providerTrace &&
            providerTrace.ProviderGeneration != incarnation.Value)
            QueueDmaTraceLocked(record, SemanticTraceEventKindV1.GenerationChanged,
                incarnation);
        if (newlyPinned)
            QueueDmaTraceLocked(record, SemanticTraceEventKindV1.Quarantined);
    }

    internal KernelResult<PlatformDmaSubmission> SubmitDmaGrant(
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence,
        PlatformDomainIdentity expectedSubject)
    {
        // Keep the bridge lock order stable: DSC1 state precedes DMA state.
        // RuntimeKernel holds its platform-memory-use gate outside both.
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
            return SubmitDmaGrantLocked(grant, prepareEvidence, expectedSubject, null,
                null, out _);
    }

    internal KernelResult<(PlatformDmaSubmission Submission, DmaExecutionBindingV1 Binding)>
        SubmitDmaGrantBound(
            PlatformDmaGrant grant,
            PlatformDmaPrepareEvidence prepareEvidence,
            PlatformDomainIdentity expectedSubject,
            ulong regionMutationGeneration,
            ISemanticTraceSinkV1? traceSink,
            out DmaTraceObserver? ambiguityTrace)
    {
        ambiguityTrace = null;
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
        {
            var result = SubmitDmaGrantLocked(grant, prepareEvidence, expectedSubject,
                regionMutationGeneration, traceSink, out ambiguityTrace);
            if (!result.IsSuccess)
                return KernelResult<(PlatformDmaSubmission, DmaExecutionBindingV1)>.Fail(
                    result.Error, result.Message!);
            var record = _activeDmaSubmissions[result.Value!.GrantId];
            if (traceSink is not null)
                RegisterDmaTraceLocked(record, traceSink);
            return KernelResult<(PlatformDmaSubmission, DmaExecutionBindingV1)>.Ok(
                (result.Value, record.SemanticBinding!.Value));
        }
    }

    private KernelResult<PlatformDmaSubmission> SubmitDmaGrantLocked(
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence,
        PlatformDomainIdentity expectedSubject,
        ulong? regionMutationGeneration,
        ISemanticTraceSinkV1? traceSink,
        out DmaTraceObserver? ambiguityTrace)
    {
        ambiguityTrace = null;
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
        if (regionMutationGeneration is not null &&
            _provider is not IPlatformDmaBoundSubmissionProvider)
            return KernelResult<PlatformDmaSubmission>.Fail(KernelError.PlatformUnsupported,
                "The platform provider does not expose the v6 DMA semantic-binding submit boundary.");
        if (regionMutationGeneration is not null &&
            _provider is not IPlatformProviderIncarnationSource)
            return KernelResult<PlatformDmaSubmission>.Fail(KernelError.PlatformUnsupported,
                "Bound DMA requires a live provider incarnation source; the legacy fallback cannot validate generation drift.");
        if (regionMutationGeneration == 0)
            return KernelResult<PlatformDmaSubmission>.Fail(KernelError.StaleGeneration,
                "The v6 DMA semantic binding requires a current non-zero Region mutation generation.");

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
        var grantRecord = _dmaGrants[grant.GrantId];
        var submitIncarnation = CurrentProviderIncarnation();
        var submitBackendEpoch = BackendEpoch;
        if (submitIncarnation.Value == 0 || submitIncarnation != grantRecord.ProviderIncarnation)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.StaleGeneration,
                "The DMA provider restarted or reset after the exact grant was admitted.");
        }
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
        DmaExecutionBindingV1? admittedBinding = null;
        if (regionMutationGeneration is { } mutationGeneration)
        {
            try
            {
                admittedBinding = CreateDmaExecutionBinding(grant, expectedSubject,
                    mutationGeneration, submitIncarnation, DmaEffectStateV1.Admitted);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return KernelResult<PlatformDmaSubmission>.Fail(KernelError.InvalidMessage,
                    exception.Message);
            }
        }
        DmaTraceObserver? TraceAmbiguity(PlatformProviderIncarnation observedIncarnation,
            PlatformBackendEpoch observedEpoch)
        {
            return traceSink is not null && admittedBinding is { } binding
                ? CreateAmbiguousDmaSubmitTraceLocked(traceSink, binding,
                    operationId, submitIncarnation, submitBackendEpoch,
                    observedIncarnation, observedEpoch)
                : null;
        }
        PlatformAuthorityResult<PlatformProviderDmaSubmission> providerResult;
        try
        {
            providerResult = admittedBinding is { } exactBinding
                ? ((IPlatformDmaBoundSubmissionProvider)submissionProvider)
                    .SubmitDmaBound(request, exactBinding)
                : submissionProvider.SubmitDma(request);
        }
        catch (Exception exception)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            ambiguityTrace = TraceAmbiguity(submitIncarnation, submitBackendEpoch);
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformFaulted,
                $"The DMA provider threw during submission; acceptance is ambiguous and the exact mapping remains pinned: {exception.Message}");
        }

        var observedIncarnation = CurrentProviderIncarnation();
        var observedEpoch = BackendEpoch;
        if (observedIncarnation != submitIncarnation || observedEpoch != submitBackendEpoch)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            ambiguityTrace = TraceAmbiguity(observedIncarnation, observedEpoch);
            return KernelResult<PlatformDmaSubmission>.Fail(
                KernelError.PlatformFaulted,
                "The DMA provider incarnation or local backend epoch changed during submission; acceptance is ambiguous and the exact mapping remains pinned.");
        }

        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status == PlatformAuthorityStatus.Faulted ||
                admittedBinding is not null && providerResult.Status != PlatformAuthorityStatus.NotAccepted)
            {
                _dmaSubmissionFaultPins.Add(grant.GrantId);
                ambiguityTrace = TraceAmbiguity(observedIncarnation, observedEpoch);
            }

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
            ambiguityTrace = TraceAmbiguity(observedIncarnation, observedEpoch);
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
            var effectBinding = admittedBinding is { } bound
                ? bound with { EffectState = DmaEffectStateV1.EffectPossible }
                : (DmaExecutionBindingV1?)null;
            var record = new DmaSubmissionRecord(submission, providerSubmission, effectBinding);
            _activeDmaSubmissions.Add(grant.GrantId, record);
            visibilityState.Consumed = true;
        }
        catch (Exception exception) when (
            exception is OutOfMemoryException or InvalidOperationException)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            ambiguityTrace = TraceAmbiguity(observedIncarnation, observedEpoch);
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

    private static DmaExecutionBindingV1 CreateDmaExecutionBinding(
        PlatformDmaGrant grant,
        PlatformDomainIdentity subject,
        ulong mutationGeneration,
        PlatformProviderIncarnation providerIncarnation,
        DmaEffectStateV1 effectState)
    {
        var mapping = grant.Mapping.Mapping;
        var regionUseDigest = Digest(FormattableString.Invariant(
            $"region={mapping.Region.RegionId.Value}/{mapping.Region.Generation.Value};mapping={mapping.MappingId.Value}/{mapping.Generation.Value};range={grant.Range.Offset}/{grant.Range.Length};direction={(int)grant.Direction}"));
        var securityDomainDigest = Digest(FormattableString.Invariant(
            $"domain={subject.DomainId.Value};process={subject.ProcessId.Value}/{subject.ProcessGeneration};binding={mapping.DomainBinding.BindingId.Value}/{mapping.DomainBinding.Generation.Value};device={grant.DeviceLease.Device.ResourceId}"));
        return new DmaExecutionBindingV1(1, regionUseDigest,
            mapping.Region.Generation.Value, mutationGeneration, subject.ProcessGeneration,
            mapping.DomainBinding.Generation.Value, mapping.Generation.Value,
            grant.DeviceLease.Generation.Value, providerIncarnation.Value,
            grant.Generation.Value, 1, effectState, securityDomainDigest).Validate();
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
