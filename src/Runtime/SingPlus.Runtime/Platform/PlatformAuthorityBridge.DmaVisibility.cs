using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDmaVisibilityCycle(ulong Value);

public readonly record struct PlatformDmaPrepareEvidence(
    PlatformDmaGrantId GrantId,
    PlatformDmaGrantGeneration GrantGeneration,
    PlatformDmaVisibilityCycle Cycle,
    PlatformDmaDirection Direction,
    PlatformMemoryVisibilityRequirement Requirement,
    PlatformMemoryVisibilityOutcome Outcome)
{
    public bool IsSatisfied =>
        GrantId.Value != 0 &&
        GrantGeneration.Value != 0 &&
        Cycle.Value != 0 &&
        Requirement == PlatformMemoryVisibilityRequirement.PublicationFence &&
        Outcome == PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied;
}

public readonly record struct PlatformDmaAcquireEvidence(
    PlatformDmaGrantId GrantId,
    PlatformDmaGrantGeneration GrantGeneration,
    PlatformDmaVisibilityCycle Cycle,
    PlatformDmaDirection Direction,
    PlatformMemoryAcquireRequirement Requirement,
    PlatformMemoryAcquireOutcome Outcome)
{
    public bool IsSatisfied =>
        GrantId.Value != 0 &&
        GrantGeneration.Value != 0 &&
        Cycle.Value != 0 &&
        Requirement == PlatformMemoryAcquireRequirement.AcquisitionFence &&
        Outcome == PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied;
}

public sealed partial class PlatformAuthorityBridge
{
    private sealed class DmaVisibilityState(
        PlatformDmaVisibilityCycle localCycle,
        PlatformProviderDmaVisibilityCycle providerCycle)
    {
        public PlatformDmaVisibilityCycle LocalCycle { get; set; } = localCycle;
        public PlatformProviderDmaVisibilityCycle ProviderCycle { get; set; } = providerCycle;
        public bool Acquired { get; set; }
        public bool Consumed { get; set; }
    }

    private readonly Dictionary<PlatformDmaGrantId, DmaVisibilityState> _dmaVisibilityStates = [];
    private ulong _nextDmaVisibilityCycle = 1;

