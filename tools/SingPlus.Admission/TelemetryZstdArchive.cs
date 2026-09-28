using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Admission;

// Storage-only compression for an NDJSON qualification trace. Identity is SHA-256 of the raw trace.
public sealed record TelemetryZstdArchive(string RawDigest, int RawLength, byte[] CompressedBytes);

public static class TelemetryZstdStorage
{
    private const int MaxRawLength = 64 * 1024 * 1024;
    private const int HeaderLength = 8 + 4 + 32;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SINGZST1");

    public static TelemetryZstdArchive Pack(ReadOnlySpan<byte> raw, CompressionLevel level = CompressionLevel.Optimal)
    {
        if (raw.Length > MaxRawLength) throw new ArgumentOutOfRangeException(nameof(raw));
        var digest = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        using var compressed = new MemoryStream();
        using (var zstd = new ZstandardStream(compressed, level, leaveOpen: true))
            zstd.Write(raw);
        return new TelemetryZstdArchive(digest, raw.Length, compressed.ToArray());
    }

    // Versioned storage envelope. It is not a semantic artifact or an authority token.
    public static byte[] Serialize(TelemetryZstdArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.RawLength < 0 || archive.RawLength > MaxRawLength || archive.CompressedBytes.Length == 0)
            throw new InvalidDataException("Telemetry archive length or compressed payload is invalid.");
        if (archive.RawDigest.Length != 64 || !archive.RawDigest.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Telemetry archive raw digest must be lowercase SHA-256.");
        var bytes = new byte[checked(HeaderLength + archive.CompressedBytes.Length)];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), archive.RawLength);
        Convert.FromHexString(archive.RawDigest).CopyTo(bytes, 12);
        archive.CompressedBytes.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    public static TelemetryZstdArchive Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= HeaderLength || !bytes[..8].SequenceEqual(Magic))
            throw new InvalidDataException("Telemetry archive magic or length is invalid.");
        var rawLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4));
        if (rawLength < 0 || rawLength > MaxRawLength)
            throw new InvalidDataException("Telemetry archive raw length is unsupported.");
        return new TelemetryZstdArchive(Convert.ToHexString(bytes.Slice(12, 32)).ToLowerInvariant(),
            rawLength, bytes[HeaderLength..].ToArray());
    }

    public static byte[] Unpack(TelemetryZstdArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.RawLength < 0 || archive.RawLength > MaxRawLength)
            throw new InvalidDataException("Telemetry archive raw length is unsupported.");
        using var compressed = new MemoryStream(archive.CompressedBytes, writable: false);
        using var zstd = new ZstandardStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = zstd.Read(chunk)) != 0)
        {
            if (raw.Length + read > archive.RawLength)
                throw new InvalidDataException("Telemetry archive decompresses beyond its declared raw length.");
            raw.Write(chunk, 0, read);
        }
        if (compressed.Position != compressed.Length)
            throw new InvalidDataException("Telemetry archive has trailing compressed bytes.");
        var bytes = raw.ToArray();
        if (bytes.Length != archive.RawLength ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), archive.RawDigest, StringComparison.Ordinal))
            throw new InvalidDataException("Telemetry archive raw semantic digest does not match.");
        return bytes;
    }
}
