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
        public DmaSubmissionRecord? OwnerSelf { get; private set; }
        public void EstablishSelf() => OwnerSelf = this;
        public bool OriginalCompletionLookupInvoked { get; set; }
        public PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>? RawCompletion { get; set; }
        public PlatformDmaSubmission Submission { get; } = submission;
        public PlatformProviderDmaSubmission ProviderSubmission { get; } = providerSubmission;
        public DmaExecutionBindingV1? SemanticBinding { get; } = semanticBinding;
        public PlatformProviderDmaCopySubmissionId? CopySubmissionId { get; } = copySubmissionId;
        public PlatformProviderDmaCopySubmissionGeneration? CopyGeneration { get; } = copyGeneration;
        public bool CompletionObservationInFlight { get; set; }
        public bool CompletionProven { get; set; }
        public PlatformDmaCompletionEvidence? CompletionEvidence { get; set; }
        public PlatformDmaPostCompletionVisibilityEvidence? CopyVisibilityEvidence { get; set; }
        public PlatformProviderDmaCompletionEvidence? KnownCompletionEvidence { get; set; }
        public PlatformBackendEpoch KnownCompletionEpoch { get; set; }
        public HashSet<ulong> PageFaultsInFlight { get; } = [];
        public HashSet<ulong> ResolvedPageFaults { get; } = [];
        public Dictionary<ulong, (PlatformBackendEpoch Epoch, PlatformProviderDmaPageFaultEvidence Evidence)> KnownPageFaultEvidence { get; } = [];
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
        bool newlyPinned = _dmaSubmissionFaultPins.Add(grantId);
        if (!_activeDmaSubmissions.TryGetValue(grantId, out DmaSubmissionRecord? record))
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
        if (newlyPinned && record.Trace?.QuarantineQueued != true)
            QueueDmaTraceLocked(record, SemanticTraceEventKindV1.Quarantined);
    }

    internal KernelResult<PlatformDmaSubmission> SubmitDmaGrant(
        PlatformDmaGrant grant, PlatformDmaPrepareEvidence prepareEvidence, PlatformDomainIdentity expectedSubject,
        Func<Func<KernelResult<PlatformDmaSubmission>>, KernelResult<PlatformDmaSubmission>> ownerCommit) =>
        SubmitDmaGrantCore(grant, prepareEvidence, expectedSubject, null, null, ownerCommit, out _, out _);

    private KernelResult<PlatformDmaSubmission> SubmitDmaGrantCore(
        PlatformDmaGrant grant, PlatformDmaPrepareEvidence prepareEvidence, PlatformDomainIdentity expectedSubject,
        ulong? regionMutationGeneration, ISemanticTraceSinkV1? traceSink,
        Func<Func<KernelResult<PlatformDmaSubmission>>, KernelResult<PlatformDmaSubmission>> ownerCommit,
        out DmaExecutionBindingV1? effectBinding, out DmaTraceObserver? trace)
    {
        DmaGrantRecord record;
        DmaVisibilityState visibility;
        IPlatformDmaSubmissionProvider provider;
        PlatformBackendEpoch epoch;
        PlatformDmaSubmission submission = default;
        PlatformProviderDmaSubmitRequest request = default;
        DmaSubmissionRecord? accepted = null;
        DmaExecutionBindingV1? admittedBinding = null;
        DmaTraceObserver? observation = null;
        bool invoked = false;
        PlatformProviderIncarnation observedSource = default(PlatformProviderIncarnation);
        effectBinding = null;
        trace = null;
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        lock (_dmaCompletionGate)
        {
                    KernelResult valid = ValidateDmaSubmitIntentLocked(grant, prepareEvidence, expectedSubject, regionMutationGeneration,
                out record, out visibility, out provider);
            if (!valid.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(valid.Error, valid.Message!);
            epoch = BackendEpoch;
            record.SubmissionInFlight = true;
        }
        try
        {
            PlatformProviderIncarnation incarnation = CurrentProviderIncarnation();
            KernelResult<PlatformDmaSubmission> admission = ownerCommit(() =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dsc1Gate)
                lock (_dmaCompletionGate)
                {
                            KernelResult exact = ValidateOriginal(incarnation, false);
                    if (!exact.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(exact.Error, exact.Message!);
                    if (_nextDmaOperationId == 0)
                        return KernelResult<PlatformDmaSubmission>.Fail(KernelError.CapacityExhausted, "DMA operation identities exhausted.");
                            KernelResult mappingUse = ValidateDmaMappingUseAdmissionLocked(grant);
                    if (!mappingUse.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(mappingUse.Error, mappingUse.Message!);
                    try
                    {
                        _activeDmaSubmissions.EnsureCapacity(_activeDmaSubmissions.Count + 1);
                        _dmaSubmissionFaultPins.EnsureCapacity(_dmaSubmissionFaultPins.Count + 1);
                    }
                    catch (OutOfMemoryException)
                    {
                        return KernelResult<PlatformDmaSubmission>.Fail(KernelError.CapacityExhausted,
                            "Local DMA tracking capacity exhausted before invocation.");
                    }
                    request = new(record.ProviderGrant, visibility.ProviderCycle);
                            PlatformAuthorityResult requestValid = PlatformDmaSubmissionContract.ValidateRequest(request);
                    if (!requestValid.IsSuccess)
                        return KernelResult<PlatformDmaSubmission>.Fail(KernelError.PlatformFaulted, requestValid.Message!);
                    if (regionMutationGeneration is { } mutation)
                    {
                        try { admittedBinding = CreateDmaExecutionBinding(grant, expectedSubject, mutation, incarnation, DmaEffectStateV1.Admitted); }
                        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                        {
                            return KernelResult<PlatformDmaSubmission>.Fail(KernelError.InvalidMessage, exception.Message);
                        }
                    }
                    submission = new(new(NextLocalDmaOperationId()), new(1), grant.GrantId, grant.Generation,
                        visibility.LocalCycle, grant.Range, grant.Direction);
                    return KernelResult<PlatformDmaSubmission>.Ok(submission);
                }
            });
            if (!admission.IsSuccess) return admission;
            invoked = true;
            PlatformAuthorityResult<PlatformProviderDmaSubmission> reply = admittedBinding is { } binding
                ? ((IPlatformDmaBoundSubmissionProvider)provider).SubmitDmaBound(request, binding)
                : provider.SubmitDma(request);
            if (!reply.IsSuccess)
            {
                PlatformProviderIncarnation refusedIncarnation = CurrentProviderIncarnation();
                observedSource = refusedIncarnation;
                lock (_secureDomainLifecycleGate)
                lock (_dmaCompletionGate)
                {
                    if (BackendEpoch != epoch || refusedIncarnation.Value == 0 ||
                        refusedIncarnation != record.ProviderIncarnation)
                    {
                        FaultPinDmaSubmissionLocked(grant.GrantId);
                        return KernelResult<PlatformDmaSubmission>.Fail(KernelError.PlatformFaulted,
                            "DMA provider refusal lost original source continuity; mapping pinned.");
                    }
                }
                if (reply.Status == PlatformAuthorityStatus.Faulted ||
                    admittedBinding is not null && reply.Status != PlatformAuthorityStatus.NotAccepted)
                {
                    lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);

                }
                return FromProviderFailure<PlatformDmaSubmission>(reply.Status, reply.Message);
            }
            PlatformAuthorityResult replyValid = PlatformDmaSubmissionContract.ValidateSubmission(request, reply.Value!);
            if (!replyValid.IsSuccess)
            {
                lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult<PlatformDmaSubmission>.Fail(KernelError.PlatformFaulted, replyValid.Message!);
            }
            // Preserve exact original accepted effect before late query/publication refusal.
            lock (_dmaCompletionGate)
            {
                DmaExecutionBindingV1? semantic = admittedBinding is { } admitted ? admitted with { EffectState = DmaEffectStateV1.EffectPossible } : (DmaExecutionBindingV1?)null;
                accepted = new(submission, reply.Value!, semantic);
                _activeDmaSubmissions.Add(grant.GrantId, accepted);
                visibility.Consumed = true;
                if (traceSink is not null && admittedBinding is not null)
                {
                    RegisterDmaTraceLocked(accepted, traceSink, epoch);
                    observation = accepted.Trace;
                }
            }
            incarnation = CurrentProviderIncarnation();
            observedSource = incarnation;
            KernelResult<PlatformDmaSubmission> published = ownerCommit(() =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dsc1Gate)
                lock (_dmaCompletionGate)
                {
                            KernelResult exact = ValidateOriginal(incarnation, true);
                    return exact.IsSuccess ? KernelResult<PlatformDmaSubmission>.Ok(submission)
                        : KernelResult<PlatformDmaSubmission>.Fail(exact.Error, exact.Message!);
                }
            });
            if (!published.IsSuccess)
            {
                lock (_dmaCompletionGate)
                {
                    ObserveRefusal(observedSource.Value != 0 ? observedSource : record.ProviderIncarnation);
                    FaultPinDmaSubmissionLocked(grant.GrantId);
                }
            }
            return published;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_dmaCompletionGate) FaultPinDmaSubmissionLocked(grant.GrantId);
            return KernelResult<PlatformDmaSubmission>.Fail(KernelError.PlatformFaulted,
                $"DMA submit refused; accepted/unknown original effect retained: {exception.GetType().Name}.");
        }
        finally
        {
            lock (_dmaCompletionGate)
            {
                if (invoked && _dmaSubmissionFaultPins.Contains(grant.GrantId))
                    ObserveRefusal(observedSource.Value != 0 ? observedSource : record.ProviderIncarnation);
                record.SubmissionInFlight = false;
                effectBinding = admittedBinding is { } admitted ? admitted with { EffectState = DmaEffectStateV1.EffectPossible } : null;
                trace = observation;
            }
        }

        void ObserveRefusal(PlatformProviderIncarnation observed)
        {
            if (traceSink is not null && admittedBinding is { } binding)
            {
                if (accepted?.Trace is { } existing)
                {
                    observation = existing;
                    PlatformProviderIncarnation? providerChange = observed.Value > 0 && observed.Value != existing.ProviderGeneration ? observed : null;
                    PlatformBackendEpoch? epochChange = BackendEpoch.Value != existing.BackendEpoch ? BackendEpoch : null;
                    if (providerChange is not null || epochChange is not null)
                        QueueDmaTraceLocked(accepted, SemanticTraceEventKindV1.GenerationChanged, providerChange, epochChange);
                    if (!existing.QuarantineQueued) QueueDmaTraceLocked(accepted, SemanticTraceEventKindV1.Quarantined);
                }
                else
                {
                    observation = CreateAmbiguousDmaSubmitTraceLocked(traceSink, binding,
                        submission.OperationId, record.ProviderIncarnation, epoch, observed, BackendEpoch);
                    if (accepted is not null) accepted.Trace = observation;
                }
            }
        }
        KernelResult ValidateOriginal(PlatformProviderIncarnation incarnation, bool publishing)
        {
            if (BackendEpoch != epoch || _dmaSubmissionFaultPins.Contains(grant.GrantId))
                return KernelResult.Fail(KernelError.PlatformFaulted, "DMA backend/possible effect continuity lost.");
            if (incarnation.Value == 0 || incarnation != record.ProviderIncarnation)
                return KernelResult.Fail(publishing ? KernelError.PlatformFaulted : KernelError.StaleGeneration, "DMA provider source changed.");
            if (!_dmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? current) || !ReferenceEquals(current, record) ||
                !record.SubmissionInFlight || record.PlatformClosed ||
                !_dmaVisibilityStates.TryGetValue(grant.GrantId, out DmaVisibilityState? cycle) || !ReferenceEquals(cycle, visibility) ||
                visibility.Acquired || (!publishing && visibility.Consumed) ||
                publishing && (!visibility.Consumed || !_activeDmaSubmissions.TryGetValue(grant.GrantId, out DmaSubmissionRecord? active) || !ReferenceEquals(active, accepted)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "DMA original grant/cycle/lifetime changed.");
            KernelResult device = ValidateDeviceLease(grant.DeviceLease, expectedSubject);
            KernelResult mapping = device.IsSuccess ? ValidateExactMapping(grant.Mapping, expectedSubject) : device;
            if (!mapping.IsSuccess)
            {
                FaultPinDmaSubmissionLocked(grant.GrantId);
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original DMA authorization changed before publication.");
            }
            return KernelResult.Ok();
        }
    }
    internal KernelResult<(PlatformDmaSubmission Submission, DmaExecutionBindingV1 Binding)> SubmitDmaGrantBound(
        PlatformDmaGrant grant, PlatformDmaPrepareEvidence prepareEvidence, PlatformDomainIdentity expectedSubject,
        ulong regionMutationGeneration, ISemanticTraceSinkV1? traceSink,
        Func<Func<KernelResult<PlatformDmaSubmission>>, KernelResult<PlatformDmaSubmission>> ownerCommit,
        out DmaTraceObserver? trace)
    {
        KernelResult<PlatformDmaSubmission> submitted = SubmitDmaGrantCore(grant, prepareEvidence, expectedSubject, regionMutationGeneration,
            traceSink, ownerCommit, out DmaExecutionBindingV1? binding, out trace);
        return submitted.IsSuccess
            ? KernelResult<(PlatformDmaSubmission, DmaExecutionBindingV1)>.Ok((submitted.Value!, binding!.Value))
            : KernelResult<(PlatformDmaSubmission, DmaExecutionBindingV1)>.Fail(submitted.Error, submitted.Message!);
    }
    private KernelResult ValidateDmaSubmitIntentLocked(
        PlatformDmaGrant grant, PlatformDmaPrepareEvidence prepareEvidence, PlatformDomainIdentity expectedSubject,
        ulong? regionMutationGeneration, out DmaGrantRecord grantRecord,
        out DmaVisibilityState visibilityState, out IPlatformDmaSubmissionProvider submissionProvider)
    {
        grantRecord = null!;
        visibilityState = null!;
        submissionProvider = null!;
        KernelResult validation = ValidateDmaGrant(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult.Fail(
                validation.Error,
                validation.Message!);
        }

        if (HasFaultPinnedDmaSubmission(grant.GrantId))
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "DMA submission state is fault-pinned because an external effect may have been accepted ambiguously.");
        }

        if (HasActiveDmaSubmission(grant.GrantId))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingActive,
                "The exact DMA grant already has a submitted operation whose post-submit lifecycle is not closed.");
        }

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaSubmissionContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission))
        {
            return KernelResult.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise bounded DMA submission contract v3.");
        }

        if (_provider is not IPlatformDmaSubmissionProvider availableProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose bounded DMA submission.");
        }
        submissionProvider = availableProvider;
        if (regionMutationGeneration is not null &&
            _provider is not IPlatformDmaBoundSubmissionProvider)
            return KernelResult.Fail(KernelError.PlatformUnsupported,
                "The platform provider does not expose the v6 DMA semantic-binding submit boundary.");

        if (regionMutationGeneration is not null &&
            _provider is not IPlatformProviderIncarnationSource)
            return KernelResult.Fail(KernelError.PlatformUnsupported,
                "Bound DMA requires a live provider incarnation source; the legacy fallback cannot validate generation drift.");
        if (regionMutationGeneration == 0)
            return KernelResult.Fail(KernelError.StaleGeneration,
                "The v6 DMA semantic binding requires a current non-zero Region mutation generation.");

        if (!prepareEvidence.IsSatisfied)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA submission requires satisfied publication evidence for the exact prepared cycle.");
        }

        if (prepareEvidence.GrantId != grant.GrantId)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA prepare evidence belongs to a different local grant.");
        }

        if (prepareEvidence.GrantGeneration != grant.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "DMA prepare evidence uses a stale local grant generation.");
        }

        if (prepareEvidence.Direction != grant.Direction)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA prepare evidence direction does not match the exact grant.");
        }

        if (!_dmaVisibilityStates.TryGetValue(grant.GrantId, out visibilityState) ||
            visibilityState.LocalCycle.Value == 0)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "The exact DMA grant has no prepared visibility cycle to submit.");
        }

        if (visibilityState.LocalCycle != prepareEvidence.Cycle)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "DMA prepare evidence does not match the current exact visibility cycle.");
        }

        if (visibilityState.Acquired || visibilityState.Consumed)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "The current DMA visibility cycle was already consumed and cannot be submitted.");
        }

        grantRecord = _dmaGrants[grant.GrantId];
        return KernelResult.Ok();
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
            return _activeDmaSubmissions.TryGetValue(grantId, out DmaSubmissionRecord? record) &&
                   !record.CompletionProven;
        }
    }

    internal bool HasCompletedDmaSubmission(PlatformDmaGrantId grantId)
    {
        lock (_dmaCompletionGate)
        {
            return _activeDmaSubmissions.TryGetValue(grantId, out DmaSubmissionRecord? record) &&
                   record.CompletionProven;
        }
    }

    internal bool HasFaultPinnedDmaSubmission(PlatformDmaGrantId grantId)
    {
        lock (_dmaCompletionGate)
            return _dmaSubmissionFaultPins.Contains(grantId) || _dmaGrants.TryGetValue(grantId,out DmaGrantRecord? grant) &&
                ((ulong)grant.CopyHistory.Count != grant.CopiesIssued || grant.CopyHistory.Any(copy => !ReferenceEquals(copy.OwnerSelf,copy)) ||
                 grant.CopiesIssued != 0 && grant.OriginalCopy is null || grant.OriginalCopy is { Uncertain: true });
    }

    private ulong NextLocalDmaOperationId()
    {
        ulong value = _nextDmaOperationId;
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
        PlatformRegionMapping mapping = grant.Mapping.Mapping;
        string regionUseDigest = Digest(FormattableString.Invariant(
            $"region={mapping.Region.RegionId.Value}/{mapping.Region.Generation.Value};mapping={mapping.MappingId.Value}/{mapping.Generation.Value};range={grant.Range.Offset}/{grant.Range.Length};direction={(int)grant.Direction}"));
        string securityDomainDigest = Digest(FormattableString.Invariant(
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
