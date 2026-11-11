using System.Buffers.Binary;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class SemanticExtensionContractsV1Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingMandatoryGuaranteeDeniesBeforeAnySemanticCallback(bool optionalFirst)
    {
        SemanticExtensionClauseV1 first = Clause(Memory, optionalFirst ? SemanticExtensionRequirement.Optional : SemanticExtensionRequirement.Mandatory,
            "exclusive");
        SemanticExtensionClauseV1 missing = Clause(new("zzz.missing"), SemanticExtensionRequirement.Mandatory, "required");
        int callbacks = 0;
        SemanticExtensionMatchDecision result = SemanticExtensionRefinementV1.Evaluate(OperationSemanticExtensionsV1.Create([first, missing]),
            ExecutionGuaranteeExtensionsV1.Create(optionalFirst ? [] : [first]),
            new HashSet<SemanticExtensionClassId> { Memory, missing.ClassId },
            (_, _) => { callbacks++; return true; }, _ => { callbacks++; return true; });
        Assert.Equal(SemanticExtensionMatchStatus.MissingMandatory, result.Status);
        Assert.Equal(missing.ClassId, result.ClassId);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void SupportSnapshotPreservesInputSetContainsComparer()
    {
        SemanticExtensionClauseV1 required = Clause(new("class.lower"), SemanticExtensionRequirement.Mandatory, "value");
        EqualityComparer<SemanticExtensionClassId> comparer = EqualityComparer<SemanticExtensionClassId>.Create(
            (left, right) => StringComparer.OrdinalIgnoreCase.Equals(left.Value, right.Value),
            value => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value));
        HashSet<SemanticExtensionClassId> supported = new HashSet<SemanticExtensionClassId>(comparer) { new("CLASS.LOWER") };
        SemanticExtensionMatchDecision decision = SemanticExtensionRefinementV1.Evaluate(OperationSemanticExtensionsV1.Create([required]),
            ExecutionGuaranteeExtensionsV1.Create([required]), supported, (_, _) => true, _ => false);
        Assert.Equal(SemanticExtensionMatchStatus.Refines, decision.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SemanticCallbackCannotChangeLaterClassSupportDecision(bool removeKnown)
    {
        SemanticExtensionClauseV1 first = Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive");
        SemanticExtensionClauseV1 second = Clause(new("zzz.later"), removeKnown ? SemanticExtensionRequirement.Mandatory : SemanticExtensionRequirement.Optional,
            "opaque");
        HashSet<SemanticExtensionClassId> supported = new HashSet<SemanticExtensionClassId> { Memory };
        if (removeKnown) supported.Add(second.ClassId);
        int secondRefinements = 0;
        SemanticExtensionMatchDecision decision = SemanticExtensionRefinementV1.Evaluate(OperationSemanticExtensionsV1.Create([first, second]),
            ExecutionGuaranteeExtensionsV1.Create([first, second]), supported,
            (_, required) =>
            {
                if (required.ClassId == Memory)
                {
                    if (removeKnown) supported.Remove(second.ClassId);
                    else supported.Add(second.ClassId);
                }
                else secondRefinements++;
                return true;
            }, _ => false);
        Assert.Equal(removeKnown ? SemanticExtensionMatchStatus.Refines : SemanticExtensionMatchStatus.OptionalAbsenceNotAllowed,
            decision.Status);
        Assert.Equal(removeKnown ? 1 : 0, secondRefinements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownMandatoryRequirementDeniesBeforeAnySemanticCallback(bool optionalKnown)
    {
        SemanticExtensionClauseV1 known = Clause(Memory, optionalKnown ? SemanticExtensionRequirement.Optional : SemanticExtensionRequirement.Mandatory,
            "exclusive");
        SemanticExtensionClauseV1 unknown = Clause(new("zzz.requirement-unknown"), SemanticExtensionRequirement.Mandatory, "opaque");
        int callbacks = 0;
        SemanticExtensionMatchDecision decision = SemanticExtensionRefinementV1.Evaluate(OperationSemanticExtensionsV1.Create([known, unknown]),
            ExecutionGuaranteeExtensionsV1.Create(optionalKnown ? [] : [known]),
            new HashSet<SemanticExtensionClassId> { Memory },
            (_, _) => { callbacks++; return true; }, _ => { callbacks++; return true; });
        Assert.Equal(SemanticExtensionMatchStatus.UnknownMandatory, decision.Status);
        Assert.Equal(unknown.ClassId, decision.ClassId);
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void MalformedUtf16IdentifiersCannotAliasReplacementCharacterDigests(int codeUnit)
    {
        string malformed = new string((char)codeUnit, 1);
        Assert.Throws<ArgumentException>(() => SemanticExtensionClauseV1.Create(
            new(malformed), "schema/1", 1, SemanticExtensionRequirement.Mandatory, []));
        Assert.Throws<ArgumentException>(() => SemanticExtensionClauseV1.Create(
            new("class"), malformed, 1, SemanticExtensionRequirement.Mandatory, []));
    }

    private static readonly SemanticExtensionClassId Memory = new("memory.staged-exclusive");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BindingBaseDigestsRejectUppercaseAndMixedCase(bool guarantee, bool mixed)
    {
        string changed = mixed ? "A" + new string('a', 63) : new string('A', 64);
        Assert.Throws<ArgumentException>(() => SemanticBindingExtensionSetV1.Create(
            guarantee ? Hex('a') : changed, guarantee ? changed : Hex('b'),
            OperationSemanticExtensionsV1.Create([]), ExecutionGuaranteeExtensionsV1.Create([]), []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClauseEnumerationStopsAtOverflowWitness(bool guaranteeFamily)
    {
        int observed = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (guaranteeFamily) _ = ExecutionGuaranteeExtensionsV1.Create(Values());
            else _ = OperationSemanticExtensionsV1.Create(Values());
        });
        Assert.Equal(65, observed);
        IEnumerable<SemanticExtensionClauseV1> Values()
        {
            for (int index = 0; ; index++)
            {
                if (++observed > 65) throw new InvalidOperationException("Enumeration exceeded clause overflow witness.");
                yield return Clause(new($"class-{index:D2}"), SemanticExtensionRequirement.Mandatory, "value");
            }
        }
    }

    [Fact]
    public void MaximumClauseCountPreservesBothFamilyCanonicalDigests()
    {
        SemanticExtensionClauseV1[] values = Enumerable.Range(0, 64).Select(index =>
            Clause(new($"class-{index:D2}"), SemanticExtensionRequirement.Mandatory, "value")).ToArray();
        OperationSemanticExtensionsV1 operation = OperationSemanticExtensionsV1.Create(values);
        ExecutionGuaranteeExtensionsV1 guarantee = ExecutionGuaranteeExtensionsV1.Create(values.Reverse());
        Assert.Equal(64, operation.Clauses.Count);
        Assert.Equal(operation.Digest, guarantee.Digest);
        Assert.Equal(operation.Digest, OperationSemanticExtensionsV1.Create(values.Reverse()).Digest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GenerationOwnerRejectsOversizedOrMalformedUnicode(int kind)
    {
        string owner = kind switch
        {
            0 => new string('a', 97),
            1 => string.Concat(Enumerable.Repeat("\U0001f680", 25)),
            _ => "\ud800"
        };
        Assert.Throws<ArgumentException>(() => GenerationBinding([new(owner, 1)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationOwnerPreservesExactUtf8Boundary(bool supplementary)
    {
        string owner = supplementary ? string.Concat(Enumerable.Repeat("\U0001f680", 24)) : new string('a', 96);
        SemanticBindingExtensionSetV1 binding = GenerationBinding([new(owner, 1)]);
        Assert.Equal(owner, Assert.Single(binding.Generations).Owner);
        Assert.Equal(binding.Digest, GenerationBinding([new(owner, 1)]).Digest);
    }

    [Fact]
    public void GenerationEnumerationStopsAtOverflowWitness()
    {
        int observed = 0;
        Assert.Throws<ArgumentException>(() => GenerationBinding(Values()));
        Assert.Equal(33, observed);
        IEnumerable<SemanticGenerationSnapshotV1> Values()
        {
            for (int index = 0; ; index++)
            {
                if (++observed > 33) throw new InvalidOperationException("Enumeration exceeded overflow witness.");
                yield return new($"owner-{index}", 1);
            }
        }
    }

    [Fact]
    public void MaximumGenerationCountPreservesCanonicalOrder()
    {
        SemanticGenerationSnapshotV1[] values = Enumerable.Range(0, 32).Select(index => new SemanticGenerationSnapshotV1($"owner-{index:D2}", 1)).ToArray();
        Assert.Equal(32, GenerationBinding(values).Generations.Count);
        Assert.Equal(GenerationBinding(values).Digest, GenerationBinding(values.Reverse()).Digest);
    }

    private static SemanticBindingExtensionSetV1 GenerationBinding(IEnumerable<SemanticGenerationSnapshotV1> values) =>
        SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'), OperationSemanticExtensionsV1.Create([]),
            ExecutionGuaranteeExtensionsV1.Create([]), values);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownMandatoryProviderClauseIsDeniedBeforeRefinement(bool emptyRequirements)
    {
        SemanticExtensionClauseV1 known = Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive");
        SemanticExtensionClauseV1 unknown = Clause(new("zzz.provider-unknown"), SemanticExtensionRequirement.Mandatory, "opaque");
        OperationSemanticExtensionsV1 required = OperationSemanticExtensionsV1.Create(emptyRequirements ? [] : [known]);
        ExecutionGuaranteeExtensionsV1 offered = ExecutionGuaranteeExtensionsV1.Create([known, unknown]);
        SemanticExtensionMatchDecision decision = SemanticExtensionRefinementV1.Evaluate(required, offered,
            new HashSet<SemanticExtensionClassId> { Memory },
            (_, _) => throw new InvalidOperationException("Unknown mandatory must deny before refinement."),
            _ => throw new InvalidOperationException("Mandatory cannot downgrade to optional absence."));
        Assert.Equal(SemanticExtensionMatchStatus.UnknownMandatory, decision.Status);
    }

    [Fact]
    public void AdditionalUnknownOptionalProviderClauseRetainsDigestWithoutWeakeningRequirement()
    {
        SemanticExtensionClauseV1 known = Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive");
        ExecutionGuaranteeExtensionsV1 baseline = ExecutionGuaranteeExtensionsV1.Create([known]);
        ExecutionGuaranteeExtensionsV1 offered = ExecutionGuaranteeExtensionsV1.Create([known,
            Clause(new("zzz.provider-unknown"), SemanticExtensionRequirement.Optional, "opaque")]);
        Assert.NotEqual(baseline.Digest, offered.Digest);
        SemanticExtensionMatchDecision decision = SemanticExtensionRefinementV1.Evaluate(OperationSemanticExtensionsV1.Create([known]),
            offered, new HashSet<SemanticExtensionClassId> { Memory },
            (provided, required) => provided.Payload.Span.SequenceEqual(required.Payload.Span), _ => false);
        Assert.True(decision.IsAccepted);
    }
    [Fact]
    public void ValidSupplementaryUnicodeAndReplacementCharacterRemainExactAndBounded()
    {
        string exact = string.Concat(Enumerable.Repeat("\U0001f680", 24));
        SemanticExtensionClauseV1 clause = SemanticExtensionClauseV1.Create(new(exact), "schema/\ufffd", 1,
            SemanticExtensionRequirement.Mandatory, []);
        OperationSemanticExtensionsV1 set = OperationSemanticExtensionsV1.Create([clause]);
        OperationSemanticExtensionsV1 parsed = OperationSemanticExtensionsV1.ParseCanonical(set.SerializeCanonical());
        Assert.Equal(exact, parsed.Clauses.Single().ClassId.Value);
        Assert.Equal("schema/\ufffd", parsed.Clauses.Single().SchemaId);
        Assert.Equal(set.Digest, parsed.Digest);
        Assert.Throws<ArgumentException>(() => SemanticExtensionClauseV1.Create(new(exact + "a"),
            "schema/1", 1, SemanticExtensionRequirement.Mandatory, []));
    }

    private static readonly SemanticExtensionClassId Dma = new("dma.generation-binding");

    [Fact]
    public void CanonicalOrderAndDigestAreStableAcrossInputOrderAndRoundTrip()
    {
        SemanticExtensionClauseV1 memory = Clause(Memory, SemanticExtensionRequirement.Mandatory, "read-only-input/staged-output");
        SemanticExtensionClauseV1 dma = Clause(Dma, SemanticExtensionRequirement.Optional, "mapping-generation/7");

        OperationSemanticExtensionsV1 left = OperationSemanticExtensionsV1.Create([memory, dma]);
        OperationSemanticExtensionsV1 right = OperationSemanticExtensionsV1.Create([dma, memory]);
        OperationSemanticExtensionsV1 parsed = OperationSemanticExtensionsV1.ParseCanonical(left.SerializeCanonical());

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
        byte[] source = Encoding.UTF8.GetBytes("exclusive");
        SemanticExtensionClauseV1 clause = SemanticExtensionClauseV1.Create(Memory, "singnext.memory/1", 1,
            SemanticExtensionRequirement.Mandatory, source);
        OperationSemanticExtensionsV1 contract = OperationSemanticExtensionsV1.Create([clause]);
        byte[] before = contract.SerializeCanonical();

        source[0] ^= 0x7f;
        byte[] exposed = clause.Payload.ToArray();
        exposed[0] ^= 0x7f;

        Assert.Equal(before, contract.SerializeCanonical());
        Assert.Equal("exclusive", Encoding.UTF8.GetString(clause.Payload.Span));
    }

    [Fact]
    public void DuplicateClassIsRejectedEvenWhenPayloadIsIdentical()
    {
        SemanticExtensionClauseV1 clause = Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive");
        Assert.Throws<ArgumentException>(() => OperationSemanticExtensionsV1.Create([clause, clause]));
        Assert.Throws<ArgumentException>(() => OperationSemanticExtensionsV1.Create([
            clause, Clause(Memory, SemanticExtensionRequirement.Mandatory, "conflicting")
        ]));
    }

    [Fact]
    public void UnknownMandatoryFailsClosedAndUnknownOptionalNeedsExplicitWeakContour()
    {
        HashSet<SemanticExtensionClassId> supported = new HashSet<SemanticExtensionClassId>();
        ExecutionGuaranteeExtensionsV1 none = ExecutionGuaranteeExtensionsV1.Create([]);
        OperationSemanticExtensionsV1 mandatory = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        OperationSemanticExtensionsV1 optional = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Optional, "exclusive")
        ]);

        SemanticExtensionMatchDecision mandatoryDecision = Evaluate(mandatory, none, supported, _ => true);
        SemanticExtensionMatchDecision deniedOptional = Evaluate(optional, none, supported, _ => false);
        SemanticExtensionMatchDecision acceptedOptional = Evaluate(optional, none, supported, _ => true);

        Assert.Equal(SemanticExtensionMatchStatus.UnknownMandatory, mandatoryDecision.Status);
        Assert.Equal(SemanticExtensionMatchStatus.OptionalAbsenceNotAllowed, deniedOptional.Status);
        Assert.Equal(SemanticExtensionMatchStatus.Refines, acceptedOptional.Status);
        Assert.False(mandatoryDecision.AuthorizesExecution);
        Assert.False(mandatoryDecision.AuthorizesEffect);
    }

    [Fact]
    public void PermittedUnknownOptionalCannotShortCircuitALaterMandatoryClause()
    {
        OperationSemanticExtensionsV1 requirements = OperationSemanticExtensionsV1.Create([
            Clause(Dma, SemanticExtensionRequirement.Optional, "mapping"),
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        ExecutionGuaranteeExtensionsV1 guarantees = ExecutionGuaranteeExtensionsV1.Create([]);
        HashSet<SemanticExtensionClassId> supported = new HashSet<SemanticExtensionClassId> { Memory };

        SemanticExtensionMatchDecision decision = Evaluate(requirements, guarantees, supported, _ => true);

        Assert.Equal(SemanticExtensionMatchStatus.MissingMandatory, decision.Status);
        Assert.Equal(Memory, decision.ClassId);
    }

    [Fact]
    public void MissingMandatoryAndWeakenedGuaranteeAreRejected()
    {
        OperationSemanticExtensionsV1 requirement = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "strong")
        ]);
        ExecutionGuaranteeExtensionsV1 none = ExecutionGuaranteeExtensionsV1.Create([]);
        ExecutionGuaranteeExtensionsV1 weak = ExecutionGuaranteeExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "weak")
        ]);
        ExecutionGuaranteeExtensionsV1 exact = ExecutionGuaranteeExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "strong")
        ]);
        HashSet<SemanticExtensionClassId> supported = new HashSet<SemanticExtensionClassId> { Memory };

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
        OperationSemanticExtensionsV1 requirements = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        ExecutionGuaranteeExtensionsV1 guarantees = ExecutionGuaranteeExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]);
        SemanticGenerationSnapshotV1[] generations = new[]
        {
            new SemanticGenerationSnapshotV1("region", 3),
            new SemanticGenerationSnapshotV1("provider", 8),
        };
        SemanticBindingExtensionSetV1 left = SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'), requirements, guarantees, generations);
        SemanticBindingExtensionSetV1 reordered = SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'), requirements, guarantees,
            generations.Reverse());
        SemanticBindingExtensionSetV1 stale = SemanticBindingExtensionSetV1.Create(Hex('a'), Hex('b'), requirements, guarantees,
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
        byte[] canonical = OperationSemanticExtensionsV1.Create([
            Clause(Memory, SemanticExtensionRequirement.Mandatory, "exclusive")
        ]).SerializeCanonical();
        Assert.Throws<FormatException>(() => OperationSemanticExtensionsV1.ParseCanonical(canonical[..^1]));
        Assert.Throws<FormatException>(() => OperationSemanticExtensionsV1.ParseCanonical([.. canonical, 0]));

        byte[] oversized = canonical.ToArray();
        // magic(7), set version(2), count(2), clause version(2), class length(4)
        BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(13, 4), int.MaxValue);
        Assert.Throws<FormatException>(() => OperationSemanticExtensionsV1.ParseCanonical(oversized));

        byte[] reversed = BuildRawSet([
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
        List<byte> bytes = new List<byte>("SNXSEM1"u8.ToArray());
        WriteU16(bytes, 1);
        WriteU16(bytes, checked((ushort)clauses.Count));
        foreach (SemanticExtensionClauseV1 clause in clauses)
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
