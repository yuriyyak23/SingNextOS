namespace YAKSys_Hybrid_CPU.Boot.Contracts;

public static class BootManifestCodec
{
    public const int HeaderSize = 200;
    private const ushort TlvHeaderSize = 8;

    public static byte[] Encode(SingNextBootManifestV1 m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.SigningKeyId.Length != 32 || m.ComponentCount > BootAbiV1.MaxManifestComponents) throw new ArgumentOutOfRangeException(nameof(m));
        var extensionBytes = m.Extensions.Sum(static x => Align8(checked(TlvHeaderSize + x.Value.Length)));
        var total = checked(HeaderSize + extensionBytes);
        if (total > BootAbiV1.MaxManifestBytes) throw new ArgumentOutOfRangeException(nameof(m));
        var b = new byte[total];
        BootWire.U32(b, 0, BootWire.ManifestMagic); BootWire.U16(b, 4, 1); BootWire.U16(b, 6, 0); BootWire.U32(b, 8, HeaderSize);
        BootWire.U32(b, 12, (uint)total); BootWire.U32(b, 16, (uint)total); BootWire.U32(b, 20, m.Flags);
        BootWire.WriteUuid(b.AsSpan(24), m.BootVolumeId); BootWire.WriteUuid(b.AsSpan(40), m.ReplicaId); BootWire.WriteUuid(b.AsSpan(56), m.ImageId);
        BootWire.WriteUuid(b.AsSpan(72), m.RollbackDomainId); BootWire.U64(b, 88, m.ImageGeneration); BootWire.WriteUuid(b.AsSpan(96), m.PlatformFamilyId);
        BootWire.U32(b, 112, m.RequiredCpuAbiMin); BootWire.U32(b, 116, m.RequiredCpuAbiMax); BootWire.U32(b, 120, m.RequiredFirmwareAbiMin); BootWire.U32(b, 124, m.RequiredFirmwareAbiMax);
        BootWire.U64(b, 128, m.RequiredPlatformFeatures); BootWire.U16(b, 136, m.SlotKind); BootWire.U16(b, 138, m.ComponentCount); BootWire.U32(b, 140, m.ComponentTableOffset);
        BootWire.U64(b, 144, m.KernelEntryOffsetInComponent); BootWire.U16(b, 152, m.KernelComponentIndex); BootWire.U16(b, 154, m.Stage1ComponentIndex);
        BootWire.U16(b, 160, (ushort)m.HashAlgorithm); BootWire.U16(b, 162, (ushort)m.SignatureAlgorithm); BootWire.U32(b, 164, m.SignatureBlockOffset); m.SigningKeyId.Span.CopyTo(b.AsSpan(168, 32));
        var cursor = HeaderSize;
        foreach (var tlv in m.Extensions)
        {
            if (tlv.Value.Length > BootAbiV1.MaxTlvBytes) throw new ArgumentOutOfRangeException(nameof(m));
            BootWire.U16(b, cursor, tlv.Type); BootWire.U16(b, cursor + 2, (ushort)tlv.Flags); BootWire.U32(b, cursor + 4, (uint)tlv.Value.Length);
            tlv.Value.Span.CopyTo(b.AsSpan(cursor + 8)); cursor += Align8(8 + tlv.Value.Length);
        }
        return b;
    }

    public static BootParseResult<SingNextBootManifestV1> Parse(ReadOnlySpan<byte> b, ulong supportedFeatures, ISet<ushort>? knownTlvTypes = null)
    {
        if (b.Length < HeaderSize) return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.Truncated, "Manifest header is truncated.");
        if (BootWire.R32(b, 0) != BootWire.ManifestMagic) return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.InvalidMagic, "Manifest magic is invalid.");
        if (BootWire.R16(b, 4) != 1) return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.UnsupportedVersion, "Manifest major version is unsupported.");
        var header = BootWire.R32(b, 8); var total = BootWire.R32(b, 12); var signed = BootWire.R32(b, 16);
        if (header != HeaderSize || total < HeaderSize || total > BootAbiV1.MaxManifestBytes || total > b.Length || signed < HeaderSize || signed > total)
            return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.InvalidLength, "Manifest envelope sizes are invalid.");
        if ((BootWire.R64(b, 128) & ~supportedFeatures) != 0)
            return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.UnknownRequiredFeature, "Manifest requires unsupported platform features.");
        if (!Enum.IsDefined((BootHashAlgorithm)BootWire.R16(b, 160)) || !Enum.IsDefined((BootSignatureAlgorithm)BootWire.R16(b, 162)))
            return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.InvalidEnum, "Manifest algorithm is unknown.");
        var components = BootWire.R16(b, 138);
        if (components > BootAbiV1.MaxManifestComponents) return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.LimitExceeded, "Manifest component count exceeds v1 limit.");
        if (BootWire.R32(b, 140) > total || BootWire.R32(b, 164) > total)
            return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.RangeOutsideContainer, "Manifest offset is outside the envelope.");
        if (BootWire.R32(b, 156) != 0) return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.InvalidReserved, "Manifest reserved field is nonzero.");
        var tlvs = new List<BootTlv>(); var seenRequired = new HashSet<ushort>(); var cursor = HeaderSize;
        while (cursor < total)
        {
            if (total - cursor < TlvHeaderSize) return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.Truncated, "Manifest TLV header is truncated.");
            var type = BootWire.R16(b, cursor); var flags = (BootRecordFlags)BootWire.R16(b, cursor + 2); var length = BootWire.R32(b, cursor + 4);
            if (length > BootAbiV1.MaxTlvBytes || !BootWire.TryRange((ulong)cursor + 8, length, total, out var valueStart, out var valueLength))
                return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.InvalidLength, "Manifest TLV length is invalid.");
            if ((flags & ~BootRecordFlagMasks.Known) != 0)
                return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.UnknownRequiredFeature, "Manifest TLV has unknown flag bits.");
            if ((flags & BootRecordFlags.Required) != 0 && knownTlvTypes?.Contains(type) != true)
                return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.UnknownRequiredRecord, "Unknown required manifest TLV.");
            if ((flags & BootRecordFlags.Required) != 0 && !seenRequired.Add(type))
                return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.DuplicateRequiredField, "Required manifest TLV is duplicated.");
            tlvs.Add(new(type, flags, b.Slice(valueStart, valueLength).ToArray()));
            var next = (ulong)cursor + (ulong)Align8(checked(8 + valueLength));
            if (next > total) return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.Truncated, "Manifest TLV padding is truncated.");
            if (b.Slice(valueStart + valueLength, (int)next - valueStart - valueLength).IndexOfAnyExcept((byte)0) >= 0)
                return BootParseResult<SingNextBootManifestV1>.Fail(BootParseFailure.InvalidReserved, "Manifest TLV padding is nonzero.");
            cursor = (int)next;
        }
        return BootParseResult<SingNextBootManifestV1>.Success(new(BootWire.ReadUuid(b[24..]), BootWire.ReadUuid(b[40..]), BootWire.ReadUuid(b[56..]), BootWire.ReadUuid(b[72..]),
            BootWire.R64(b, 88), BootWire.ReadUuid(b[96..]), BootWire.R32(b, 112), BootWire.R32(b, 116), BootWire.R32(b, 120), BootWire.R32(b, 124),
            BootWire.R64(b, 128), BootWire.R16(b, 136), components, BootWire.R32(b, 140), BootWire.R64(b, 144), BootWire.R16(b, 152), BootWire.R16(b, 154),
            (BootHashAlgorithm)BootWire.R16(b, 160), (BootSignatureAlgorithm)BootWire.R16(b, 162), BootWire.R32(b, 164), b.Slice(168, 32).ToArray(), BootWire.R32(b, 20), tlvs));
    }

    private static int Align8(int value) => checked((value + 7) & ~7);
}
