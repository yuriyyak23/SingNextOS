using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Native;

/// <summary>A sanitizing bounded-copy payload. It is not shared mutable memory.</summary>
[BoundedPayload(MaxLength)]
public readonly struct BoundedBytes : IBoundedPayload
{
    public const int MaxLength = 4096;
    private readonly byte[] _bytes;
    public BoundedBytes(ReadOnlySpan<byte> bytes) { if (bytes.Length > MaxLength) throw new ArgumentOutOfRangeException(nameof(bytes)); _bytes = bytes.ToArray(); }
    public int Length => _bytes?.Length ?? 0;
    public int PayloadSize => Length;
    public int MaxPayloadSize => MaxLength;
    public byte[] ToArray() => _bytes is null ? [] : (byte[])_bytes.Clone();
}
