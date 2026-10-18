namespace SingPlus.Contracts;

public enum PersistenceDomainClassV1 : byte
{
    NamedManagedModel = 1,
    Adr = 2,
    Eadr = 3,
    CxlPersistentMemory = 4,
}

public enum PersistenceOrderingV1 : byte
{
    DataThenMetadataThenCommitRecord = 1,
}

public enum PersistEvidenceAssuranceV1 : byte
{
    ModelOnly = 1,
    HardwareQualified = 2,
}

/// <summary>Named provider persistence semantics. Media identity alone never proves durability.</summary>
public readonly record struct PersistenceSemanticsV1(
    ushort Version,
    string ProviderIdentity,
    string MediaIdentity,
    PersistenceDomainClassV1 DomainClass,
    PersistenceOrderingV1 Ordering,
    ulong ProviderGeneration,
    ulong MediaGeneration,
    bool DurableBeforePublication)
{
    public const ushort CurrentVersion = 1;
    public bool GrantsRegionAuthority => false;
    public bool AuthorizesPublication => false;

    public PersistenceSemanticsV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(DomainClass) || !Enum.IsDefined(Ordering))
            throw new NotSupportedException("Persistence semantics version or vocabulary is unsupported.");
        PersistenceContractValidationV1.Identity(ProviderIdentity, nameof(ProviderIdentity));
        PersistenceContractValidationV1.Identity(MediaIdentity, nameof(MediaIdentity));
        if (ProviderGeneration == 0 || MediaGeneration == 0)
            throw new ArgumentException("Persistence provider and media generations must be non-zero.");
        if (!DurableBeforePublication)
            throw new ArgumentException("The durable-output contract requires durability before publication.");
        return this;
    }
}

public readonly record struct DurableOutputBindingV1(
    ushort Version,
    string OperationCorrelation,
    ulong OperationGeneration,
    string ContentDigest,
    string MetadataDigest,
    ulong RecoveryGeneration,
    PersistenceSemanticsV1 Semantics)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesPublication => false;

    public DurableOutputBindingV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Durable output binding version is unsupported.");
        PersistenceContractValidationV1.Identity(OperationCorrelation, nameof(OperationCorrelation));
        PersistenceContractValidationV1.Digest(ContentDigest, nameof(ContentDigest));
        PersistenceContractValidationV1.Digest(MetadataDigest, nameof(MetadataDigest));
        if (OperationGeneration == 0 || RecoveryGeneration == 0)
            throw new ArgumentException("Operation and recovery generations must be non-zero.");
        Semantics.Validate();
        return this;
    }
}

/// <summary>Provider observation only. This receipt is never publication, execution, or recovery authority.</summary>
public readonly record struct ProviderPersistEvidenceV1(
    ushort Version,
    string ProviderIdentity,
    string MediaIdentity,
    string OperationCorrelation,
    string ContentDigest,
    string MetadataDigest,
    PersistenceDomainClassV1 DomainClass,
    PersistEvidenceAssuranceV1 Assurance,
    ulong ProviderGeneration,
    ulong MediaGeneration,
    ulong OperationGeneration,
    ulong RecoveryGeneration,
    ulong WriteSequence,
    ulong DataPersistedSequence,
    ulong MetadataPersistedSequence,
    ulong DurableSequence,
    ulong PublishedSequence)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesPublication => false;
    public bool AuthorizesExecution => false;
    public bool RestoresAuthority => false;

    public ProviderPersistEvidenceV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(DomainClass) || !Enum.IsDefined(Assurance))
            throw new NotSupportedException("Persist evidence version or vocabulary is unsupported.");
        PersistenceContractValidationV1.Identity(ProviderIdentity, nameof(ProviderIdentity));
        PersistenceContractValidationV1.Identity(MediaIdentity, nameof(MediaIdentity));
        PersistenceContractValidationV1.Identity(OperationCorrelation, nameof(OperationCorrelation));
        PersistenceContractValidationV1.Digest(ContentDigest, nameof(ContentDigest));
        PersistenceContractValidationV1.Digest(MetadataDigest, nameof(MetadataDigest));
        if (ProviderGeneration == 0 || MediaGeneration == 0 || OperationGeneration == 0 || RecoveryGeneration == 0)
            throw new ArgumentException("Every persist evidence generation must be non-zero.");
        if (!(WriteSequence < DataPersistedSequence && DataPersistedSequence < MetadataPersistedSequence &&
              MetadataPersistedSequence < DurableSequence) ||
            (PublishedSequence != 0 && PublishedSequence <= DurableSequence))
            throw new ArgumentException("Persist evidence ordering is invalid.");
        if (Assurance == PersistEvidenceAssuranceV1.ModelOnly && DomainClass != PersistenceDomainClassV1.NamedManagedModel)
            throw new ArgumentException("Model-only evidence cannot qualify a physical persistence domain.");
        return this;
    }
}

