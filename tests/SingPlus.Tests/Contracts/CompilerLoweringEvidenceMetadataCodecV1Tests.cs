using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class CompilerLoweringEvidenceMetadataCodecV1Tests
{
    [Fact]
    public void CanonicalMetadataRoundTripsAllSupportedFacts()
    {
        var envelope = Envelope();
        var metadata = CompilerLoweringEvidenceMetadataCodecV1.Emit(envelope);
        var parsed = CompilerLoweringEvidenceMetadataCodecV1.Parse(metadata);

        Assert.Equal(envelope.EvidenceDigest, parsed.EvidenceDigest);
        Assert.Equal(envelope.Evidence.SerializeCanonical(), parsed.Evidence.SerializeCanonical());
        Assert.Equal(metadata, CompilerLoweringEvidenceMetadataCodecV1.Emit(parsed));
        Assert.False(parsed.AuthorizesExecution);
    }

    [Fact]
    public void ByteDigestAndFactMutationFailClosed()
    {
        var metadata = CompilerLoweringEvidenceMetadataCodecV1.Emit(Envelope());
        var digestMutation = metadata.ToArray();
        digestMutation["singnext.pcl.metadata/1\nevidence-digest=".Length] ^= 1;
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceMetadataCodecV1.Parse(digestMutation));

        var factMutation = metadata.ToArray();
        var index = Array.IndexOf(factMutation, (byte)'i', metadata.Length / 2);
        factMutation[index] = (byte)'z';
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceMetadataCodecV1.Parse(factMutation));
    }

    [Fact]
    public void UnknownReorderedAndOversizedMetadataFailClosed()
    {
        var text = global::System.Text.Encoding.UTF8.GetString(CompilerLoweringEvidenceMetadataCodecV1.Emit(Envelope()));
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceMetadataCodecV1.Parse(
            global::System.Text.Encoding.UTF8.GetBytes(text.Replace("footprint=", "unknown=", StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceMetadataCodecV1.Parse(
            new byte[CompilerLoweringEvidenceMetadataCodecV1.MaximumBytes + 1]));
        var first = "footprint=input,0,64,1\n";
        var second = "footprint=output,0,32,2\n";
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceMetadataCodecV1.Parse(
            global::System.Text.Encoding.UTF8.GetBytes(text.Replace(first + second, second + first, StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => CompilerLoweringEvidenceMetadataCodecV1.Parse(
            global::System.Text.Encoding.UTF8.GetBytes(text.Replace("compiler=compiler-contract-v6\n", string.Empty, StringComparison.Ordinal))));
    }

    [Fact]
    public void CombinedReadWriteFootprintRoundTripsCanonically()
    {
        var envelope = CompilerLoweringEvidenceEnvelopeV1.Create(
            CompilerLoweringEvidenceV1.Create("compiler-contract-v6", D('a'), D('b'), D('c'), D('d'),
                [new("inout", 8, 16,
                    LoweringFootprintAccessV1.Read | LoweringFootprintAccessV1.Write)]));

        var parsed = CompilerLoweringEvidenceMetadataCodecV1.Parse(
            CompilerLoweringEvidenceMetadataCodecV1.Emit(envelope));

        Assert.Equal(LoweringFootprintAccessV1.Read | LoweringFootprintAccessV1.Write,
            parsed.Evidence.Footprints.Single().Access);
    }

    private static CompilerLoweringEvidenceEnvelopeV1 Envelope() => CompilerLoweringEvidenceEnvelopeV1.Create(
        CompilerLoweringEvidenceV1.Create("compiler-contract-v6", D('a'), D('b'), D('c'), D('d'),
            [new("input", 0, 64, LoweringFootprintAccessV1.Read), new("output", 0, 32, LoweringFootprintAccessV1.Write)],
            [new("input", "output", true)], [new("release-before-publish", true, true)],
            [new("vector-add", "nearest-even", "wrap", 128)],
            [new("managed-sp-0", 16, D('e'))],
            [new("canonical-instruction-count", 12, "instructions")]));

    private static string D(char value) => new(value, 64);
}
