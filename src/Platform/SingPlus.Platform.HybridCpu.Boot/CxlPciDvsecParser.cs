using System.Buffers.Binary;
using SingNext.Boot.Core;

namespace SingPlus.Platform.HybridCpu.Boot;

public readonly record struct PciDvsecHeader(ushort Offset, ushort PcieCapabilityId, ushort PcieCapabilityVersion,
    ushort NextOffset, ushort VendorId, byte Revision, ushort Length, ushort DvsecId)
{
    public bool NextOverlapsPayload => NextOffset != 0 && NextOffset >= Offset && NextOffset < Offset + Length;
}
public readonly record struct CxlDeviceDvsecInfo(PciDvsecHeader Header, ushort Capability, ushort Control, ushort Status)
{
    public bool CacheCapable => (Capability & 1) != 0;
    public bool IoCapable => (Capability & 2) != 0;
    public bool MemoryCapable => (Capability & 4) != 0;
}
public readonly record struct CxlRegisterBlock(byte BarIndex, byte BlockIdentifier, ulong Offset);
public sealed record CxlRegisterLocatorInfo(PciDvsecHeader Header, IReadOnlyList<CxlRegisterBlock> Blocks);

/// <summary>Decodes actual PCI DVSEC wire headers. The legacy bounded v1 abstraction remains unchanged.</summary>
public static class CxlPciDvsecParser
{
    public const ushort PcieDvsecCapabilityId = 0x23;
    public const ushort CxlVendorId = 0x1e98;
    public const ushort DeviceDvsecId = 0;
    public const ushort RegisterLocatorDvsecId = 8;

    public static BootResult<PciDvsecHeader> ParseHeader(ReadOnlySpan<byte> configuration, ushort offset, bool opaqueHeaderOnly = false)
    {
        if (configuration.Length > 4096 || offset < 0x100 || (offset & 3) != 0 || offset > configuration.Length - 10)
            return BootResult<PciDvsecHeader>.Fail(BootFailure.BoundsViolation, "DVSEC header is outside bounded PCI configuration.");
        uint cap = U32(configuration, offset), header1 = U32(configuration, offset + 4);
        ushort id = (ushort)cap, version = (ushort)((cap >> 16) & 15), next = (ushort)(cap >> 20);
        ushort length = (ushort)(header1 >> 20);
        if (id != PcieDvsecCapabilityId || version != 1)
            return BootResult<PciDvsecHeader>.Fail(BootFailure.Unsupported, "Expected PCIe DVSEC capability version 1.");
        if (length < 10 || length > configuration.Length - offset ||
            (next != 0 && (next < 0x100 || next > 0xffc || (next & 3) != 0 || (next >= offset && next < offset + (opaqueHeaderOnly ? 10 : length)))))
            return BootResult<PciDvsecHeader>.Fail(BootFailure.Malformed, "DVSEC length or next capability pointer is invalid.");
        return BootResult<PciDvsecHeader>.Success(new(offset, id, version, next, (ushort)header1,
            (byte)((header1 >> 16) & 15), length, U16(configuration, offset + 8)));
    }

    public static BootResult<CxlDeviceDvsecInfo> ParseDevice(ReadOnlySpan<byte> configuration, ushort offset)
    {
        var parsed = ParseHeader(configuration, offset);
        if (!parsed.IsSuccess) return BootResult<CxlDeviceDvsecInfo>.Fail(parsed.Failure, parsed.Detail!);
        var h = parsed.Value;
        if (h.VendorId != CxlVendorId || h.DvsecId != DeviceDvsecId || h.Revision is < 1 or > 3)
            return BootResult<CxlDeviceDvsecInfo>.Fail(BootFailure.Unsupported, "Unsupported CXL Device DVSEC identity/revision.");
        // Only the common capability/control/status prefix is interpreted. Later optional features are not enabled.
        int minimum = h.Revision == 3 ? 0x3c : 0x38;
        if (h.Length < minimum || (h.Length & 3) != 0)
            return BootResult<CxlDeviceDvsecInfo>.Fail(BootFailure.Malformed, "CXL Device DVSEC is shorter than its revision layout.");
        return BootResult<CxlDeviceDvsecInfo>.Success(new(h, U16(configuration, offset + 10), U16(configuration, offset + 12), U16(configuration, offset + 14)));
    }

