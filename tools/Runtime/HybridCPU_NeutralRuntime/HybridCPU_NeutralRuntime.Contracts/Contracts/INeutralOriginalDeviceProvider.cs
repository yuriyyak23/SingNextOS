namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralOriginalDeviceBindRequest(Guid InvocationId, Guid RuntimeIncarnation,
    Guid DomainBindInvocationId, NeutralDomainBindingLease Domain, NeutralDeviceIdentity Device, NeutralDeviceRights Rights);
public readonly record struct NeutralOriginalDeviceBindObservation(NeutralOriginalDeviceBindRequest Request,
    bool SourceFenceMatched, bool NoEffectGuaranteed, NeutralDeviceBindResult Observation);
public readonly record struct NeutralOriginalDeviceCloseRequest(Guid InvocationId, Guid RuntimeIncarnation,
    Guid DeviceBindInvocationId, NeutralDeviceLease Device);
public readonly record struct NeutralOriginalDeviceCloseObservation(NeutralOriginalDeviceCloseRequest Request,
    bool SourceFenceMatched, bool NoEffectGuaranteed, NeutralDeviceCloseResult Observation);

/// <summary>Optional original device producing-owner protocol. Original request/source/
/// parent factory and dependent inventory checked atomically with effect; intent/raw
/// retained by existing owners. Exact no-effect is separate from closure. Pure lookup
/// cannot effect and missing/history loss is unknown.</summary>
public interface INeutralOriginalDeviceProvider : INeutralOriginalDomainCloseProvider
{
    NeutralOriginalDeviceBindObservation BindDevice(NeutralOriginalDeviceBindRequest request);
    NeutralOriginalDeviceBindObservation? ResolveDeviceBind(NeutralOriginalDeviceBindRequest request);
    NeutralOriginalDeviceCloseObservation CloseDevice(NeutralOriginalDeviceCloseRequest request);
    NeutralOriginalDeviceCloseObservation? ResolveDeviceClose(NeutralOriginalDeviceCloseRequest request);
}
