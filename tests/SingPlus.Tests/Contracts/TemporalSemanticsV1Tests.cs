using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class TemporalSemanticsV1Tests
{
    [Fact]
    public void EnforcedUpperBoundRoundTripsWithoutBecomingCapacityOrDeadlineAuthority()
    {
        var semantics = UpperBound(100);

        var parsed = TemporalSemanticsV1.ParseCanonical(semantics.SerializeCanonical());
        var clause = semantics.ToClause(SemanticExtensionRequirement.Mandatory);

        Assert.Equal(semantics, parsed);
        Assert.Equal(TemporalSemanticsV1.ExtensionClassId, clause.ClassId);
        Assert.False(semantics.AuthorizesExecution);
        Assert.False(semantics.ReservesCapacity);
        Assert.False(semantics.AuthorizesCancellation);
        Assert.False(semantics.GuaranteesCompletion);
    }

    [Fact]
    public void SmallerUpperBoundRefinesLargerButReservationUsesCapacityDirection()
    {
        Assert.True(TemporalSemanticPartialOrderV1.Refines(UpperBound(50), UpperBound(100)));
        Assert.False(TemporalSemanticPartialOrderV1.Refines(UpperBound(100), UpperBound(50)));

        var smallReservation = Reservation(50);
        var largeReservation = Reservation(100);
        Assert.True(TemporalSemanticPartialOrderV1.Refines(largeReservation, smallReservation));
        Assert.False(TemporalSemanticPartialOrderV1.Refines(smallReservation, largeReservation));
        Assert.False(TemporalSemanticPartialOrderV1.Refines(smallReservation, UpperBound(100)));
        Assert.False(TemporalSemanticPartialOrderV1.Refines(UpperBound(50), largeReservation));
        Assert.False(TemporalSemanticPartialOrderV1.Refines(largeReservation,
            UpperBound(200) with { Assurance = ResourceAssuranceV1.RuntimeEnforced }));
    }

    [Fact]
    public void CancellationDeadlineIsObservationNotCompletionGuarantee()
    {
        var cancellation = UpperBound(100) with
        {
            DeadlineSemantics = TemporalDeadlineSemanticsV1.CancellationRequestOnly,
            DeadlineTimestamp = 12345,
        };
        var guaranteed = Reservation(100) with
        {
            DeadlineSemantics = TemporalDeadlineSemanticsV1.GuaranteedCompletion,
            DeadlineTimestamp = 12345,
        };

        Assert.Equal(cancellation, cancellation.Validate());
        Assert.Equal(guaranteed, guaranteed.Validate());
        Assert.False(TemporalSemanticPartialOrderV1.Refines(guaranteed, cancellation));
        Assert.False(TemporalSemanticPartialOrderV1.Refines(cancellation, guaranteed));
    }

    [Fact]
    public void GuaranteedCompletionCannotBeLaunderedFromAccountingOrUpperBound()
    {
        Assert.Throws<ArgumentException>(() => (UpperBound(100) with
        {
            DeadlineSemantics = TemporalDeadlineSemanticsV1.GuaranteedCompletion,
            DeadlineTimestamp = 1,
        }).Validate());
        Assert.Throws<ArgumentException>(() => (UpperBound(100) with
        {
            DeadlineSemantics = TemporalDeadlineSemanticsV1.None,
            DeadlineTimestamp = 1,
        }).Validate());
        Assert.Throws<ArgumentException>(() => (UpperBound(100) with
        {
            DeadlineSemantics = TemporalDeadlineSemanticsV1.CancellationRequestOnly,
            DeadlineTimestamp = 0,
        }).Validate());
    }

    [Fact]
    public void UnitScopeAndMalformedCanonicalPayloadFailClosed()
    {
        Assert.Throws<ArgumentException>(() => (UpperBound(100) with
        {
            ComputeEnvelope = new(1, ResourceDimensionFamilyV1.Throughput,
                ResourceClassV1.DmaThroughput, ResourceUnitV1.BytesPerWindow,
                100, 1_000, "host:dma"),
        }).Validate());
        Assert.False(TemporalSemanticPartialOrderV1.Refines(UpperBound(50, "provider:b"),
            UpperBound(100, "provider:a")));
        Assert.Throws<FormatException>(() => TemporalSemanticsV1.ParseCanonical(new byte[33]));
        var trailing = UpperBound(100).SerializeCanonical().Concat([byte.MinValue]).ToArray();
        Assert.Throws<FormatException>(() => TemporalSemanticsV1.ParseCanonical(trailing));
    }

    [Fact]
    public void PartialOrderIsReflexiveAntisymmetricAndTransitiveForQualifiedClasses()
    {
        var values = new[] { UpperBound(25), UpperBound(50), UpperBound(100) };
        Assert.All(values, item => Assert.True(TemporalSemanticPartialOrderV1.Refines(item, item)));
        Assert.True(TemporalSemanticPartialOrderV1.Refines(values[0], values[1]));
        Assert.True(TemporalSemanticPartialOrderV1.Refines(values[1], values[2]));
        Assert.True(TemporalSemanticPartialOrderV1.Refines(values[0], values[2]));
        Assert.False(TemporalSemanticPartialOrderV1.Refines(values[2], values[0]));
    }

    private static TemporalSemanticsV1 UpperBound(ulong amount, string scope = "host:compute-v1") =>
        new TemporalSemanticsV1(1, Envelope(amount, scope), ResourceAssuranceV1.EnforcedUpperBound,
            TemporalDeadlineSemanticsV1.None, DeadlineClockClass.MonotonicRuntime, 0).Validate();

    private static TemporalSemanticsV1 Reservation(ulong amount) =>
        new TemporalSemanticsV1(1, Envelope(amount, "host:compute-v1"), ResourceAssuranceV1.GuaranteedReservation,
            TemporalDeadlineSemanticsV1.None, DeadlineClockClass.MonotonicRuntime, 0).Validate();

    private static ResourceEnvelopeV1 Envelope(ulong amount, string scope) =>
        new(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, amount, 0, scope);
}
