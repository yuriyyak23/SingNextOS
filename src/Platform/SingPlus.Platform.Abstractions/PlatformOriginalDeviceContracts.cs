namespace SingPlus.Platform;

/// <summary>Same original request object retained before possible Bind. Copies/equal input
/// tuples are not invocation identity. This object grants no device permission.</summary>
public sealed class PlatformOriginalDeviceBindRequest(PlatformProviderDomainLease domain,PlatformDeviceIdentity device,PlatformDeviceRights rights)
{
    public Guid InvocationId { get; } = Guid.NewGuid();
    public PlatformProviderDomainLease Domain { get; } = domain;
    public PlatformDeviceIdentity Device { get; } = device;
    public PlatformDeviceRights Rights { get; } = rights;
}
public readonly record struct PlatformOriginalDeviceBindResolution(PlatformOriginalDeviceBindRequest Request,bool NoEffectGuaranteed,PlatformProviderDeviceLease? CleanupLease);
public interface IPlatformOriginalDeviceProvider : IPlatformDeviceLeaseProvider
{
    PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformOriginalDeviceBindRequest request);
    PlatformAuthorityResult<PlatformOriginalDeviceBindResolution> ResolveDeviceBind(PlatformOriginalDeviceBindRequest request);
    PlatformAuthorityResult ResolveDeviceRevocation(PlatformProviderDeviceLease device); // Pure surviving original closure, never effect/no-effect by absence.
}
/// <summary>Optional exact guarantee: NotAccepted on this RevokeDevice invocation
/// proves zero effect only; the admitted original device remains open.</summary>
public interface IPlatformDeviceRevocationNoEffectProvider : IPlatformDeviceLeaseProvider { }
/// <summary>Optional exact guarantee: NotAccepted on this Bind invocation proves zero
/// effect; no synthetic device/Closed is materialized.</summary>
public interface IPlatformDeviceAdmissionNoEffectProvider : IPlatformDeviceLeaseProvider { }
