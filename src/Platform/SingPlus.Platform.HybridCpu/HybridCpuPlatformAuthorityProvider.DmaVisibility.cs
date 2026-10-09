using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDmaVisibilityProvider
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

    private sealed class DmaVisibilityInvocation(bool acquire, DmaSource source,
        ProviderDmaVisibilityState? state, PlatformProviderDmaVisibilityCycle cycle)
    {
        public bool Acquire { get; } = acquire;
        public DmaSource Source { get; } = source;
        public ProviderDmaVisibilityState? State { get; } = state;
        public PlatformProviderDmaVisibilityCycle Cycle { get; } = cycle;
        public PlatformProviderDmaVisibilityCycle OriginalProviderCycle { get; } = state?.ProviderCycle ?? default;
        public NeutralDmaVisibilityCycle OriginalNeutralCycle { get; } = state?.NeutralCycle ?? default;
        public bool OriginalAcquired { get; } = state?.Acquired ?? false;
        public object? Observation { get; set; }
    }

    public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(PlatformProviderDmaGrant grant)
    {
        var result = RunDmaVisibility(grant, acquire: false);
        return result.IsSuccess ? PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(
            new(grant.GrantId, grant.Generation, result.Value!, grant.Direction,
                PlatformMemoryVisibilityRequirement.PublicationFence, PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied)) :
            PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(result.Status, result.Message!);
    }

    public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> AcquireDmaGrantVisibility(PlatformProviderDmaGrant grant)
    {
        var result = RunDmaVisibility(grant, acquire: true);
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
            var validation = ValidateProviderDmaGrantForVisibility(grant);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaVisibilityCycle>.Fail(validation.Status, validation.Message!);
            record = _dmaGrants[grant.GrantId];
            if (record.VisibilityInFlight || record.VisibilityUnknown)
                return Fail("Original DMA visibility invocation is pending or unknown; repeat refused.");
            _dmaVisibilityStates.TryGetValue(grant.GrantId, out var routedState);
            var state = record.VisibilityState;
            if (!ReferenceEquals(routedState, state))
                return Fail("Original DMA visibility cycle owner is not current before effect.");
            if (!ValidateProviderDeviceLease(grant.DeviceLease).IsSuccess ||
                !ValidateProviderMapping(grant.MappingLease, expectedSlice: null).IsSuccess)
                return Fail("DMA visibility sources are not current before effect.");
            var source = new DmaSource(_deviceLeases[grant.DeviceLease.LeaseId],
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
                record.VisibilityInvocation = invocation;
            }
            record.VisibilityInFlight = true;
        }
        try
        {
            object? observation;
            try { observation = invocation.Observation ?? (acquire
                ? (object?)_runtime.AcquireDmaVisibility(record.HybridCpuGrant)
                : _runtime.PrepareDmaVisibility(record.HybridCpuGrant)); }
            catch { lock (_childCreateCommitGate) record.VisibilityUnknown = true; throw; }
            lock (_childCreateCommitGate)
            {
                invocation.Observation = observation;
                var prepare = observation as NeutralDmaPrepareResult;
                var acquired = observation as NeutralDmaAcquireResult;
                var success = acquire ? acquired is { IsAcquired: true, Decision: NeutralDmaAcquireDecision.Acquired } :
                    prepare is { IsPrepared: true, Decision: NeutralDmaPrepareDecision.Prepared };
                var evidence = acquire ? acquired?.Evidence : prepare?.Evidence;
                if (!success || evidence is null || evidence.GrantHandle != record.HybridCpuGrant.Handle ||
                    evidence.GrantEpoch != record.HybridCpuGrant.Epoch || evidence.Direction != ToNeutralDmaDirection(grant.Direction) ||
                    evidence.Cycle.Value == 0 || (acquire && evidence.Cycle != invocation.OriginalNeutralCycle))
                {
                    record.VisibilityUnknown = true;
                    return Fail("Original DMA visibility outcome is absent, unknown or does not refine its exact invocation.");
                }
                var source = invocation.Source;
                _dmaVisibilityStates.TryGetValue(grant.GrantId, out var currentState);
                if (!_dmaGrants.TryGetValue(grant.GrantId, out var current) || !ReferenceEquals(current, record) ||
                    !_deviceLeases.TryGetValue(grant.DeviceLease.LeaseId, out var device) || !ReferenceEquals(device, source.Device) ||
                    !_providerMappings.TryGetValue(grant.MappingLease.MappingId, out var mapping) || !ReferenceEquals(mapping, source.Mapping) ||
                    !_domains.TryGetValue(grant.DeviceLease.DomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, source.Parent) ||
                    !ReferenceEquals(currentState, invocation.State) || !ReferenceEquals(record.VisibilityState, invocation.State) ||
                    (currentState is not null && (currentState.NeutralCycle != invocation.OriginalNeutralCycle ||
                        currentState.Acquired != invocation.OriginalAcquired || currentState.ProviderCycle != invocation.OriginalProviderCycle)) ||
                    !ValidateProviderDmaGrantForVisibility(grant).IsSuccess ||
                    !ValidateProviderDeviceLease(grant.DeviceLease).IsSuccess || !ValidateProviderMapping(grant.MappingLease, expectedSlice: null).IsSuccess ||
                    source.Device.CloseInFlight || source.Device.CloseUncertain || source.Device.CloseObservation is not null ||
                    record.HybridCpuGrant.DeviceLease != source.Device.HybridCpuLease || record.HybridCpuGrant.MappingLease != source.Mapping.HybridCpuLease)
                    return Fail("Exact visibility outcome retained without current original source/cycle publication.");
                var contractValidation = acquire
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
                    var publishedState = new ProviderDmaVisibilityState(invocation.Cycle, evidence.Cycle);
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
        if (!_dmaGrants.TryGetValue(grant.GrantId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider DMA grant does not exist.");
        }

        if (!_originalDmaGrants.TryGetValue(grant.GrantId, out var original) || !ReferenceEquals(original, record))
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
