using System.Text.Json;
using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class VNextPhase01ResourceModelTests
{
    [Fact]
    public void ComputeTimeIsCanonicalInitialResourceAndRoundTrips()
    {
        var value = Constraint(500);
        var json = JsonSerializer.Serialize(value);
        var decoded = JsonSerializer.Deserialize<ResourceUseConstraintV1>(json);

        Assert.Equal(value, decoded.Canonicalize());
        Assert.Equal(ResourceDimensionFamilyV1.Time, decoded.Envelope.Family);
        Assert.Equal(ResourceUnitV1.Nanoseconds, decoded.Envelope.Unit);
    }

    [Fact]
    public void SubsetIsReflexiveTransitiveAndNeverWidensAmountOrAssurance()
    {
        var parent = Constraint(1000, assurance: ResourceAssuranceV1.EnforcedUpperBound, depth: 4);
        var child = Constraint(500, assurance: ResourceAssuranceV1.RuntimeEnforced, depth: 3);
        var grandchild = Constraint(250, assurance: ResourceAssuranceV1.AccountingOnly, depth: 2);

        Assert.True(ResourceUseConstraintV1.IsSubset(parent, parent));
        Assert.True(ResourceUseConstraintV1.IsSubset(child, parent));
        Assert.True(ResourceUseConstraintV1.IsSubset(grandchild, child));
        Assert.True(ResourceUseConstraintV1.IsSubset(grandchild, parent));
        Assert.False(ResourceUseConstraintV1.IsSubset(parent, child));
        Assert.False(ResourceUseConstraintV1.IsSubset(child with { AssuranceCeiling = ResourceAssuranceV1.GuaranteedReservation }, parent));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(7919)]
    public void GeneratedNarrowingChainsRemainReflexiveAndTransitive(int seed)
    {
        var random = new Random(seed);
        for (var index = 0; index < 256; index++)
        {
            var parentAmount = (ulong)random.Next(3, int.MaxValue);
            var childAmount = (ulong)random.Next(2, checked((int)Math.Min(parentAmount, int.MaxValue)));
            var grandchildAmount = (ulong)random.Next(1, checked((int)Math.Min(childAmount, int.MaxValue)));
            var parent = Constraint(parentAmount, depth: 4);
            var child = Constraint(childAmount, depth: 3);
            var grandchild = Constraint(grandchildAmount, assurance: ResourceAssuranceV1.AccountingOnly, depth: 2);

            Assert.True(ResourceUseConstraintV1.IsSubset(parent, parent));
            Assert.True(ResourceUseConstraintV1.IsSubset(child, parent));
            Assert.True(ResourceUseConstraintV1.IsSubset(grandchild, child));
            Assert.True(ResourceUseConstraintV1.IsSubset(grandchild, parent));
        }
    }

    [Fact]
    public void SiblingDeclarativeEnvelopesCannotBeUnionedIntoParentAuthority()
    {
        var parent = Constraint(100);
        var left = Constraint(75);
        var right = Constraint(75);
        var attemptedUnion = Constraint(checked(left.Envelope.Amount + right.Envelope.Amount));

        Assert.True(ResourceUseConstraintV1.IsSubset(left, parent));
        Assert.True(ResourceUseConstraintV1.IsSubset(right, parent));
        Assert.False(ResourceUseConstraintV1.IsSubset(attemptedUnion, parent));
    }

    [Fact]
    public void DimensionClassUnitWindowAndScopeCannotBeReinterpreted()
    {
        var time = Constraint(500);
        var throughput = time with { Envelope = new(1, ResourceDimensionFamilyV1.Throughput,
            ResourceClassV1.DmaThroughput, ResourceUnitV1.BytesPerWindow, 500, 1000, "host:compute-v1") };
        var occupancy = time with { Envelope = new(1, ResourceDimensionFamilyV1.Occupancy,
            ResourceClassV1.DeviceMemoryOccupancy, ResourceUnitV1.Bytes, 500, 0, "host:compute-v1") };

        Assert.False(ResourceUseConstraintV1.IsSubset(throughput, time));
        Assert.False(ResourceUseConstraintV1.IsSubset(occupancy, time));
        Assert.False(ResourceUseConstraintV1.IsSubset(time with { Envelope = time.Envelope with { SemanticScope = "gpu:compute-v1" } }, time));
        Assert.Throws<ArgumentException>(() => (time.Envelope with { Unit = ResourceUnitV1.Bytes }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (throughput.Envelope with { WindowNanoseconds = 0 }).Canonicalize());
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public void ZeroAndMaximumSentinelsFailClosed(ulong amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Constraint(amount).Canonicalize());

    [Fact]
    public void UnknownVersionsAndEnumsFailClosed()
    {
        var valid = Constraint(1);
        Assert.False(ResourceUseConstraintV1.IsSubset(valid with { Version = 2 }, valid));
        Assert.False(ResourceUseConstraintV1.IsSubset(valid with { AssuranceCeiling = (ResourceAssuranceV1)255 }, valid));
        Assert.False(ResourceUseConstraintV1.IsSubset(valid with { Envelope = valid.Envelope with { ResourceClass = (ResourceClassV1)ushort.MaxValue } }, valid));
    }

    [Fact]
    public void WindowMultiplicationOverflowFailsClosed()
    {
        var throughput = new ResourceEnvelopeV1(1, ResourceDimensionFamilyV1.Throughput,
            ResourceClassV1.DmaThroughput, ResourceUnitV1.BytesPerWindow,
            ulong.MaxValue - 1, 2, "dma:semantic-v1");

        Assert.Throws<OverflowException>(() => throughput.CheckedWindowQuantity());
    }

    private static ResourceUseConstraintV1 Constraint(
        ulong amount,
        ResourceAssuranceV1 assurance = ResourceAssuranceV1.RuntimeEnforced,
        ushort depth = 4) =>
        new(1, new(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1"),
            10, 1000, assurance, depth);
}
