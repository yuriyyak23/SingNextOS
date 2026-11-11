using System.Security.Cryptography;
using System.Text.Json;
using SingPlus.Admission;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class SingCapPhase10ManifestAuditTests
{
    private static readonly byte[] Image = [0x53, 0x43, 0x41, 0x50, 0x32];
    private static readonly string FillerDigest = new('a', 64);

    [Fact]
    public void ManifestAndAuditAreByteStableForEquivalentOrderedInputs()
    {
        ServiceManifestV2 first = Manifest(dependencies: [Dependency("z"), Dependency("a")]);
        ServiceManifestV2 second = Manifest(dependencies: [Dependency("a"), Dependency("z")]);
        ExternalProviderArtifactEvidenceV1[] providersA = new[] { Provider("z", FillerDigest), Provider("a", new string('b', 64)) };
        IEnumerable<ExternalProviderArtifactEvidenceV1> providersB = providersA.Reverse();

        SingCapAuditV1 auditA = SingCapAuditV1.Create(first, Proof(first), "11.0.100-rc.1", "11.0.0-rc.1", providersA);
        SingCapAuditV1 auditB = SingCapAuditV1.Create(second, Proof(second), "11.0.100-rc.1", "11.0.0-rc.1", providersB);

        Assert.Equal(first.SerializeCanonical(), second.SerializeCanonical());
        Assert.Equal(auditA.SerializeCanonical(), auditB.SerializeCanonical());
    }

    [Fact]
    public void DependencyDigestChangeChangesManifestAndAudit()
    {
        ServiceManifestV2 first = Manifest(dependencies: [Dependency("dep", FillerDigest)]);
        ServiceManifestV2 second = Manifest(dependencies: [Dependency("dep", new string('b', 64))]);

        SingCapAuditV1 auditA = SingCapAuditV1.Create(first, Proof(first), "sdk", "runtime");
        SingCapAuditV1 auditB = SingCapAuditV1.Create(second, Proof(second), "sdk", "runtime");

        Assert.NotEqual(first.NormalizedDigest, second.NormalizedDigest);
        Assert.NotEqual(auditA.AuditDigest, auditB.AuditDigest);
    }

    [Fact]
    public void V2StaticIntentMustMatchRuntimeGrantsBeforeAnyAuthorityIsCreated()
    {
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.Device, "sensor", CapabilityRights.Read);
        SingProcessManifestV1 process = TestFixtures.Manifest(910, 9100, capabilities: [requirement]);
        ServiceManifestV1 baseManifest = new ServiceManifestV1(new("p10-runtime"), new("1"), Digest(Image), process);
        ServiceManifestV2 manifest = Manifest(baseManifest, staticImports: [requirement]);
        RuntimeKernel kernel = new RuntimeKernel();

        KernelResult<ComponentLifecycleSnapshot> result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, Image, grants: []));

        Assert.Equal(KernelError.InvalidManifest, result.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(910), 1)).Error);
    }

    [Fact]
    public void AuditEvidenceCannotBeUsedAsRuntimeAuthority()
    {
        ServiceManifestV2 manifest = Manifest();
        SingCapAuditV1 audit = SingCapAuditV1.Create(manifest, Proof(manifest), "sdk", "runtime");
        Type[] exposed = typeof(SingCapAuditV1).GetProperties().Select(static property => property.PropertyType).ToArray();

        Assert.DoesNotContain(typeof(CapabilityId), exposed);
        Assert.DoesNotContain(typeof(CapabilityHandleV2), exposed);
        Assert.DoesNotContain(typeof(ProcessHandle), exposed);
        Assert.DoesNotContain(audit.GetType(), typeof(ComponentAdmissionPlan).GetConstructors().SelectMany(static constructor => constructor.GetParameters()).Select(static parameter => parameter.ParameterType));
    }

    [Fact]
    public void UnknownSchemaProfileAndMalformedPolicyFailClosed()
    {
        ServiceManifestV1 baseManifest = BaseManifest();
        ManagedCapPolicyDescriptor policy = AdmissionVerifier.GetManagedCapPolicyDescriptor();
        Assert.Throws<ArgumentException>(() => Create(baseManifest, (ComponentSecurityProfile)99, policy, schemaId: "future"));
        Assert.Throws<ArgumentException>(() => Create(baseManifest, ComponentSecurityProfile.ManagedCap, policy,
            memoryPolicy: new("unknown", 0, "bad")));
        Assert.Throws<ArgumentException>(() => Create(baseManifest, ComponentSecurityProfile.ManagedCap, policy,
            memoryPolicy: new("future-memory-policy", 1, FillerDigest)));
        Assert.Throws<ArgumentException>(() => Create(baseManifest, ComponentSecurityProfile.ManagedCap, policy,
            memoryPolicy: new("memory", 2, FillerDigest)));
    }

    [Fact]
    public void AuditCarriesReviewableCanonicalIntentRatherThanOnlyManifestDigests()
    {
        ServiceManifestV2 manifest = Manifest(dependencies: [Dependency("dep")]);
        SingCapAuditV1 audit = SingCapAuditV1.Create(manifest, Proof(manifest), "sdk", "runtime");
        using JsonDocument document = global::System.Text.Json.JsonDocument.Parse(audit.SerializeCanonical());

        JsonElement root = document.RootElement;
        Assert.Equal(manifest.BaseManifest.Identity.Name, root.GetProperty("ManifestV1").GetProperty("Identity").GetString());
        Assert.Equal(manifest.SchemaId, root.GetProperty("ManifestV2").GetProperty("SchemaId").GetString());
        Assert.Equal("dep", root.GetProperty("ManifestV2").GetProperty("DependencyContentDigests")[0][0].GetString());
        Assert.Equal(manifest.NormalizedDigest, root.GetProperty("ManifestDigest").GetString());
    }

    [Fact]
    public void SameProviderVersionWithDifferentBytesRequiresRequalification()
    {
        ServiceManifestV2 manifest = Manifest();
        SingCapAuditV1 prior = SingCapAuditV1.Create(manifest, Proof(manifest), "sdk", "runtime", [Provider("HybridCPU.ExternalRuntime.Contracts", FillerDigest)]);
        SingCapAuditV1 rebuilt = SingCapAuditV1.Create(manifest, Proof(manifest), "sdk", "runtime", [Provider("HybridCPU.ExternalRuntime.Contracts", new string('b', 64))]);

        Assert.Throws<InvalidOperationException>(() => SingCapAuditV1.EnsureNoSameVersionArtifactMutation(prior, rebuilt));
    }

    [Fact]
    public void VerifierAndFrameworkSurfaceDriftInvalidateAudit()
    {
        ManagedCapPolicyDescriptor policy = AdmissionVerifier.GetManagedCapPolicyDescriptor();
        ServiceManifestV2 verifierDrift = Create(BaseManifest(), ComponentSecurityProfile.ManagedCap,
            policy with { AdmissionPolicyDigest = new string('b', 64) });
        ServiceManifestV2 surfaceDrift = Create(BaseManifest(), ComponentSecurityProfile.ManagedCap,
            policy with { FrameworkSurfaceDigest = new string('c', 64) });

        Assert.Throws<ArgumentException>(() => SingCapAuditV1.Create(verifierDrift, Proof(verifierDrift), "sdk", "runtime"));
        Assert.Throws<ArgumentException>(() => SingCapAuditV1.Create(surfaceDrift, Proof(surfaceDrift), "sdk", "runtime"));
    }

    private static ServiceManifestV2 Manifest(ServiceManifestV1? baseManifest = null,
        IEnumerable<ContentDigestReferenceV1>? dependencies = null,
        IEnumerable<CapabilityRequirementV1>? staticImports = null) =>
        Create(baseManifest ?? BaseManifest(), ComponentSecurityProfile.ManagedCap, AdmissionVerifier.GetManagedCapPolicyDescriptor(),
            dependencies, staticImports);

    private static ServiceManifestV2 Create(ServiceManifestV1 baseManifest, ComponentSecurityProfile profile,
        ManagedCapPolicyDescriptor policy, IEnumerable<ContentDigestReferenceV1>? dependencies = null,
        IEnumerable<CapabilityRequirementV1>? staticImports = null, ManifestPolicyReferenceV1? memoryPolicy = null,
        string schemaId = ServiceManifestV2.CurrentSchemaId) =>
        new(baseManifest, profile, dependencies, staticImports ?? baseManifest.Process.RequiredCapabilities,
            [new("SingPlus.Seal.Input", "v1")], [new("SingPlus.Seal.Output", "v1")],
            memoryPolicy ?? Policy("memory"), Policy("authority-table-quota"), Policy("runtime"), Policy("delegation"), Policy("aot"),
            policy.AdmissionPolicyVersion, policy.AdmissionPolicyDigest, policy.FrameworkSurfaceVersion, policy.FrameworkSurfaceDigest,
            schemaId);

    private static ServiceManifestV1 BaseManifest() => new(new("p10"), new("1"), Digest(Image), TestFixtures.Manifest(900, 9000));
    private static ManifestPolicyReferenceV1 Policy(string schema) => new(schema, 1, FillerDigest);
    private static ContentDigestReferenceV1 Dependency(string identity, string? digest = null) => new(identity, "1", digest ?? FillerDigest);
    private static ExternalProviderArtifactEvidenceV1 Provider(string id, string digest) => new(id, "1.14.0", digest, new string('c', 40), "ExternalRuntime-v1");
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static AdmissionVerificationResult Proof(ServiceManifestV2 manifest)
    {
        ManagedCapPolicyDescriptor policy = AdmissionVerifier.GetManagedCapPolicyDescriptor();
        return new(new SingPlusAdmissionProofV1
        {
            Root = manifest.BaseManifest.EntryPoint,
            Profile = "ManagedCap",
            AssemblyDigest = manifest.BaseManifest.ImageDigest,
            ReachableMethodCount = 1,
            ForbiddenOperationCount = 0,
            DependencyDigest = FillerDigest,
            RulesetDigest = policy.AdmissionPolicyDigest,
            ProofDigest = new string('d', 64)
        }, []);
    }
}
