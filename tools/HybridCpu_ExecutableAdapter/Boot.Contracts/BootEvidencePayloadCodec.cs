using System.Buffers.Binary;

namespace YAKSys_Hybrid_CPU.Boot.Contracts;

public readonly record struct BootSecurityEvidenceV1(
    BootSecurityStatus Status, ulong TrustEpoch, ulong ProtectedRollbackFloor, ulong AcceptedImageGeneration);

public static class BootEvidencePayloadCodec
{
    public const int SecurityBytes = 32;

    public static byte[] EncodeSecurity(BootSecurityEvidenceV1 value)
    {
        if (value.Status == BootSecurityStatus.Rejected || !Enum.IsDefined(value.Status) ||
            value.TrustEpoch == 0 || value.AcceptedImageGeneration < value.ProtectedRollbackFloor)
            throw new ArgumentOutOfRangeException(nameof(value));
        var bytes = new byte[SecurityBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value.Status);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), value.TrustEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), value.ProtectedRollbackFloor);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), value.AcceptedImageGeneration);
        return bytes;
    }

    public static BootParseResult<BootSecurityEvidenceV1> ParseSecurity(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SecurityBytes)
            return BootParseResult<BootSecurityEvidenceV1>.Fail(BootParseFailure.InvalidLength, "Security evidence size is invalid.");
        var status = (BootSecurityStatus)BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (status == BootSecurityStatus.Rejected || !Enum.IsDefined(status) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 0)
            return BootParseResult<BootSecurityEvidenceV1>.Fail(BootParseFailure.InvalidEnum, "Security evidence status or reserved field is invalid.");
        var epoch = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var floor = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        var image = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        if (epoch == 0 || image < floor)
            return BootParseResult<BootSecurityEvidenceV1>.Fail(BootParseFailure.InvalidLength, "Security evidence violates trust epoch or rollback floor.");
        return BootParseResult<BootSecurityEvidenceV1>.Success(new(status, epoch, floor, image));
    }
}
