namespace SingPlus.Platform;

public readonly record struct PlatformProviderDmaVisibilityCycle(ulong Value);

public readonly record struct PlatformProviderDmaPrepareEvidence(
    PlatformProviderDmaGrantId GrantId,
    PlatformProviderLeaseGeneration GrantGeneration,
    PlatformProviderDmaVisibilityCycle Cycle,
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

public readonly record struct PlatformProviderDmaAcquireEvidence(
    PlatformProviderDmaGrantId GrantId,
    PlatformProviderLeaseGeneration GrantGeneration,
    PlatformProviderDmaVisibilityCycle Cycle,
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

/// <summary>
/// Version 2 extends admission-only DMA grants with exact non-coherent visibility cycles.
/// Prepare is release/publication evidence for a future device access. Acquire is CPU
/// visibility evidence for a direction in which the device may write. Neither is transfer
/// submission or completion evidence.
/// </summary>
public static class PlatformDmaVisibilityContract
{
    public const uint ContractVersion = 2;

    public static PlatformAuthorityResult ValidatePrepareEvidence(
        PlatformProviderDmaGrant grant,
        PlatformProviderDmaPrepareEvidence evidence)
    {
        if (grant.GrantId.Value == 0 || grant.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA visibility preparation requires a materialized provider DMA grant.");
        }

        if (evidence.GrantId != grant.GrantId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA prepare evidence belongs to a different provider grant.");
        }

        if (evidence.GrantGeneration != grant.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "DMA prepare evidence uses a stale provider grant generation.");
        }

        if (evidence.Cycle.Value == 0 || evidence.Direction != grant.Direction)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA prepare evidence does not match the exact grant visibility cycle or direction.");
        }

        if (!evidence.IsSatisfied)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "DMA prepare evidence does not prove the required publication fence.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateAcquireEvidence(
        PlatformProviderDmaGrant grant,
        PlatformProviderDmaVisibilityCycle expectedCycle,
        PlatformProviderDmaAcquireEvidence evidence)
    {
        if (grant.Direction == PlatformDmaDirection.DeviceReadsMemory)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Read-only device DMA does not require post-write CPU acquire evidence.");
        }

        if (evidence.GrantId != grant.GrantId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA acquire evidence belongs to a different provider grant.");
        }

        if (evidence.GrantGeneration != grant.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "DMA acquire evidence uses a stale provider grant generation.");
        }

        if (expectedCycle.Value == 0 ||
            evidence.Cycle != expectedCycle ||
            evidence.Direction != grant.Direction)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA acquire evidence does not match the exact prepared grant cycle or direction.");
        }

        if (!evidence.IsSatisfied)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "DMA acquire evidence does not prove the required CPU acquisition fence.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDmaVisibilityProvider
{
    PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
        PlatformProviderDmaGrant grant);

    PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> AcquireDmaGrantVisibility(
        PlatformProviderDmaGrant grant);
}
