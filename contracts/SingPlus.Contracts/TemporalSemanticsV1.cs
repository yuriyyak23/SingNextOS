using System.Buffers.Binary;
using System.Text;

namespace SingPlus.Contracts;

public enum TemporalDeadlineSemanticsV1 : byte
{
    None = 1,
    CancellationRequestOnly = 2,
    GuaranteedCompletion = 3,
}

/// <summary>
/// Non-authoritative temporal/resource requirement or guarantee. Reservation, accounting,
/// cancellation deadlines, and guaranteed completion remain distinct claims.
/// </summary>
public readonly record struct TemporalSemanticsV1(
    ushort Version,
    ResourceEnvelopeV1 ComputeEnvelope,
    ResourceAssuranceV1 Assurance,
    TemporalDeadlineSemanticsV1 DeadlineSemantics,
    DeadlineClockClass DeadlineClock,
    long DeadlineTimestamp)
{
    public const ushort CurrentVersion = 1;
    public const int MaxSemanticScopeBytes = 160;
    public static SemanticExtensionClassId ExtensionClassId => new("temporal.semantics");
    public bool AuthorizesExecution => false;
    public bool ReservesCapacity => false;
    public bool AuthorizesCancellation => false;
    public bool GuaranteesCompletion => false;

    public TemporalSemanticsV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Assurance) ||
            !Enum.IsDefined(DeadlineSemantics) || !Enum.IsDefined(DeadlineClock))
            throw new NotSupportedException("Temporal semantics version or class is unsupported.");
        var envelope = ComputeEnvelope.Canonicalize();
        if (envelope.Family != ResourceDimensionFamilyV1.Time ||
            envelope.ResourceClass != ResourceClassV1.ComputeTime ||
            envelope.Unit != ResourceUnitV1.Nanoseconds || envelope.WindowNanoseconds != 0)
            throw new ArgumentException("Temporal v1 supports only canonical ComputeTime/Nanoseconds envelopes.");
        int scopeBytes;
        try { scopeBytes = new UTF8Encoding(false, true).GetByteCount(envelope.SemanticScope); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Temporal semantic scope must contain valid Unicode scalar values.",
                nameof(ComputeEnvelope), exception);
        }
        if (scopeBytes > MaxSemanticScopeBytes)
            throw new ArgumentException("Temporal semantic scope exceeds its canonical bound.");
        if (DeadlineSemantics == TemporalDeadlineSemanticsV1.None && DeadlineTimestamp != 0)
            throw new ArgumentException("A contour without a deadline cannot carry a deadline timestamp.");
        if (DeadlineSemantics != TemporalDeadlineSemanticsV1.None && DeadlineTimestamp <= 0)
            throw new ArgumentException("A deadline contour requires a positive monotonic timestamp.");
        if (DeadlineSemantics == TemporalDeadlineSemanticsV1.GuaranteedCompletion &&
            Assurance != ResourceAssuranceV1.GuaranteedReservation)
            throw new ArgumentException("Guaranteed completion requires a separately qualified capacity reservation.");
        return this with { ComputeEnvelope = envelope };
    }

    public byte[] SerializeCanonical()
    {
        var exact = Validate();
        var scope = new UTF8Encoding(false, true).GetBytes(exact.ComputeEnvelope.SemanticScope);
        var bytes = new byte[34 + scope.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, exact.Version);
        bytes[2] = (byte)exact.ComputeEnvelope.Family;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3), (ushort)exact.ComputeEnvelope.ResourceClass);
        bytes[5] = (byte)exact.ComputeEnvelope.Unit;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(6), exact.ComputeEnvelope.Amount);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(14), exact.ComputeEnvelope.WindowNanoseconds);
        bytes[22] = (byte)exact.Assurance;
        bytes[23] = (byte)exact.DeadlineSemantics;
        bytes[24] = (byte)exact.DeadlineClock;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(25), exact.DeadlineTimestamp);
        bytes[33] = checked((byte)scope.Length);
        scope.CopyTo(bytes, 34);
        return bytes;
    }

    public static TemporalSemanticsV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 34 || bytes[33] != bytes.Length - 34)
            throw new FormatException("Temporal semantics payload has a non-canonical size.");
        string scope;
        try { scope = new UTF8Encoding(false, true).GetString(bytes[34..]); }
        catch (DecoderFallbackException exception)
        { throw new FormatException("Temporal semantic scope is not canonical UTF-8.", exception); }
        return new TemporalSemanticsV1(BinaryPrimitives.ReadUInt16BigEndian(bytes),
            new ResourceEnvelopeV1(1, (ResourceDimensionFamilyV1)bytes[2],
                (ResourceClassV1)BinaryPrimitives.ReadUInt16BigEndian(bytes[3..]),
                (ResourceUnitV1)bytes[5], BinaryPrimitives.ReadUInt64BigEndian(bytes[6..]),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[14..]), scope),
            (ResourceAssuranceV1)bytes[22], (TemporalDeadlineSemanticsV1)bytes[23],
            (DeadlineClockClass)bytes[24], BinaryPrimitives.ReadInt64BigEndian(bytes[25..])).Validate();
    }

    public SemanticExtensionClauseV1 ToClause(SemanticExtensionRequirement requirement) =>
        SemanticExtensionClauseV1.Create(ExtensionClassId, "singnext.temporal-semantics/1",
            CurrentVersion, requirement, SerializeCanonical());
}

public static class TemporalSemanticPartialOrderV1
{
    public static bool Refines(TemporalSemanticsV1 provided, TemporalSemanticsV1 required)
    {
        try { provided = provided.Validate(); required = required.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return false; }
        if (provided.ComputeEnvelope.Family != required.ComputeEnvelope.Family ||
            provided.ComputeEnvelope.ResourceClass != required.ComputeEnvelope.ResourceClass ||
            provided.ComputeEnvelope.Unit != required.ComputeEnvelope.Unit ||
            provided.ComputeEnvelope.WindowNanoseconds != required.ComputeEnvelope.WindowNanoseconds ||
            !string.Equals(provided.ComputeEnvelope.SemanticScope,
                required.ComputeEnvelope.SemanticScope, StringComparison.Ordinal) ||
            (provided.Assurance == ResourceAssuranceV1.GuaranteedReservation) !=
            (required.Assurance == ResourceAssuranceV1.GuaranteedReservation) ||
            !SemanticPartialOrdersV1.ResourceAssuranceRefines(provided.Assurance, required.Assurance) ||
            provided.DeadlineSemantics != required.DeadlineSemantics ||
            provided.DeadlineClock != required.DeadlineClock)
            return false;

        var quantityRefines = required.Assurance == ResourceAssuranceV1.GuaranteedReservation
            ? provided.ComputeEnvelope.Amount >= required.ComputeEnvelope.Amount
            : provided.ComputeEnvelope.Amount <= required.ComputeEnvelope.Amount;
        if (!quantityRefines) return false;
        return required.DeadlineSemantics switch
        {
            TemporalDeadlineSemanticsV1.None => provided.DeadlineTimestamp == 0,
            TemporalDeadlineSemanticsV1.CancellationRequestOnly =>
                provided.DeadlineTimestamp == required.DeadlineTimestamp,
            TemporalDeadlineSemanticsV1.GuaranteedCompletion =>
                provided.DeadlineTimestamp <= required.DeadlineTimestamp,
            _ => false,
        };
    }
}
