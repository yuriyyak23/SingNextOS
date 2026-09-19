using System.Security.Cryptography;
using System.Text.Json;

namespace SingPlus.Contracts;

public enum ComponentSecurityProfile
{
    ManagedCap = 1,
    TrustedRuntime,
    NativeIsolated,
    PlatformExternal,
    BuildToolTestOnly,
}

public readonly record struct ContentDigestReferenceV1(string Identity, string Version, string Sha256);
public readonly record struct SealedTypeImportV1(string TypeIdentity, string PolicyVersion);
public readonly record struct SealedTypeExportV1(string TypeIdentity, string PolicyVersion);
public readonly record struct ManifestPolicyReferenceV1(string Schema, int Version, string PolicyDigest);

public sealed class ServiceManifestV2
{
    public const string CurrentSchemaId = "SingServiceManifestV2";
    public const int CurrentSchemaVersion = 2;

    private readonly ContentDigestReferenceV1[] _dependencies;
    private readonly CapabilityRequirementV1[] _staticCapabilities;
    private readonly SealedTypeImportV1[] _sealedImports;
    private readonly SealedTypeExportV1[] _sealedExports;

    public ServiceManifestV2(
        ServiceManifestV1 baseManifest,
        ComponentSecurityProfile securityProfile,
        IEnumerable<ContentDigestReferenceV1>? dependencyContentDigests,
        IEnumerable<CapabilityRequirementV1>? staticCapabilityImports,
        IEnumerable<SealedTypeImportV1>? sealedTypeImports,
        IEnumerable<SealedTypeExportV1>? sealedTypeExports,
        ManifestPolicyReferenceV1 memoryPolicy,
        ManifestPolicyReferenceV1 authorityTableQuotaPolicy,
        ManifestPolicyReferenceV1 runtimePolicy,
        ManifestPolicyReferenceV1 delegationPolicy,
        ManifestPolicyReferenceV1 aotPolicy,
        string admissionPolicyVersion,
        string admissionPolicyDigest,
        string managedCapFrameworkSurfaceVersion,
        string managedCapFrameworkSurfaceDigest,
        string schemaId = CurrentSchemaId,
        int schemaVersion = CurrentSchemaVersion)
    {
        BaseManifest = baseManifest ?? throw new ArgumentNullException(nameof(baseManifest));
        if (!string.Equals(schemaId, CurrentSchemaId, StringComparison.Ordinal)) throw new ArgumentException("Unsupported service manifest schema id.", nameof(schemaId));
        if (schemaVersion != CurrentSchemaVersion) throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Unsupported service manifest schema version.");
        if (!Enum.IsDefined(securityProfile)) throw new ArgumentException("Unknown security profile.", nameof(securityProfile));
        ValidatePolicy(memoryPolicy, nameof(memoryPolicy));
        ValidatePolicy(authorityTableQuotaPolicy, nameof(authorityTableQuotaPolicy));
        ValidatePolicy(runtimePolicy, nameof(runtimePolicy));
        ValidatePolicy(delegationPolicy, nameof(delegationPolicy));
        ValidatePolicy(aotPolicy, nameof(aotPolicy));
        if (string.IsNullOrWhiteSpace(admissionPolicyVersion)) throw new ArgumentException("Admission policy version is required.", nameof(admissionPolicyVersion));
        ValidateDigest(admissionPolicyDigest, nameof(admissionPolicyDigest));
        if (string.IsNullOrWhiteSpace(managedCapFrameworkSurfaceVersion)) throw new ArgumentException("Framework surface version is required.", nameof(managedCapFrameworkSurfaceVersion));
        ValidateDigest(managedCapFrameworkSurfaceDigest, nameof(managedCapFrameworkSurfaceDigest));

        _dependencies = (dependencyContentDigests ?? []).OrderBy(static item => item.Identity, StringComparer.Ordinal).ThenBy(static item => item.Version, StringComparer.Ordinal).ToArray();
        if (_dependencies.Any(static item => string.IsNullOrWhiteSpace(item.Identity) || string.IsNullOrWhiteSpace(item.Version) || !IsDigest(item.Sha256)) ||
            _dependencies.Select(static item => (item.Identity, item.Version)).Distinct().Count() != _dependencies.Length)
            throw new ArgumentException("Dependency content identities, versions, and SHA-256 digests must be complete and unique.", nameof(dependencyContentDigests));

        _staticCapabilities = (staticCapabilityImports ?? []).OrderBy(static item => item.ResourceKind).ThenBy(static item => item.ResourceId, StringComparer.Ordinal).ThenBy(static item => item.Rights).ToArray();
        var baseCapabilities = baseManifest.ResourceRequirements.Where(static item => item.Kind == ComponentResourceRequirementKind.LocalCapability)
            .Select(static item => item.ToCapabilityRequirement()).OrderBy(static item => item.ResourceKind).ThenBy(static item => item.ResourceId, StringComparer.Ordinal).ThenBy(static item => item.Rights).ToArray();
        if (!_staticCapabilities.SequenceEqual(baseCapabilities))
            throw new ArgumentException("Static capability imports must exactly match V1 local resource intent.", nameof(staticCapabilityImports));

        _sealedImports = NormalizeSealed(sealedTypeImports, nameof(sealedTypeImports));
        _sealedExports = NormalizeSealed(sealedTypeExports, nameof(sealedTypeExports));
        SchemaId = schemaId;
        SchemaVersion = schemaVersion;
        SecurityProfile = securityProfile;
        MemoryPolicy = memoryPolicy;
        AuthorityTableQuotaPolicy = authorityTableQuotaPolicy;
        RuntimePolicy = runtimePolicy;
        DelegationPolicy = delegationPolicy;
        AotPolicy = aotPolicy;
        AdmissionPolicyVersion = admissionPolicyVersion;
        AdmissionPolicyDigest = admissionPolicyDigest.ToLowerInvariant();
        ManagedCapFrameworkSurfaceVersion = managedCapFrameworkSurfaceVersion;
        ManagedCapFrameworkSurfaceDigest = managedCapFrameworkSurfaceDigest.ToLowerInvariant();
        NormalizedDigest = ComputeDigest();
    }

