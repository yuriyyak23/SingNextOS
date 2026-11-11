namespace SingPlus.Platform;

public enum PlatformInterruptTrigger : byte
{
    Edge = 0,
    Level = 1,
}

public readonly record struct PlatformInterruptSourceIdentity(
    string ResourceId,
    PlatformInterruptTrigger Trigger);

public readonly record struct PlatformProviderIrqBindingId(ulong Value);
public readonly record struct PlatformProviderInterruptDeliverySequence(ulong Value);

public readonly record struct PlatformProviderIrqBinding(
    PlatformProviderIrqBindingId BindingId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDeviceLease DeviceLease,
    PlatformInterruptSourceIdentity Source);

public readonly record struct PlatformInterruptDeliveryObservation(
    PlatformProviderIrqBinding Binding,
    bool DeliveryAvailable,
    PlatformProviderInterruptDeliverySequence Sequence);

public static class PlatformIrqBindingContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformInterruptSourceIdentity source)
    {
        if (string.IsNullOrWhiteSpace(source.ResourceId) ||
            source.ResourceId.Length > 128 ||
            !Enum.IsDefined(source.Trigger))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Platform interrupt source must be a bounded semantic identity with a defined trigger mode.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateBinding(
        PlatformProviderDeviceLease requestedDevice,
        PlatformInterruptSourceIdentity requestedSource,
        PlatformProviderIrqBinding binding)
    {
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider interrupt binding identity must be materialized.");
        }

        if (binding.DeviceLease != requestedDevice || binding.Source != requestedSource)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider interrupt binding does not match the exact requested device and semantic source.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateObservation(
        PlatformProviderIrqBinding expectedBinding,
        PlatformInterruptDeliveryObservation observation)
    {
        if (observation.Binding != expectedBinding)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider interrupt delivery evidence belongs to a different binding.");
        }

        if (observation.DeliveryAvailable == (observation.Sequence.Value == 0))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider interrupt delivery evidence must carry a nonzero sequence exactly when delivery is available.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformIrqBindingProvider
{
    PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(
        PlatformProviderDeviceLease deviceLease,
        PlatformInterruptSourceIdentity source);

    PlatformAuthorityResult<PlatformInterruptDeliveryObservation> PollInterrupt(
        PlatformProviderIrqBinding binding);

    PlatformAuthorityResult CompleteInterruptDelivery(
        PlatformProviderIrqBinding binding,
        PlatformProviderInterruptDeliverySequence sequence);

    PlatformAuthorityResult RevokeInterrupt(PlatformProviderIrqBinding binding);
}
