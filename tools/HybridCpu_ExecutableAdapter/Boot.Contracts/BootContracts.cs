namespace YAKSys_Hybrid_CPU.Boot.Contracts;

public static class BootAbiV1
{
    public const ushort Major = 1;
    public const ushort Minor = 0;
    public const int MaxPolicyBytes = 64 * 1024;
    public const int MaxPolicyTargets = 16;
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxManifestComponents = 64;
    public const int MaxComponentExtents = 8;
    public const int MaxBootInfoBytes = 1024 * 1024;
    public const int MaxBootInfoRecords = 4096;
    public const int MaxTlvBytes = 16 * 1024;
    public const int MaxEvidenceBytes = 4096;
    public const int VolumeHeaderBytes = 4096;
}

public enum ResetReason : uint
{
    ColdPowerOn = 0, WarmSoftware = 1, Watchdog = 2, CrashRecovery = 3,
    FirmwareUpdate = 4, BootTrialFailure = 5, PlatformRecovery = 6,
    ExternalReset = 7, Unknown = uint.MaxValue
}

[Flags]
public enum BootPolicyFlags : uint
{
    None = 0, RequireSecureBoot = 1, AllowDevelopmentImages = 2,
    RequirePhysicalSelectorMatch = 4, PreferPhysicalSelectorMatch = 8,
    AllowReplicaFailover = 16, AllowBootstrapHighestValid = 32, RecoveryOnly = 64
}

public enum BootTargetKind : ushort { CxlPersistentVolume = 1, LocalRecovery = 2, LocalServiceImage = 3 }
public enum PhysicalSelectorKind : ushort { None = 0, PciDsn = 1, DeviceSerial = 2, PlatformSlot = 3 }
public enum BootHashAlgorithm : ushort { Sha256 = 1, Sha384 = 2, Sha512 = 3 }
public enum BootSignatureAlgorithm : ushort { Ed25519 = 1, EcdsaP384 = 2 }
public enum BootSecurityStatus : uint { Rejected = 0, ManifestVerified = 1, Development = 2, Recovery = 3 }

[Flags]
public enum BootRecordFlags : ushort
{
    None = 0, Required = 1, EvidenceOnly = 2, Temporary = 4, MustNotUseAsRam = 8
}

public static class BootRecordFlagMasks
{
    public const BootRecordFlags Known = BootRecordFlags.Required | BootRecordFlags.EvidenceOnly |
        BootRecordFlags.Temporary | BootRecordFlags.MustNotUseAsRam;
}

public enum BootEvidenceKind : ushort
{
    Reset = 1, Selection = 2, Security = 3, PhysicalDevice = 4,
    TemporaryAperture = 5, Diagnostic = 6
}

public readonly record struct BootTargetV1(
    Guid TargetId, BootTargetKind Kind, ushort Priority, uint Flags,
    Guid BootVolumeId, Guid RollbackDomainId, ulong RequiredProperties,
    PhysicalSelectorKind PhysicalSelectorKind, ReadOnlyMemory<byte> PhysicalSelector,
    ushort MaxAttemptsPerBoot);

public sealed record HybridBootPolicyV1(
    ulong PolicyGeneration, Guid PlatformId, Guid KeySetId, Guid DefaultRecoveryTargetId,
    BootPolicyFlags Flags, IReadOnlyList<BootTargetV1> Targets);

public sealed record BootVolumeHeaderV1(
    ulong MetadataSequence, Guid BootVolumeId, Guid ReplicaId, ulong PersistentCapacityBytes,
    ulong ManifestAOffset, uint ManifestAMaxBytes, uint ManifestAFlags,
    ulong ManifestBOffset, uint ManifestBMaxBytes, uint ManifestBFlags,
    ulong RecoveryManifestOffset, uint RecoveryManifestMaxBytes, uint Flags);

public sealed record SingNextBootManifestV1(
    Guid BootVolumeId, Guid ReplicaId, Guid ImageId, Guid RollbackDomainId,
    ulong ImageGeneration, Guid PlatformFamilyId, uint RequiredCpuAbiMin,
    uint RequiredCpuAbiMax, uint RequiredFirmwareAbiMin, uint RequiredFirmwareAbiMax,
    ulong RequiredPlatformFeatures, ushort SlotKind, ushort ComponentCount,
    uint ComponentTableOffset, ulong KernelEntryOffsetInComponent,
    ushort KernelComponentIndex, ushort Stage1ComponentIndex,
    BootHashAlgorithm HashAlgorithm, BootSignatureAlgorithm SignatureAlgorithm,
    uint SignatureBlockOffset, ReadOnlyMemory<byte> SigningKeyId, uint Flags,
    IReadOnlyList<BootTlv> Extensions);

public readonly record struct BootEvidenceRecord(
    BootEvidenceKind Kind, BootRecordFlags Flags, ReadOnlyMemory<byte> Payload);

public readonly record struct TemporaryApertureDescriptor(
    ulong HpaBase, ulong HpaBytes, ulong PersistentOffset, ulong SourceBytes,
    ulong FirmwareMappingGeneration, Guid BootVolumeId, Guid ReplicaId, uint State, uint Flags);

public sealed record HybridBootInfoV1(
    ulong Flags, Guid PlatformId, uint CpuAbiVersion, uint FirmwareBootAbiVersion,
    ResetReason ResetReason, ulong ResetSequence, IReadOnlyList<BootEvidenceRecord> Records);

public readonly record struct BootTlv(ushort Type, BootRecordFlags Flags, ReadOnlyMemory<byte> Value);

public enum BootParseFailure
{
    None, Truncated, InvalidMagic, UnsupportedVersion, InvalidLength, LimitExceeded,
    IntegerOverflow, InvalidCrc, InvalidDigest, InvalidEnum, InvalidReserved,
    DuplicateRequiredField, UnknownRequiredFeature, UnknownRequiredRecord,
    Misaligned, RangeOutsideContainer
}

public readonly record struct BootParseResult<T>(T? Value, BootParseFailure Failure, string? Detail)
{
    public bool IsSuccess => Failure == BootParseFailure.None;
    public static BootParseResult<T> Success(T value) => new(value, BootParseFailure.None, null);
    public static BootParseResult<T> Fail(BootParseFailure failure, string detail) => new(default, failure, detail);
}