    public string SchemaId { get; }
    public int SchemaVersion { get; }
    public ServiceManifestV1 BaseManifest { get; }
    public ComponentSecurityProfile SecurityProfile { get; }
    public IReadOnlyList<ContentDigestReferenceV1> DependencyContentDigests => _dependencies;
    public IReadOnlyList<CapabilityRequirementV1> StaticCapabilityImports => _staticCapabilities;
    public IReadOnlyList<SealedTypeImportV1> SealedTypeImports => _sealedImports;
    public IReadOnlyList<SealedTypeExportV1> SealedTypeExports => _sealedExports;
    public ManifestPolicyReferenceV1 MemoryPolicy { get; }
    public ManifestPolicyReferenceV1 AuthorityTableQuotaPolicy { get; }
    public ManifestPolicyReferenceV1 RuntimePolicy { get; }
    public ManifestPolicyReferenceV1 DelegationPolicy { get; }
    public ManifestPolicyReferenceV1 AotPolicy { get; }
    public string AdmissionPolicyVersion { get; }
    public string AdmissionPolicyDigest { get; }
    public string ManagedCapFrameworkSurfaceVersion { get; }
    public string ManagedCapFrameworkSurfaceDigest { get; }
    public string NormalizedDigest { get; }

    public byte[] SerializeCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(SchemaId), SchemaId); writer.WriteNumber(nameof(SchemaVersion), SchemaVersion);
            writer.WriteString(nameof(BaseManifest), BaseManifest.NormalizedDigest); writer.WriteNumber(nameof(SecurityProfile), (int)SecurityProfile);
            WriteDependencies(writer); WriteCapabilities(writer); WriteSealed(writer, nameof(SealedTypeImports), _sealedImports); WriteSealed(writer, nameof(SealedTypeExports), _sealedExports);
            WritePolicy(writer, nameof(MemoryPolicy), MemoryPolicy); WritePolicy(writer, nameof(AuthorityTableQuotaPolicy), AuthorityTableQuotaPolicy);
            WritePolicy(writer, nameof(RuntimePolicy), RuntimePolicy); WritePolicy(writer, nameof(DelegationPolicy), DelegationPolicy); WritePolicy(writer, nameof(AotPolicy), AotPolicy);
            writer.WriteString(nameof(AdmissionPolicyVersion), AdmissionPolicyVersion); writer.WriteString(nameof(AdmissionPolicyDigest), AdmissionPolicyDigest);
            writer.WriteString(nameof(ManagedCapFrameworkSurfaceVersion), ManagedCapFrameworkSurfaceVersion); writer.WriteString(nameof(ManagedCapFrameworkSurfaceDigest), ManagedCapFrameworkSurfaceDigest);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public string ComputeDigest() => Convert.ToHexString(SHA256.HashData(SerializeCanonical())).ToLowerInvariant();

    private void WriteDependencies(Utf8JsonWriter writer) { writer.WritePropertyName(nameof(DependencyContentDigests)); writer.WriteStartArray(); foreach (var item in _dependencies) { writer.WriteStartArray(); writer.WriteStringValue(item.Identity); writer.WriteStringValue(item.Version); writer.WriteStringValue(item.Sha256.ToLowerInvariant()); writer.WriteEndArray(); } writer.WriteEndArray(); }
    private void WriteCapabilities(Utf8JsonWriter writer) { writer.WritePropertyName(nameof(StaticCapabilityImports)); writer.WriteStartArray(); foreach (var item in _staticCapabilities) { writer.WriteStartArray(); writer.WriteNumberValue((int)item.ResourceKind); writer.WriteStringValue(item.ResourceId); writer.WriteNumberValue((int)item.Rights); writer.WriteEndArray(); } writer.WriteEndArray(); }
    private static void WriteSealed<T>(Utf8JsonWriter writer, string name, IReadOnlyList<T> items) { writer.WritePropertyName(name); writer.WriteStartArray(); foreach (var item in items) { var values = item switch { SealedTypeImportV1 x => (x.TypeIdentity, x.PolicyVersion), SealedTypeExportV1 x => (x.TypeIdentity, x.PolicyVersion), _ => throw new InvalidOperationException() }; writer.WriteStartArray(); writer.WriteStringValue(values.Item1); writer.WriteStringValue(values.Item2); writer.WriteEndArray(); } writer.WriteEndArray(); }
    private static void WritePolicy(Utf8JsonWriter writer, string name, ManifestPolicyReferenceV1 value) { writer.WritePropertyName(name); writer.WriteStartArray(); writer.WriteStringValue(value.Schema); writer.WriteNumberValue(value.Version); writer.WriteStringValue(value.PolicyDigest.ToLowerInvariant()); writer.WriteEndArray(); }
    private static T[] NormalizeSealed<T>(IEnumerable<T>? values, string name) where T : struct { var result = (values ?? []).OrderBy(static item => item.ToString(), StringComparer.Ordinal).ToArray(); var pairs = result.Select(item => item switch { SealedTypeImportV1 x => (x.TypeIdentity, x.PolicyVersion), SealedTypeExportV1 x => (x.TypeIdentity, x.PolicyVersion), _ => default }).ToArray(); if (pairs.Any(static pair => string.IsNullOrWhiteSpace(pair.TypeIdentity) || string.IsNullOrWhiteSpace(pair.PolicyVersion)) || pairs.Distinct().Count() != pairs.Length) throw new ArgumentException("Sealed type identities and policy versions must be complete and unique.", name); return result; }
    private static void ValidatePolicy(ManifestPolicyReferenceV1 value, string name) { if (string.IsNullOrWhiteSpace(value.Schema) || value.Version <= 0 || !IsDigest(value.PolicyDigest)) throw new ArgumentException("Policy references require schema, positive version, and SHA-256 digest.", name); }
    private static void ValidateDigest(string value, string name) { if (!IsDigest(value)) throw new ArgumentException("A SHA-256 hexadecimal digest is required.", name); }
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
