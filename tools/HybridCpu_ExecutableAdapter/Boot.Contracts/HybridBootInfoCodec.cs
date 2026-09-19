using System.Security.Cryptography;

namespace YAKSys_Hybrid_CPU.Boot.Contracts;

public static class HybridBootInfoCodec
{
    public const int HeaderSize = 120;
    public const int DigestOffset = 64;
    public const int DigestSize = 48;
    private const int RecordHeaderSize = 8;

    public static byte[] Encode(HybridBootInfoV1 info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Records.Count > BootAbiV1.MaxBootInfoRecords) throw new ArgumentOutOfRangeException(nameof(info));
        var recordBytes = info.Records.Sum(static x => Align8(checked(RecordHeaderSize + x.Payload.Length)));
        var total = checked(HeaderSize + recordBytes);
        if (total > BootAbiV1.MaxBootInfoBytes) throw new ArgumentOutOfRangeException(nameof(info));
        var b = new byte[total];
        BootWire.U32(b, 0, BootWire.BootInfoMagic); BootWire.U16(b, 4, 1); BootWire.U16(b, 6, 0); BootWire.U32(b, 8, HeaderSize); BootWire.U32(b, 12, (uint)total);
        BootWire.U64(b, 16, info.Flags); BootWire.WriteUuid(b.AsSpan(24), info.PlatformId); BootWire.U32(b, 40, info.CpuAbiVersion); BootWire.U32(b, 44, info.FirmwareBootAbiVersion);
        BootWire.U32(b, 48, (uint)info.ResetReason); BootWire.U32(b, 52, (uint)info.Records.Count); BootWire.U64(b, 56, info.ResetSequence);
        var cursor = HeaderSize;
        foreach (var record in info.Records)
        {
            if (record.Payload.Length > BootAbiV1.MaxEvidenceBytes) throw new ArgumentOutOfRangeException(nameof(info));
            BootWire.U16(b, cursor, (ushort)record.Kind); BootWire.U16(b, cursor + 2, (ushort)record.Flags); BootWire.U32(b, cursor + 4, (uint)(RecordHeaderSize + record.Payload.Length));
            record.Payload.Span.CopyTo(b.AsSpan(cursor + 8)); cursor += Align8(8 + record.Payload.Length);
        }
        ComputeDigest(b).CopyTo(b, DigestOffset);
        BootWire.U32(b, 112, BootWire.Crc32C(b.AsSpan(0, 112).ToArray().Concat(b.AsSpan(116).ToArray()).ToArray()));
        return b;
    }

    public static BootParseResult<HybridBootInfoV1> Parse(ReadOnlySpan<byte> b, ISet<BootEvidenceKind>? supportedRequiredRecords = null)
    {
        if (b.Length < HeaderSize) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.Truncated, "BootInfo header is truncated.");
        if (BootWire.R32(b, 0) != BootWire.BootInfoMagic) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidMagic, "BootInfo magic is invalid.");
        if (BootWire.R16(b, 4) != 1) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.UnsupportedVersion, "BootInfo major version is unsupported.");
        var header = BootWire.R32(b, 8); var total = BootWire.R32(b, 12); var count = BootWire.R32(b, 52);
        if (header != HeaderSize || total < HeaderSize || total > BootAbiV1.MaxBootInfoBytes || total > b.Length)
            return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidLength, "BootInfo size is invalid.");
        if (count > BootAbiV1.MaxBootInfoRecords) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.LimitExceeded, "BootInfo record count exceeds v1 limit.");
        if (BootWire.R32(b, 116) != 0) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidReserved, "BootInfo reserved field is nonzero.");
        var crcBytes = b[..112].ToArray().Concat(b[116..(int)total].ToArray()).ToArray();
        if (BootWire.R32(b, 112) != BootWire.Crc32C(crcBytes)) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidCrc, "BootInfo CRC32C mismatch.");
        var expected = ComputeDigest(b[..(int)total]);
        if (!CryptographicOperations.FixedTimeEquals(expected, b.Slice(DigestOffset, DigestSize)))
            return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidDigest, "BootInfo SHA-384 mismatch.");
        var records = new List<BootEvidenceRecord>((int)count); var requiredKinds = new HashSet<ushort>(); var cursor = HeaderSize;
        for (var i = 0u; i < count; i++)
        {
            if (total - cursor < RecordHeaderSize) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.Truncated, "BootInfo record header is truncated.");
            var rawKind = BootWire.R16(b, cursor); var flags = (BootRecordFlags)BootWire.R16(b, cursor + 2); var size = BootWire.R32(b, cursor + 4);
            if (size < RecordHeaderSize || size > BootAbiV1.MaxEvidenceBytes + RecordHeaderSize || !BootWire.TryRange((ulong)cursor, size, total, out _, out _))
                return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidLength, "BootInfo record size is invalid.");
            if ((flags & ~BootRecordFlagMasks.Known) != 0)
                return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.UnknownRequiredFeature, "BootInfo record has unknown flag bits.");
            if ((flags & BootRecordFlags.Required) != 0 && !requiredKinds.Add(rawKind))
                return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.DuplicateRequiredField, "Required BootInfo record is duplicated.");
            var known = Enum.IsDefined((BootEvidenceKind)rawKind);
            if (!known && (flags & BootRecordFlags.Required) != 0)
                return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.UnknownRequiredRecord, "Unknown required BootInfo record.");
            if (known && (flags & BootRecordFlags.Required) != 0 && supportedRequiredRecords?.Contains((BootEvidenceKind)rawKind) != true)
                return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.UnknownRequiredRecord, "Unsupported required BootInfo record.");
            if (known) records.Add(new((BootEvidenceKind)rawKind, flags, b.Slice(cursor + 8, (int)size - 8).ToArray()));
            var next = (ulong)cursor + (ulong)Align8((int)size);
            if (next > total) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.Truncated, "BootInfo record padding is truncated.");
            if (b.Slice(cursor + (int)size, (int)next - cursor - (int)size).IndexOfAnyExcept((byte)0) >= 0)
                return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidReserved, "BootInfo record padding is nonzero.");
            cursor = (int)next;
        }
        if (cursor != total) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidLength, "BootInfo record count does not consume the envelope.");
        var reset = (ResetReason)BootWire.R32(b, 48);
        if (!Enum.IsDefined(reset)) return BootParseResult<HybridBootInfoV1>.Fail(BootParseFailure.InvalidEnum, "BootInfo reset reason is invalid.");
        return BootParseResult<HybridBootInfoV1>.Success(new(BootWire.R64(b, 16), BootWire.ReadUuid(b[24..]), BootWire.R32(b, 40), BootWire.R32(b, 44), reset, BootWire.R64(b, 56), records));
    }

    private static int Align8(int value) => checked((value + 7) & ~7);

    private static byte[] ComputeDigest(ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        copy.AsSpan(DigestOffset, DigestSize).Clear();
        copy.AsSpan(112, 4).Clear();
        return SHA384.HashData(copy);
    }
}
