namespace YAKSys_Hybrid_CPU.Boot.Contracts;

public static class BootVolumeHeaderCodec
{
    public const int FixedFieldsSize = 112;

    public static byte[] Encode(BootVolumeHeaderV1 h)
    {
        var b = new byte[BootAbiV1.VolumeHeaderBytes];
        BootWire.U32(b, 0, BootWire.VolumeMagic); BootWire.U16(b, 4, 1); BootWire.U16(b, 6, BootAbiV1.VolumeHeaderBytes);
        BootWire.U64(b, 8, h.MetadataSequence); BootWire.WriteUuid(b.AsSpan(16), h.BootVolumeId); BootWire.WriteUuid(b.AsSpan(32), h.ReplicaId);
        BootWire.U64(b, 48, h.PersistentCapacityBytes); BootWire.U64(b, 56, h.ManifestAOffset); BootWire.U32(b, 64, h.ManifestAMaxBytes); BootWire.U32(b, 68, h.ManifestAFlags);
        BootWire.U64(b, 72, h.ManifestBOffset); BootWire.U32(b, 80, h.ManifestBMaxBytes); BootWire.U32(b, 84, h.ManifestBFlags);
        BootWire.U64(b, 88, h.RecoveryManifestOffset); BootWire.U32(b, 96, h.RecoveryManifestMaxBytes); BootWire.U32(b, 100, h.Flags);
        BootWire.U32(b, BootAbiV1.VolumeHeaderBytes - 4, BootWire.Crc32C(b.AsSpan(0, BootAbiV1.VolumeHeaderBytes - 4)));
        return b;
    }

    public static BootParseResult<BootVolumeHeaderV1> Parse(ReadOnlySpan<byte> b, ulong actualCapacity)
    {
        if (b.Length < BootAbiV1.VolumeHeaderBytes) return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.Truncated, "HBV header is truncated.");
        b = b[..BootAbiV1.VolumeHeaderBytes];
        if (BootWire.R32(b, 0) != BootWire.VolumeMagic) return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.InvalidMagic, "HBV magic is invalid.");
        if (BootWire.R16(b, 4) != 1) return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.UnsupportedVersion, "HBV version is unsupported.");
        if (BootWire.R16(b, 6) != BootAbiV1.VolumeHeaderBytes) return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.InvalidLength, "HBV header size is invalid.");
        if (BootWire.R32(b, BootAbiV1.VolumeHeaderBytes - 4) != BootWire.Crc32C(b[..^4])) return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.InvalidCrc, "HBV CRC32C mismatch.");
        if (b[104..^4].IndexOfAnyExcept((byte)0) >= 0) return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.InvalidReserved, "HBV reserved bytes are nonzero.");
        var declared = BootWire.R64(b, 48);
        if (declared == 0 || declared > actualCapacity) return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.RangeOutsideContainer, "HBV declared capacity exceeds actual capacity.");
        foreach (var (offset, length) in new[] { (BootWire.R64(b, 56), (ulong)BootWire.R32(b, 64)), (BootWire.R64(b, 72), (ulong)BootWire.R32(b, 80)), (BootWire.R64(b, 88), (ulong)BootWire.R32(b, 96)) })
        {
            if (length == 0 && offset == 0) continue;
            if (length == 0 || length > BootAbiV1.MaxManifestBytes || !BootWire.IsAligned(offset, 4096) ||
                !BootWire.TryRange(offset, length, declared, out _, out _))
                return BootParseResult<BootVolumeHeaderV1>.Fail(BootParseFailure.RangeOutsideContainer, "HBV manifest range is invalid.");
        }
        return BootParseResult<BootVolumeHeaderV1>.Success(new(BootWire.R64(b, 8), BootWire.ReadUuid(b[16..]), BootWire.ReadUuid(b[32..]), declared,
            BootWire.R64(b, 56), BootWire.R32(b, 64), BootWire.R32(b, 68), BootWire.R64(b, 72), BootWire.R32(b, 80), BootWire.R32(b, 84),
            BootWire.R64(b, 88), BootWire.R32(b, 96), BootWire.R32(b, 100)));
    }
}