    public static BootResult<CxlRegisterLocatorInfo> ParseRegisterLocator(ReadOnlySpan<byte> configuration, ushort offset)
    {
        var parsed = ParseHeader(configuration, offset);
        if (!parsed.IsSuccess) return BootResult<CxlRegisterLocatorInfo>.Fail(parsed.Failure, parsed.Detail!);
        var h = parsed.Value;
        if (h.VendorId != CxlVendorId || h.DvsecId != RegisterLocatorDvsecId || h.Revision != 0)
            return BootResult<CxlRegisterLocatorInfo>.Fail(BootFailure.Unsupported, "Unsupported Register Locator DVSEC identity/revision.");
        if (h.Length < 20 || (h.Length - 12) % 8 != 0 || (h.Length - 12) / 8 > 16 || U16(configuration, offset + 10) != 0)
            return BootResult<CxlRegisterLocatorInfo>.Fail(BootFailure.Malformed, "Invalid Register Locator envelope/reserved field.");
        List<CxlRegisterBlock> blocks = new();
        for (int at = offset + 12; at < offset + h.Length; at += 8)
        {
            uint low = U32(configuration, at), high = U32(configuration, at + 4);
            byte bir = (byte)(low & 7), blockId = (byte)((low >> 8) & 255);
            if (low == 0 && high == 0) continue;
            if (bir > 5 || (low & 0xf8) != 0 || blockId is < 1 or > 3)
                return BootResult<CxlRegisterLocatorInfo>.Fail(BootFailure.Unsupported, "Unknown register block or invalid BIR/reserved bits.");
            var block = new CxlRegisterBlock(bir, blockId, ((ulong)high << 32) | (low & 0xffff0000u));
            if (blocks.Any(x => x.BlockIdentifier == blockId || (x.BarIndex == bir && x.Offset == block.Offset)))
                return BootResult<CxlRegisterLocatorInfo>.Fail(BootFailure.AmbiguousState, "Register block identity or address is duplicated.");
            blocks.Add(block);
        }
        if (blocks.Count == 0) return BootResult<CxlRegisterLocatorInfo>.Fail(BootFailure.NotFound, "No nonempty register blocks.");
        return BootResult<CxlRegisterLocatorInfo>.Success(new(h, blocks));
    }

    public static BootResult<ulong> ResolveRegisterBlock(ReadOnlySpan<byte> configuration, CxlRegisterBlock block)
    {
        if (configuration.Length < 64 || block.BarIndex > 5 || block.BlockIdentifier is < 1 or > 3 || (block.Offset & 0xffff) != 0)
            return BootResult<ulong>.Fail(BootFailure.BoundsViolation, "PCI header/BAR index is out of bounds.");
        byte type = (byte)(configuration[0x0e] & 0x7f);
        int count = type switch { 0 => 6, 1 => 2, _ => 0 };
        for (int index = 0; index < count; index++)
        {
            uint low = U32(configuration, 0x10 + index * 4);
            bool wide = (low & 7) == 4;
            if (index == block.BarIndex)
            {
                if ((low & 1) != 0 || (low & 6) is not (0 or 4) || (wide && index + 1 >= count))
                    return BootResult<ulong>.Fail(BootFailure.Unsupported, "Register locator does not name a valid memory BAR.");
                ulong address = low & 0xfffffff0u;
                if (wide) address |= (ulong)U32(configuration, 0x14 + index * 4) << 32;
                if (address == 0 || address == 0xfffffff0 || address == 0xfffffffffffffff0 || block.Offset > ulong.MaxValue - address)
                    return BootResult<ulong>.Fail(BootFailure.BoundsViolation, "Unassigned BAR or address overflow.");
                return BootResult<ulong>.Success(address + block.Offset);
            }
            if (wide) { if (index + 1 == block.BarIndex) return BootResult<ulong>.Fail(BootFailure.Malformed, "BIR names the upper half of a 64-bit BAR."); index++; }
        }
        return BootResult<ulong>.Fail(BootFailure.BoundsViolation, "BIR is unavailable for this PCI header type.");
    }
    private static ushort U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
}
