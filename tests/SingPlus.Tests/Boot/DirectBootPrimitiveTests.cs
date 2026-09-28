using SingNext.Boot.Core;

namespace SingPlus.Tests.Boot;

public sealed class DirectBootPrimitiveTests
{
    [Fact]
    public void P15_02_RangeArithmeticRejectsOverflowEmptyAndOutsideContainer()
    {
        Assert.False(BootRange.TryCreate(0, 0, 0, 1, out _));
        Assert.False(BootRange.TryCreate(ulong.MaxValue - 1, 4, 0, ulong.MaxValue, out _));
        Assert.False(BootRange.TryCreate(0x2000, 0x1000, 0x1000, 0x1000, out _));
        Assert.True(BootRange.TryCreate(0x1800, 0x800, 0x1000, 0x1000, out var range));
        Assert.Equal(0x2000UL, range.EndExclusive);
    }

    [Fact]
    public void P15_02_RangesUseHalfOpenOverlapRules()
    {
        var left = new BootRange(0x1000, 0x1000);
        var touching = new BootRange(0x2000, 0x1000);
        var overlapping = new BootRange(0x1fff, 2);

        Assert.False(left.Overlaps(touching));
        Assert.True(left.Overlaps(overlapping));
        Assert.True(left.Contains(0x1000));
        Assert.False(left.Contains(0x2000));
    }

    [Fact]
    public void P15_02_FailureResultCannotClaimSuccess()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BootResult<int>.Fail(BootFailure.None, "invalid"));
        Assert.False(BootResult<int>.Fail(BootFailure.Timeout, "deadline").IsSuccess);
        Assert.True(BootResult<int>.Success(7).IsSuccess);
    }
}
