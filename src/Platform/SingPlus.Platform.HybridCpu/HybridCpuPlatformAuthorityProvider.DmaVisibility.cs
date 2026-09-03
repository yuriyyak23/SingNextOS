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

    public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
        PlatformProviderDmaGrant grant)
    {
        var validation = ValidateProviderDmaGrantForVisibility(grant);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                validation.Status,
                validation.Message ?? "The provider DMA grant is not live for visibility preparation.");
        }

        var record = _dmaGrants[grant.GrantId];
        var external = _runtime.PrepareDmaVisibility(record.HybridCpuGrant);
        if (!external.IsPrepared)
        {
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                external.Decision switch
                {
                    NeutralDmaPrepareDecision.NotFound => PlatformAuthorityStatus.Faulted,
                    NeutralDmaPrepareDecision.Stale => PlatformAuthorityStatus.Faulted,
                    NeutralDmaPrepareDecision.Revoked => PlatformAuthorityStatus.Revoked,
                    NeutralDmaPrepareDecision.VisibilityUnsupported => PlatformAuthorityStatus.Unsupported,
                    _ => PlatformAuthorityStatus.Faulted,
                },
                external.Reason);
        }

        if (external.Evidence.GrantHandle != record.HybridCpuGrant.Handle ||
            external.Evidence.GrantEpoch != record.HybridCpuGrant.Epoch ||
            external.Evidence.Direction != ToNeutralDmaDirection(grant.Direction) ||
            external.Evidence.Cycle.Value == 0)
        {
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned DMA prepare evidence for a different neutral grant cycle.");
        }

        var providerCycle = new PlatformProviderDmaVisibilityCycle(
            NextNonZero(ref _nextProviderDmaVisibilityCycle));
        _dmaVisibilityStates[grant.GrantId] = new ProviderDmaVisibilityState(
            providerCycle,
            external.Evidence.Cycle);

        var evidence = new PlatformProviderDmaPrepareEvidence(
            grant.GrantId,
            grant.Generation,
            providerCycle,
            grant.Direction,
            PlatformMemoryVisibilityRequirement.PublicationFence,
            PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied);
        var evidenceValidation = PlatformDmaVisibilityContract.ValidatePrepareEvidence(
            grant,
            evidence);
        return evidenceValidation.IsSuccess
            ? PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(evidence)
            : PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                evidenceValidation.Status,
                evidenceValidation.Message ?? "Provider DMA prepare evidence is malformed.");
    }

    public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> AcquireDmaGrantVisibility(
        PlatformProviderDmaGrant grant)
    {
        var validation = ValidateProviderDmaGrantForVisibility(grant);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                validation.Status,
                validation.Message ?? "The provider DMA grant is not live for CPU acquire.");
        }

        if (grant.Direction == PlatformDmaDirection.DeviceReadsMemory)
        {
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                PlatformAuthorityStatus.Denied,
                "Read-only device DMA does not require post-write CPU acquire evidence.");
        }

        if (!_dmaVisibilityStates.TryGetValue(grant.GrantId, out var state) ||
            state.ProviderCycle.Value == 0)
        {
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider DMA grant has no prepared visibility cycle to acquire.");
        }

        if (state.Acquired)
        {
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider DMA visibility cycle has already been acquired.");
        }

        var record = _dmaGrants[grant.GrantId];
        var external = _runtime.AcquireDmaVisibility(record.HybridCpuGrant);
        if (!external.IsAcquired)
        {
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                external.Decision switch
                {
                    NeutralDmaAcquireDecision.NotFound => PlatformAuthorityStatus.Faulted,
                    NeutralDmaAcquireDecision.Stale => PlatformAuthorityStatus.Faulted,
                    NeutralDmaAcquireDecision.Revoked => PlatformAuthorityStatus.Revoked,
                    NeutralDmaAcquireDecision.VisibilityUnsupported => PlatformAuthorityStatus.Unsupported,
                    NeutralDmaAcquireDecision.NotRequired => PlatformAuthorityStatus.Denied,
                    NeutralDmaAcquireDecision.NotPrepared => PlatformAuthorityStatus.Denied,
                    NeutralDmaAcquireDecision.AlreadyAcquired => PlatformAuthorityStatus.Denied,
                    _ => PlatformAuthorityStatus.Faulted,
                },
                external.Reason);
        }

        if (external.Evidence.GrantHandle != record.HybridCpuGrant.Handle ||
            external.Evidence.GrantEpoch != record.HybridCpuGrant.Epoch ||
            external.Evidence.Cycle != state.NeutralCycle ||
            external.Evidence.Direction != ToNeutralDmaDirection(grant.Direction))
        {
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned DMA acquire evidence for a different neutral grant cycle.");
        }

        var evidence = new PlatformProviderDmaAcquireEvidence(
            grant.GrantId,
            grant.Generation,
            state.ProviderCycle,
            grant.Direction,
            PlatformMemoryAcquireRequirement.AcquisitionFence,
            PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied);
        var evidenceValidation = PlatformDmaVisibilityContract.ValidateAcquireEvidence(
            grant,
            state.ProviderCycle,
            evidence);
        if (!evidenceValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                evidenceValidation.Status,
                evidenceValidation.Message ?? "Provider DMA acquire evidence is malformed.");
        }

        state.Acquired = true;
        return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(evidence);
    }

    private PlatformAuthorityResult ValidateProviderDmaGrantForVisibility(
        PlatformProviderDmaGrant grant)
    {
        if (!_dmaGrants.TryGetValue(grant.GrantId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider DMA grant does not exist.");
        }

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

        return PlatformAuthorityResult.Ok();
    }
}
