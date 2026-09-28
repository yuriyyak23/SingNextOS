using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase17MigrationCutoverTests
{
    private const string Gate = "FG-VNX-HOST-RESOURCE-ADAPTER";
    private const string Evidence = "65e85181878847e622489506de5a939828a1235562da4e4d5abf26098f72760c";

    [Fact]
    public void OldManifestAndGeneratedContourRemainByteShapeCompatibleAndAdmitted()
    {
        var image = new byte[] { 17, 1, 0 };
        var manifest = Manifest(image);
        var canonical = Encoding.UTF8.GetString(manifest.SerializeCanonical());
        var kernel = new RuntimeKernel();

        var admission = kernel.EvaluateComponentAdmission(new ComponentAdmissionPlan(manifest, image));
        var decision = VNextMigrationCoordinator.Evaluate(new VNextFeatureGateAuthority(),
            new(VNextMigrationRequest.CurrentVersion, VNextCallerContract.LegacySipV1,
                VNextResourceMigrationRequirement.None, 0, null, false));

        Assert.Equal(ManifestAdmissionDisposition.Granted, admission.Disposition);
        Assert.DoesNotContain("ResourceUseRequirements", canonical, StringComparison.Ordinal);
        Assert.Equal(VNextMigrationDisposition.OrdinaryFallback, decision.Disposition);
        Assert.True(decision.IsSuccess);
    }

    [Fact]
    public void NewOptionalCallerWithOldProviderFallsBackWithoutProviderSubmission()
    {
        var gates = EnabledGates();
        var lease = gates.TryAcquire(Gate).Value!;
        var provider = new HostPlatformAuthorityProvider(enableResourceAccounting: false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(170),
            new ProcessHandle(new ProcessId(171), 1));
        var domain = bridge.BindDomain(subject).Value!;
        var decision = VNextMigrationCoordinator.Evaluate(gates,
            Request(VNextResourceMigrationRequirement.OptionalTransportOptimization, 0, lease));

        Assert.Equal(VNextMigrationDisposition.OrdinaryFallback, decision.Disposition);
        Assert.True(decision.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, bridge.PrepareResource(domain, subject,
            new(new(1), new(1)), new(ResourceEnvelopeV1.CurrentVersion,
                ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds, 1, 0, "host:compute-v1")).Error);
    }

    [Fact]
    public void ResourceAwareManifestIsAdditiveAndOldManifestAdmissionRemainsIndependent()
    {
        var image = new byte[] { 17, 2, 0 };
        var requirement = new SipResourceRequirementV1(1, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, 10, "host:compute-v1",
            ResourceAssuranceV1.RuntimeEnforced, SipResourceDonationPolicyV1.None);
        var resourceAware = new ServiceManifestV1(new("p17-new"), new("2"),
            Convert.ToHexStringLower(SHA256.HashData(image)), TestFixtures.Manifest(1718, 2718),
            resourceUseRequirements: [requirement]);
        var kernel = new RuntimeKernel();

        var admission = kernel.EvaluateComponentAdmission(new ComponentAdmissionPlan(resourceAware, image));

        Assert.Equal(ManifestAdmissionDisposition.Granted, admission.Disposition);
        Assert.Single(admission.Decisions, decision =>
            decision.Kind == ManifestRequirementKind.ResourceUse &&
            decision.Disposition == ManifestRequirementDisposition.Requested);
        Assert.Contains("ResourceUseRequirements",
            Encoding.UTF8.GetString(resourceAware.SerializeCanonical()), StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredResourceCallerCannotBeDowngradedToOldOrUnknownProvider()
    {
        var gates = EnabledGates();
        var lease = gates.TryAcquire(Gate).Value!;

        var oldProvider = VNextMigrationCoordinator.Evaluate(gates,
            Request(VNextResourceMigrationRequirement.RequiredForSemanticExecution, 0, lease));
        var unknownProvider = VNextMigrationCoordinator.Evaluate(gates,
            Request(VNextResourceMigrationRequirement.RequiredForSemanticExecution, 2, lease));

        Assert.Equal(VNextMigrationDisposition.Denied, oldProvider.Disposition);
        Assert.Equal(KernelError.PlatformUnsupported, oldProvider.Error);
        Assert.Equal(VNextMigrationDisposition.Denied, unknownProvider.Disposition);
        Assert.Equal(KernelError.PlatformUnsupported, unknownProvider.Error);
    }

    [Fact]
    public void ExactNewCallerProviderAndGateGenerationSelectResourcePath()
    {
        var gates = EnabledGates();
        var lease = gates.TryAcquire(Gate).Value!;

        var decision = VNextMigrationCoordinator.Evaluate(gates,
            Request(VNextResourceMigrationRequirement.RequiredForSemanticExecution,
                PlatformResourceContract.ContractVersion, lease));

        Assert.Equal(VNextMigrationDisposition.ResourcePath, decision.Disposition);
        Assert.True(decision.IsSuccess);
    }

    [Fact]
    public void RollbackBeforeSubmitUsesOrdinaryPathButAfterPossibleSubmitQuarantines()
    {
        var gates = EnabledGates();
        var lease = gates.TryAcquire(Gate).Value!;
        Assert.True(gates.Apply(2, VNextFeatureGateConfiguration.DefaultOff(3)).IsSuccess);

        var before = VNextMigrationCoordinator.Evaluate(gates,
            Request(VNextResourceMigrationRequirement.OptionalTransportOptimization,
                PlatformResourceContract.ContractVersion, lease));
        var after = VNextMigrationCoordinator.Evaluate(gates,
            Request(VNextResourceMigrationRequirement.OptionalTransportOptimization,
                PlatformResourceContract.ContractVersion, lease) with { PossibleSubmit = true });

        Assert.Equal(VNextMigrationDisposition.OrdinaryFallback, before.Disposition);
        Assert.Equal(VNextMigrationDisposition.Quarantined, after.Disposition);
        Assert.Equal(KernelError.Quarantined, after.Error);
    }

    [Fact]
    public void MixedVersionMatrixNeverGuessesOrTransfersProof()
    {
        var gates = EnabledGates();
        var lease = gates.TryAcquire(Gate).Value!;
        var cases = new[]
        {
            (new VNextMigrationRequest(1, VNextCallerContract.LegacySipV1,
                VNextResourceMigrationRequirement.None, 0, null, false), VNextMigrationDisposition.OrdinaryFallback),
            (Request(VNextResourceMigrationRequirement.OptionalTransportOptimization, 0, null), VNextMigrationDisposition.OrdinaryFallback),
            (Request(VNextResourceMigrationRequirement.RequiredForSemanticExecution, 0, null), VNextMigrationDisposition.Denied),
            (Request(VNextResourceMigrationRequirement.OptionalTransportOptimization, 1, lease), VNextMigrationDisposition.ResourcePath),
            (Request(VNextResourceMigrationRequirement.RequiredForSemanticExecution, 1, lease), VNextMigrationDisposition.ResourcePath),
            (Request(VNextResourceMigrationRequirement.OptionalTransportOptimization, 99, lease), VNextMigrationDisposition.Denied),
        };

        Assert.All(cases, item => Assert.Equal(item.Item2,
            VNextMigrationCoordinator.Evaluate(gates, item.Item1).Disposition));
        Assert.Equal(VNextMigrationDisposition.Denied,
            VNextMigrationCoordinator.Evaluate(gates, Request(
                VNextResourceMigrationRequirement.OptionalTransportOptimization, 1, lease) with
            { Version = 99 }).Disposition);
    }

    [Fact]
    public void OldManifestCannotSmuggleUnsupportedResourceFeature()
    {
        var decision = VNextMigrationCoordinator.Evaluate(new VNextFeatureGateAuthority(),
            new(VNextMigrationRequest.CurrentVersion, VNextCallerContract.LegacySipV1,
                VNextResourceMigrationRequirement.OptionalTransportOptimization, 1, null, false));

        Assert.Equal(VNextMigrationDisposition.Denied, decision.Disposition);
        Assert.Equal(KernelError.InvalidMessage, decision.Error);
    }

    [Fact]
    public void CleanupProofRequiresEmptyExactGenerationAndIsInvalidatedByNewConsumer()
    {
        var registry = new VNextCompatibilityUsageRegistry();
        var legacy = registry.Register(new(1), VNextCompatibilityConsumerKind.LegacyManifest, 1).Value!;
        var observed = registry.Generation;

        Assert.Equal(KernelError.InvalidTransition,
            registry.TryProveNoLiveConsumers("ordinary-sip-v1", observed).Error);
        Assert.True(registry.Release(legacy).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            registry.TryProveNoLiveConsumers("ordinary-sip-v1", observed).Error);

        var proof = registry.TryProveNoLiveConsumers("ordinary-sip-v1", registry.Generation).Value!;
        Assert.True(registry.IsCurrent(proof));
        Assert.True(registry.Register(new(2), VNextCompatibilityConsumerKind.LegacyGeneratedSip, 1).IsSuccess);
        Assert.False(registry.IsCurrent(proof));
    }

    [Fact]
    public void CleanupRegistryGenerationExhaustionFailsWithoutPartialMutation()
    {
        var registry = new VNextCompatibilityUsageRegistry();
        var generation = typeof(VNextCompatibilityUsageRegistry).GetField("_generation",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        generation.SetValue(registry, ulong.MaxValue - 1);
        var live = registry.Register(new(99), VNextCompatibilityConsumerKind.LegacyProvider, 1).Value!;

        var release = registry.Release(live);

        Assert.Equal(KernelError.CapacityExhausted, release.Error);
        Assert.Equal(ulong.MaxValue, registry.Generation);
        Assert.Equal(KernelError.InvalidTransition,
            registry.TryProveNoLiveConsumers("ordinary-provider-v0", ulong.MaxValue).Error);
        Assert.Equal(KernelError.DuplicateIdentity,
            registry.Register(new(99), VNextCompatibilityConsumerKind.LegacyProvider, 1).Error);

        var emptyRegistry = new VNextCompatibilityUsageRegistry();
        generation.SetValue(emptyRegistry, ulong.MaxValue);
        Assert.Equal(KernelError.CapacityExhausted,
            emptyRegistry.Register(new(100), VNextCompatibilityConsumerKind.LegacyProvider, 1).Error);
        Assert.True(emptyRegistry.TryProveNoLiveConsumers(
            "ordinary-provider-v0", ulong.MaxValue).IsSuccess);
    }

    [Fact]
    public void CleanupProofRejectsMalformedDigestWithoutThrowing()
    {
        var registry = new VNextCompatibilityUsageRegistry();
        var proof = registry.TryProveNoLiveConsumers("ordinary-sip-v1", registry.Generation).Value!;

        Assert.False(registry.IsCurrent(proof with { SnapshotSha256 = null! }));
        Assert.False(registry.IsCurrent(proof with { SnapshotSha256 = "00" }));
        Assert.False(registry.IsCurrent(proof with { SnapshotSha256 = proof.SnapshotSha256.ToUpperInvariant() }));
    }

    [Fact]
    public async Task CleanupScanRacingRegistrationHasNoFalseNoConsumerWinner()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var registry = new VNextCompatibilityUsageRegistry();
            var generation = registry.Generation;
            using var start = new ManualResetEventSlim(false);
            var proofTask = Task.Run(() =>
            {
                start.Wait();
                return registry.TryProveNoLiveConsumers("ordinary-provider-v0", generation);
            });
            var registerTask = Task.Run(() =>
            {
                start.Wait();
                return registry.Register(new((ulong)iteration + 1),
                    VNextCompatibilityConsumerKind.LegacyProvider, 1);
            });
            start.Set();
            var proof = await proofTask;
            Assert.True((await registerTask).IsSuccess);
            if (proof.IsSuccess) Assert.False(registry.IsCurrent(proof.Value!));
            else Assert.Equal(KernelError.StaleGeneration, proof.Error);
        }
    }

    [Fact]
    public void MigrationAndCleanupMetadataCannotMutateAuthorityOwners()
    {
        var types = new[] { typeof(VNextMigrationCoordinator), typeof(VNextCompatibilityUsageRegistry) };
        var forbidden = new[] { "Mint", "Reserve", "Settle", "Publish", "Transfer", "Reclaim", "Submit" };
        Assert.All(types.SelectMany(type => type.GetMethods(
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Static |
                global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic)),
            method => Assert.DoesNotContain(forbidden,
                name => method.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static VNextMigrationRequest Request(
        VNextResourceMigrationRequirement requirement,
        uint providerVersion,
        VNextFeatureGateLease? lease) =>
        new(VNextMigrationRequest.CurrentVersion, VNextCallerContract.ResourceAwareSipV2,
            requirement, providerVersion, lease, false);

    private static VNextFeatureGateAuthority EnabledGates()
    {
        var authority = new VNextFeatureGateAuthority();
        var configuration = new VNextFeatureGateConfiguration(
            VNextFeatureGateConfiguration.CurrentVersion, 2,
            VNextFeatureGateAuthority.QualifiedHostContour, "host-test",
            PlatformResourceContract.ContractVersion, "Windows-x64/.NET-11/JIT", Evidence, [Gate]);
        Assert.True(authority.Apply(1, configuration).IsSuccess);
        return authority;
    }

    private static ServiceManifestV1 Manifest(byte[] image) => new(
        new("p17-legacy"), new("1"),
        Convert.ToHexStringLower(SHA256.HashData(image)), TestFixtures.Manifest(1717, 2717));
}
