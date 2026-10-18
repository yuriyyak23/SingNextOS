using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

/// <summary>
/// Canonical bounded metadata transport for compiler lowering evidence. The
/// transport carries integrity-bound static facts only and grants no authority.
/// </summary>
public static class CompilerLoweringEvidenceMetadataCodecV1
{
    public const string MetadataIdentity = "singnext.pcl.metadata/1";
    public const int MaximumBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Emit(CompilerLoweringEvidenceEnvelopeV1 envelope)
    {
        var valid = envelope.Validate();
        var prefix = StrictUtf8.GetBytes($"{MetadataIdentity}\nevidence-digest={valid.EvidenceDigest}\n");
        var evidence = valid.Evidence.SerializeCanonical();
        if (prefix.Length + evidence.Length > MaximumBytes)
            throw new ArgumentException("Compiler lowering evidence metadata exceeds its bounded transport size.");
        var result = new byte[prefix.Length + evidence.Length];
        prefix.CopyTo(result, 0);
        evidence.CopyTo(result, prefix.Length);
        return result;
    }

    public static CompilerLoweringEvidenceEnvelopeV1 Parse(ReadOnlySpan<byte> metadata)
    {
        if (metadata.IsEmpty || metadata.Length > MaximumBytes)
            throw new ArgumentException("Compiler lowering evidence metadata is empty or exceeds its bound.");
        string text;
        try { text = StrictUtf8.GetString(metadata); }
        catch (DecoderFallbackException exception) { throw new ArgumentException("Compiler lowering evidence metadata is not canonical UTF-8.", exception); }
        if (!text.EndsWith('\n') || text.Contains('\r'))
            throw new ArgumentException("Compiler lowering evidence metadata must use canonical LF termination.");
        var lines = text[..^1].Split('\n');
        if (lines.Length < 9 || lines[0] != MetadataIdentity)
            throw new NotSupportedException("Compiler lowering evidence metadata identity is unsupported.");

        var declaredDigest = Required(lines[1], "evidence-digest=");
        CompilerLoweringEvidenceValidationV1.Digest(declaredDigest, nameof(metadata));
        if (Required(lines[2], "schema=") != $"{CompilerLoweringEvidenceV1.CurrentSchemaId}/{CompilerLoweringEvidenceV1.CurrentVersion}")
            throw new NotSupportedException("Compiler lowering evidence schema is unsupported.");
        var compiler = Required(lines[3], "compiler=");
        var toolchain = Required(lines[4], "toolchain=");
        var input = Required(lines[5], "input=");
        var output = Required(lines[6], "output=");
        var producer = Required(lines[7], "producer=");
        var footprints = new List<LoweringFootprintFactV1>();
        var aliases = new List<LoweringAliasFactV1>();
        var ordering = new List<LoweringOrderingFactV1>();
        var numeric = new List<LoweringNumericModeFactV1>();
        var safePoints = new List<LoweringSafePointFactV1>();
        var resourceEstimates = new List<LoweringStaticResourceEstimateFactV1>();
        foreach (var line in lines.Skip(8))
        {
            if (line.StartsWith("footprint=", StringComparison.Ordinal))
            {
                var fields = Fields(line, "footprint=", 4);
                footprints.Add(new(fields[0], U64(fields[1]), U64(fields[2]),
                    (LoweringFootprintAccessV1)Byte(fields[3])));
            }
            else if (line.StartsWith("alias=", StringComparison.Ordinal))
            {
                var fields = Fields(line, "alias=", 3);
                if (fields[2] != "disjoint") throw new ArgumentException("Unsupported alias metadata fact.");
                aliases.Add(new(fields[0], fields[1], true));
            }
            else if (line.StartsWith("ordering=", StringComparison.Ordinal))
            {
                var fields = Fields(line, "ordering=", 3);
                if (fields[1] != "preserved" || fields[2] is not ("0" or "1"))
                    throw new ArgumentException("Unsupported ordering metadata fact.");
                ordering.Add(new(fields[0], true, fields[2] == "1"));
            }
            else if (line.StartsWith("numeric=", StringComparison.Ordinal))
            {
                var fields = Fields(line, "numeric=", 4);
                numeric.Add(new(fields[0], fields[1], fields[2], U16(fields[3])));
            }
            else if (line.StartsWith("safe-point=", StringComparison.Ordinal))
            {
                var fields = Fields(line, "safe-point=", 3);
                safePoints.Add(new(fields[0], U64(fields[1]), fields[2]));
            }
            else if (line.StartsWith("resource-estimate=", StringComparison.Ordinal))
            {
                var fields = Fields(line, "resource-estimate=", 4);
                if (fields[3] != "advisory")
                    throw new ArgumentException("Static resource estimates must remain advisory.");
                resourceEstimates.Add(new(fields[0], U64(fields[1]), fields[2]));
            }
            else throw new ArgumentException("Unknown compiler lowering evidence metadata fact.");
        }

        var evidence = CompilerLoweringEvidenceV1.Create(compiler, toolchain, input, output, producer,
            footprints, aliases, ordering, numeric, safePoints, resourceEstimates);
        var envelope = CompilerLoweringEvidenceEnvelopeV1.Create(evidence);
        if (!FixedHex(envelope.EvidenceDigest, declaredDigest) ||
            !CryptographicOperations.FixedTimeEquals(Emit(envelope), metadata))
            throw new ArgumentException("Compiler lowering evidence metadata is non-canonical or its digest was substituted.");
        return envelope;
    }

    private static string Required(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length == prefix.Length)
            throw new ArgumentException($"Required metadata field '{prefix}' is missing.");
        return line[prefix.Length..];
    }

    private static string[] Fields(string line, string prefix, int count)
    {
        var fields = Required(line, prefix).Split(',');
        if (fields.Length != count || fields.Any(static field => field.Length == 0))
            throw new ArgumentException($"Metadata fact '{prefix}' has an invalid field count.");
        return fields;
    }

    private static ulong U64(string value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException("Metadata integer is not canonical UInt64.");

    private static ushort U16(string value) =>
        ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException("Metadata integer is not canonical UInt16.");

    private static byte Byte(string value) =>
        byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException("Metadata integer is not canonical byte.");

    private static bool FixedHex(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}
