using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Virtualization;

/// <summary>Immutable bounded-copy compiler package data; never execution authority.</summary>
[BoundedPayload(MaxLength)]
public readonly struct BoundedExecutablePackage : IBoundedPayload
{
    public const int MaxLength = 1024 * 1024;
    private readonly byte[]? bytes;
    public BoundedExecutablePackage(ReadOnlySpan<byte> package)
    {
        if(package.Length is <=0 or >MaxLength)throw new ArgumentOutOfRangeException(nameof(package));
        bytes=package.ToArray();
    }
    public int PayloadSize=>bytes?.Length??0;
    public int MaxPayloadSize=>MaxLength;
    public byte[] ToArray()=>bytes is null?[]:(byte[])bytes.Clone();
}
