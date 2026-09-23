using SingPlus.Platform;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingPlus.Runtime;

public sealed class ProviderFreshCxlBootDiscovery(
    ICxlEndpointEnumerationProvider enumeration,
    ICxlDiscoveryProvider provider) : IFreshCxlBootDiscovery
{
    public IReadOnlyList<CxlEndpointId> EnumerateCurrentEndpoints()
    {
        var current = enumeration.EnumerateCurrentEndpoints();
        if (!current.IsSuccess || current.Value is null) return [];
        var result = new List<CxlEndpointId>(current.Value.Count);
        foreach (var endpointId in current.Value.Distinct().OrderBy(static id => id.Value, StringComparer.Ordinal))
        {
            var live = provider.QueryEndpoint(endpointId);
            if (live.IsSuccess && live.Value is { Available: true } snapshot && snapshot.EndpointId == endpointId &&
                snapshot.DeviceGeneration.Value != 0 &&
                (snapshot.Features & (CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory)) ==
                (CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory))
                result.Add(endpointId);
        }
        return result;
    }
}

public sealed class PlatformFirmwareApertureRetirement(IFirmwareBootApertureOwner owner) : IFirmwareApertureRetirement
{
    public FirmwareApertureDisposition ReleaseInvalidateOrQuarantine(ulong resetSequence)
    {
        var result = owner.RetireFirmwareBootAperture(resetSequence);
        if (result.ResetSequence != resetSequence) return FirmwareApertureDisposition.Quarantined;
        return result.Status switch
        {
            FirmwareBootApertureRetirementStatus.Released => FirmwareApertureDisposition.Released,
            FirmwareBootApertureRetirementStatus.ProvenPreviouslyRetired => FirmwareApertureDisposition.Stale,
            _ => FirmwareApertureDisposition.Quarantined,
        };
    }
}

public interface IKernelPhysicalBootInfoReader
{
    bool TryCopy(ulong physicalAddress, Span<byte> kernelOwnedDestination);
}

public sealed class HybridBootInfoEntryAdapter(IKernelPhysicalBootInfoReader memory, HybridBootInfoImporter importer)
{
    public HybridBootTakeoverResult Import(ulong physicalAddress, uint byteLength)
    {
        if ((physicalAddress & 7) != 0 || byteLength < HybridBootInfoCodec.HeaderSize || byteLength > BootAbiV1.MaxBootInfoBytes ||
            physicalAddress > ulong.MaxValue - byteLength)
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.InvalidBootInfo, "BootInfo address, alignment, or length is invalid.", BootParseFailure.InvalidLength);
        var kernelOwned = GC.AllocateUninitializedArray<byte>(checked((int)byteLength));
        if (!memory.TryCopy(physicalAddress, kernelOwned))
        {
            Array.Clear(kernelOwned);
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.InvalidBootInfo, "BootInfo could not be copied into kernel-owned storage.", BootParseFailure.Truncated);
        }
        return importer.ImportAndDiscover(kernelOwned);
    }
}

public static class HybridBootRuntimeHandoff
{
    public static HybridBootInfoEntryAdapter Create(
        IKernelPhysicalBootInfoReader memory,
        ICxlEndpointEnumerationProvider enumeration,
        ICxlDiscoveryProvider provider,
        IFirmwareBootApertureOwner apertureOwner) =>
        new(memory, new HybridBootInfoImporter(
            new ProviderFreshCxlBootDiscovery(enumeration, provider),
            provider,
            new PlatformFirmwareApertureRetirement(apertureOwner)));
}
