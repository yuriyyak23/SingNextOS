using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class TemporaryApertureModelTests
{
    [Fact]
    public void Single_target_transaction_commits_and_destroys_without_authority_object()
    {
        var model = new TemporaryApertureModel(); var result = Create(model);
        Assert.Equal(ApertureFailure.None, result.Failure); Assert.Equal(ApertureFailure.None, model.Validate(result.Mapping!));
        Assert.Equal(ApertureFailure.None, model.Read(result.Mapping!, 0, 4096));
        Assert.Equal(ApertureFailure.ActiveMappingExists, Create(model).Failure);
        Assert.Equal(ApertureFailure.None, model.Destroy(result.Mapping!));
        Assert.Equal(ApertureFailure.StaleGeneration, model.Validate(result.Mapping!));
    }

    [Fact]
    public void Partial_commit_compensates_in_exact_reverse_order()
    {
        var result = Create(new TemporaryApertureModel(), fail: 3);
        Assert.Equal(ApertureFailure.CommitFailed, result.Failure);
        Assert.Equal(new[] { "Rollback:root", "Rollback:host" }, result.Trace.Where(x => x.StartsWith("Rollback", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reset_makes_mapping_stale_even_if_physical_programming_might_remain()
    {
        var model = new TemporaryApertureModel(); var mapping = Create(model).Mapping!; model.Reset(); Assert.Equal(ApertureFailure.StaleGeneration, model.Validate(mapping));
    }

    [Theory]
    [InlineData(true, (int)ApertureFailure.InterleaveForbidden)]
    [InlineData(false, (int)ApertureFailure.Overlap)]
    public void Interleave_and_hpa_overlap_fail_closed(bool interleave, int expected)
    {
        var reserved = interleave ? Array.Empty<(ulong, ulong)>() : new[] { (0x8000_0000UL, 0x1000UL) };
        var result = new TemporaryApertureModel().Create(0x8000_0000, 16 * 1024 * 1024, 0, 4096, 32 * 1024 * 1024, reserved, Hops(), interleave);
        Assert.Equal((ApertureFailure)expected, result.Failure);
    }

    [Fact]
    public void Overflow_capacity_and_unavailable_decoder_fail_before_programming()
    {
        Assert.Equal(ApertureFailure.InvalidRange, new TemporaryApertureModel().Create(0x8000_0000, 16 * 1024 * 1024, ulong.MaxValue - 1, 4096, ulong.MaxValue, [], Hops(), false).Failure);
        Assert.Equal(ApertureFailure.InvalidRange, new TemporaryApertureModel().Create(ulong.MaxValue - 4095, 16 * 1024 * 1024, 0, 4096, 32 * 1024 * 1024, [], Hops(), false).Failure);
        Assert.Equal(ApertureFailure.InvalidRange, new TemporaryApertureModel().Create(0x8000_0000, 16 * 1024 * 1024, 0, 4096, 32 * 1024 * 1024, [(ulong.MaxValue - 1, 4)], Hops(), false).Failure);
        Assert.Equal(ApertureFailure.DecoderUnavailable, new TemporaryApertureModel().Create(0x8000_0000, 16 * 1024 * 1024, 0, 4096, 32 * 1024 * 1024, [], [new("host", false)], false).Failure);
        Assert.Equal(ApertureFailure.DecoderUnavailable, new TemporaryApertureModel().Create(0x8000_0000, 16 * 1024 * 1024, 0, 4096, 32 * 1024 * 1024, [], [new("host", true, true)], false).Failure);
    }

    [Theory]
    [InlineData((int)ApertureFailure.Timeout)]
    [InlineData((int)ApertureFailure.LinkLost)]
    [InlineData((int)ApertureFailure.ReadFault)]
    public void Read_faults_invalidate_mapping_and_never_imply_cleanup_authority(int rawFailure)
    {
        var failure = (ApertureFailure)rawFailure;
        var model = new TemporaryApertureModel(); var mapping = Create(model).Mapping!;
        Assert.Equal(failure, model.Read(mapping, 0, 4096, failure));
        Assert.Equal(ApertureFailure.StaleGeneration, model.Validate(mapping));
        Assert.Equal(ApertureFailure.StaleGeneration, model.Destroy(mapping));
    }

    private static ApertureResult Create(TemporaryApertureModel model, int fail = 0) => model.Create(0x8000_0000, 16 * 1024 * 1024, 0, 8192, 32 * 1024 * 1024, [], Hops(), false, fail);
    private static DecoderHop[] Hops() => [new("host", true), new("root", true), new("endpoint", true)];
}
