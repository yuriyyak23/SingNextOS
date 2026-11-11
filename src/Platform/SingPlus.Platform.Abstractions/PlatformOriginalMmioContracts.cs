namespace SingPlus.Platform;
/// <summary>Opaque original invocation identity, never permission or closure.</summary>
public sealed class PlatformOriginalMmioMapRequest
{
    public PlatformOriginalMmioMapRequest(PlatformProviderDeviceLease device,PlatformMmioRegionIdentity region,PlatformMmioRange range,PlatformMmioAccess access,PlatformMmioBacking? backing = null)
    { InvocationId = Guid.NewGuid(); Device = device; Region = region; Range = range; Access = access; Backing=backing; }
    public Guid InvocationId { get; }
    public PlatformProviderDeviceLease Device { get; }
    public PlatformMmioRegionIdentity Region { get; }
    public PlatformMmioRange Range { get; }
    public PlatformMmioAccess Access { get; }
    public PlatformMmioBacking? Backing { get; }
}
public readonly record struct PlatformOriginalMmioMapResolution(PlatformOriginalMmioMapRequest Request,bool NoEffectGuaranteed,PlatformProviderMmioLease? CleanupLease);
public interface IPlatformOriginalMmioProvider : IPlatformMmioLeaseProvider
{
    PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(PlatformOriginalMmioMapRequest request);
    PlatformAuthorityResult<PlatformOriginalMmioMapResolution> ResolveMmioMap(PlatformOriginalMmioMapRequest request);
    PlatformAuthorityResult ResolveMmioRevocation(PlatformProviderMmioLease lease);
}
/// <summary>NotAccepted proves only exact zero effect of this call, never MMIO closure.</summary>
public interface IPlatformMmioRevocationNoEffectProvider : IPlatformMmioLeaseProvider { }
