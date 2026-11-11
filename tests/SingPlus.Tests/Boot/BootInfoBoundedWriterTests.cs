using System.Security.Cryptography;
using SingNext.Boot.Capsule;
using SingNext.Boot.Core;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingPlus.Tests.Boot;

public sealed class BootInfoBoundedWriterTests
{
    [Fact]
    public void P15_09_UnwiredCapsuleEntryCannotReportSuccess()
    {
        Assert.Equal((int)BootFailure.Unsupported, BootCapsuleEntry.Run());
        Assert.NotEqual(0, BootCapsuleEntry.Run());
    }

    [Fact]
    public void P15_09_BoundedWriterMatchesExistingCodecGoldenVectorsByteForByte()
    {
        foreach (HybridBootInfoV1 info in GoldenVectors())
        {
            byte[] expected = HybridBootInfoCodec.Encode(info);
            byte[] actual = new byte[BootAbiV1.MaxBootInfoBytes];
            byte[] scratch = new byte[48];
            Assert.Equal(BootFailure.None, HybridBootInfoBoundedWriter.TryWrite(info, actual, scratch, out int written));
            Assert.Equal(expected, actual.AsSpan(0, written).ToArray());
            Assert.True(HybridBootInfoCodec.Parse(actual.AsSpan(0, written), new HashSet<BootEvidenceKind> { BootEvidenceKind.Security }).IsSuccess);
        }
    }

    [Fact]
    public void P15_09_CapsuleCompositionRejectsCorruptImageAndDoesNotEmitHandoff()
    {
        byte[] image = new byte[] { 1, 2, 3, 4 };
        byte[] ram = new byte[image.Length];
        byte[] handoff = Enumerable.Repeat((byte)0xcc, 512).ToArray();
        BootResult<CapsuleLaunchEvidence> result = DirectBootCapsule.PrepareKernel(1, 2, new(3, 7, 0x8000_0000, 16 * 1024 * 1024, 0, 4096),
            image, new byte[48], ram, GoldenVectors()[0], handoff, new byte[48]);
        Assert.Equal(BootFailure.HashMismatch, result.Failure);
        Assert.All(ram, static value => Assert.Equal(0, value));
        Assert.All(handoff, static value => Assert.Equal(0xcc, value));
    }

    [Fact]
    public void P15_09_DeterministicAdapterCompositionProducesParsableBootInfo()
    {
        byte[] image = Enumerable.Range(0, 128).Select(static x => (byte)x).ToArray();
        byte[] ram = new byte[image.Length];
        byte[] handoff = new byte[1024];
        BootResult<CapsuleLaunchEvidence> result = DirectBootCapsule.PrepareKernel(10, 20, new(30, 7, 0x8000_0000, 16 * 1024 * 1024, 0, 4096),
            image, SHA384.HashData(image), ram, GoldenVectors()[1], handoff, new byte[48]);
        Assert.True(result.IsSuccess);
        Assert.Equal(image, ram);
        Assert.True(HybridBootInfoCodec.Parse(handoff.AsSpan(0, result.Value!.BootInfoBytes), new HashSet<BootEvidenceKind> { BootEvidenceKind.Security }).IsSuccess);
        Assert.Equal(10UL, result.Value.BootCapsuleGeneration);
        Assert.Equal(20UL, result.Value.ImageGeneration);
        Assert.Equal(30UL, result.Value.BootMappingGeneration);
    }

    private static HybridBootInfoV1[] GoldenVectors()
    {
        byte[] security = BootEvidencePayloadCodec.EncodeSecurity(new(BootSecurityStatus.ManifestVerified, 4, 5, 6));
        return
        [
            new(0, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 6, 1, ResetReason.ColdPowerOn, 1, []),
            new(3, Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"), 6, 1, ResetReason.Watchdog, 9,
            [
                new(BootEvidenceKind.Security, BootRecordFlags.Required | BootRecordFlags.EvidenceOnly, security),
                new(BootEvidenceKind.TemporaryAperture, BootRecordFlags.EvidenceOnly | BootRecordFlags.Temporary | BootRecordFlags.MustNotUseAsRam, new byte[] { 1, 2, 3 })
            ])
        ];
    }
}
