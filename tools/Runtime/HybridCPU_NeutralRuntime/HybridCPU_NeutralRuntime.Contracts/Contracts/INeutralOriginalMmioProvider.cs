namespace YAKSys_Hybrid_CPU.Core;
public readonly record struct NeutralOriginalMmioMapRequest(Guid InvocationId,Guid RuntimeIncarnation,Guid DeviceBindInvocationId,NeutralDeviceLease Device,NeutralMmioRegionIdentity Region,NeutralMmioRange Range,NeutralMmioAccess Access,NeutralMmioBacking? Backing = null);
public readonly record struct NeutralOriginalMmioMapObservation(NeutralOriginalMmioMapRequest Request,bool SourceFenceMatched,bool NoEffectGuaranteed,NeutralMmioMapResult Observation);
public readonly record struct NeutralOriginalMmioCloseRequest(Guid InvocationId,Guid RuntimeIncarnation,Guid MapInvocationId,NeutralMmioLease Mapping);
public readonly record struct NeutralOriginalMmioCloseObservation(NeutralOriginalMmioCloseRequest Request,bool SourceFenceMatched,bool NoEffectGuaranteed,NeutralMmioCloseResult Observation);
/// <summary>Original MMIO owner fence; pure surviving-original lookup never effects or infers closure from absence.</summary>
public interface INeutralOriginalMmioProvider : INeutralOriginalDeviceProvider
{
    NeutralOriginalMmioMapObservation MapMmio(NeutralOriginalMmioMapRequest request);
    bool ValidateMmioAdmission(NeutralOriginalMmioMapRequest request);
    NeutralOriginalMmioMapObservation? ResolveMmioMap(NeutralOriginalMmioMapRequest request);
    NeutralOriginalMmioCloseObservation CloseMmio(NeutralOriginalMmioCloseRequest request);
    NeutralOriginalMmioCloseObservation? ResolveMmioClose(NeutralOriginalMmioCloseRequest request);
}
