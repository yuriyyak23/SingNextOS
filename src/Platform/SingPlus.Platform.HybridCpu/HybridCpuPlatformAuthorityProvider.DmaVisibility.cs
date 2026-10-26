using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDmaVisibilityProvider, IPlatformDmaVisibilityClosureProvider
{
    private sealed class ProviderDmaVisibilityState(
        PlatformProviderDmaVisibilityCycle providerCycle,
        NeutralDmaVisibilityCycle neutralCycle)
    {
        public PlatformProviderDmaVisibilityCycle ProviderCycle { get; set; } = providerCycle;
        public NeutralDmaVisibilityCycle NeutralCycle { get; set; } = neutralCycle;
        public bool Acquired { get; set; }
    }

    private readonly Dictionary<PlatformProviderDmaGrantId, ProviderDmaVisibilityState>
        _dmaVisibilityStates = [];
    private ulong _nextProviderDmaVisibilityCycle = 1;

    private sealed class DmaVisibilityInvocation
    {
        public DmaVisibilityInvocation(bool acquire, DmaSource source, ProviderDmaVisibilityState? state, PlatformProviderDmaVisibilityCycle cycle)
        { OwnerSelf = this; Acquire = acquire; Source = source; State = state; Cycle = cycle; OriginalProviderCycle = state?.ProviderCycle ?? default; OriginalNeutralCycle = state?.NeutralCycle ?? default; OriginalAcquired = state?.Acquired ?? false; }
        public DmaVisibilityInvocation OwnerSelf { get; }
        public bool Acquire { get; }
        public DmaSource Source { get; }
        public ProviderDmaVisibilityState? State { get; }
        public PlatformProviderDmaVisibilityCycle Cycle { get; }
        public PlatformProviderDmaVisibilityCycle OriginalProviderCycle { get; }
        public NeutralDmaVisibilityCycle OriginalNeutralCycle { get; }
        public bool OriginalAcquired { get; }
        public object? Observation { get; set; }
        public NeutralOriginalDmaVisibilityRequest? NativeRequest { get; set; }
        public NeutralOriginalDmaVisibilityObservation? NativeObservation { get; set; }
        public NeutralOriginalDmaVisibilityObservation? NativeValidation { get; set; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
    }

    public bool CanCloseOriginalDmaVisibility(PlatformProviderDmaGrant grant)
    {
        lock (_childCreateCommitGate)
            return _runtime is INeutralOriginalDmaVisibilityProvider && _originalDmaGrants.TryGetValue(grant.GrantId,out DmaGrantRecord? owner) &&
                ReferenceEquals(owner.OwnerSelf,owner) && owner.Grant == grant && _dmaGrants.TryGetValue(grant.GrantId,out DmaGrantRecord? routed) && ReferenceEquals(routed,owner) &&
                owner.AdmissionSource is { } admission && ReferenceEquals(admission.OwnerSelf,admission) && admission.NativeRequest is { } request &&
                admission.NativeObservation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw && raw.Request == request &&
                raw.Observation is { IsGranted: true } && raw.Observation.Grant == owner.HybridCpuGrant;
    }

    public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(PlatformProviderDmaGrant grant)
    {
        PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle> result = RunDmaVisibility(grant, acquire: false);
        return result.IsSuccess ? PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(
            new(grant.GrantId, grant.Generation, result.Value!, grant.Direction,
                PlatformMemoryVisibilityRequirement.PublicationFence, PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied)) :
            PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(result.Status, result.Message!);
    }

    public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> AcquireDmaGrantVisibility(PlatformProviderDmaGrant grant)
    {
        PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle> result = RunDmaVisibility(grant, acquire: true);
        return result.IsSuccess ? PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(
            new(grant.GrantId, grant.Generation, result.Value!, grant.Direction,
                PlatformMemoryAcquireRequirement.AcquisitionFence, PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied)) :
            PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(result.Status, result.Message!);
    }

    private PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle> RunDmaVisibility(PlatformProviderDmaGrant grant, bool acquire)
    {
        PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle> Fail(string message) =>
            PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle>.Fail(PlatformAuthorityStatus.Faulted, message);
        DmaGrantRecord record;
        DmaVisibilityInvocation invocation;
        lock (_childCreateCommitGate)
        {
            PlatformAuthorityResult validation = ValidateProviderDmaGrantForVisibility(grant);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle>.Fail(validation.Status, validation.Message!);
            record = _dmaGrants[grant.GrantId];
            if (record.CopyFact is { } copy && !copy.NoEffectConsumed && (copy.InFlight || !acquire &&
                (!copy.SourceCompletionObserved || !copy.DestinationCompletionObserved || !copy.DestinationCycle.Acquired)))
                return Fail("Original copy bytes/cycles are retained; visibility effect refused.");
            if (record.VisibilityInFlight || record.VisibilityUnknown)
                return Fail("Original DMA visibility invocation is pending or unknown; repeat refused.");
            _dmaVisibilityStates.TryGetValue(grant.GrantId, out ProviderDmaVisibilityState? routedState);
            ProviderDmaVisibilityState? state = record.VisibilityState;
            if (!ReferenceEquals(routedState, state))
                return Fail("Original DMA visibility cycle owner is not current before effect.");
            if (!ValidateProviderDeviceLease(grant.DeviceLease).IsSuccess ||
                !ValidateProviderMapping(grant.MappingLease, expectedSlice: null).IsSuccess)
                return Fail("DMA visibility sources are not current before effect.");
            DmaSource source = new DmaSource(_deviceLeases[grant.DeviceLease.LeaseId],
                _providerMappings[grant.MappingLease.MappingId], _domains[grant.DeviceLease.DomainLease.LeaseId]);
            if (source.Device.CloseInFlight || source.Device.CloseUncertain || source.Device.CloseObservation is not null)
                return Fail("DMA visibility device is closing or retained.");
            if (record.VisibilityInvocation is { } previous)
            {
                if (previous.Acquire != acquire || !ReferenceEquals(previous.Source.Device, source.Device) ||
                    !ReferenceEquals(previous.Source.Mapping, source.Mapping) || !ReferenceEquals(previous.Source.Parent, source.Parent))
                    return Fail("Retained visibility invocation belongs to a different source or operation.");
                invocation = previous;
            }
            else
            {
                if (acquire && (grant.Direction == PlatformDmaDirection.DeviceReadsMemory || state is null || state.Acquired))
                    return PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle>.Fail(PlatformAuthorityStatus.Denied,
                        "DMA acquire requires an unacquired prepared write cycle.");
                if (!acquire && _nextProviderDmaVisibilityCycle is 0 or ulong.MaxValue)
                    return Fail("DMA visibility cycle capacity exhausted before neutral effect.");
                if (!acquire) _dmaVisibilityStates.EnsureCapacity(_dmaVisibilityStates.Count + 1);
                invocation = new(acquire, source, state, acquire ? state!.ProviderCycle : new(_nextProviderDmaVisibilityCycle++));
                record.VisibilityHistory.Add(invocation);
                record.VisibilityInvocation = invocation;
            }
            if (!ReferenceEquals(invocation.OwnerSelf,invocation) || !record.VisibilityHistory.Any(old => ReferenceEquals(old,invocation)))
                return Fail("Original DMA visibility invocation owner was substituted.");
            record.VisibilityInFlight = true;
        }
        try
        {
            object? observation;
            try
            {
                if (_runtime is INeutralOriginalDmaVisibilityProvider original && record.AdmissionSource?.NativeRequest is { } bind &&
                    _originalNativeMappingIncarnation is { } incarnation)
                {
                    NeutralOriginalDmaVisibilityRequest request; bool invoke;
                    lock (_childCreateCommitGate)
                    {
                        invocation.NativeRequest ??= new(Guid.NewGuid(),incarnation,bind.InvocationId,record.HybridCpuGrant,acquire,acquire ? invocation.OriginalNeutralCycle : default);
                        request = invocation.NativeRequest.Value; invoke = !invocation.Invoked;
                        if (invoke) invocation.Invoked = true;
                        if (!invoke && invocation.NativeObservation is null) return Fail("Original visibility reply unknown; repeat refused, exact grant cleanup only.");
                    }
                    NeutralOriginalDmaVisibilityObservation raw = invocation.NativeObservation ?? (acquire ? original.AcquireDmaVisibility(request) : original.PrepareDmaVisibility(request));
                    lock (_childCreateCommitGate)
                    {
                        invocation.NativeObservation = raw; invocation.Observation = acquire ? raw.Acquire : raw.Prepare; // Before source callback.
                        if (raw.Request != request || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed ||
                            acquire && (raw.Acquire is null || raw.Prepare is not null) || !acquire && (raw.Prepare is null || raw.Acquire is not null) ||
                            raw.NoEffectGuaranteed && (raw.Prepare is { IsPrepared: true } || raw.Acquire is { IsAcquired: true }))
                        { record.VisibilityUnknown = true; return Fail("Original visibility response foreign/malformed; cleanup only."); }
                        if (raw.NoEffectGuaranteed)
                        {
                            invocation.NoEffectConsumed = true; record.VisibilityInvocation = null;
                            return PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle>.Fail(PlatformAuthorityStatus.NotAccepted,"Original visibility invocation exact no-effect; grant remains open.");
                        }
                    }
                    PlatformAuthorityResult source = ValidateOriginalNativeMappingSource();
                    if (!source.IsSuccess) return Fail(source.Message ?? "Original visibility source lost after effect.");
                    NeutralOriginalDmaVisibilityObservation? current = original.ResolveDmaVisibility(request);
                    lock (_childCreateCommitGate) invocation.NativeValidation = current; // Retained before callback.
                    PlatformAuthorityResult after = ValidateOriginalNativeMappingSource();
                    if (!after.IsSuccess || current is null || current.Value != raw) return Fail("Original native visibility owner/cycle changed before publication.");
                    NeutralOriginalDmaVisibilityObservation? final = original.ResolveDmaVisibility(request); // Fresh owner/cycle after the intervening source callback.
                    lock (_childCreateCommitGate) invocation.NativeValidation = final;
                    if (final is null || final.Value != raw) return Fail("Original native visibility owner changed after source callback.");
                    observation = invocation.Observation;
                }
                else
                {
                    lock (_childCreateCommitGate)
                    {
                        if (invocation.Invoked && invocation.Observation is null) return Fail("Legacy visibility outcome unknown; no repeat.");
                        invocation.Invoked = true;
                    }
                    observation = invocation.Observation ?? (acquire ? (object?)_runtime.AcquireDmaVisibility(record.HybridCpuGrant) : _runtime.PrepareDmaVisibility(record.HybridCpuGrant));
                }
            }
            catch { lock (_childCreateCommitGate) record.VisibilityUnknown = true; throw; }
            lock (_childCreateCommitGate)
            {
                invocation.Observation = observation;
                NeutralDmaPrepareResult? prepare = observation as NeutralDmaPrepareResult;
                NeutralDmaAcquireResult? acquired = observation as NeutralDmaAcquireResult;
                bool success = acquire ? acquired is { IsAcquired: true, Decision: NeutralDmaAcquireDecision.Acquired } :
                    prepare is { IsPrepared: true, Decision: NeutralDmaPrepareDecision.Prepared };
                NeutralDmaVisibilityEvidence? evidence = acquire ? acquired?.Evidence : prepare?.Evidence;
                if (!success || evidence is null || evidence.GrantHandle != record.HybridCpuGrant.Handle ||
                    evidence.GrantEpoch != record.HybridCpuGrant.Epoch || evidence.Direction != ToNeutralDmaDirection(grant.Direction) ||
                    evidence.Cycle.Value == 0 || (acquire && evidence.Cycle != invocation.OriginalNeutralCycle))
                {
                    record.VisibilityUnknown = true;
                    return Fail("Original DMA visibility outcome is absent, unknown or does not refine its exact invocation.");
                }
                DmaSource source = invocation.Source;
                _dmaVisibilityStates.TryGetValue(grant.GrantId, out ProviderDmaVisibilityState? currentState);
                if (!_dmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? current) || !ReferenceEquals(current, record) ||
                    !_deviceLeases.TryGetValue(grant.DeviceLease.LeaseId, out DeviceLeaseRecord? device) || !ReferenceEquals(device, source.Device) ||
                    !_providerMappings.TryGetValue(grant.MappingLease.MappingId, out ProviderMappingRecord? mapping) || !ReferenceEquals(mapping, source.Mapping) ||
                    !_domains.TryGetValue(grant.DeviceLease.DomainLease.LeaseId, out DomainRecord? parent) || !ReferenceEquals(parent, source.Parent) ||
                    !ReferenceEquals(currentState, invocation.State) || !ReferenceEquals(record.VisibilityState, invocation.State) ||
                    (currentState is not null && (currentState.NeutralCycle != invocation.OriginalNeutralCycle ||
                        currentState.Acquired != invocation.OriginalAcquired || currentState.ProviderCycle != invocation.OriginalProviderCycle)) ||
                    !ValidateProviderDmaGrantForVisibility(grant).IsSuccess ||
                    !ValidateProviderDeviceLease(grant.DeviceLease).IsSuccess || !ValidateProviderMapping(grant.MappingLease, expectedSlice: null).IsSuccess ||
                    source.Device.CloseInFlight || source.Device.CloseUncertain || source.Device.CloseObservation is not null ||
                    record.HybridCpuGrant.DeviceLease != source.Device.HybridCpuLease || record.HybridCpuGrant.MappingLease != source.Mapping.HybridCpuLease)
                    return Fail("Exact visibility outcome retained without current original source/cycle publication.");
                PlatformAuthorityResult contractValidation = acquire
                    ? PlatformDmaVisibilityContract.ValidateAcquireEvidence(grant, invocation.Cycle,
                        new(grant.GrantId, grant.Generation, invocation.Cycle, grant.Direction,
                            PlatformMemoryAcquireRequirement.AcquisitionFence, PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied))
                    : PlatformDmaVisibilityContract.ValidatePrepareEvidence(grant,
                        new(grant.GrantId, grant.Generation, invocation.Cycle, grant.Direction,
                            PlatformMemoryVisibilityRequirement.PublicationFence, PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
                if (!contractValidation.IsSuccess)
                {
                    record.VisibilityUnknown = true;
                    return Fail(contractValidation.Message ?? "DMA visibility contract validation failed before commit.");
                }
                if (acquire) invocation.State!.Acquired = true;
                else
                {
                    ProviderDmaVisibilityState publishedState = new ProviderDmaVisibilityState(invocation.Cycle, evidence.Cycle);
                    _dmaVisibilityStates[grant.GrantId] = publishedState;
                    record.VisibilityState = publishedState;
                }
                record.VisibilityInvocation = null;
                return PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle>.Ok(invocation.Cycle);
            }
        }
        finally { lock (_childCreateCommitGate) record.VisibilityInFlight = false; }
    }

    private PlatformAuthorityResult ValidateProviderDmaGrantForVisibility(
        PlatformProviderDmaGrant grant)
    {
        lock (_childCreateCommitGate)
        {
        if (!_dmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider DMA grant does not exist.");
        }

        if (!ReferenceEquals(record.OwnerSelf,record) || !_originalDmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? original) || !ReferenceEquals(original, record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA owner is not current before visibility.");

        if (record.Grant.Generation != grant.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider DMA grant generation is stale.");
        }

        if (record.Grant != grant)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider DMA grant identity is malformed.");
        }

        if (record.CleanupOnly)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Original DMA cleanup outcome cannot admit visibility or use.");

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider DMA grant has already been revoked.");
        }

        if (record.CloseInFlight || record.CloseUncertain || record.CloseObservation is not null)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA close is pending or retained; visibility effect refused.");
        if (!IsCurrentDmaBindSource(record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA bind sources changed before visibility.");
        return PlatformAuthorityResult.Ok();
        }
    }
}