    internal KernelResult<PlatformDmaPrepareEvidence> PrepareDmaGrantVisibility(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_dmaCompletionGate)
            return PrepareDmaGrantVisibilityLocked(grant, expectedSubject);
    }

    private KernelResult<PlatformDmaPrepareEvidence> PrepareDmaGrantVisibilityLocked(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDmaGrant(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                validation.Error,
                validation.Message!);
        }

        if (HasFaultPinnedDmaSubmission(grant.GrantId))
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                KernelError.PlatformFaulted,
                "DMA visibility cannot be re-prepared while submission state is fault-pinned.");
        }

        if (HasActiveDmaSubmission(grant.GrantId))
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                KernelError.PlatformBindingDraining,
                "DMA visibility cannot be re-prepared while the exact submitted operation is still in its post-submit lifecycle.");
        }

        if (!_featureManifest.Supports(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaVisibilityContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission))
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not advertise DMA visibility contract v2.");
        }

        if (_provider is not IPlatformDmaVisibilityProvider visibilityProvider)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not expose grant-scoped DMA visibility preparation.");
        }

        var record = _dmaGrants[grant.GrantId];
        var providerGrant = record.ProviderGrant;
        var epoch = BackendEpoch;
        PlatformProviderIncarnation incarnation;
        PlatformProviderIncarnation observedIncarnation;
        PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> providerResult;
        record.PreparationInFlight = true;
        try
        {
            incarnation = CurrentProviderIncarnation();
            if (BackendEpoch != epoch || incarnation.Value == 0 || incarnation != record.ProviderIncarnation ||
                HasFaultPinnedDmaSubmission(grant.GrantId) || HasActiveDmaSubmission(grant.GrantId) ||
                !ValidateDeviceLease(grant.DeviceLease, expectedSubject).IsSuccess ||
                !ValidateExactMapping(grant.Mapping, expectedSubject).IsSuccess)
            {
                _dmaSubmissionFaultPins.Add(grant.GrantId);
                return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted,
                    "DMA continuity changed before preparation; grant remains pinned.");
            }
            providerResult = visibilityProvider.PrepareDmaGrantVisibility(providerGrant);
            observedIncarnation = CurrentProviderIncarnation();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted,
                $"DMA preparation receipt was lost; grant remains pinned: {exception.GetType().Name}.");
        }
        finally { record.PreparationInFlight = false; }
        if (observedIncarnation != incarnation || BackendEpoch != epoch ||
            !_dmaGrants.TryGetValue(grant.GrantId, out var current) || !ReferenceEquals(current, record) ||
            !ValidateDmaGrant(grant, expectedSubject).IsSuccess)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted,
                "DMA grant or backend continuity changed during preparation; grant remains pinned.");
        }
        if (!providerResult.IsSuccess)
        {
            return FromProviderFailure<PlatformDmaPrepareEvidence>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerEvidence = providerResult.Value!;
        var providerValidation = PlatformDmaVisibilityContract.ValidatePrepareEvidence(
            providerGrant,
            providerEvidence);
        if (!providerValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed DMA prepare evidence.");
        }

        var localCycle = new PlatformDmaVisibilityCycle(NextLocalDmaVisibilityCycle());
        _dmaVisibilityStates[grant.GrantId] = new DmaVisibilityState(
            localCycle,
            providerEvidence.Cycle);
        return KernelResult<PlatformDmaPrepareEvidence>.Ok(
            new PlatformDmaPrepareEvidence(
                grant.GrantId,
                grant.Generation,
                localCycle,
                grant.Direction,
                PlatformMemoryVisibilityRequirement.PublicationFence,
                PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
    }

    internal KernelResult<PlatformDmaAcquireEvidence> AcquireDmaGrantVisibility(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_dmaCompletionGate)
            return AcquireDmaGrantVisibilityLocked(grant, expectedSubject);
    }

    private KernelResult<PlatformDmaAcquireEvidence> AcquireDmaGrantVisibilityLocked(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDmaGrant(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                validation.Error,
                validation.Message!);
        }

        if (HasFaultPinnedDmaSubmission(grant.GrantId))
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformFaulted,
                "CPU acquire is forbidden while DMA submission state is fault-pinned.");
        }

        if (grant.Direction == PlatformDmaDirection.DeviceReadsMemory)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformDenied,
                "Read-only device DMA cannot modify memory and does not require post-write CPU acquire evidence.");
        }

        if (HasActiveDmaSubmission(grant.GrantId))
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformBindingDraining,
                "The legacy acquire path cannot consume a submitted cycle; exact post-completion visibility must finish first.");
        }

        if (!_dmaVisibilityStates.TryGetValue(grant.GrantId, out var state) ||
            state.LocalCycle.Value == 0)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformDenied,
                "The exact DMA grant has no prepared visibility cycle to acquire.");
        }

        if (state.Consumed)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformDenied,
                "The current DMA visibility cycle has already been consumed and cannot satisfy another phase.");
        }

        if (state.Acquired)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformDenied,
                "The current DMA visibility cycle has already been acquired.");
        }

        if (_provider is not IPlatformDmaVisibilityProvider visibilityProvider)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformFaulted,
                "The provider that prepared DMA visibility no longer exposes CPU acquire.");
        }

        var record = _dmaGrants[grant.GrantId];
        var providerGrant = record.ProviderGrant;
        var epoch = BackendEpoch;
        PlatformProviderIncarnation incarnation;
        PlatformProviderIncarnation observedIncarnation;
        PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> providerResult;
        record.AcquisitionInFlight = true;
        try
        {
            incarnation = CurrentProviderIncarnation();
            if (BackendEpoch != epoch || incarnation.Value == 0 || incarnation != record.ProviderIncarnation ||
                HasFaultPinnedDmaSubmission(grant.GrantId) || HasActiveDmaSubmission(grant.GrantId) ||
                !ValidateDeviceLease(grant.DeviceLease, expectedSubject).IsSuccess ||
                !ValidateExactMapping(grant.Mapping, expectedSubject).IsSuccess ||
                !_dmaVisibilityStates.TryGetValue(grant.GrantId, out var preparedState) ||
                !ReferenceEquals(preparedState, state) || state.Consumed || state.Acquired)
            {
                _dmaSubmissionFaultPins.Add(grant.GrantId);
                return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted,
                    "DMA visibility cycle or continuity changed before acquire; grant remains pinned.");
            }
            providerResult = visibilityProvider.AcquireDmaGrantVisibility(providerGrant);
            observedIncarnation = CurrentProviderIncarnation();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted,
                $"DMA acquire receipt was lost; grant remains pinned: {exception.GetType().Name}.");
        }
        finally { record.AcquisitionInFlight = false; }
        if (observedIncarnation != incarnation || BackendEpoch != epoch ||
            !_dmaGrants.TryGetValue(grant.GrantId, out var current) || !ReferenceEquals(current, record) ||
            !_dmaVisibilityStates.TryGetValue(grant.GrantId, out var currentState) || !ReferenceEquals(currentState, state) ||
            !ValidateDmaGrant(grant, expectedSubject).IsSuccess)
        {
            _dmaSubmissionFaultPins.Add(grant.GrantId);
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted,
                "DMA grant, visibility cycle or backend continuity changed during acquire; grant remains pinned.");
        }
        if (!providerResult.IsSuccess)
        {
            return FromProviderFailure<PlatformDmaAcquireEvidence>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerEvidence = providerResult.Value!;
        var providerValidation = PlatformDmaVisibilityContract.ValidateAcquireEvidence(
            providerGrant,
            state.ProviderCycle,
            providerEvidence);
        if (!providerValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed DMA acquire evidence.");
        }

        state.Acquired = true;
        state.Consumed = true;
        return KernelResult<PlatformDmaAcquireEvidence>.Ok(
            new PlatformDmaAcquireEvidence(
                grant.GrantId,
                grant.Generation,
                state.LocalCycle,
                grant.Direction,
                PlatformMemoryAcquireRequirement.AcquisitionFence,
                PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied));
    }

    internal bool HasPreparedUnacquiredDmaVisibilityCycle(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_dmaCompletionGate)
        {
            return ValidateDmaGrant(grant, expectedSubject).IsSuccess &&
                   !HasFaultPinnedDmaSubmission(grant.GrantId) &&
                   !HasActiveDmaSubmission(grant.GrantId) &&
                   _dmaVisibilityStates.TryGetValue(grant.GrantId, out var state) &&
                   state.LocalCycle.Value != 0 &&
                   !state.Acquired &&
                   !state.Consumed;
        }
    }

    private ulong NextLocalDmaVisibilityCycle()
    {
        var value = _nextDmaVisibilityCycle;
        unchecked { _nextDmaVisibilityCycle++; }
        if (value == 0)
            throw new InvalidOperationException("Local DMA visibility cycle identity space is exhausted.");
        return value;
    }
}
