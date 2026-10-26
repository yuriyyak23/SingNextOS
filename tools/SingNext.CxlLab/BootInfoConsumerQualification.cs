using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.CxlLab;

/// <summary>
/// Lab-only adapter for the existing runtime importer. It deliberately supplies no
/// authoritative OS provider: a successful evidence parse must therefore stop at
/// ProviderRefused, after a fresh-discovery attempt, rather than claim takeover.
/// </summary>
internal static class BootInfoConsumerQualification
{
    private static readonly ISet<BootEvidenceKind> SupportedRequiredRecords = new HashSet<BootEvidenceKind> { BootEvidenceKind.Security };

    public static void VerifyDevelopmentEvidence(ReadOnlySpan<byte> wireBytes, string output, Dictionary<string, object?> report)
    {
        HybridBootTakeoverResult result = Import(wireBytes, out int discoveryCalls);
        if (result.Failure != HybridBootImportFailure.ProviderRefused || discoveryCalls != 1)
            throw new InvalidDataException($"Native BootInfo did not reach the expected fresh-provider refusal: {result.Failure} / {discoveryCalls}.");

        BootParseResult<HybridBootInfoV1> parsed = HybridBootInfoCodec.Parse(wireBytes, SupportedRequiredRecords);
        BootParseResult<BootSecurityEvidenceV1> securityParse = BootEvidencePayloadCodec.ParseSecurity(
            parsed.Value!.Records.Single(record => record.Kind == BootEvidenceKind.Security).Payload.Span);
        if (!securityParse.IsSuccess) throw new InvalidDataException("Security evidence could not be parsed.");
        BootSecurityEvidenceV1 security = securityParse.Value!;
        if (security.Status != BootSecurityStatus.Development)
            throw new InvalidDataException("The test EFI must publish development, not production, security evidence.");

        var evidence = new
        {
            Scope = "Lab adapter: native EFI BootInfo into existing HybridBootInfoImporter; no authoritative OS CXL provider",
            CodecParsed = true,
            SecurityStatus = security.Status.ToString(),
            security.TrustEpoch,
            security.ProtectedRollbackFloor,
            security.AcceptedImageGeneration,
            FreshDiscoveryCalls = discoveryCalls,
            ImportFailure = result.Failure.ToString(),
            RuntimeAuthorityAdmitted = false,
            KernelHandoffPerformed = false
        };
        File.WriteAllText(Path.Combine(output, "bootinfo-importer-result.json"), System.Text.Json.JsonSerializer.Serialize(evidence, LabVm.JsonOptions));
        report["GuestBootInfoRuntimeCodecParsed"] = true;
        report["GuestBootInfoSecurityEvidenceAccepted"] = true;
        report["GuestBootInfoSecurityStatus"] = security.Status.ToString();
        report["GuestBootInfoFreshDiscoveryAttempted"] = true;
        report["GuestBootInfoImporterOutcome"] = result.Failure.ToString();
        report["RuntimeAuthorityAdmissionPerformed"] = false;
    }

    public static object Inspect(ReadOnlySpan<byte> wireBytes)
    {
        HybridBootTakeoverResult result = Import(wireBytes, out int discoveryCalls);
        return new { Bytes = wireBytes.Length, ImportFailure = result.Failure.ToString(), ParseFailure = result.ParseFailure.ToString(), FreshDiscoveryCalls = discoveryCalls, RuntimeAuthorityAdmitted = false };
    }

