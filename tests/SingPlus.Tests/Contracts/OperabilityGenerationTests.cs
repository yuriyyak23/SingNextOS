using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class OperabilityGenerationTests
{
    [Fact]
    public void ExactGenerationIsAcceptedWithoutMintingAuthority()
    {
        Assert.Equal(GenerationMatch.Exact, OperabilityGeneration.Compare(7, 7));
        Assert.Empty(typeof(GenerationMatch).GetProperties());
    }

    [Theory]
    [InlineData(7UL, 6UL)]
    [InlineData(7UL, 8UL)]
    public void AnyUnequalGenerationIsStaleRatherThanOrdered(ulong authoritative, ulong presented)
    {
        Assert.Equal(GenerationMatch.Stale, OperabilityGeneration.Compare(authoritative, presented));
    }

    [Theory]
    [InlineData(0UL, 1UL)]
    [InlineData(1UL, 0UL)]
    [InlineData(0UL, 0UL)]
    public void ZeroGenerationFailsClosed(ulong authoritative, ulong presented)
    {
        Assert.Equal(GenerationMatch.Invalid, OperabilityGeneration.Compare(authoritative, presented));
    }
}
