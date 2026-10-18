using SingPlus.Platform;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalGuestUnmapProvider
{
    public bool SupportsOriginalGuestUnmapResolution => _childRuntime is INeutralOriginalGuestUnmapProvider { SupportsOriginalGuestUnmapResolution: true };
    public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> ResolveOriginalGuestUnmap(PlatformProviderGuestRegionMappingLease originalMapping)
    {
        var result = UnmapGuestRegionCore(originalMapping, true, out var exactNoEffect);
        return result.Status == PlatformAuthorityStatus.NotAccepted && !exactNoEffect
            ? PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original guest no-effect is not proven.") : result;
    }
}
