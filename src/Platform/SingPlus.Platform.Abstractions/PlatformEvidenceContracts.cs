using SingPlus.Contracts;

namespace SingPlus.Platform;

public readonly record struct PlatformEvidenceCatalogRequest(
    PlatformProviderDomainLease Subject,
    EvidenceSubjectIdentity EvidenceSubject);

public readonly record struct PlatformEvidenceReadRequest(
    PlatformProviderDomainLease Subject,
    EvidenceSubjectIdentity EvidenceSubject,
    EvidenceIdentity Identity,
    EvidenceVisibilityClass Visibility,
    EvidenceFreshness MinimumFreshness);

public readonly record struct PlatformEvidenceCatalog(IReadOnlyList<EvidenceCatalogEntry> Entries, ulong ProviderEpoch);

public static class PlatformEvidenceContract
{
    public const uint ContractVersion = 1;
    public static bool IsOrdinaryVisible(EvidenceVisibilityClass visibility) =>
        Enum.IsDefined(visibility) && visibility != EvidenceVisibilityClass.HostInternal;
}

public interface IPlatformEvidenceProvider
{
    PlatformAuthorityResult<PlatformEvidenceCatalog> QueryEvidenceCatalog(PlatformEvidenceCatalogRequest request);
    PlatformAuthorityResult<EvidenceRecord> ReadEvidence(PlatformEvidenceReadRequest request);
}