public enum PersistEvidenceMatchCodeV1 : byte
{
    Exact = 1,
    WrongProvider = 2,
    WrongMedia = 3,
    WrongOperation = 4,
    StaleProviderGeneration = 5,
    StaleMediaGeneration = 6,
    StaleOperationGeneration = 7,
    StaleRecoveryGeneration = 8,
    PersistenceDomainDowngrade = 9,
    ContentMismatch = 10,
}

public static class PersistEvidenceMatcherV1
{
    public static PersistEvidenceMatchCodeV1 Match(DurableOutputBindingV1 binding, ProviderPersistEvidenceV1 evidence)
    {
        binding.Validate();
        evidence.Validate();
        var semantics = binding.Semantics;
        if (evidence.ProviderIdentity != semantics.ProviderIdentity) return PersistEvidenceMatchCodeV1.WrongProvider;
        if (evidence.MediaIdentity != semantics.MediaIdentity) return PersistEvidenceMatchCodeV1.WrongMedia;
        if (evidence.OperationCorrelation != binding.OperationCorrelation) return PersistEvidenceMatchCodeV1.WrongOperation;
        if (evidence.ProviderGeneration != semantics.ProviderGeneration) return PersistEvidenceMatchCodeV1.StaleProviderGeneration;
        if (evidence.MediaGeneration != semantics.MediaGeneration) return PersistEvidenceMatchCodeV1.StaleMediaGeneration;
        if (evidence.OperationGeneration != binding.OperationGeneration) return PersistEvidenceMatchCodeV1.StaleOperationGeneration;
        if (evidence.RecoveryGeneration != binding.RecoveryGeneration) return PersistEvidenceMatchCodeV1.StaleRecoveryGeneration;
        if (evidence.DomainClass != semantics.DomainClass) return PersistEvidenceMatchCodeV1.PersistenceDomainDowngrade;
        return evidence.ContentDigest != binding.ContentDigest || evidence.MetadataDigest != binding.MetadataDigest
            ? PersistEvidenceMatchCodeV1.ContentMismatch : PersistEvidenceMatchCodeV1.Exact;
    }
}

/// <summary>Non-authoritative crash-recovery correlation. Fresh admission must create all runtime authority.</summary>
public readonly record struct RecoveryFreshnessRecord(
    ushort Version,
    string OperationCorrelation,
    string DurableEvidenceDigest,
    ulong PriorRuntimeGeneration,
    ulong PriorProcessGeneration,
    ulong PriorProviderAdmissionGeneration,
    ulong RecoveryGeneration)
{
    public const ushort CurrentVersion = 1;
    public bool PreservesCapability => false;
    public bool PreservesSession => false;
    public bool PreservesProviderAuthority => false;
    public bool GrantsRegionAuthority => false;

    public RecoveryFreshnessRecord Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Recovery freshness record version is unsupported.");
        PersistenceContractValidationV1.Identity(OperationCorrelation, nameof(OperationCorrelation));
        PersistenceContractValidationV1.Digest(DurableEvidenceDigest, nameof(DurableEvidenceDigest));
        if (PriorRuntimeGeneration == 0 || PriorProcessGeneration == 0 ||
            PriorProviderAdmissionGeneration == 0 || RecoveryGeneration == 0)
            throw new ArgumentException("Recovery freshness generations must be non-zero.");
        return this;
    }

    public bool HasFreshAdmission(ulong runtimeGeneration, ulong processGeneration, ulong providerAdmissionGeneration) =>
        Validate().PriorRuntimeGeneration < runtimeGeneration &&
        PriorProcessGeneration < processGeneration &&
        PriorProviderAdmissionGeneration < providerAdmissionGeneration;
}

internal static class PersistenceContractValidationV1
{
    internal static void Identity(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Persistence identity must be canonical and bounded.", parameter);
    }

    internal static void Digest(string? value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Persistence digest must be canonical SHA-256 hex.", parameter);
    }
}
