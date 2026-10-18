using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformEvidenceCatalog> QueryEvidenceCatalog(
        PlatformDomainBinding binding,
        PlatformDomainIdentity subject,
        EvidenceDomainIdentity? virtualDomain,
        EvidenceDomainIdentity? secureDomain = null)
    {
        var domain = ValidateDomain(binding, subject);
        if (!domain.IsSuccess) return KernelResult<PlatformEvidenceCatalog>.Fail(domain.Error, domain.Message!);
        if (_provider is not IPlatformEvidenceProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.PlatformEvidence, PlatformEvidenceContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission))
            return KernelResult<PlatformEvidenceCatalog>.Fail(KernelError.PlatformUnsupported, "Platform evidence is unavailable.");
        var evidenceSubject = ToEvidenceSubject(subject, virtualDomain, secureDomain);
        var result = provider.QueryEvidenceCatalog(new(_domains[binding.BindingId].ProviderLease, evidenceSubject));
        if (!result.IsSuccess)
            return FromProviderFailure<PlatformEvidenceCatalog>(result.Status, result.Message);
        if (result.Value.ProviderEpoch == 0 || result.Value.Entries is null || result.Value.Entries.Any(entry =>
                !ValidIdentity(entry.Identity) || !ValidProducer(entry.Producer) ||
                !Enum.IsDefined(entry.Visibility) ||
                (entry.HardwareRooted && _featureManifest.Resolve(PlatformFeatureFamily.SecureDomains).Availability != PlatformFeatureAvailability.ProductionSecure)))
            return KernelResult<PlatformEvidenceCatalog>.Fail(KernelError.PlatformFaulted, "Provider evidence catalog is malformed.");
        return KernelResult<PlatformEvidenceCatalog>.Ok(new(
            result.Value.Entries.Where(entry => PlatformEvidenceContract.IsOrdinaryVisible(entry.Visibility)).ToArray(),
            result.Value.ProviderEpoch));
    }

    internal KernelResult<EvidenceRecord> ReadEvidence(
        PlatformDomainBinding binding,
        PlatformDomainIdentity subject,
        EvidenceDomainIdentity? virtualDomain,
        EvidenceIdentity identity,
        EvidenceVisibilityClass visibility,
        EvidenceFreshness minimumFreshness,
        EvidenceDomainIdentity? secureDomain = null)
    {
        var domain = ValidateDomain(binding, subject);
        if (!domain.IsSuccess) return KernelResult<EvidenceRecord>.Fail(domain.Error, domain.Message!);
        if (!ValidIdentity(identity) || !PlatformEvidenceContract.IsOrdinaryVisible(visibility) ||
            minimumFreshness.ProviderEpoch == 0 || minimumFreshness.Sequence == 0)
            return KernelResult<EvidenceRecord>.Fail(KernelError.PlatformDenied, "HostInternal evidence cannot cross the ordinary boundary.");
        if (_provider is not IPlatformEvidenceProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.PlatformEvidence, PlatformEvidenceContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission))
            return KernelResult<EvidenceRecord>.Fail(KernelError.PlatformUnsupported, "Platform evidence is unavailable.");
        var evidenceSubject = ToEvidenceSubject(subject, virtualDomain, secureDomain);
        var result = provider.ReadEvidence(new(_domains[binding.BindingId].ProviderLease, evidenceSubject, identity, visibility, minimumFreshness));
        if (!result.IsSuccess)
            return FromProviderFailure<EvidenceRecord>(result.Status, result.Message);
        var evidence = result.Value;
        if (evidence.Identity != identity || evidence.Visibility != visibility || evidence.Subject != evidenceSubject ||
            evidence.Freshness.ProviderEpoch == 0 || evidence.Freshness.Sequence == 0 ||
            evidence.Freshness.ProviderEpoch != minimumFreshness.ProviderEpoch ||
            evidence.Freshness.Sequence < minimumFreshness.Sequence ||
            !ValidProducer(evidence.Producer) || string.IsNullOrWhiteSpace(evidence.Result) ||
            evidence.Result.Length > EvidenceContractLimits.MaximumResultCharacters ||
            evidence.Payload is null || evidence.Payload.Length > EvidenceContractLimits.MaximumPayloadCharacters ||
            (evidence.HardwareRooted && _featureManifest.Resolve(PlatformFeatureFamily.SecureDomains).Availability != PlatformFeatureAvailability.ProductionSecure) ||
            !PlatformEvidenceContract.IsOrdinaryVisible(evidence.Visibility))
            return KernelResult<EvidenceRecord>.Fail(KernelError.PlatformFaulted, "Provider evidence is stale, mismatched, or malformed.");
        return KernelResult<EvidenceRecord>.Ok(evidence);
    }

    private static EvidenceSubjectIdentity ToEvidenceSubject(PlatformDomainIdentity subject,
        EvidenceDomainIdentity? virtualDomain, EvidenceDomainIdentity? secureDomain) =>
        new(subject.DomainId, subject.ProcessId, subject.ProcessGeneration, virtualDomain, secureDomain);

    private static bool ValidIdentity(EvidenceIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.Kind) && identity.Kind.Length <= EvidenceContractLimits.MaximumKindCharacters && identity.Version != 0;

    private static bool ValidProducer(EvidenceProducerIdentity producer) =>
        !string.IsNullOrWhiteSpace(producer.Value) && producer.Value.Length <= EvidenceContractLimits.MaximumProducerCharacters;
}
