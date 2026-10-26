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
        public PlatformProviderDmaAcquireEvidence? KnownAcquireEvidence { get; set; }
        public PlatformBackendEpoch KnownAcquireEpoch { get; set; }
    }

    private readonly Dictionary<PlatformDmaGrantId, DmaVisibilityState> _dmaVisibilityStates = [];
    private ulong _nextDmaVisibilityCycle = 1;

    internal KernelResult<PlatformDmaPrepareEvidence> PrepareDmaGrantVisibility(
        PlatformDmaGrant grant, PlatformDomainIdentity expectedSubject,
        Func<Func<KernelResult<PlatformDmaPrepareEvidence>>, KernelResult<PlatformDmaPrepareEvidence>> ownerCommit)
    {
        DmaGrantRecord record;
        PlatformBackendEpoch epoch;
        IPlatformDmaVisibilityProvider visibilityProvider;
        DmaVisibilityOwnerInvocation? invocation = null;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
                KernelResult validation = ValidateDmaGrant(grant, expectedSubject);
            if (!validation.IsSuccess) return KernelResult<PlatformDmaPrepareEvidence>.Fail(validation.Error, validation.Message!);
            if (HasFaultPinnedDmaSubmission(grant.GrantId))
                return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted, "DMA visibility is fault-pinned.");
            if (HasActiveDmaSubmission(grant.GrantId))
                return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformBindingDraining, "Submitted DMA lifecycle forbids re-prepare.");
            if (_provider is not IPlatformDmaVisibilityProvider provider ||
                !_featureManifest.Supports(PlatformFeatureFamily.DmaMapping,
                    PlatformDmaVisibilityContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission))
                return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformUnsupported,
                    "The provider does not expose DMA visibility contract v2.");
            visibilityProvider = provider;
            record = _dmaGrants[grant.GrantId];
            epoch = BackendEpoch;
            record.PreparationInFlight = true;
            _dmaVisibilityStates.Remove(grant.GrantId); // A new provider cycle cannot reuse old local evidence.
        }
        try
        {
            bool canContain = _provider is IPlatformDmaVisibilityClosureProvider closure && closure.CanCloseOriginalDmaVisibility(record.ProviderGrant); // Pure provider callback outside owners.
            PlatformProviderIncarnation incarnation = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                if (BackendEpoch != epoch || incarnation.Value == 0 || incarnation != record.ProviderIncarnation ||
                    HasFaultPinnedDmaSubmission(grant.GrantId) || HasActiveDmaSubmission(grant.GrantId) ||
                    !ValidateDeviceLease(grant.DeviceLease, expectedSubject).IsSuccess ||
                    !ValidateExactMapping(grant.Mapping, expectedSubject).IsSuccess)
                {
                    _dmaSubmissionFaultPins.Add(grant.GrantId);
                    return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted, "DMA continuity changed before preparation; grant pinned.");
                }
            }
            lock (_dmaCompletionGate)
            {
                if (!OriginalVisibilityHistoryIntact(record)) throw new InvalidOperationException("Original visibility history lost.");
                if (record.VisibilityIssued == ulong.MaxValue) return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.CapacityExhausted,"Visibility history exhausted before effect.");
                invocation = new(record,false,epoch,canContain); record.VisibilityHistory.Add(invocation); record.VisibilityIssued++; invocation.Invoked = true;
            }
            PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> providerResult = visibilityProvider.PrepareDmaGrantVisibility(record.ProviderGrant);
            lock (_dmaCompletionGate) { invocation.RawPrepare = providerResult; if (!providerResult.IsSuccess) PinOriginalDmaVisibilityLocked(record,invocation); }
            if (providerResult.IsSuccess &&
                PlatformDmaVisibilityContract.ValidatePrepareEvidence(record.ProviderGrant, providerResult.Value!).IsSuccess)
            {
                lock (_dmaCompletionGate)
                {
                    record.KnownPreparationEvidence = providerResult.Value!;
                    record.KnownPreparationEpoch = epoch;
                }
            }
            PlatformProviderIncarnation observedIncarnation;
            try { observedIncarnation = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                lock (_dmaCompletionGate)
                {
                    if (!providerResult.IsSuccess || record.KnownPreparationEvidence != providerResult.Value ||
                        record.KnownPreparationEpoch != epoch)
                        PinOriginalDmaVisibilityLocked(record,invocation);
                }
                return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted,
                    "DMA prepare source unavailable; exact known receipt retained for grant cleanup, no local cycle published.");
            }
            // Trusted local owner composition only, never a provider callback.
            return ownerCommit(() =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dmaCompletionGate)
                {
                    record.PreparationInFlight = false;
                    if (!providerResult.IsSuccess && record.OwnVisibilityUncertain && IsOriginalDmaGrant(record) && BackendEpoch == epoch && observedIncarnation == incarnation)
                        return FromProviderFailure<PlatformDmaPrepareEvidence>(providerResult.Status,providerResult.Message);
                    if (observedIncarnation != incarnation || BackendEpoch != epoch ||
                        !_dmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? current) || !ReferenceEquals(current, record) ||
                        HasFaultPinnedDmaSubmission(grant.GrantId) || HasActiveDmaSubmission(grant.GrantId) ||
                        !ValidateDmaGrant(grant, expectedSubject).IsSuccess)
                    {
                        _dmaSubmissionFaultPins.Add(grant.GrantId);
                        return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted, "DMA source changed during preparation; grant pinned.");
                    }
                    if (!providerResult.IsSuccess)
                        return FromProviderFailure<PlatformDmaPrepareEvidence>(providerResult.Status, providerResult.Message);
                        PlatformProviderDmaPrepareEvidence evidence = providerResult.Value!;
                        PlatformAuthorityResult valid = PlatformDmaVisibilityContract.ValidatePrepareEvidence(record.ProviderGrant, evidence);
                    if (!valid.IsSuccess)
                        return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted, valid.Message ?? "Malformed DMA preparation evidence.");
                        PlatformDmaVisibilityCycle cycle = new PlatformDmaVisibilityCycle(NextLocalDmaVisibilityCycle());
                    _dmaVisibilityStates[grant.GrantId] = new DmaVisibilityState(cycle, evidence.Cycle);
                    return KernelResult<PlatformDmaPrepareEvidence>.Ok(new(grant.GrantId, grant.Generation, cycle,
                        grant.Direction, PlatformMemoryVisibilityRequirement.PublicationFence,
                        PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
                }
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_dmaCompletionGate) PinOriginalDmaVisibilityLocked(record,invocation);
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.PlatformFaulted,
                $"DMA preparation outcome unavailable; grant pinned: {exception.GetType().Name}.");
        }
        finally { lock (_dmaCompletionGate) record.PreparationInFlight = false; }
    }
    internal KernelResult<PlatformDmaAcquireEvidence> AcquireDmaGrantVisibility(
        PlatformDmaGrant grant, PlatformDomainIdentity expectedSubject,
        Func<Func<KernelResult<PlatformDmaAcquireEvidence>>, KernelResult<PlatformDmaAcquireEvidence>> ownerCommit)
    {
        DmaGrantRecord record;
        DmaVisibilityState state;
        PlatformBackendEpoch epoch;
        IPlatformDmaVisibilityProvider visibilityProvider;
        DmaVisibilityOwnerInvocation? invocation = null;
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
                KernelResult valid = ValidateDmaGrant(grant, expectedSubject);
            if (!valid.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(valid.Error, valid.Message!);
            if (HasFaultPinnedDmaSubmission(grant.GrantId))
                return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted, "Acquire grant is fault-pinned.");
            if (grant.Direction == PlatformDmaDirection.DeviceReadsMemory)
                return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformDenied, "Read-only DMA cannot require post-write acquire.");
            if (HasActiveDmaSubmission(grant.GrantId))
                return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformBindingDraining, "Submitted cycle requires exact post-completion visibility.");
            if (!_dmaVisibilityStates.TryGetValue(grant.GrantId, out state!) || state.LocalCycle.Value == 0 || state.Consumed || state.Acquired)
                return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformDenied, "No unconsumed exact prepared cycle exists.");
            if (_provider is not IPlatformDmaVisibilityProvider provider)
                return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted, "Prepared provider no longer exposes acquire.");
            visibilityProvider = provider;
            record = _dmaGrants[grant.GrantId];
            epoch = BackendEpoch;
            record.AcquisitionInFlight = true;
        }
        try
        {
            bool canContain = _provider is IPlatformDmaVisibilityClosureProvider closure && closure.CanCloseOriginalDmaVisibility(record.ProviderGrant); // Pure provider callback outside owners.
            PlatformProviderIncarnation incarnation = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            lock (_dmaCompletionGate)
            {
                if (BackendEpoch != epoch || incarnation.Value == 0 || incarnation != record.ProviderIncarnation ||
                    HasFaultPinnedDmaSubmission(grant.GrantId) || HasActiveDmaSubmission(grant.GrantId) ||
                    !ValidateDeviceLease(grant.DeviceLease, expectedSubject).IsSuccess ||
                    !ValidateExactMapping(grant.Mapping, expectedSubject).IsSuccess ||
                    !_dmaVisibilityStates.TryGetValue(grant.GrantId, out DmaVisibilityState? current) || !ReferenceEquals(current, state) || state.Consumed || state.Acquired)
                {
                    _dmaSubmissionFaultPins.Add(grant.GrantId);
                    return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted, "Acquire lost exact source/cycle; grant pinned.");
                }
            }
            lock (_dmaCompletionGate)
            {
                if (!OriginalVisibilityHistoryIntact(record)) throw new InvalidOperationException("Original visibility history lost.");
                if (record.VisibilityIssued == ulong.MaxValue) return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.CapacityExhausted,"Visibility history exhausted before effect.");
                invocation = new(record,true,epoch,canContain); record.VisibilityHistory.Add(invocation); record.VisibilityIssued++; invocation.Invoked = true;
            }
            PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> providerResult = visibilityProvider.AcquireDmaGrantVisibility(record.ProviderGrant);
            lock (_dmaCompletionGate) { invocation.RawAcquire = providerResult; if (!providerResult.IsSuccess) PinOriginalDmaVisibilityLocked(record,invocation); }
            if (providerResult.IsSuccess && PlatformDmaVisibilityContract.ValidateAcquireEvidence(
                    record.ProviderGrant, state.ProviderCycle, providerResult.Value!).IsSuccess)
            {
                lock (_dmaCompletionGate)
                {
                    state.KnownAcquireEvidence = providerResult.Value!;
                    state.KnownAcquireEpoch = epoch;
                    state.Consumed = true; // One exact provider acquire effect cannot be invoked again.
                }
            }
            PlatformProviderIncarnation observed;
            try { observed = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                lock (_dmaCompletionGate)
                    if (!providerResult.IsSuccess || state.KnownAcquireEvidence != providerResult.Value || state.KnownAcquireEpoch != epoch)
                        PinOriginalDmaVisibilityLocked(record,invocation);
                return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted, "Acquire source unavailable; known facts retained without CPU publication.");
            }
            return ownerCommit(() =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dmaCompletionGate)
                {
                    record.AcquisitionInFlight = false;
                    if (!providerResult.IsSuccess && record.OwnVisibilityUncertain && IsOriginalDmaGrant(record) && BackendEpoch == epoch && observed == incarnation)
                        return FromProviderFailure<PlatformDmaAcquireEvidence>(providerResult.Status,providerResult.Message);
                    if (observed != incarnation || BackendEpoch != epoch ||
                        !_dmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? current) || !ReferenceEquals(current, record) ||
                        !_dmaVisibilityStates.TryGetValue(grant.GrantId, out DmaVisibilityState? cycle) || !ReferenceEquals(cycle, state) ||
                        HasFaultPinnedDmaSubmission(grant.GrantId) || HasActiveDmaSubmission(grant.GrantId) ||
                        !ValidateDmaGrant(grant, expectedSubject).IsSuccess || state.Acquired)
                    {
                        _dmaSubmissionFaultPins.Add(grant.GrantId);
                        return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted, "Acquire source changed before local publication; grant pinned.");
                    }
                    if (!providerResult.IsSuccess)
                        return FromProviderFailure<PlatformDmaAcquireEvidence>(providerResult.Status, providerResult.Message);
                        PlatformAuthorityResult valid = PlatformDmaVisibilityContract.ValidateAcquireEvidence(record.ProviderGrant, state.ProviderCycle, providerResult.Value!);
                    if (!valid.IsSuccess)
                        return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted, valid.Message ?? "Malformed acquire evidence.");
                    state.Acquired = true;
                    state.Consumed = true;
                    return KernelResult<PlatformDmaAcquireEvidence>.Ok(new(grant.GrantId, grant.Generation, state.LocalCycle,
                        grant.Direction, PlatformMemoryAcquireRequirement.AcquisitionFence, PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied));
                }
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_dmaCompletionGate) PinOriginalDmaVisibilityLocked(record,invocation);
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.PlatformFaulted, $"Acquire outcome unavailable; grant pinned: {exception.GetType().Name}.");
        }
        finally { lock (_dmaCompletionGate) record.AcquisitionInFlight = false; }
    }
    internal bool HasPreparedUnacquiredDmaVisibilityCycle(
        PlatformDmaGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dmaCompletionGate)
        {
            return ValidateDmaGrant(grant, expectedSubject).IsSuccess &&
                   !HasFaultPinnedDmaSubmission(grant.GrantId) &&
                   !HasActiveDmaSubmission(grant.GrantId) &&
                   _dmaVisibilityStates.TryGetValue(grant.GrantId, out DmaVisibilityState? state) &&
                   state.LocalCycle.Value != 0 &&
                   !state.Acquired &&
                   !state.Consumed;
        }
    }

    private ulong NextLocalDmaVisibilityCycle()
    {
        ulong value = _nextDmaVisibilityCycle;
        unchecked { _nextDmaVisibilityCycle++; }
        if (value == 0)
            throw new InvalidOperationException("Local DMA visibility cycle identity space is exhausted.");
        return value;
    }
}
