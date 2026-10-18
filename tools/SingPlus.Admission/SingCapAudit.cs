using System.Security.Cryptography;
using System.Text.Json;
using SingPlus.Contracts;

namespace SingPlus.Admission;

public readonly record struct ExternalProviderArtifactEvidenceV1(
    string PackageId,
    string PackageVersion,
    string PackageSha256,
    string SourceCommit,
    string ContractSchemaVersion);

public sealed class SingCapAuditV1
{
    public const string Schema = "singcap-audit-v1";
    private readonly ExternalProviderArtifactEvidenceV1[] _providers;

    private SingCapAuditV1(ServiceManifestV2 manifest, SingPlusAdmissionProofV1 proof, string sdkVersion,
        string runtimeVersion, IEnumerable<ExternalProviderArtifactEvidenceV1>? providers)
    {
        Manifest = manifest;
        AdmissionProof = proof;
        SdkVersion = sdkVersion;
        RuntimeVersion = runtimeVersion;
        _providers = (providers ?? []).OrderBy(static item => item.PackageId, StringComparer.Ordinal).ThenBy(static item => item.PackageVersion, StringComparer.Ordinal).ToArray();
        AuditDigest = ComputeDigest(SerializeCanonical(includeAuditDigest: false));
    }

    public ServiceManifestV2 Manifest { get; }
    public SingPlusAdmissionProofV1 AdmissionProof { get; }
    public string SdkVersion { get; }
    public string RuntimeVersion { get; }
    public IReadOnlyList<ExternalProviderArtifactEvidenceV1> ProviderArtifacts => _providers;
    public string AuditDigest { get; }

    public static SingCapAuditV1 Create(ServiceManifestV2 manifest, AdmissionVerificationResult admission,
        string sdkVersion, string runtimeVersion, IEnumerable<ExternalProviderArtifactEvidenceV1>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(admission);
        if (!admission.IsAdmitted) throw new ArgumentException("Rejected admission evidence cannot produce a successful SingCap audit.", nameof(admission));
        if (manifest.SecurityProfile != ComponentSecurityProfile.ManagedCap) throw new ArgumentException("ManagedCap audit requires the ManagedCap security profile.", nameof(manifest));
        if (!string.Equals(admission.Proof.Profile, "ManagedCap", StringComparison.Ordinal)) throw new ArgumentException("Admission proof profile does not match the manifest.", nameof(admission));
        if (!string.Equals(admission.Proof.AssemblyDigest, manifest.BaseManifest.ImageDigest, StringComparison.Ordinal)) throw new ArgumentException("Admission proof image digest does not match the manifest.", nameof(admission));
        var policy = AdmissionVerifier.GetManagedCapPolicyDescriptor();
        if (!string.Equals(manifest.AdmissionPolicyVersion, policy.AdmissionPolicyVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.AdmissionPolicyDigest, policy.AdmissionPolicyDigest, StringComparison.Ordinal) ||
            !string.Equals(admission.Proof.RulesetDigest, policy.AdmissionPolicyDigest, StringComparison.Ordinal))
            throw new ArgumentException("Admission policy identity or digest drifted from the live verifier.", nameof(manifest));
        if (!string.Equals(manifest.ManagedCapFrameworkSurfaceVersion, policy.FrameworkSurfaceVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.ManagedCapFrameworkSurfaceDigest, policy.FrameworkSurfaceDigest, StringComparison.Ordinal))
            throw new ArgumentException("ManagedCap framework surface identity or digest drifted from the live verifier.", nameof(manifest));
        if (string.IsNullOrWhiteSpace(sdkVersion) || string.IsNullOrWhiteSpace(runtimeVersion)) throw new ArgumentException("SDK and runtime versions are required.");
        ValidateProviders(providers ?? []);
        return new SingCapAuditV1(manifest, admission.Proof, sdkVersion, runtimeVersion, providers);
    }

    public byte[] SerializeCanonical() => SerializeCanonical(includeAuditDigest: true);

    public static void EnsureNoSameVersionArtifactMutation(SingCapAuditV1 previous, SingCapAuditV1 current)
    {
        ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(current);
        foreach (var prior in previous._providers)
        {
            var candidate = current._providers.SingleOrDefault(item => item.PackageId == prior.PackageId && item.PackageVersion == prior.PackageVersion);
            if (!string.IsNullOrEmpty(candidate.PackageId) && !string.Equals(candidate.PackageSha256, prior.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Provider package '{prior.PackageId}/{prior.PackageVersion}' changed content without an identity/version change.");
        }
    }

    private byte[] SerializeCanonical(bool includeAuditDigest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("Schema", Schema);
            writer.WriteString("ManifestDigest", Manifest.NormalizedDigest); writer.WriteString("ImageDigest", Manifest.BaseManifest.ImageDigest);
            writer.WritePropertyName("ManifestV1"); writer.WriteRawValue(Manifest.BaseManifest.SerializeCanonical(), skipInputValidation: false);
            writer.WritePropertyName("ManifestV2"); writer.WriteRawValue(Manifest.SerializeCanonical(), skipInputValidation: false);
            writer.WriteString("SecurityProfile", Manifest.SecurityProfile.ToString()); writer.WriteString("SdkVersion", SdkVersion); writer.WriteString("RuntimeVersion", RuntimeVersion);
            writer.WriteString("AdmissionProofDigest", AdmissionProof.ProofDigest); writer.WriteString("AdmissionPolicyVersion", Manifest.AdmissionPolicyVersion);
            writer.WriteString("AdmissionPolicyDigest", Manifest.AdmissionPolicyDigest); writer.WriteString("ManagedCapFrameworkSurfaceVersion", Manifest.ManagedCapFrameworkSurfaceVersion);
            writer.WriteString("ManagedCapFrameworkSurfaceDigest", Manifest.ManagedCapFrameworkSurfaceDigest);
            writer.WriteString("DependencyClosureDigest", AdmissionProof.DependencyDigest);
            writer.WritePropertyName("ProviderArtifacts"); writer.WriteStartArray();
            foreach (var item in _providers) { writer.WriteStartArray(); writer.WriteStringValue(item.PackageId); writer.WriteStringValue(item.PackageVersion); writer.WriteStringValue(item.PackageSha256.ToLowerInvariant()); writer.WriteStringValue(item.SourceCommit); writer.WriteStringValue(item.ContractSchemaVersion); writer.WriteEndArray(); }
            writer.WriteEndArray(); if (includeAuditDigest) writer.WriteString(nameof(AuditDigest), AuditDigest); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void ValidateProviders(IEnumerable<ExternalProviderArtifactEvidenceV1> providers)
    {
        var array = providers.ToArray();
        if (array.Any(static item => string.IsNullOrWhiteSpace(item.PackageId) || string.IsNullOrWhiteSpace(item.PackageVersion) ||
            item.PackageSha256.Length != 64 || !item.PackageSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(item.SourceCommit) || string.IsNullOrWhiteSpace(item.ContractSchemaVersion)) ||
            array.Select(static item => (item.PackageId, item.PackageVersion)).Distinct().Count() != array.Length)
            throw new ArgumentException("Provider artifacts require unique package identity/version, exact digest, source commit, and contract schema.", nameof(providers));
    }

    private static string ComputeDigest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
