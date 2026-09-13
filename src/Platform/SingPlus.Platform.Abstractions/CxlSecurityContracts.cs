using SingPlus.Contracts;

namespace SingPlus.Platform;

[Flags]
public enum CxlSecurityProperties
{
    None = 0,
    LinkEncryptionAvailable = 1 << 0,
    IdeEnabled = 1 << 1,
    DeviceAuthenticated = 1 << 2,
    FirmwareMeasured = 1 << 3,
    TrustedExecutionCapable = 1 << 4
}

public enum CxlSecurityHealth { Unavailable = 0, Ready, Failed }
public enum CxlSecurityEvidenceAssurance { ModelOnly = 0, HardwareAttested = 1 }
public readonly record struct CxlSecurityEvidenceGeneration(ulong Value);

public sealed record CxlSecurityStateSnapshot(
    CxlEndpointId EndpointId,
    CxlDeviceGeneration DeviceGeneration,
    CxlSecurityEvidenceGeneration EvidenceGeneration,
    CxlSecurityProperties Properties,
    CxlSecurityHealth Health,
    EvidenceRecord Evidence,
    CxlSecurityEvidenceAssurance Assurance = CxlSecurityEvidenceAssurance.ModelOnly);

public sealed record CxlSecurityPolicy(
    CxlSecurityProperties RequiredProperties,
    CxlSecurityEvidenceGeneration ExpectedGeneration,
    bool RequiredForOperation,
    bool RequireHardwareAttestation = false);

public interface ICxlSecurityStateProvider : ICxlSecurityEvidenceProvider
{
    PlatformAuthorityResult<CxlSecurityStateSnapshot> QuerySecurityState(
        CxlEndpointId endpointId,
        CxlDeviceGeneration expectedDeviceGeneration);
}
