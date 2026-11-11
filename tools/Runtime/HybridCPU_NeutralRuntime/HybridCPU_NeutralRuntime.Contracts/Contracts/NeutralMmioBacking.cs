namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Single-original managed software register storage. No pointer or hardware authority.</summary>
public sealed class NeutralMmioBacking
{
    public NeutralMmioBacking(byte[] bytes) { Original=this;_bytes=bytes ?? throw new ArgumentNullException(nameof(bytes));ByteLength=bytes.Length; }
    private byte[]? _bytes;
    internal NeutralMmioBacking Original { get; }
    internal object? Owner { get; set; }
    public int ByteLength { get; }
    public bool IsActive => _bytes is not null;
    internal Span<byte> Bytes => _bytes ?? throw new InvalidOperationException("Original register backing is closed.");
    internal void Release() => _bytes=null;
}
