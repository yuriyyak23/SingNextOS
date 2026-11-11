using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.Boot.Contracts;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class Stage1LoadModelTests
{
    [Fact]
    public void CopiesAndVerifiesEveryComponentBeforePublishingResult()
    {
        byte[] kernel = Bytes(8192, 1);
        byte[] service = Bytes(4096, 2);
        Stage1LoadResult result = new Stage1LoadModel().Load([
            Descriptor("kernel", kernel, 0x102000, 0x102100),
            Descriptor("service", service, 0x106000, null)], 0x100000, 0x20000);

        Assert.True(result.IsSuccess);
        Assert.Equal(SHA384.HashData(kernel), SHA384.HashData(result.Components[0].Bytes.Span));
        Assert.Equal(2, result.Components.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsOverlapAndBadDestinationHashWithoutPartialExecutableBytes(bool overlap)
    {
        byte[] first = Bytes(4096, 3);
        byte[] second = Bytes(4096, 4);
        Stage1ComponentDescriptor descriptor = Descriptor("second", second, overlap ? 0x102000UL : 0x104000UL, null);
        if (!overlap)
            descriptor = descriptor with { ExpectedSha384 = new byte[48] };

        Stage1LoadResult result = new Stage1LoadModel().Load([
            Descriptor("kernel", first, 0x102000, 0x102000), descriptor], 0x100000, 0x20000);

        Assert.Equal(overlap ? Stage1LoadFailure.RangeOverlap : Stage1LoadFailure.HashMismatch, result.Failure);
        Assert.Empty(result.Components);
    }

    [Fact]
    public void RejectsInvalidEntryAndEntryAbiMismatch()
    {
        byte[] bytes = Bytes(4096, 5);
        Stage1LoadResult load = new Stage1LoadModel().Load([
            Descriptor("kernel", bytes, 0x102000, 0x103000)], 0x100000, 0x20000);
        Assert.Equal(Stage1LoadFailure.InvalidEntry, load.Failure);
        Assert.False(Stage1LoadModel.ValidateEntryAbi(new(2, 0x102000, 0x104000, 120, 0), 0x100000, 0x20000));
        Assert.True(Stage1LoadModel.ValidateEntryAbi(new(1, 0x102000, 0x104000, 120, 0), 0x100000, 0x20000));
        Assert.Equal(Stage1LoadFailure.InvalidEntry, new Stage1LoadModel().Load([
            Descriptor("kernel", bytes, 0x102000, 0x102004)], 0x100000, 0x20000).Failure);
        Assert.Equal(Stage1LoadFailure.RangeOverflow, new Stage1LoadModel().Load([
            Descriptor("kernel", bytes, ulong.MaxValue - 0xfff, null)], ulong.MaxValue - 0x1fff, 0x2000).Failure);
    }

    [Fact]
    public void BootInfoCorruptionFailsAndCxlLossAfterCopyDoesNotAlterRamBytes()
    {
        byte[] payload = Bytes(4096, 6);
        Stage1LoadResult load = new Stage1LoadModel().Load([Descriptor("kernel", payload, 0x102000, 0x102000)], 0x100000, 0x20000);
        Assert.True(load.IsSuccess);
        byte[] beforeLoss = load.Components[0].Bytes.ToArray();
        TemporaryApertureModel aperture = new TemporaryApertureModel();
        TemporaryMapping mapping = aperture.Create(0x8000_0000, 16 * 1024 * 1024, 0, 4096, 32 * 1024 * 1024, [],
            [new("host", true), new("root", true), new("endpoint", true)], false).Mapping!;
        Assert.Equal(ApertureFailure.LinkLost, aperture.Read(mapping, 0, 4096, ApertureFailure.LinkLost));
        Assert.Equal(ApertureFailure.StaleGeneration, aperture.Validate(mapping));

        byte[] info = HybridBootInfoCodec.Encode(new(0, Guid.NewGuid(), 1, 1, ResetReason.ColdPowerOn, 1, []));
        info[16] ^= 0x80;
        Assert.Equal(BootParseFailure.InvalidCrc, HybridBootInfoCodec.Parse(info).Failure);
        Assert.Equal(beforeLoss, load.Components[0].Bytes.ToArray());
    }

    [Fact]
    public void Semantic_copy_fault_clears_all_prior_components_and_rejects_malformed_plan()
    {
        byte[] first = Bytes(4096, 7); byte[] second = Bytes(4096, 8);
        Stage1LoadResult result = new Stage1LoadModel().Load([
            Descriptor("kernel", first, 0x102000, 0x102000), Descriptor("service", second, 0x104000, null)],
            0x100000, 0x20000, new("CopyComponent", 2, Stage1LoadFailure.CopyFault));
        Assert.Equal(Stage1LoadFailure.CopyFault, result.Failure); Assert.Empty(result.Components);
        Assert.Throws<ArgumentException>(() => new Stage1LoadModel().Load([
            Descriptor("kernel", first, 0x102000, 0x102000)], 0x100000, 0x20000,
            new("CopyComponent", 0, Stage1LoadFailure.CopyFault)));
    }

    private static Stage1ComponentDescriptor Descriptor(string name, byte[] bytes, ulong load, ulong? entry) =>
        new(name, bytes, SHA384.HashData(bytes), load, entry, 4096);

    private static byte[] Bytes(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }
}