    public static void SelfTest()
    {
        byte[] valid = ValidDevelopmentWire();
        if (Import(valid, out int calls).Failure != HybridBootImportFailure.ProviderRefused || calls != 1)
            throw new InvalidDataException("Development evidence did not reach the fresh-provider refusal seam.");

        AssertRejected(HybridBootInfoCodec.Encode(new(0, LabCapsule.Platform, 1, 1, ResetReason.ColdPowerOn, 1, [])), HybridBootImportFailure.MissingSecurityEvidence);
        BootEvidenceRecord security = new(BootEvidenceKind.Security, BootRecordFlags.Required | BootRecordFlags.EvidenceOnly,
            BootEvidencePayloadCodec.EncodeSecurity(new(BootSecurityStatus.Development, 1, 1, 1)));
        AssertRejected(HybridBootInfoCodec.Encode(new(0, LabCapsule.Platform, 1, 1, ResetReason.ColdPowerOn, 1, [security, security])), HybridBootImportFailure.InvalidBootInfo);
        AssertRejected(HybridBootInfoCodec.Encode(new(0, LabCapsule.Platform, 1, 1, ResetReason.ColdPowerOn, 1,
            [security with { Flags = BootRecordFlags.EvidenceOnly }])), HybridBootImportFailure.SecurityRejected);

        byte[] reserved = ValidDevelopmentWire(); reserved[132] = 1; Rehash(reserved);
        AssertRejected(reserved, HybridBootImportFailure.SecurityRejected);
        byte[] epoch = ValidDevelopmentWire(); Array.Clear(epoch, 136, 8); Rehash(epoch);
        AssertRejected(epoch, HybridBootImportFailure.SecurityRejected);
        byte[] floor = ValidDevelopmentWire(); System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(floor.AsSpan(152), 0); System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(floor.AsSpan(144), 2); Rehash(floor);
        AssertRejected(floor, HybridBootImportFailure.SecurityRejected);
        Console.WriteLine("BootInfo importer adapter: development evidence reaches fresh refusal; missing/duplicate/flags/reserved/epoch/floor negatives reject before discovery.");
    }

    private static HybridBootTakeoverResult Import(ReadOnlySpan<byte> wireBytes, out int discoveryCalls)
    {
        FreshDiscovery discovery = new();
        HybridBootInfoImporter importer = new(discovery, new CxlType3ModelProvider(), new NoApertureRetirement());
        HybridBootTakeoverResult result = importer.ImportAndDiscover(wireBytes);
        discoveryCalls = discovery.Calls;
        return result;
    }

    private static byte[] ValidDevelopmentWire() => HybridBootInfoCodec.Encode(new(0, LabCapsule.Platform, 1, 1, ResetReason.ColdPowerOn, 1,
        [new(BootEvidenceKind.Security, BootRecordFlags.Required | BootRecordFlags.EvidenceOnly,
            BootEvidencePayloadCodec.EncodeSecurity(new(BootSecurityStatus.Development, 1, 1, 1)))]));

    private static void AssertRejected(byte[] bytes, HybridBootImportFailure expected)
    {
        HybridBootTakeoverResult result = Import(bytes, out int calls);
        if (result.Failure != expected || calls != 0)
            throw new InvalidDataException($"Expected {expected} before discovery, got {result.Failure} / {calls}.");
    }

    private static void Rehash(byte[] wire)
    {
        wire.AsSpan(HybridBootInfoCodec.DigestOffset, HybridBootInfoCodec.DigestSize).Clear();
        wire.AsSpan(112, 4).Clear();
        System.Security.Cryptography.SHA384.HashData(wire).CopyTo(wire, HybridBootInfoCodec.DigestOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(112), Crc32C(wire));
    }

    private static uint Crc32C(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i is >= 112 and < 116) continue;
            crc ^= bytes[i];
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((0u - (crc & 1)) & 0x82f63b78u);
        }
        return ~crc;
    }

    private sealed class FreshDiscovery : IFreshCxlBootDiscovery
    {
        public int Calls { get; private set; }
        public IReadOnlyList<CxlEndpointId> EnumerateCurrentEndpoints()
        {
            Calls++;
            return [new("lab-provider-not-present")];
        }
    }

    private sealed class NoApertureRetirement : IFirmwareApertureRetirement
    {
        public FirmwareApertureDisposition ReleaseInvalidateOrQuarantine(ulong resetSequence) => FirmwareApertureDisposition.Absent;
    }
}
