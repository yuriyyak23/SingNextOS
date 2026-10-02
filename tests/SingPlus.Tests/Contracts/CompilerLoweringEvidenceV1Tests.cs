using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class CompilerLoweringEvidenceV1Tests
{
    public static IEnumerable<object[]> InvalidUnicodeTokens() =>
        Enumerable.Range(0, 11).SelectMany(field => new[]
        { new object[] { field, false }, new object[] { field, true } });

    [Theory]
    [MemberData(nameof(InvalidUnicodeTokens))]
    public void InvalidUnicodeCannotEnterCanonicalLoweringFacts(int field, bool lowSurrogate)
    {
        var bad = "fact:" + (lowSurrogate ? '\uDC00' : '\uD800');
        Action validate = field switch
        {
            0 => () => CompilerLoweringEvidenceV1.Create(bad, D('a'), D('b'), D('c'), D('d'), Footprints()),
            1 => () => new LoweringFootprintFactV1(bad, 0, 1, LoweringFootprintAccessV1.Read).Validate(),
            2 => () => new LoweringAliasFactV1(bad, "output", true).Validate(),
            3 => () => new LoweringAliasFactV1("input", bad, true).Validate(),
            4 => () => new LoweringOrderingFactV1(bad, true, true).Validate(),
            5 => () => new LoweringNumericModeFactV1(bad, "nearest", "wrap", 128).Validate(),
            6 => () => new LoweringNumericModeFactV1("add", bad, "wrap", 128).Validate(),
            7 => () => new LoweringNumericModeFactV1("add", "nearest", bad, 128).Validate(),
            8 => () => new LoweringSafePointFactV1(bad, 0, D('e')).Validate(),
            9 => () => new LoweringStaticResourceEstimateFactV1(bad, 1, "count").Validate(),
            _ => () => new LoweringStaticResourceEstimateFactV1("estimate", 1, bad).Validate(),
        };
        Assert.ThrowsAny<ArgumentException>(validate);
    }

    [Fact]
    public void ValidUnicodeLoweringFactsRetainExactCanonicalBytes()
    {
        var evidence = Evidence(safePoints: [new("точка-😀", 0, D('e'))],
            resourceEstimates: [new("ресурс-零", 1, "count")]);
        Assert.Contains("точка-😀", global::System.Text.Encoding.UTF8.GetString(evidence.SerializeCanonical()));
        Assert.Equal(evidence.EvidenceDigest, Evidence(safePoints: [new("точка-😀", 0, D('e'))],
            resourceEstimates: [new("ресурс-零", 1, "count")]).EvidenceDigest);
        Assert.False(evidence.AuthorizesExecution);
        var metadata = CompilerLoweringEvidenceMetadataCodecV1.Emit(CompilerLoweringEvidenceEnvelopeV1.Create(evidence));
        Assert.Equal(evidence.EvidenceDigest, CompilerLoweringEvidenceMetadataCodecV1.Parse(metadata).EvidenceDigest);
    }

    [Fact]
    public void CanonicalEvidenceIsOrderIndependentImmutableAndNonAuthoritative()
    {
        var footprints = new[]
        {
            new LoweringFootprintFactV1("output", 0, 32, LoweringFootprintAccessV1.Write),
            new LoweringFootprintFactV1("input", 0, 64, LoweringFootprintAccessV1.Read),
        };
        var first = Evidence(footprints);
        var second = Evidence(footprints.Reverse());
        footprints[0] = new("mutated", 0, 1, LoweringFootprintAccessV1.Read);

        Assert.Equal(first.SerializeCanonical(), second.SerializeCanonical());
        Assert.Equal(first.EvidenceDigest, second.EvidenceDigest);
        Assert.Equal(first.StaticFactSetDigest, second.StaticFactSetDigest);
        Assert.DoesNotContain(first.Footprints, fact => fact.Symbol == "mutated");
        Assert.False(first.AuthorizesExecution);
        Assert.False(first.ProvesRuntimeLegality);
        Assert.False(first.ProvesCurrentAuthority);
        Assert.False(first.ProvesProviderAvailability);
    }

    [Fact]
    public void SchemaVersionAndCanonicalDigestMutationAreRejected()
    {
        var envelope = CompilerLoweringEvidenceEnvelopeV1.Create(Evidence());

        Assert.Throws<NotSupportedException>(() => (envelope with { SchemaVersion = 2 }).Validate());
        Assert.Throws<NotSupportedException>(() => (envelope with { SchemaId = "schema:other" }).Validate());
        Assert.Throws<ArgumentException>(() => (envelope with { EvidenceDigest = new('0', 64) }).Validate());
        Assert.False(envelope.AuthorizesExecution);
    }

    [Fact]
    public void MandatoryFactShapesAndDuplicateFactsFailClosed()
    {
        Assert.Throws<ArgumentException>(() => Evidence([
            new("input", 0, 0, LoweringFootprintAccessV1.Read)]));
        Assert.Throws<ArgumentException>(() => Evidence([
            new("input", 0, 64, LoweringFootprintAccessV1.Read),
            new("input", 0, 64, LoweringFootprintAccessV1.Read)]));
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceV1.Create(
            "compiler-contract-v6", D('a'), D('b'), D('c'), D('d'),
            [new("input", 0, 64, LoweringFootprintAccessV1.Read)],
            [new("input", "missing", true)]));
        Assert.Equal(LoweringFootprintAccessV1.Read | LoweringFootprintAccessV1.Write,
            CompilerLoweringEvidenceV1.Create(
                "compiler-contract-v6", D('a'), D('b'), D('c'), D('d'),
                [new("inout", 0, 64,
                    LoweringFootprintAccessV1.Read | LoweringFootprintAccessV1.Write)])
                .Footprints.Single().Access);
    }

    [Fact]
    public void OrderingAndNumericFactsRejectSemanticDowngrade()
    {
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceV1.Create(
            "compiler-contract-v6", D('a'), D('b'), D('c'), D('d'), Footprints(),
            orderingFacts: [new("release-before-publish", false, false)]));
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceV1.Create(
            "compiler-contract-v6", D('a'), D('b'), D('c'), D('d'), Footprints(),
            numericFacts: [new("vector-add", "nearest-even", "wrap", 96)]));
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceV1.Create(
            "compiler-contract-v6", D('a'), D('b'), D('c'), D('d'), Footprints(),
            safePointMap: [new("sp0", 16, "not-a-digest")]));
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceV1.Create(
            "compiler-contract-v6", D('a'), D('b'), D('c'), D('d'), Footprints(),
            safePointMap: [new("sp0", 16, D('e')), new("sp0", 32, D('f'))]));
    }

    [Fact]
    public void StaticFactSetDigestChangesWithoutDependingOnArtifactIdentity()
    {
        var first = Evidence();
        var rebound = CompilerLoweringEvidenceV1.Create(
            first.CompilerContractVersion, D('9'), D('8'), D('7'), D('6'),
            first.Footprints, first.AliasFacts, first.OrderingFacts, first.NumericFacts);
        var weakened = CompilerLoweringEvidenceV1.Create(
            first.CompilerContractVersion, D('9'), D('8'), D('7'), D('6'),
            first.Footprints, [], first.OrderingFacts, first.NumericFacts);

        Assert.Equal(first.StaticFactSetDigest, rebound.StaticFactSetDigest);
        Assert.NotEqual(first.EvidenceDigest, rebound.EvidenceDigest);
        Assert.NotEqual(first.StaticFactSetDigest, weakened.StaticFactSetDigest);
    }

    [Fact]
    public void SafePointMapIsCanonicalImmutableAndHasAnIndependentDigest()
    {
        var first = Evidence(safePoints:
            [new("sp-b", 32, D('F')), new("sp-a", 16, D('E'))]);
        var reordered = Evidence(safePoints:
            [new("sp-a", 16, D('e')), new("sp-b", 32, D('f'))]);
        var changed = Evidence(safePoints: [new("sp-a", 16, D('e'))]);

        Assert.Equal(first.SerializeCanonical(), reordered.SerializeCanonical());
        Assert.Equal(first.SafePointMapDigest, reordered.SafePointMapDigest);
        Assert.NotEqual(first.SafePointMapDigest, changed.SafePointMapDigest);
        Assert.Equal(["sp-a", "sp-b"], first.SafePointMap.Select(static point => point.SafePointIdentity));
        Assert.False(first.AuthorizesExecution);
    }

    [Fact]
    public void StaticResourceEstimatesAreCanonicalAdvisoryAndIndependentlyDigestible()
    {
        var first = Evidence(resourceEstimates:
            [new("instructions", 42, "count"), new("blocks", 7, "count")]);
        var reordered = Evidence(resourceEstimates:
            [new("blocks", 7, "count"), new("instructions", 42, "count")]);
        var changed = Evidence(resourceEstimates: [new("instructions", 43, "count")]);

        Assert.Equal(first.SerializeCanonical(), reordered.SerializeCanonical());
        Assert.Equal(first.StaticResourceEstimateDigest, reordered.StaticResourceEstimateDigest);
        Assert.NotEqual(first.StaticResourceEstimateDigest, changed.StaticResourceEstimateDigest);
        Assert.All(first.StaticResourceEstimates, estimate =>
        {
            Assert.True(estimate.AdvisoryOnly);
            Assert.False(estimate.ReservesResources);
            Assert.False(estimate.GuaranteesCapacity);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => Evidence(resourceEstimates:
            [new("instructions", 0, "count")]));
        Assert.Throws<ArgumentException>(() => Evidence(resourceEstimates:
            [new("instructions", 1, "count"), new("instructions", 2, "count")]));
    }

    private static CompilerLoweringEvidenceV1 Evidence(
        IEnumerable<LoweringFootprintFactV1>? footprints = null,
        IEnumerable<LoweringSafePointFactV1>? safePoints = null,
        IEnumerable<LoweringStaticResourceEstimateFactV1>? resourceEstimates = null) =>
        CompilerLoweringEvidenceV1.Create("compiler-contract-v6", D('a'), D('b'), D('c'), D('d'),
            footprints ?? Footprints(),
            [new("input", "output", true)],
            [new("release-before-publish", true, true)],
            [new("vector-add", "nearest-even", "wrap", 128)],
            safePoints, resourceEstimates);

    private static LoweringFootprintFactV1[] Footprints() =>
    [new("input", 0, 64, LoweringFootprintAccessV1.Read),
     new("output", 0, 32, LoweringFootprintAccessV1.Write)];

    private static string D(char value) => new(value, 64);
}
