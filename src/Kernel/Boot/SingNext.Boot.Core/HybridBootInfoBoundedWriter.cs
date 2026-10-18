using System.Buffers.Binary;
using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.Boot.Core;

public static class HybridBootInfoBoundedWriter
{
    private const int RecordHeaderSize = 8;

    public static BootFailure TryWrite(
        HybridBootInfoV1 info,
        Span<byte> destination,
        Span<byte> sha384Scratch,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (info is null || sha384Scratch.Length < HybridBootInfoCodec.DigestSize ||
            info.Records.Count > BootAbiV1.MaxBootInfoRecords)
            return BootFailure.BoundsViolation;

        var total = HybridBootInfoCodec.HeaderSize;
        for (var i = 0; i < info.Records.Count; i++)
        {
            var length = info.Records[i].Payload.Length;
            if (length > BootAbiV1.MaxEvidenceBytes || total > BootAbiV1.MaxBootInfoBytes - Align8(RecordHeaderSize + length))
                return BootFailure.LimitExceeded;
            total += Align8(RecordHeaderSize + length);
        }
        if (total > destination.Length || total > BootAbiV1.MaxBootInfoBytes) return BootFailure.BoundsViolation;

        var output = destination[..total];
        output.Clear();
        U32(output, 0, BootWire.BootInfoMagic);
        U16(output, 4, BootAbiV1.Major);
        U16(output, 6, BootAbiV1.Minor);
        U32(output, 8, HybridBootInfoCodec.HeaderSize);
        U32(output, 12, (uint)total);
        U64(output, 16, info.Flags);
        BootWire.WriteUuid(output[24..], info.PlatformId);
        U32(output, 40, info.CpuAbiVersion);
        U32(output, 44, info.FirmwareBootAbiVersion);
        U32(output, 48, (uint)info.ResetReason);
        U32(output, 52, (uint)info.Records.Count);
        U64(output, 56, info.ResetSequence);

        var cursor = HybridBootInfoCodec.HeaderSize;
        for (var i = 0; i < info.Records.Count; i++)
        {
            var record = info.Records[i];
            U16(output, cursor, (ushort)record.Kind);
            U16(output, cursor + 2, (ushort)record.Flags);
            U32(output, cursor + 4, (uint)(RecordHeaderSize + record.Payload.Length));
            record.Payload.Span.CopyTo(output[(cursor + RecordHeaderSize)..]);
            cursor += Align8(RecordHeaderSize + record.Payload.Length);
        }

        var digest = sha384Scratch[..HybridBootInfoCodec.DigestSize];
        digest.Clear();
        if (!SHA384.TryHashData(output, digest, out var digestBytes) || digestBytes != digest.Length)
        {
            output.Clear();
            return BootFailure.Unsupported;
        }
        digest.CopyTo(output.Slice(HybridBootInfoCodec.DigestOffset, HybridBootInfoCodec.DigestSize));
        U32(output, 112, Crc32C(output[..112], output[116..]));
        bytesWritten = total;
        return BootFailure.None;
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private static uint Crc32C(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = uint.MaxValue;
        Update(ref crc, first);
        Update(ref crc, second);
        return ~crc;
    }

    private static void Update(ref uint crc, ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            crc ^= bytes[index];
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82F63B78u & (uint)-(int)(crc & 1));
        }
    }

    private static void U16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);
    private static void U32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void U64(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
}
