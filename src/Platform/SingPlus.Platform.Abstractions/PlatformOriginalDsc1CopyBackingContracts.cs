namespace SingPlus.Platform;
public static class PlatformOriginalDsc1CopyBackingContract
{
    public const uint ContractVersion = 2;
    public static bool Supports(PlatformFeatureDescriptor feature) =>
        feature.Family == PlatformFeatureFamily.Dsc1BulkCompute &&
        feature.ContractVersion == ContractVersion && feature.Availability == PlatformFeatureAvailability.RuntimeAdmission;
}
public sealed class PlatformOriginalDsc1CopyBackingRequest(PlatformDsc1CopyRequest request,ReadOnlyMemory<byte> source,Memory<byte> destination)
{
    public Guid InvocationId {get;}=Guid.NewGuid();
    public PlatformDsc1CopyRequest Request {get;}=request;
    public ReadOnlyMemory<byte> Source {get;private set;}=source;
    public Memory<byte> Destination {get;private set;}=destination;
    public int ByteLength {get;}=source.Length;
    internal void Drop() {Source=default;Destination=default;}
}
public sealed record PlatformOriginalDsc1CopyBackingObservation(PlatformOriginalDsc1CopyBackingRequest Request,
    bool Completed,bool NoEffectGuaranteed,bool NoFurtherByteUseGuaranteed);
/// <summary>Additive software custody producer, independently of the lifecycle-only
/// ModelOnly DSC1 contract. It confers no compute, Region or budget permission.</summary>
public interface IPlatformOriginalDsc1CopyBackingProvider
{
    PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation> CopyDsc1Backing(PlatformOriginalDsc1CopyBackingRequest request);
    PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation> ResolveDsc1Backing(PlatformOriginalDsc1CopyBackingRequest request);
}
