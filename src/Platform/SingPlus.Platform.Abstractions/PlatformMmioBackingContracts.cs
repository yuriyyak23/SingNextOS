[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SingPlus.Runtime")]
namespace SingPlus.Platform;

/// <summary>Managed register bytes retained by an original Map. Never MMIO permission.</summary>
public sealed class PlatformMmioBacking
{
    public PlatformMmioBacking(byte[] bytes) { _bytes=bytes ?? throw new ArgumentNullException(nameof(bytes));ByteLength=bytes.Length; }
    private byte[]? _bytes;
    public int ByteLength { get; }
    internal byte[] Bytes => _bytes ?? throw new InvalidOperationException("MMIO byte lifetime ended; original metadata only.");
    internal void Release() => _bytes=null;
}
