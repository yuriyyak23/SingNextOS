using System.Buffers.Binary;

namespace SingPlus.Contracts;

public enum DmaEffectStateV1 : byte
{
    Admitted = 1,
    EffectPossible = 2,
    Closed = 3,
    Quarantined = 4,
}

/// <summary>
/// Non-authoritative exact DMA correlation. Opaque digests deliberately exclude Region handles,
/// PASID/IOVA values, capabilities, and provider receipts from the machine-permission ABI.
/// </summary>
public readonly record struct DmaExecutionBindingV1(
    ushort Version,
    string RegionUseDigest,
    ulong RegionGeneration,
    ulong MutationGeneration,
    ulong ProcessIncarnation,
    ulong AddressSpaceGeneration,
    ulong TranslationGeneration,
    ulong DeviceLeaseGeneration,
    ulong ProviderGeneration,
    ulong SessionGeneration,
    ulong ExternalOperationGeneration,
    DmaEffectStateV1 EffectState,
    string SecurityDomainDigest)
{
    public const ushort CurrentVersion = 1;
    public const int CanonicalSize = 2 + 32 + (9 * 8) + 1 + 32;
    public static SemanticExtensionClassId ExtensionClassId => new("dma.execution-binding");
    public bool AuthorizesDma => false;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;

    public DmaExecutionBindingV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(EffectState))
            throw new NotSupportedException("DMA binding version or effect state is unsupported.");
        ValidateDigest(RegionUseDigest, nameof(RegionUseDigest));
        ValidateDigest(SecurityDomainDigest, nameof(SecurityDomainDigest));
        ulong[] generations = [RegionGeneration, MutationGeneration, ProcessIncarnation,
            AddressSpaceGeneration, TranslationGeneration, DeviceLeaseGeneration, ProviderGeneration,
            SessionGeneration, ExternalOperationGeneration];
        if (generations.Any(value => value == 0))
            throw new ArgumentException("Every mutable DMA dependency generation must be non-zero.");
        return this;
    }

    public byte[] SerializeCanonical()
    {
        var exact = Validate();
        var bytes = new byte[CanonicalSize];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, exact.Version);
        Convert.FromHexString(exact.RegionUseDigest).CopyTo(bytes, 2);
        var offset = 34;
        foreach (var generation in new[] { exact.RegionGeneration, exact.MutationGeneration,
                     exact.ProcessIncarnation, exact.AddressSpaceGeneration, exact.TranslationGeneration,
                     exact.DeviceLeaseGeneration, exact.ProviderGeneration, exact.SessionGeneration,
                     exact.ExternalOperationGeneration })
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), generation);
            offset += 8;
        }
        bytes[offset++] = (byte)exact.EffectState;
        Convert.FromHexString(exact.SecurityDomainDigest).CopyTo(bytes, offset);
        return bytes;
    }

    public static DmaExecutionBindingV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CanonicalSize)
            throw new FormatException("DMA execution binding has a non-canonical size.");
        var offset = 34;
        Span<ulong> generations = stackalloc ulong[9];
        for (var index = 0; index < generations.Length; index++)
        {
            generations[index] = BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..]);
            offset += 8;
        }
        return new DmaExecutionBindingV1(BinaryPrimitives.ReadUInt16BigEndian(bytes), Convert.ToHexStringLower(bytes.Slice(2, 32)),
            generations[0], generations[1], generations[2], generations[3], generations[4], generations[5],
            generations[6], generations[7], generations[8], (DmaEffectStateV1)bytes[offset++],
            Convert.ToHexStringLower(bytes.Slice(offset, 32))).Validate();
    }

    public SemanticExtensionClauseV1 ToClause(SemanticExtensionRequirement requirement) =>
        SemanticExtensionClauseV1.Create(ExtensionClassId, "singnext.dma-execution-binding/1", CurrentVersion,
            requirement, SerializeCanonical());

    public bool MatchesCurrent(DmaExecutionBindingV1 current)
    {
        try { Validate(); current.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        return this == current;
    }

    private static void ValidateDigest(string? value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("DMA correlation digest must be canonical SHA-256 hex.", parameter);
    }
}
