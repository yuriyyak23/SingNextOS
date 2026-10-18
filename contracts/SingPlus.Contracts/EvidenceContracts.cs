namespace SingPlus.Contracts;

public enum EvidenceVisibilityClass
{
    PublicDiagnostic = 0,
    DomainDiagnostic,
    PrivilegedDiagnostic,
    SecurityMeasurement,
    HostInternal
}

public readonly record struct EvidenceIdentity(string Kind, uint Version);
public readonly record struct EvidenceProducerIdentity(string Value);
public readonly record struct EvidenceFreshness(ulong ProviderEpoch, ulong Sequence);
public readonly record struct EvidenceDomainIdentity(ulong Value, ulong Generation);
public readonly record struct EvidenceSubjectIdentity(
    DomainId SubjectDomain,
    ProcessId SubjectProcess,
    ulong SubjectGeneration,
    EvidenceDomainIdentity? VirtualDomain,
    EvidenceDomainIdentity? SecureDomain);

public readonly record struct EvidenceCatalogEntry(
    EvidenceIdentity Identity,
    EvidenceVisibilityClass Visibility,
    EvidenceProducerIdentity Producer,
    bool HardwareRooted);

public readonly record struct EvidenceRecord(
    EvidenceIdentity Identity,
    EvidenceSubjectIdentity Subject,
    EvidenceProducerIdentity Producer,
    EvidenceVisibilityClass Visibility,
    string Result,
    string Payload,
    EvidenceFreshness Freshness,
    bool HardwareRooted);

public static class EvidenceContractLimits
{
    public const int MaximumKindCharacters = 128;
    public const int MaximumProducerCharacters = 128;
    public const int MaximumResultCharacters = 256;
    public const int MaximumPayloadCharacters = 4096;
}

public static class EvidenceResourceIds
{
    public const string Read = "platform-evidence:read:v1";
}

public enum SecureProperty
{
    PrivateMemory = 0,
    SharedMemory,
    MeasuredLaunch,
    IsolatedExecution
}

public readonly record struct SecureDomainId(ulong Value);
public readonly record struct SecureDomainGeneration(ulong Value);
public readonly record struct SecureDomainHandle(SecureDomainId DomainId, SecureDomainGeneration Generation);
public enum SecureDomainState { Created = 0, Configured, Running, Parked, Draining, Closed, Faulted, Quarantined }
public readonly record struct SecureDomainProfile(IReadOnlyList<SecureProperty> RequiredProperties, long MaximumMemoryBytes);
public readonly record struct SecureDomainAuthoritySet(
    SecureDomainHandle Domain,
    CapabilityId ConfigureCapability,
    CapabilityId MemoryCapability,
    CapabilityId ExecuteCapability,
    CapabilityId EvidenceCapability);

public static class SecureComputeResourceIds
{
    public const string Create = "secure-compute:create:v1";
    public static string Domain(SecureDomainId id) => $"secure-domain:{id.Value}";
    public static string Memory(SecureDomainId id) => $"secure-domain:{id.Value}:memory";
    public static string Evidence(SecureDomainId id) => $"secure-domain:{id.Value}:evidence";
}
