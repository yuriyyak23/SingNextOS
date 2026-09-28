namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Features;

/// <summary>
/// Feature publication is intentionally disabled until per-family neutral
/// discovery and a validated backend manifest are both available.
/// </summary>
internal static class HybridCpuAdapterFeatureSource
{
    public static bool CanPublishExecutableClaims => false;
}
