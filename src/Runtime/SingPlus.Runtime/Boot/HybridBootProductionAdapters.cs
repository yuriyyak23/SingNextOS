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

public enum KernelBootMemoryKind
{
    NormalRam = 0,
    Mmio,
    FirmwareReserved,
    TemporaryCxlAperture,
    Reserved,
}

public interface IKernelBootMemoryMap
{
    bool TryClassify(ulong physicalAddress, ulong byteLength, out KernelBootMemoryKind kind);
    bool OverlapsKernelImage(ulong physicalAddress, ulong byteLength);
}

public interface IKernelPhysicalMemory
{
    bool TryCopyFromPhysical(ulong physicalAddress, Span<byte> destination);
}

public interface IKernelBootCopyEpoch
{
    ulong CurrentEpoch { get; }
}

/// <summary>
/// Copies BootInfo only from a range that the authoritative kernel memory map classifies wholly
/// as ordinary RAM. MMIO, reserved memory, firmware apertures, temporary CXL mappings and kernel
/// image overlap are rejected. A reset on either side of the copy invalidates and clears it.
/// </summary>
public sealed class KernelOwnedPhysicalBootInfoReader(
    IKernelBootMemoryMap memoryMap,
    IKernelPhysicalMemory physicalMemory,
    IKernelBootCopyEpoch copyEpoch,
    ulong expectedEpoch) : IKernelPhysicalBootInfoReader
{
    public bool TryCopy(ulong physicalAddress, Span<byte> kernelOwnedDestination)
    {
        if (kernelOwnedDestination.IsEmpty || physicalAddress > ulong.MaxValue - (ulong)kernelOwnedDestination.Length ||
            copyEpoch.CurrentEpoch != expectedEpoch ||
            !memoryMap.TryClassify(physicalAddress, (ulong)kernelOwnedDestination.Length, out var kind) ||
            kind != KernelBootMemoryKind.NormalRam ||
            memoryMap.OverlapsKernelImage(physicalAddress, (ulong)kernelOwnedDestination.Length) ||
            !physicalMemory.TryCopyFromPhysical(physicalAddress, kernelOwnedDestination))
        {
            kernelOwnedDestination.Clear();
            return false;
        }
        if (copyEpoch.CurrentEpoch == expectedEpoch) return true;
        kernelOwnedDestination.Clear();
        return false;
    }
}

public sealed class HybridBootInfoEntryAdapter(IKernelPhysicalBootInfoReader memory, HybridBootInfoImporter importer)
{
    public HybridBootTakeoverResult Import(ulong physicalAddress, uint byteLength)
    {
        if (physicalAddress == 0 || (physicalAddress & 7) != 0 || byteLength < HybridBootInfoCodec.HeaderSize || byteLength > BootAbiV1.MaxBootInfoBytes ||
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
