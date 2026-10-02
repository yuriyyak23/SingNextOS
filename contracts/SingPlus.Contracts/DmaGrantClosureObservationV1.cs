using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

/// <summary>Observation of exact grant closure, separate from output publication,
/// budget settlement and Region release. Never permission or provider containment proof.</summary>
public readonly record struct DmaGrantClosureObservationV1(
    ushort Version,
    string GrantIdentityDigest,
    ulong ProviderGeneration,
    ulong BackendEpoch,
    SemanticTraceEventV1 LastVisibleEvent)
{
    public const ushort CurrentVersion = 1;
    public const string SchemaId = "singnext.dma-grant-closure-observation/1";
    private static readonly Encoding CanonicalUtf8 = new UTF8Encoding(false, true);
    public bool AuthorizesEffect => false;
    public bool AuthorizesReclaim => false;
    public bool ProvesPublication => false;
    public bool ProvesSettlement => false;

    public DmaGrantClosureObservationV1 Validate()
    {
        if (Version != CurrentVersion || ProviderGeneration == 0 || BackendEpoch == 0)
            throw new NotSupportedException("DMA grant closure observation tuple is unsupported.");
        if (GrantIdentityDigest is null || GrantIdentityDigest.Length != 64 ||
            GrantIdentityDigest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Grant identity digest must be canonical SHA256 hex.");
        LastVisibleEvent.Validate();
        _ = CanonicalUtf8.GetByteCount(LastVisibleEvent.OperationCorrelation);
        if (LastVisibleEvent.Kind != SemanticTraceEventKindV1.Visible ||
            LastVisibleEvent.Source != "singnext.platform-dma")
            throw new ArgumentException("Closure must correlate the exact DMA visibility observation.");
        return this;
    }

    public byte[] SerializeCanonical()
    {
        Validate();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, CanonicalUtf8, leaveOpen: true);
        // Fixed-width little-endian numbers; BinaryWriter length-prefixed UTF8 tokens.
        writer.Write(Version);
        writer.Write(Convert.FromHexString(GrantIdentityDigest));
        writer.Write(ProviderGeneration);
        writer.Write(BackendEpoch);
        writer.Write(LastVisibleEvent.Version);
        writer.Write(LastVisibleEvent.OperationCorrelation);
        writer.Write(LastVisibleEvent.Sequence);
        writer.Write((byte)LastVisibleEvent.Kind);
        writer.Write(LastVisibleEvent.Source);
        writer.Write(Convert.FromHexString(LastVisibleEvent.GenerationVectorDigest));
        writer.Write(Convert.FromHexString(LastVisibleEvent.EvidenceDigest));
        writer.Flush();
        return stream.ToArray();
    }

    public string ComputeDigest() => Convert.ToHexStringLower(SHA256.HashData(SerializeCanonical()));

    public static DmaGrantClosureObservationV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        // Bound the allocation before reading any attacker-controlled token length.
        if (bytes.Length > 512)
            throw new FormatException("DMA grant closure payload exceeds the canonical size bound.");
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, CanonicalUtf8, leaveOpen: true);
            var version = reader.ReadUInt16();
            var grant = ReadDigest();
            var provider = reader.ReadUInt64();
            var epoch = reader.ReadUInt64();
            var eventVersion = reader.ReadUInt16();
            var correlation = ReadToken();
            var sequence = reader.ReadUInt64();
            var kind = (SemanticTraceEventKindV1)reader.ReadByte();
            var source = ReadToken();
            var generation = ReadDigest();
            var evidence = ReadDigest();
            var result = new DmaGrantClosureObservationV1(version, grant, provider, epoch,
                new(eventVersion, correlation, sequence, kind, source, generation, evidence)).Validate();
            if (stream.Position != stream.Length || !bytes.SequenceEqual(result.SerializeCanonical()))
                throw new FormatException("DMA grant closure payload is not canonical.");
            return result;

            string ReadDigest()
            {
                var digest = reader.ReadBytes(32);
                if (digest.Length != 32) throw new EndOfStreamException();
                return Convert.ToHexStringLower(digest);
            }

            string ReadToken()
            {
                var length = reader.Read7BitEncodedInt();
                if (length is < 1 or > 256 || length > stream.Length - stream.Position)
                    throw new FormatException("Closure observation token length is invalid.");
                return CanonicalUtf8.GetString(reader.ReadBytes(length));
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or NotSupportedException)
        {
            throw new FormatException("Malformed or unsupported DMA grant closure payload.", exception);
        }
    }
}

public static class DmaGrantClosureProjectionV1
{
    // Checks observation consistency only. The tuple is supplied by the evidence consumer;
    // neither this checker nor the trace is consulted by a runtime authorization decision.
    public static bool Validate(
        IEnumerable<SemanticTraceEventV1> prefix,
        DmaGrantClosureObservationV1 closure,
        string expectedGrantIdentityDigest,
        ulong expectedProviderGeneration,
        ulong expectedBackendEpoch)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        try { closure.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return false; }
        var events = prefix.ToArray();
        return closure.GrantIdentityDigest == expectedGrantIdentityDigest &&
            closure.ProviderGeneration == expectedProviderGeneration &&
            closure.BackendEpoch == expectedBackendEpoch && events.Length != 0 &&
            events[^1] == closure.LastVisibleEvent &&
            SemanticTraceValidatorV1.Validate(events).IsValid;
    }
}
