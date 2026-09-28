using System.Buffers.Binary;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class SemanticExtensionContractsV1Tests
{
    private static readonly SemanticExtensionClassId Memory = new("memory.staged-exclusive");
    private static readonly SemanticExtensionClassId Dma = new("dma.generation-binding");

    [Fact]
    public void CanonicalOrderAndDigestAreStableAcrossInputOrderAndRoundTrip()
    {
        var memory = Clause(Memory, SemanticExtensionRequirement.Mandatory, "read-only-input/staged-output");
        var dma = Clause(Dma, SemanticExtensionRequirement.Optional, "mapping-generation/7");

        var left = OperationSemanticExtensionsV1.Create([memory, dma]);
        var right = OperationSemanticExtensionsV1.Create([dma, memory]);
        var parsed = OperationSemanticExtensionsV1.ParseCanonical(left.SerializeCanonical());

        Assert.Equal(left.SerializeCanonical(), right.SerializeCanonical());
        Assert.Equal(left.Digest, right.Digest);
        Assert.Equal(left.Digest, parsed.Digest);
        Assert.Equal([Dma, Memory], parsed.Clauses.Select(clause => clause.ClassId));
        Assert.False(left.AuthorizesExecution);
        Assert.False(left.AuthorizesEffect);
    }

    [Fact]
    public void PayloadMutationCannotChangeCanonicalContract()
    {
        var source = Encoding.UTF8.GetBytes("exclusive");
        var clause = SemanticExtensionClauseV1.Create(Memory, "singnext.memory/1", 1,
            SemanticExtensionRequirement.Mandatory, source);
        var contract = OperationSemanticExtensionsV1.Create([clause]);
        var before = contract.SerializeCanonical();

        source[0] ^= 0x7f;
        var exposed = clause.Payload.ToArray();
        exposed[0] ^= 0x7f;

        Assert.Equal(before, contract.SerializeCanonical());
        Assert.Equal("exclusive", Encoding.UTF8.GetString(clause.Payload.Span));
    }

    [Fact]
    public void DuplicateClassIsRejectedEvenWhenPayloadIsIdentical()
    {
        var clause = Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive");
        Assert.Throws<ArgumentException>(() => OperationSemanticExtensionsV1.Create([clause, clause]));
        Assert.Throws<ArgumentException>(() => OperationSemanticExtensionsV1.Create([
            clause, Clause(Memory, SemanticExtensionRequirement.Mandatory, "conflicting")
        ]));
    }

    [Fact]
    public void UnknownMandatoryFailsClosedAndUnknownOptionalNeedsExplicitWeakContour()
    {
        var supported = new HashSet<SemanticExtensionClassId>();
        var none = ExecutionGuaranteeExtensionsV1.Create([]);
        var mandatory = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        var optional = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Optional, "exclusive")
        ]);

        var mandatoryDecision = Evaluate(mandatory, none, supported, _ => true);
        var deniedOptional = Evaluate(optional, none, supported, _ => false);
        var acceptedOptional = Evaluate(optional, none, supported, _ => true);

        Assert.Equal(SemanticExtensionMatchStatus.UnknownMandatory, mandatoryDecision.Status);
        Assert.Equal(SemanticExtensionMatchStatus.OptionalAbsenceNotAllowed, deniedOptional.Status);
        Assert.Equal(SemanticExtensionMatchStatus.Refines, acceptedOptional.Status);
        Assert.False(mandatoryDecision.AuthorizesExecution);
        Assert.False(mandatoryDecision.AuthorizesEffect);
    }

    [Fact]
    public void PermittedUnknownOptionalCannotShortCircuitALaterMandatoryClause()
    {
        var requirements = OperationSemanticExtensionsV1.Create([
            Clause(Dma, SemanticExtensionRequirement.Optional, "mapping"),
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        var guarantees = ExecutionGuaranteeExtensionsV1.Create([]);
        var supported = new HashSet<SemanticExtensionClassId> { Memory };

        var decision = Evaluate(requirements, guarantees, supported, _ => true);

        Assert.Equal(SemanticExtensionMatchStatus.MissingMandatory, decision.Status);
        Assert.Equal(Memory, decision.ClassId);
    }

    [Fact]
    public void MissingMandatoryAndWeakenedGuaranteeAreRejected()
    {
        var requirement = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "strong")
        ]);
        var none = ExecutionGuaranteeExtensionsV1.Create([]);
        var weak = ExecutionGuaranteeExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "weak")
        ]);
        var exact = ExecutionGuaranteeExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "strong")
        ]);
        var supported = new HashSet<SemanticExtensionClassId> { Memory };

        Assert.Equal(SemanticExtensionMatchStatus.MissingMandatory,
            Evaluate(requirement, none, supported, _ => false).Status);
        Assert.Equal(SemanticExtensionMatchStatus.GuaranteeDoesNotRefine,
            Evaluate(requirement, weak, supported, _ => false).Status);
        Assert.Equal(SemanticExtensionMatchStatus.Refines,
            Evaluate(requirement, exact, supported, _ => false).Status);
    }

    [Fact]
    public void BindingDigestIncludesBothV1ObjectsExtensionsAndGenerationVector()
    {
        var requirements = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        var guarantees = ExecutionGuaranteeExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        var generations = new[]
        {
            new SemanticGenerationSnapshotV1("region", 3),
            new SemanticGenerationSnapshotV1("provider", 8),
        };
        var left = SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'), requirements, guarantees, generations);
        var reordered = SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'), requirements, guarantees,
            generations.Reverse());
        var stale = SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'), requirements, guarantees,
            [new("region", 4), new("provider", 8)]);

        Assert.Equal(left.Digest, reordered.Digest);
        Assert.NotEqual(left.Digest, stale.Digest);
        Assert.Equal(["provider", "region"], left.Generations.Select(item => item.Owner));
        Assert.False(left.AuthorizesExecution);
        Assert.False(left.AuthorizesEffect);
    }

    [Fact]
    public void ParserRejectsTruncationTrailingBytesNonCanonicalOrderAndOversizedLengths()
    {
        var canonical = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]).SerializeCanonical();
        Assert.Throws<FormatException>(() => OperationSemanticExtensionsV1.ParseCanonical(canonical[..^1]));
        Assert.Throws<FormatException>(() => OperationSemanticExtensionsV1.ParseCanonical([.. canonical, 0]));

        var oversized = canonical.ToArray();
        // magic(7), set version(2), count(2), clause version(2), class length(4)
        BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(13, 4), int.MaxValue);
        Assert.Throws<FormatException>(() => OperationSemanticExtensionsV1.ParseCanonical(oversized));

        var reversed = BuildRawSet([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive"),
            Clause(Dma, SemanticExtensionRequirement.Optional, "mapping")
        ]);
        Assert.Throws<FormatException>(() => OperationSemanticExtensionsV1.ParseCanonical(reversed));
    }

    [Fact]
    public void CheckedBoundsAndInvalidEnumsFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SemanticExtensionClauseV1.Create(Memory,
            "singnext.memory/1", 1, SemanticExtensionRequirement.Mandatory,
            new byte[SemanticExtensionClauseV1.MaxPayloadBytes + 1]));
        Assert.Throws<NotSupportedException>(() => SemanticExtensionClauseV1.Create(Memory,
            "singnext.memory/1", 1, (SemanticExtensionRequirement)0, []));
        Assert.Throws<ArgumentException>(() => SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'),
            OperationSemanticExtensionsV1.Create([]), ExecutionGuaranteeExtensionsV1.Create([]),
            [new("region", 1), new("region", 2)]));
    }

    private static SemanticExtensionMatchDecision Evaluate(
        OperationSemanticExtensionsV1 requirements,
        ExecutionGuaranteeExtensionsV1 guarantees,
        IReadOnlySet<SemanticExtensionClassId> supported,
        Func<SemanticExtensionClauseV1, bool> optional) =>
        SemanticExtensionRefinementV1.Evaluate(requirements, guarantees, supported,
            (provided, required) => provided.Payload.Span.SequenceEqual(required.Payload.Span), optional);

    private static SemanticExtensionClauseV1 Clause(
        SemanticExtensionClassId id,
        SemanticExtensionRequirement requirement,
        string value) =>
        SemanticExtensionClauseV1.Create(id, $"singnext.{id.Value}/1", 1, requirement,
            Encoding.UTF8.GetBytes(value));

    private static string Hex(char value) => new(value, 64);

    private static byte[] BuildRawSet(IReadOnlyList<SemanticExtensionClauseV1> clauses)
    {
        var bytes = new List<byte>("SNXSEM1"u8.ToArray());
        WriteU16(bytes, 1);
        WriteU16(bytes, checked((ushort)clauses.Count));
        foreach (var clause in clauses)
        {
            WriteU16(bytes, clause.Version);
            WriteBytes(bytes, Encoding.UTF8.GetBytes(clause.ClassId.Value));
            WriteBytes(bytes, Encoding.UTF8.GetBytes(clause.SchemaId));
            WriteU16(bytes, clause.SchemaVersion);
            bytes.Add((byte)clause.Requirement);
            WriteBytes(bytes, clause.Payload.Span);
        }
        return bytes.ToArray();
    }

    private static void WriteU16(List<byte> target, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        target.AddRange(bytes);
    }

    private static void WriteBytes(List<byte> target, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        target.AddRange(length);
        target.AddRange(value);
    }
}
