namespace SingPlus.Contracts;

using System.Buffers.Binary;

public readonly record struct AuthorityRealmId(Guid Value)
{
    public override string ToString() => Value.ToString("N");
}

public sealed record CapabilityDescriptorV1(
    CapabilityId CapabilityId,
    DomainId IssuerDomainId,
    DomainId SubjectDomainId,
    ResourceKind ResourceKind,
    string ResourceId,
    CapabilityRights Rights,
    ulong Generation,
    ulong RevocationEpoch);

public static class CapabilityHandleV2Contract
{
    public const uint Version = 2;
    public const int SerializedSize = sizeof(uint) + 16 + 16;
}

/// <summary>
/// Ephemeral opaque authority reference. Rights, resource, quota and lifecycle state are
/// intentionally absent and are resolved only from the runtime capability ledger.
/// </summary>
public readonly record struct CapabilityHandleV2(
    uint Version,
    AuthorityRealmId RealmId,
    Guid OpaqueToken)
{
    public byte[] SerializeCanonical()
    {
        var bytes = new byte[CapabilityHandleV2Contract.SerializedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Version);
        RealmId.Value.TryWriteBytes(bytes.AsSpan(sizeof(uint), 16));
        OpaqueToken.TryWriteBytes(bytes.AsSpan(sizeof(uint) + 16, 16));
        return bytes;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> bytes, out CapabilityHandleV2 handle)
    {
        handle = default;
        if (bytes.Length != CapabilityHandleV2Contract.SerializedSize) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (version != CapabilityHandleV2Contract.Version) return false;
        var realm = new Guid(bytes.Slice(sizeof(uint), 16));
        var token = new Guid(bytes.Slice(sizeof(uint) + 16, 16));
        if (realm == Guid.Empty || token == Guid.Empty) return false;
        handle = new(version, new AuthorityRealmId(realm), token);
        return true;
    }
}

/// <summary>Non-authoritative compatibility/inspection projection of one live V2 record.</summary>
public sealed record CapabilityInspectionDescriptorV2(
    CapabilityHandleV2 Handle,
    DomainId IssuerDomainId,
    DomainId SubjectDomainId,
    ResourceKind ResourceKind,
    string ResourceId,
    CapabilityRights Rights,
    ulong SubjectGeneration,
    ulong ResourceGeneration,
    ulong RevocationEpoch);

public readonly record struct MmioRegionCapability(CapabilityId CapabilityId);
public readonly record struct IrqCapability(CapabilityId CapabilityId);
public readonly record struct DmaCapability(CapabilityId CapabilityId);
public readonly record struct Dsc1ComputeCapability(CapabilityId CapabilityId);
