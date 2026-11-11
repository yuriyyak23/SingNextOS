namespace SingPlus.Platform;
public sealed class PlatformOriginalIrqBindRequest
{
    public PlatformOriginalIrqBindRequest(PlatformProviderDeviceLease device,PlatformInterruptSourceIdentity source)
    { InvocationId = Guid.NewGuid(); Device = device; Source = source; }
    public Guid InvocationId { get; }
    public PlatformProviderDeviceLease Device { get; }
    public PlatformInterruptSourceIdentity Source { get; }
}
public readonly record struct PlatformOriginalIrqBindResolution(PlatformOriginalIrqBindRequest Request,bool NoEffectGuaranteed,PlatformProviderIrqBinding? CleanupBinding);
public interface IPlatformOriginalIrqAdmissionProvider : IPlatformIrqBindingProvider
{
    PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(PlatformOriginalIrqBindRequest request);
    PlatformAuthorityResult<PlatformOriginalIrqBindResolution> ResolveIrqBind(PlatformOriginalIrqBindRequest request);
}
/// <summary>NotAccepted proves zero effect of this exact call only, never closure.</summary>
public interface IPlatformIrqRevocationNoEffectProvider : IPlatformIrqBindingProvider { }
public interface IPlatformOriginalIrqProvider : IPlatformIrqBindingProvider
{
    PlatformAuthorityResult ResolveIrqRevocation(PlatformProviderIrqBinding binding);
}
/// <summary>Success retires this exact original native IRQ lifetime, including unknown delivery invocations; no subsequent native delivery. Local events, independent faults and backing remain separate owners. Retry is pure after the first invocation.</summary>
public interface IPlatformOriginalIrqContainmentProvider : IPlatformIrqBindingProvider
{
    PlatformAuthorityResult ContainIrqDelivery(PlatformProviderIrqBinding binding);
}
