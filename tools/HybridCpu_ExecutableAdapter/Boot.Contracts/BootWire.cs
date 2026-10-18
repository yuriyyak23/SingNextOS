using System.Buffers.Binary;
using System.Security.Cryptography;

namespace YAKSys_Hybrid_CPU.Boot.Contracts;

public static class BootWire
{
    public const uint PolicyMagic = 0x31504248;   // HBP1
    public const uint VolumeMagic = 0x31564248;   // HBV1
    public const uint ManifestMagic = 0x31424E53; // SNB1
    public const uint BootInfoMagic = 0x31494248; // HBI1

    public static bool TryRange(ulong offset, ulong length, ulong containerLength, out int start, out int count)
    {
        start = count = 0;
        if (offset > containerLength || length > containerLength - offset || offset > int.MaxValue || length > int.MaxValue)
            return false;
        start = checked((int)offset);
        count = checked((int)length);
        return true;
    }

    public static bool IsAligned(ulong value, ulong alignment) =>
        alignment != 0 && (alignment & (alignment - 1)) == 0 && (value & (alignment - 1)) == 0;

    // UUIDs are RFC-4122/network byte order, never Guid.ToByteArray() wire order.
    public static void WriteUuid(Span<byte> destination, Guid value)
    {
        if (destination.Length < 16 || !value.TryWriteBytes(destination, bigEndian: true, out var written) || written != 16)
            throw new ArgumentException("A 16-byte destination is required.", nameof(destination));
    }

    public static Guid ReadUuid(ReadOnlySpan<byte> source)
    {
        if (source.Length < 16) throw new ArgumentException("A 16-byte UUID field is required.", nameof(source));
        return new Guid(source[..16], bigEndian: true);
    }

    public static uint Crc32C(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0x82F63B78u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    public static byte[] Sha384WithZeroedField(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length) throw new ArgumentOutOfRangeException(nameof(offset));
        var copy = bytes.ToArray();
        copy.AsSpan(offset, length).Clear();
        return SHA384.HashData(copy);
    }

    internal static void U16(Span<byte> b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b[o..], v);
    internal static void U32(Span<byte> b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
    internal static void U64(Span<byte> b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[o..], v);
    internal static ushort R16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    internal static uint R32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    internal static ulong R64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
}
