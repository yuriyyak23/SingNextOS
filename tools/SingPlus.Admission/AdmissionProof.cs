using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SingPlus.Admission;

public sealed record AdmissionViolation(string Method, string Operation, string Detail)
{
    public string CanonicalKey => Method + "|" + Operation + "|" + Detail;
}

public sealed class SingPlusAdmissionProofV1
{
    public const string Schema = "SingPlusAdmissionProofV1";

    public required string Root { get; init; }
    public required string Profile { get; init; }
    public required string AssemblyDigest { get; init; }
    public required int ReachableMethodCount { get; init; }
    public required int ForbiddenOperationCount { get; init; }
    public required string DependencyDigest { get; init; }
    public required string RulesetDigest { get; init; }
    public required string ProofDigest { get; init; }

    public byte[] SerializeCanonical(IReadOnlyList<AdmissionViolation> violations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("SchemaId", Schema);
            writer.WriteString(nameof(Root), Root);
            writer.WriteString(nameof(Profile), Profile);
            writer.WriteString(nameof(AssemblyDigest), AssemblyDigest);
            writer.WriteNumber(nameof(ReachableMethodCount), ReachableMethodCount);
            writer.WriteNumber(nameof(ForbiddenOperationCount), ForbiddenOperationCount);
            writer.WriteString(nameof(DependencyDigest), DependencyDigest);
            writer.WriteString(nameof(RulesetDigest), RulesetDigest);
            writer.WriteString(nameof(ProofDigest), ProofDigest);
            writer.WritePropertyName("Violations");
            writer.WriteStartArray();
            foreach (var violation in violations.OrderBy(static v => v.CanonicalKey, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString(nameof(AdmissionViolation.Method), violation.Method);
                writer.WriteString(nameof(AdmissionViolation.Operation), violation.Operation);
                writer.WriteString(nameof(AdmissionViolation.Detail), violation.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        return stream.ToArray();
    }

    internal static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));
    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record AdmissionVerificationResult(SingPlusAdmissionProofV1 Proof, IReadOnlyList<AdmissionViolation> Violations)
{
    public bool IsAdmitted => Violations.Count == 0;
}
