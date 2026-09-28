using System.Buffers.Binary;

namespace SingPlus.Contracts;

public interface ISealedContractMarker;

public readonly struct SocketObjectSeal : ISealedContractMarker;

public static class SealedHandleContract
{
    public const uint Version = 1;
    public const int SerializedSize = sizeof(uint) + 16 + 16;
}

/// <summary>Opaque identity/lifecycle reference. This value never carries effect rights.</summary>
public readonly record struct SealedHandle<TSeal>(uint Version, AuthorityRealmId RealmId, Guid OpaqueToken)
    where TSeal : ISealedContractMarker
{
    public byte[] SerializeCanonical()
    {
        var bytes = new byte[SealedHandleContract.SerializedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Version);
        RealmId.Value.TryWriteBytes(bytes.AsSpan(sizeof(uint), 16));
        OpaqueToken.TryWriteBytes(bytes.AsSpan(sizeof(uint) + 16, 16));
        return bytes;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> bytes, out SealedHandle<TSeal> handle)
    {
        handle = default;
        if (bytes.Length != SealedHandleContract.SerializedSize) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (version != SealedHandleContract.Version) return false;
        var realm = new Guid(bytes.Slice(sizeof(uint), 16));
        var token = new Guid(bytes.Slice(sizeof(uint) + 16, 16));
        if (realm == Guid.Empty || token == Guid.Empty) return false;
        handle = new(version, new(realm), token);
        return true;
    }
}
