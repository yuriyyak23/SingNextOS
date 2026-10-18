using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class DmaGrantClosureObservationV1Tests
{
    private static DmaGrantClosureObservationV1 Observation() => new(1, new string('a', 64), 3, 4,
        new(1, "dma:7:1", 4, SemanticTraceEventKindV1.Visible, "singnext.platform-dma",
            new string('b', 64), new string('c', 64)));

    [Fact]
    public void CanonicalBytesRoundTripForOfflineConsumer()
    {
        var value = Observation();
        Assert.Equal(value, DmaGrantClosureObservationV1.ParseCanonical(value.SerializeCanonical()));
        var unicode = value with { LastVisibleEvent = value.LastVisibleEvent with { OperationCorrelation = "dma:данные:1" } };
        Assert.Equal(unicode, DmaGrantClosureObservationV1.ParseCanonical(unicode.SerializeCanonical()));
    }

    [Fact]
    public void EveryTruncatedPrefixAndTrailingByteIsRejected()
    {
        var bytes = Observation().SerializeCanonical();
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<FormatException>(() => DmaGrantClosureObservationV1.ParseCanonical(bytes.AsSpan(0, length)));
        Assert.Throws<FormatException>(() => DmaGrantClosureObservationV1.ParseCanonical([.. bytes, 0]));
        Assert.Throws<FormatException>(() => DmaGrantClosureObservationV1.ParseCanonical(new byte[513]));
    }

    [Fact]
    public void OverlongTokenLengthAndMalformedUtf8AreRejected()
    {
        var bytes = Observation().SerializeCanonical();
        // version + grant digest + provider + epoch + event version = 52 bytes.
        const int tokenOffset = 52;
        var overlong = bytes.Take(tokenOffset).Concat(new byte[] { (byte)(bytes[tokenOffset] | 0x80), 0 })
            .Concat(bytes.Skip(tokenOffset + 1)).ToArray();
        Assert.Throws<FormatException>(() => DmaGrantClosureObservationV1.ParseCanonical(overlong));
        bytes[tokenOffset + 1] = 0xff;
        Assert.Throws<FormatException>(() => DmaGrantClosureObservationV1.ParseCanonical(bytes));
    }

    [Fact]
    public void UnknownVersionAndHugeTokenLengthAreRejectedBeforeConsumption()
    {
        var bytes = Observation().SerializeCanonical();
        bytes[0] = 2;
        Assert.Throws<FormatException>(() => DmaGrantClosureObservationV1.ParseCanonical(bytes));
        var huge = Observation().SerializeCanonical().Take(52)
            .Concat(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x07 }).ToArray();
        Assert.Throws<FormatException>(() => DmaGrantClosureObservationV1.ParseCanonical(huge));
    }

    [Fact]
    public void CanonicalObservationIsStableAndBindsEveryTupleComponent()
    {
        var value = Observation();
        Assert.Equal(value.SerializeCanonical(), value.SerializeCanonical());
        Assert.Equal(64, value.ComputeDigest().Length);
        foreach (var changed in new[]
        {
            value with { GrantIdentityDigest = new string('d', 64) },
            value with { ProviderGeneration = 5 },
            value with { BackendEpoch = 6 },
            value with { LastVisibleEvent = value.LastVisibleEvent with { OperationCorrelation = "dma:8:1" } },
            value with { LastVisibleEvent = value.LastVisibleEvent with { Sequence = 5 } },
            value with { LastVisibleEvent = value.LastVisibleEvent with { EvidenceDigest = new string('d', 64) } },
        }) Assert.NotEqual(value.ComputeDigest(), changed.ComputeDigest());
        Assert.False(value.AuthorizesReclaim);
        Assert.False(value.ProvesPublication);
        Assert.False(value.ProvesSettlement);
    }

    [Theory]
    [InlineData(0, 3, 4)]
    [InlineData(2, 3, 4)]
    [InlineData(1, 0, 4)]
    [InlineData(1, 3, 0)]
    public void UnknownVersionOrZeroTupleFailsClosed(int version, int provider, int epoch)
    {
        var value = Observation() with { Version = (ushort)version, ProviderGeneration = (ulong)provider, BackendEpoch = (ulong)epoch };
        Assert.Throws<NotSupportedException>(() => value.Validate());
        Assert.False(DmaGrantClosureProjectionV1.Validate([], value, value.GrantIdentityDigest,
            value.ProviderGeneration, value.BackendEpoch));
    }

    [Fact]
    public void NonCanonicalDigestAndNonVisibilityEventAreRejected()
    {
        var value = Observation();
        Assert.Throws<ArgumentException>(() => (value with { GrantIdentityDigest = value.GrantIdentityDigest.ToUpperInvariant() }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { LastVisibleEvent = value.LastVisibleEvent with { Kind = SemanticTraceEventKindV1.Released } }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { LastVisibleEvent = value.LastVisibleEvent with { Source = "another-provider" } }).Validate());
        Assert.ThrowsAny<ArgumentException>(() => (value with { LastVisibleEvent = value.LastVisibleEvent with { OperationCorrelation = "dma:\ud800" } }).Validate());
    }
}
