using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal static class ResetAbiProfileV1
{
    public const ulong RomBase = 0x0000_0000_FFFC_0000;
    public const ulong RomBytes = 256 * 1024;
    public const ulong ResetVector = RomBase;
    public const ulong BundleAlignment = 256;
}

[Flags] internal enum PlatformAccess { None = 0, Read = 1, Write = 2, Execute = 4 }
internal enum PlatformRegionKind { SystemRam, ImmutableRom, BootScratch, Mmio, FirmwareReserved, FirmwareCxlBootAperture, Unmapped }

internal readonly record struct PlatformRegion(ulong Base, ulong Bytes, PlatformRegionKind Kind, PlatformAccess Access)
{
    public ulong EndExclusive => checked(Base + Bytes);
}

internal sealed class AdapterPhysicalMap
{
    private readonly PlatformRegion[] _regions;

    public AdapterPhysicalMap(IEnumerable<PlatformRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        _regions = regions.OrderBy(static x => x.Base).ToArray();
        if (_regions.Any(static x => x.Bytes == 0)) throw new ArgumentException("Zero-length regions are forbidden.", nameof(regions));
        for (var i = 0; i < _regions.Length; i++)
        {
            _ = _regions[i].EndExclusive;
            if (i != 0 && _regions[i - 1].EndExclusive > _regions[i].Base) throw new ArgumentException("Platform regions overlap.", nameof(regions));
        }
    }

    public PlatformRegion Resolve(ulong address, ulong bytes, PlatformAccess access)
    {
        if (bytes == 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        foreach (var region in _regions)
            if (address >= region.Base && address < region.EndExclusive && bytes <= region.EndExclusive - address)
            {
                if ((region.Access & access) != access) throw new UnauthorizedAccessException($"{access} is denied for {region.Kind}.");
                return region;
            }
        throw new InvalidOperationException("Physical range is unmapped or crosses a region boundary.");
    }
}

internal sealed record ResetSnapshotV1(
    ResetReason Reason, ulong ResetSequence, ulong ResetGeneration, ulong ProgramCounter,
    IReadOnlyList<ulong> IntegerRegisters, bool PhysicalAddressing, bool InterruptsMasked,
    bool PipelineEmpty, bool ReplayEmpty, bool RetireQueueEmpty, bool ExternalEffectsQuiesced,
    uint BootVirtualThread, bool SecondaryContextsParked);

internal sealed class AdapterResetModel
{
    private ulong _resetSequence;
    private ulong _resetGeneration;

    public ResetSnapshotV1 Assert(ResetReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new(reason, checked(++_resetSequence), checked(++_resetGeneration), ResetAbiProfileV1.ResetVector,
            Array.AsReadOnly(new ulong[32]), true, true, true, true, true, true, 0, true);
    }

    public bool IsCurrent(ulong resetGeneration) => resetGeneration != 0 && resetGeneration == _resetGeneration;
}
