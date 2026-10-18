namespace YAKSys_Hybrid_CPU.Boot.Contracts;

public static class BootPolicyCodec
{
    public const int HeaderSize = 80;
    public const int TargetSize = 104;
    private const uint KnownFlags = 0x7f;

    public static byte[] Encode(HybridBootPolicyV1 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Targets.Count > BootAbiV1.MaxPolicyTargets) throw new ArgumentOutOfRangeException(nameof(policy));
        var orderedTargets = policy.Targets.OrderBy(static x => x.Priority).ThenBy(static x => x.TargetId.ToString("N"), StringComparer.Ordinal).ToArray();
        var total = checked(HeaderSize + orderedTargets.Length * TargetSize);
        var b = new byte[total];
        BootWire.U32(b, 0, BootWire.PolicyMagic); BootWire.U16(b, 4, 1); BootWire.U16(b, 6, HeaderSize);
        BootWire.U32(b, 8, (uint)total); BootWire.U32(b, 12, (uint)policy.Flags); BootWire.U64(b, 16, policy.PolicyGeneration);
        BootWire.WriteUuid(b.AsSpan(24), policy.PlatformId); BootWire.WriteUuid(b.AsSpan(40), policy.KeySetId);
        BootWire.WriteUuid(b.AsSpan(56), policy.DefaultRecoveryTargetId); BootWire.U16(b, 72, (ushort)orderedTargets.Length);
        for (var i = 0; i < orderedTargets.Length; i++) EncodeTarget(b.AsSpan(HeaderSize + i * TargetSize, TargetSize), orderedTargets[i]);
        BootWire.U32(b, 76, BootWire.Crc32C(ConcatForCrc(b.AsSpan(0, 76), b.AsSpan(80))));
        return b;
    }

    public static BootParseResult<HybridBootPolicyV1> Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderSize) return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.Truncated, "Policy header is truncated.");
        if (BootWire.R32(b, 0) != BootWire.PolicyMagic) return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.InvalidMagic, "Policy magic is invalid.");
        if (BootWire.R16(b, 4) != 1) return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.UnsupportedVersion, "Policy version is unsupported.");
        var header = BootWire.R16(b, 6); var total = BootWire.R32(b, 8); var flags = BootWire.R32(b, 12); var count = BootWire.R16(b, 72);
        if (header != HeaderSize || total < HeaderSize || total > BootAbiV1.MaxPolicyBytes || total > b.Length)
            return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.InvalidLength, "Policy size is invalid.");
        if (count > BootAbiV1.MaxPolicyTargets || (ulong)HeaderSize + (ulong)count * TargetSize != total)
            return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.LimitExceeded, "Policy target count or size is invalid.");
        if ((flags & ~KnownFlags) != 0) return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.UnknownRequiredFeature, "Policy has unknown flags.");
        if (BootWire.R32(b, 76) != BootWire.Crc32C(ConcatForCrc(b[..76], b[80..(int)total])))
            return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.InvalidCrc, "Policy CRC32C mismatch.");
        var targets = new List<BootTargetV1>(count);
        for (var i = 0; i < count; i++)
        {
            var parsed = ParseTarget(b.Slice(HeaderSize + i * TargetSize, TargetSize));
            if (!parsed.IsSuccess) return BootParseResult<HybridBootPolicyV1>.Fail(parsed.Failure, parsed.Detail!);
            targets.Add(parsed.Value);
        }
        if (targets.Select(x => x.TargetId).Distinct().Count() != targets.Count)
            return BootParseResult<HybridBootPolicyV1>.Fail(BootParseFailure.DuplicateRequiredField, "TargetId must be unique.");
        targets.Sort(static (a, c) => a.Priority != c.Priority ? a.Priority.CompareTo(c.Priority) : CompareUuid(a.TargetId, c.TargetId));
        return BootParseResult<HybridBootPolicyV1>.Success(new(BootWire.R64(b, 16), BootWire.ReadUuid(b[24..]), BootWire.ReadUuid(b[40..]), BootWire.ReadUuid(b[56..]), (BootPolicyFlags)flags, targets));
    }

    private static void EncodeTarget(Span<byte> b, BootTargetV1 t)
    {
        if (t.PhysicalSelector.Length > 32) throw new ArgumentOutOfRangeException(nameof(t));
        BootWire.WriteUuid(b, t.TargetId); BootWire.U16(b, 16, (ushort)t.Kind); BootWire.U16(b, 18, t.Priority); BootWire.U32(b, 20, t.Flags);
        BootWire.WriteUuid(b[24..], t.BootVolumeId); BootWire.WriteUuid(b[40..], t.RollbackDomainId); BootWire.U64(b, 56, t.RequiredProperties);
        BootWire.U16(b, 64, (ushort)t.PhysicalSelectorKind); BootWire.U16(b, 66, (ushort)t.PhysicalSelector.Length); t.PhysicalSelector.Span.CopyTo(b[68..100]);
        BootWire.U16(b, 100, t.MaxAttemptsPerBoot);
    }

    private static BootParseResult<BootTargetV1> ParseTarget(ReadOnlySpan<byte> b)
    {
        var kind = BootWire.R16(b, 16); var selectorKind = BootWire.R16(b, 64); var selectorLength = BootWire.R16(b, 66);
        if (!Enum.IsDefined((BootTargetKind)kind) || !Enum.IsDefined((PhysicalSelectorKind)selectorKind))
            return BootParseResult<BootTargetV1>.Fail(BootParseFailure.InvalidEnum, "Target kind is invalid.");
        if (selectorLength > 32 || BootWire.R16(b, 102) != 0)
            return BootParseResult<BootTargetV1>.Fail(BootParseFailure.InvalidLength, "Physical selector is invalid.");
        if (selectorKind == 0 && selectorLength != 0)
            return BootParseResult<BootTargetV1>.Fail(BootParseFailure.InvalidLength, "None selector must be empty.");
        if (b.Slice(68 + selectorLength, 32 - selectorLength).IndexOfAnyExcept((byte)0) >= 0)
            return BootParseResult<BootTargetV1>.Fail(BootParseFailure.InvalidReserved, "Physical selector padding is nonzero.");
        return BootParseResult<BootTargetV1>.Success(new(BootWire.ReadUuid(b), (BootTargetKind)kind, BootWire.R16(b, 18), BootWire.R32(b, 20),
            BootWire.ReadUuid(b[24..]), BootWire.ReadUuid(b[40..]), BootWire.R64(b, 56), (PhysicalSelectorKind)selectorKind,
            b.Slice(68, selectorLength).ToArray(), BootWire.R16(b, 100)));
    }

    private static int CompareUuid(Guid left, Guid right) => left.ToString("N").CompareTo(right.ToString("N"));

    private static byte[] ConcatForCrc(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var result = new byte[left.Length + right.Length]; left.CopyTo(result); right.CopyTo(result.AsSpan(left.Length)); return result;
    }
}
