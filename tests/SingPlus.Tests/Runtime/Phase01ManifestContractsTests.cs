using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class Phase01ManifestContractsTests
{
    [Fact]
    public void CanonicalManifestDigestIsIndependentOfDeclarationOrder()
    {
        SingProcessManifestV1 process = TestFixtures.Manifest(101, 1001);
        ServiceDependencyRequirementV1 hard = new ServiceDependencyRequirementV1(new("Hard", "1", "hard"), ServiceDependencyKind.Hard);
        ServiceDependencyRequirementV1 optional = new ServiceDependencyRequirementV1(new("Optional", "2", "optional"), ServiceDependencyKind.Optional);
        PlatformRequirementV1 featureA = new PlatformRequirementV1((ComponentPlatformFeatureFamily)1001, 1, ComponentPlatformFeatureAvailability.ModelOnly, ManifestRequirementCriticality.Optional);
        PlatformRequirementV1 featureB = new PlatformRequirementV1((ComponentPlatformFeatureFamily)1002, 2, ComponentPlatformFeatureAvailability.ProjectionOnly, ManifestRequirementCriticality.Optional);
        ServiceBudgetRequestV1[] budgets = new[]
        {
            new ServiceBudgetRequestV1(ServiceBudgetDimension.IpcMessages, 32),
            new ServiceBudgetRequestV1(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
        };

        ServiceManifestV1 a = Manifest("canonical", process, dependencies: [optional, hard], platform: [featureB, featureA], budgets: budgets);
        ServiceManifestV1 b = Manifest("canonical", process, dependencies: [hard, optional], platform: [featureA, featureB], budgets: budgets.Reverse());

        Assert.Equal(a.SerializeCanonical(), b.SerializeCanonical());
        Assert.Equal(a.NormalizedDigest, b.NormalizedDigest);
        Assert.Equal(ServiceManifestV1.CurrentSchemaId, a.SchemaId);
        Assert.Equal(ServiceManifestV1.CurrentSchemaVersion, a.SchemaVersion);
        Assert.Equal(process.EntryIdentity, a.EntryPoint);
    }

    [Fact]
    public void UnknownMandatoryFeatureIsTypedDenialBeforeAuthorityMaterialization()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ServiceManifestV1 manifest = Manifest("mandatory-unknown", TestFixtures.Manifest(102, 1002), platform:
        [
            new((ComponentPlatformFeatureFamily)4242, 1, ComponentPlatformFeatureAvailability.RuntimeAdmission, ManifestRequirementCriticality.Mandatory)
        ]);
        ComponentAdmissionPlan plan = new ComponentAdmissionPlan(manifest, Image);

        ManifestAdmissionResultV1 evaluation = kernel.EvaluateComponentAdmission(plan);
        KernelResult<ComponentLifecycleSnapshot> admission = kernel.AdmitComponent(plan);

        Assert.Equal(ManifestAdmissionDisposition.Denied, evaluation.Disposition);
        ManifestRequirementDecisionV1 decision = Assert.Single(evaluation.Decisions, decision => decision.Kind == ManifestRequirementKind.PlatformFeature);
        Assert.Equal(ManifestRequirementDisposition.Denied, decision.Disposition);
        Assert.Equal(KernelError.PlatformUnsupported, admission.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new(102), 1)).Error);
        Assert.False(evaluation.MaterializesAuthority);
    }

    [Fact]
    public void UnknownOptionalFeatureAndMissingOptionalDependencyAreExplicitlyDegraded()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ServiceDependencyRequirementV1 optionalDependency = new ServiceDependencyRequirementV1(new("Absent", "1", "absent"), ServiceDependencyKind.Optional);
        ServiceManifestV1 manifest = Manifest("optional-unknown", TestFixtures.Manifest(103, 1003),
            dependencies: [optionalDependency],
            platform:
            [
                new((ComponentPlatformFeatureFamily)4343, 1, ComponentPlatformFeatureAvailability.ModelOnly, ManifestRequirementCriticality.Optional)
            ]);
        ComponentAdmissionPlan plan = new ComponentAdmissionPlan(manifest, Image);

        KernelResult<ComponentLifecycleSnapshot> admitted = kernel.AdmitComponent(plan);

        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.Equal(ManifestAdmissionDisposition.Degraded, admitted.Value!.Admission.Disposition);
        Assert.Contains(admitted.Value.Admission.Decisions, decision => decision.Kind == ManifestRequirementKind.PlatformFeature && decision.Disposition == ManifestRequirementDisposition.Unsupported);
        Assert.Contains(admitted.Value.Admission.Decisions, decision => decision.Kind == ManifestRequirementKind.Dependency && decision.Disposition is ManifestRequirementDisposition.Unsupported or ManifestRequirementDisposition.Degraded);
        Assert.Empty(admitted.Value.Sessions);
    }

    [Fact]
    public void HardDependencyDenialUsesExistingRollbackLifecycle()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ServiceDependencyRequirementV1 dependency = new ServiceDependencyRequirementV1(new("AbsentHard", "1", "absent-hard"), ServiceDependencyKind.Hard);
        ServiceManifestV1 manifest = Manifest("hard-missing", TestFixtures.Manifest(104, 1004), dependencies: [dependency]);
        ComponentAdmissionPlan plan = new ComponentAdmissionPlan(manifest, Image);

        Assert.Equal(ManifestAdmissionDisposition.Denied, kernel.EvaluateComponentAdmission(plan).Disposition);
        KernelResult<ComponentLifecycleSnapshot> admitted = kernel.AdmitComponent(plan);

        Assert.Equal(KernelError.ServiceNotFound, admitted.Error);
        ComponentLifecycleSnapshot rolledBack = kernel.QueryComponent(manifest.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, rolledBack.State);
        Assert.Equal(ManifestAdmissionDisposition.Denied, rolledBack.Admission.Disposition);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(rolledBack.Process).Error);
    }

    [Fact]
    public void LiveCapabilityAndServiceInstanceRemainSeparateFromManifestAdmissionEvidence()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        _ = TestFixtures.Create(kernel, 190, 1900);
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "phase01", CapabilityRights.Read);
        ProvidedServiceManifestV1 provided = new ProvidedServiceManifestV1("phase01", new("Phase01", "1", "phase01-digest"));
        SingProcessManifestV1 process = TestFixtures.Manifest(105, 1005, capabilities: [requirement]);
        ServiceManifestV1 manifest = new ServiceManifestV1(
            new("authority-split"), new("1"), Digest(Image), process, [provided],
            budgetRequests: [new(ServiceBudgetDimension.IpcMessages, 8)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 4096));
        ComponentAdmissionPlan plan = new ComponentAdmissionPlan(manifest, Image,
            [new(new DomainId(1900), requirement)],
            [new(provided, Protocol("Phase01", "phase01-digest"))]);

        ManifestAdmissionResultV1 preflight = kernel.EvaluateComponentAdmission(plan);
        KernelResult<ComponentLifecycleSnapshot> admitted = kernel.AdmitComponent(plan);

        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.Single(admitted.Value!.Capabilities);
        ServiceInstanceHandle instance = Assert.Single(admitted.Value.ServiceInstances);
        Assert.Equal(admitted.Value.Process, instance.Process);
        Assert.Equal(admitted.Value.Domain, instance.Domain);
        Assert.Equal(ServiceIdentityContract.Version, instance.ContractVersion);
        Assert.Contains(preflight.Decisions, decision => decision.Kind == ManifestRequirementKind.LocalCapability && decision.Disposition == ManifestRequirementDisposition.Requested);
        Assert.Contains(admitted.Value.Admission.Decisions, decision => decision.Kind == ManifestRequirementKind.LocalCapability && decision.Disposition == ManifestRequirementDisposition.Granted);
        Assert.Contains(admitted.Value.Admission.Decisions, decision => decision.Kind == ManifestRequirementKind.Budget && decision.Disposition == ManifestRequirementDisposition.Granted);
        Assert.DoesNotContain(typeof(CapabilityId), typeof(ManifestAdmissionResultV1).GetProperties().Select(property => property.PropertyType));
        Assert.DoesNotContain(typeof(ProcessHandle), typeof(ManifestAdmissionResultV1).GetProperties().Select(property => property.PropertyType));
    }

    [Fact]
    public void InvalidSchemaPoliciesCompatibilityAndDuplicateBudgetFailDeterministically()
    {
        SingProcessManifestV1 process = TestFixtures.Manifest(106, 1006);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceManifestV1(new("schema"), new("1"), Digest(Image), process, schemaVersion: 2));
        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(new("restart"), new("1"), Digest(Image), process,
            restartPolicy: new(ServiceRestartMode.OnFailure, 0, 1, 1)));
        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(new("telemetry"), new("1"), Digest(Image), process,
            telemetryPolicy: new(ServiceTelemetryVisibility.None, 1)));
        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(new("compatibility"), new("1"), Digest(Image), process,
            compatibility: new(3, 2)));
        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(new("budget"), new("1"), Digest(Image), process,
            budgetRequests: [new(ServiceBudgetDimension.IpcBytes, 1), new(ServiceBudgetDimension.IpcBytes, 2)]));
    }

    [Fact]
    public void PublicManifestSurfaceContainsNoCxlOrHybridCpuInternalVocabulary()
    {
        string[] names = typeof(ServiceManifestV1).GetProperties().Select(property => property.Name)
            .Concat(typeof(ManifestAdmissionResultV1).GetProperties().Select(property => property.Name))
            .Concat(typeof(ServiceInstanceHandle).GetProperties().Select(property => property.Name))
            .ToArray();
        foreach (string? forbidden in new[] { "Cxl", "Bdf", "Hdm", "Dpa", "FabricManager", "ProviderLease", "Lane", "Opcode", "ReplayCertificate" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly byte[] Image = [0x50, 0x31];

    private static ServiceManifestV1 Manifest(
        string name,
        SingProcessManifestV1 process,
        IEnumerable<ServiceDependencyRequirementV1>? dependencies = null,
        IEnumerable<PlatformRequirementV1>? platform = null,
        IEnumerable<ServiceBudgetRequestV1>? budgets = null) =>
        new(new(name), new("1"), Digest(Image), process,
            dependencies: dependencies,
            platformRequirements: platform,
            budgetRequests: budgets,
            restartPolicy: new(ServiceRestartMode.OnFailure, 3, 10, 100),
            drainPolicy: new(1000, ServiceDrainTimeoutAction.FailReplacement),
            checkpointPolicy: new(ServiceCheckpointMode.PlannedReplacementOnly),
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 4096));

    private static ProtocolDefinitionV1 Protocol(string name, string digest) =>
        new(name, digest, "Idle", ["Done"], [new(1, "Invoke")], [new(1, "Idle", "Done")]);

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
