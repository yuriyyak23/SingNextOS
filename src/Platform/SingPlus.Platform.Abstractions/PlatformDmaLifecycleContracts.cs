namespace SingPlus.Platform;

public enum PlatformDmaPostCompletionVisibilityRequirement
{
    None = 0,
    AcquisitionFence,
}

public enum PlatformDmaPostCompletionVisibilityOutcome
{
    NotRequired = 0,
    AcquisitionFenceSatisfied,
}

/// <summary>
/// Version 5 composes exact completion proof with direction-aware post-completion
/// visibility and controlled release of the submitted-operation lifetime. It does
/// not make completion or visibility evidence into authority and does not weaken
/// the requirement to close DMA/mapping/device authority before CPU reclaim.
/// </summary>
public static class PlatformDmaLifecycleContract
{
    public const uint ContractVersion = 5;
}
