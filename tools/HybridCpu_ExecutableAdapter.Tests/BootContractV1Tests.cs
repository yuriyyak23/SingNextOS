using System.Buffers.Binary;
using YAKSys_Hybrid_CPU.Boot.Contracts;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class BootContractV1Tests
{
    private static readonly Guid Platform = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Volume = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");

    [Fact]
    public void Uuid_and_little_endian_golden_vector_is_canonical()
    {
        var uuid = new byte[16]; BootWire.WriteUuid(uuid, Platform);
        Assert.Equal("00112233445566778899aabbccddeeff", Convert.ToHexString(uuid).ToLowerInvariant());
        var policy = BootPolicyCodec.Encode(Policy());
        Assert.Equal("484250310100500020010000", Convert.ToHexString(policy.AsSpan(0, 12)).ToLowerInvariant());
        Assert.Equal(Platform, BootWire.ReadUuid(uuid));
    }

    [Fact]
    public void Policy_round_trips_and_canonicalizes_target_order()
    {
        var encoded = BootPolicyCodec.Encode(Policy());
        var parsed = BootPolicyCodec.Parse(encoded);
        Assert.True(parsed.IsSuccess, parsed.Detail);
        Assert.Equal(2, parsed.Value!.Targets.Count);
        Assert.True(parsed.Value.Targets[0].Priority <= parsed.Value.Targets[1].Priority);
        Assert.Equal(encoded, BootPolicyCodec.Encode(parsed.Value));
    }

    [Fact]
    public void Policy_corpus_rejects_truncation_crc_overflow_flags_and_duplicate_ids()
    {
        var valid = BootPolicyCodec.Encode(Policy());
        Assert.Equal(BootParseFailure.Truncated, BootPolicyCodec.Parse(valid.AsSpan(0, 30)).Failure);
        var badCrc = valid.ToArray(); badCrc[25] ^= 1; Assert.Equal(BootParseFailure.InvalidCrc, BootPolicyCodec.Parse(badCrc).Failure);
        var badLength = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(badLength.AsSpan(8), uint.MaxValue); Assert.Equal(BootParseFailure.InvalidLength, BootPolicyCodec.Parse(badLength).Failure);
        var badFlags = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(badFlags.AsSpan(12), 0x80000000); Assert.Equal(BootParseFailure.UnknownRequiredFeature, BootPolicyCodec.Parse(badFlags).Failure);
        var duplicate = Policy() with { Targets = new[] { Policy().Targets[0], Policy().Targets[0] } }; Assert.Equal(BootParseFailure.DuplicateRequiredField, BootPolicyCodec.Parse(BootPolicyCodec.Encode(duplicate)).Failure);
    }

    [Fact]
    public void Volume_header_is_fixed_sized_and_capacity_bounded()
    {
        var value = new BootVolumeHeaderV1(7, Volume, Guid.NewGuid(), 64 * 1024 * 1024, 0x10000, 65536, 0, 0x20000, 65536, 0, 0x30000, 65536, 0);
        var bytes = BootVolumeHeaderCodec.Encode(value);
        Assert.Equal(BootAbiV1.VolumeHeaderBytes, bytes.Length);
        Assert.Equal(value, BootVolumeHeaderCodec.Parse(bytes, value.PersistentCapacityBytes).Value);
        Assert.Equal(BootParseFailure.RangeOutsideContainer, BootVolumeHeaderCodec.Parse(bytes, 4096).Failure);
        bytes[^1] ^= 1; Assert.Equal(BootParseFailure.InvalidCrc, BootVolumeHeaderCodec.Parse(bytes, value.PersistentCapacityBytes).Failure);
    }

    [Fact]
    public void Volume_header_rejects_oversized_or_empty_manifest_regions()
    {
        var oversized = new BootVolumeHeaderV1(1, Volume, Guid.NewGuid(), 128 * 1024 * 1024, 0x10000,
            BootAbiV1.MaxManifestBytes + 1u, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(BootParseFailure.RangeOutsideContainer,
            BootVolumeHeaderCodec.Parse(BootVolumeHeaderCodec.Encode(oversized), oversized.PersistentCapacityBytes).Failure);
        var emptyAtOffset = oversized with { ManifestAMaxBytes = 0 };
        Assert.Equal(BootParseFailure.RangeOutsideContainer,
            BootVolumeHeaderCodec.Parse(BootVolumeHeaderCodec.Encode(emptyAtOffset), emptyAtOffset.PersistentCapacityBytes).Failure);
    }

    [Fact]
    public void Manifest_skips_unknown_optional_and_rejects_unknown_required_tlv_or_feature()
    {
        var optional = Manifest(new BootTlv(77, BootRecordFlags.None, new byte[] { 1, 2, 3 }));
        Assert.True(BootManifestCodec.Parse(BootManifestCodec.Encode(optional), 0).IsSuccess);
        var required = Manifest(new BootTlv(77, BootRecordFlags.Required, new byte[] { 1 }));
        Assert.Equal(BootParseFailure.UnknownRequiredRecord, BootManifestCodec.Parse(BootManifestCodec.Encode(required), 0).Failure);
        Assert.True(BootManifestCodec.Parse(BootManifestCodec.Encode(required), 0, new HashSet<ushort> { 77 }).IsSuccess);
        var feature = Manifest() with { RequiredPlatformFeatures = 1UL << 40 };
        Assert.Equal(BootParseFailure.UnknownRequiredFeature, BootManifestCodec.Parse(BootManifestCodec.Encode(feature), 0).Failure);
    }

    [Fact]
    public void Manifest_rejects_unknown_record_flags_and_noncanonical_padding()
    {
        var bytes = BootManifestCodec.Encode(Manifest(new BootTlv(77, BootRecordFlags.None, new byte[] { 1 })));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(BootManifestCodec.HeaderSize + 2), 0x8000);
        Assert.Equal(BootParseFailure.UnknownRequiredFeature, BootManifestCodec.Parse(bytes, 0).Failure);

        bytes = BootManifestCodec.Encode(Manifest(new BootTlv(77, BootRecordFlags.None, new byte[] { 1 })));
        bytes[^1] = 1;
        Assert.Equal(BootParseFailure.InvalidReserved, BootManifestCodec.Parse(bytes, 0).Failure);
    }

    [Fact]
    public void BootInfo_digest_crc_records_and_unknown_required_semantics_fail_closed()
    {
        var info = new HybridBootInfoV1(0, Platform, 1, 1, ResetReason.Watchdog, 9,
            new[] { new BootEvidenceRecord(BootEvidenceKind.Selection, BootRecordFlags.Required | BootRecordFlags.EvidenceOnly, new byte[] { 4, 5 }) });
        var bytes = HybridBootInfoCodec.Encode(info);
        var supported = new HashSet<BootEvidenceKind> { BootEvidenceKind.Selection };
        Assert.True(HybridBootInfoCodec.Parse(bytes, supported).IsSuccess);
        var corrupt = bytes.ToArray(); corrupt[^1] ^= 1; Assert.Equal(BootParseFailure.InvalidCrc, HybridBootInfoCodec.Parse(corrupt, supported).Failure);
        var unknown = bytes.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(HybridBootInfoCodec.HeaderSize), 0x7fff); Reseal(unknown);
        Assert.Equal(BootParseFailure.UnknownRequiredRecord, HybridBootInfoCodec.Parse(unknown, supported).Failure);
    }

    [Fact]
    public void BootInfo_rejects_duplicate_required_records_unknown_flags_and_noncanonical_padding()
    {
        var record = new BootEvidenceRecord(BootEvidenceKind.Selection, BootRecordFlags.Required, new byte[] { 1 });
        var duplicate = HybridBootInfoCodec.Encode(new(0, Platform, 1, 1, ResetReason.ColdPowerOn, 1, new[] { record, record }));
        Assert.Equal(BootParseFailure.DuplicateRequiredField,
            HybridBootInfoCodec.Parse(duplicate, new HashSet<BootEvidenceKind> { BootEvidenceKind.Selection }).Failure);

        var bytes = HybridBootInfoCodec.Encode(new(0, Platform, 1, 1, ResetReason.ColdPowerOn, 1, new[] { record }));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(HybridBootInfoCodec.HeaderSize + 2), 0x8000); Reseal(bytes);
        Assert.Equal(BootParseFailure.UnknownRequiredFeature,
            HybridBootInfoCodec.Parse(bytes, new HashSet<BootEvidenceKind> { BootEvidenceKind.Selection }).Failure);

        bytes = HybridBootInfoCodec.Encode(new(0, Platform, 1, 1, ResetReason.ColdPowerOn, 1, new[] { record }));
        bytes[^1] = 1; Reseal(bytes);
        Assert.Equal(BootParseFailure.InvalidReserved,
            HybridBootInfoCodec.Parse(bytes, new HashSet<BootEvidenceKind> { BootEvidenceKind.Selection }).Failure);
    }

    [Fact]
    public void Policy_rejects_noncanonical_selector_padding()
    {
        var bytes = BootPolicyCodec.Encode(Policy());
        bytes[BootPolicyCodec.HeaderSize + 68 + 8] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76),
            BootWire.Crc32C(bytes.AsSpan(0, 76).ToArray().Concat(bytes.AsSpan(80).ToArray()).ToArray()));
        Assert.Equal(BootParseFailure.InvalidReserved, BootPolicyCodec.Parse(bytes).Failure);
    }

    [Theory]
    [InlineData(0UL, 0UL, 0UL, true)]
    [InlineData(10UL, 5UL, 15UL, true)]
    [InlineData(10UL, 6UL, 15UL, false)]
    [InlineData(ulong.MaxValue, 1UL, ulong.MaxValue, false)]
    public void Checked_range_helper_has_no_wraparound(ulong offset, ulong length, ulong total, bool expected) =>
        Assert.Equal(expected, BootWire.TryRange(offset, length, total, out _, out _));

    [Fact]
    public void Reproducible_seeded_wire_mutation_corpus_is_bounded_and_never_throws()
    {
        const int seed = 0x51A6B007;
        var random = new Random(seed);
        var vectors = new (byte[] Bytes, Func<byte[], BootParseFailure> Parse)[]
        {
            (BootPolicyCodec.Encode(Policy()), static bytes => BootPolicyCodec.Parse(bytes).Failure),
            (BootManifestCodec.Encode(Manifest()), static bytes => BootManifestCodec.Parse(bytes, 0).Failure),
            (HybridBootInfoCodec.Encode(new(0, Platform, 1, 1, ResetReason.ColdPowerOn, 1, [])), static bytes => HybridBootInfoCodec.Parse(bytes).Failure)
        };
        var rejected = 0;
        foreach (var vector in vectors)
        for (var ordinal = 0; ordinal < 128; ordinal++)
        {
            var mutated = vector.Bytes.ToArray();
            var index = random.Next(mutated.Length);
            mutated[index] ^= (byte)(1 << random.Next(8));
            // An envelope-only manifest parser may accept a mutation that the mandatory
            // signature verifier rejects later; the parser property here is finite/no-throw.
            if (vector.Parse(mutated) != BootParseFailure.None) rejected++;
        }
        Assert.InRange(rejected, 1, 384);
    }

    private static HybridBootPolicyV1 Policy()
    {
        var a = new BootTargetV1(Guid.Parse("20000000-0000-0000-0000-000000000002"), BootTargetKind.LocalRecovery, 2, 0, Guid.Empty, Guid.NewGuid(), 0, PhysicalSelectorKind.None, ReadOnlyMemory<byte>.Empty, 1);
        var b = new BootTargetV1(Guid.Parse("10000000-0000-0000-0000-000000000001"), BootTargetKind.CxlPersistentVolume, 1, 0, Volume, Guid.NewGuid(), 3, PhysicalSelectorKind.PciDsn, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 3);
        return new(5, Platform, Guid.Parse("11111111-2222-3333-4444-555555555555"), a.TargetId, BootPolicyFlags.RequireSecureBoot | BootPolicyFlags.AllowReplicaFailover, new[] { a, b });
    }

    private static SingNextBootManifestV1 Manifest(params BootTlv[] tlvs) => new(Volume, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 12, Platform,
        1, 1, 1, 1, 0, 1, 0, 200, 0, 0, 0, BootHashAlgorithm.Sha384, BootSignatureAlgorithm.Ed25519, 200,
        Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 0, tlvs);

    private static void Reseal(byte[] bytes)
    {
        bytes.AsSpan(HybridBootInfoCodec.DigestOffset, HybridBootInfoCodec.DigestSize).Clear(); bytes.AsSpan(112, 4).Clear();
        System.Security.Cryptography.SHA384.HashData(bytes).CopyTo(bytes, HybridBootInfoCodec.DigestOffset);
        var crcBytes = bytes.AsSpan(0, 112).ToArray().Concat(bytes.AsSpan(116).ToArray()).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(112), BootWire.Crc32C(crcBytes));
    }
}
