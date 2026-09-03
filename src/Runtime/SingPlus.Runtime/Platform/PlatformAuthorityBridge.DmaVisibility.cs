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
    }

    private readonly Dictionary<PlatformDmaGrantId, DmaVisibilityState> _dmaVisibilityStates = [];
    private ulong _nextDmaVisibilityCycle = 1;

    internal KernelResult<PlatformDmaPrepareEvidence> PrepareDmaGrantVisibility(
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
                "DMA visibility cannot be re-prepared while the exact submitted operation is pending completion.");
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

        var providerGrant = _dmaGrants[grant.GrantId].ProviderGrant;
        var providerResult = visibilityProvider.PrepareDmaGrantVisibility(providerGrant);
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
                "CPU acquire is forbidden until completion is proven for the exact submitted operation.");
        }

        if (!_dmaVisibilityStates.TryGetValue(grant.GrantId, out var state) ||
            state.LocalCycle.Value == 0)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                KernelError.PlatformDenied,
                "The exact DMA grant has no prepared visibility cycle to acquire.");
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

        var providerGrant = _dmaGrants[grant.GrantId].ProviderGrant;
        var providerResult = visibilityProvider.AcquireDmaGrantVisibility(providerGrant);
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
        PlatformDomainIdentity expectedSubject) =>
        ValidateDmaGrant(grant, expectedSubject).IsSuccess &&
        !HasFaultPinnedDmaSubmission(grant.GrantId) &&
        !HasActiveDmaSubmission(grant.GrantId) &&
        _dmaVisibilityStates.TryGetValue(grant.GrantId, out var state) &&
        state.LocalCycle.Value != 0 &&
        !state.Acquired;

    private ulong NextLocalDmaVisibilityCycle()
    {
        var value = _nextDmaVisibilityCycle;
        unchecked { _nextDmaVisibilityCycle++; }
        if (value == 0)
            throw new InvalidOperationException("Local DMA visibility cycle identity space is exhausted.");
        return value;
    }
}
