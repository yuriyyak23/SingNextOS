using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class Phase08OrdinaryCheckpointTests
{
    private static readonly byte[] Image = [0x08, 0x88];

    [Fact]
    public void PlannedCheckpointReplacementRestoresLogicalMemoryWithFreshAuthority()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 891, 8901);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "ordinary-state", CapabilityRights.Read);
        var originalPlan = Plan("checkpoint-service", 801, 8001, 1, requirement);
        var original = kernel.AdmitComponent(originalPlan).Value!;
        var oldCapability = Assert.Single(original.Capabilities);
        var buffer = kernel.AllocateBuffer<byte>(original.Process, 4).Value!;
        buffer.Span.Fill(7);

        var restored = kernel.CheckpointDrainAndRestore(admin.Process, admin.Capability, original.Identity, new byte[] { 9, 8, 7 },
            [new(buffer)], Plan("checkpoint-service", 801, 8001, 2, requirement));

        Assert.True(restored.IsSuccess, restored.Message);
        Assert.Equal<ulong>(2, restored.Value!.Receipt.RestoredProcess.Generation);
        Assert.Equal(new byte[] { 9, 8, 7 }, restored.Value.Receipt.LogicalState);
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, Assert.Single(restored.Value.RestoredBuffers).Span.ToArray());
        Assert.False(buffer.IsValid);
        Assert.Equal(KernelError.StaleHandle,
            kernel.ValidateCapability(original.Process, oldCapability, CapabilityRights.Read).Error);
        Assert.DoesNotContain(oldCapability, restored.Value.Component.Capabilities);
        Assert.False(restored.Value.Receipt.ReusedCapability);
        Assert.False(restored.Value.Receipt.ReusedExternalAuthority);
        Assert.True(Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used > 0);
        Assert.True(kernel.DeleteOrdinaryCheckpoint(admin.Process, admin.Capability, restored.Value.Receipt.Checkpoint).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Fact]
    public void LiveExternalOperationBlocksThenExactClosureAllowsCheckpoint()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 892, 8902);
        var component = kernel.AdmitComponent(Plan("external-checkpoint", 802, 8002, 1)).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        var output = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        var prepared = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 4)), new(output.Handle, RegionUseMode.StagedOutput, new(0, 4))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(component.Process, prepared.Operation, dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(component.Process, prepared.Operation, dependencies).Value!;

        var blocked = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[] { 1 }, [new(input), new(output)]);
        Assert.Equal(KernelError.CheckpointBlocked, blocked.Error);
        Assert.Equal(KernelError.SupervisorDenied,
            kernel.QueryOrdinaryCheckpoint(admin.Process, new(new(1), new(1))).Error);
        Assert.True(kernel.QueryOrdinaryCheckpoint(component.Process, new(new(1), new(1))).IsSuccess);
        var failed = kernel.QueryOrdinaryCheckpoint(admin.Process, new(new(1), new(1)), admin.Capability).Value!;
        Assert.Equal(CheckpointLifecycleState.Failed, failed.State);
        Assert.Contains(failed.Resources, item => item.Kind == CheckpointResourceKind.ExternalOperation &&
                                                 item.Classification == CheckpointResourceClassification.NonCheckpointable);

        Assert.True(kernel.RecordExternalOperationCompletion(component.Process,
            new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(kernel.ReleaseExternalOperation(component.Process, prepared.Operation, new(true, false)).IsSuccess);
        Assert.True(kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[] { 1 }, [new(input), new(output)]).IsSuccess);
    }

    [Fact]
    public void UnknownOwnedMemoryAndSecureScopeFailClosed()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 893, 8903);
        var component = kernel.AdmitComponent(Plan("unknown-resource", 803, 8003, 1,
            platform: [new(ComponentPlatformFeatureFamily.SecureDomains, 1,
                ComponentPlatformFeatureAvailability.ProjectionOnly, ManifestRequirementCriticality.Optional)])).Value!;
        _ = kernel.AllocateBuffer<byte>(component.Process, 2).Value!;

        var blocked = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[] { 1 }, []);

        Assert.Equal(KernelError.CheckpointBlocked, blocked.Error);
        var snapshot = kernel.QueryOrdinaryCheckpoint(admin.Process, new(new(1), new(1)), admin.Capability).Value!;
        Assert.Contains(snapshot.Resources, item => item.Kind == CheckpointResourceKind.SecureDomain &&
                                                   item.Classification == CheckpointResourceClassification.NonCheckpointable);
        Assert.Contains(snapshot.Resources, item => item.Kind == CheckpointResourceKind.OwnedMemory &&
                                                   item.Classification == CheckpointResourceClassification.NonCheckpointable);
    }

    [Fact]
    public void PartialTamperedAndIncompatibleImagesNeverRestore()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 894, 8904);
        var component = kernel.AdmitComponent(Plan("integrity", 804, 8004, 1)).Value!;
        var wrong = kernel.MintCapability(new(8904), admin.Process, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.Equal(KernelError.SupervisorDenied,
            kernel.CreateOrdinaryCheckpoint(admin.Process, wrong, component.Identity, new byte[] { 1 }).Error);
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[] { 1, 2, 3 }).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);

        var partial = image with { Complete = false, State = CheckpointLifecycleState.Snapshotting };
        Assert.Equal(KernelError.CheckpointInvalid,
            kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, partial, Plan("integrity", 804, 8004, 2)).Error);
        var tampered = image with { LogicalState = new byte[] { 9, 2, 3 } };
        Assert.Equal(KernelError.CheckpointInvalid,
            kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, tampered, Plan("integrity", 804, 8004, 2)).Error);
        Assert.Equal(KernelError.CheckpointIncompatible,
            kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image, Plan("integrity", 804, 8004, 1)).Error);
    }

    [Fact]
    public void FreshCapabilityAdmissionCanFailWithoutReusingSerializedAuthority()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 895, 8905);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "fresh-only", CapabilityRights.Read);
        var component = kernel.AdmitComponent(Plan("fresh-admission", 805, 8005, 1, requirement)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[] { 5 }).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);

        var denied = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image,
            Plan("fresh-admission", 805, 8005, 2, requirement, includeGrant: false));

        Assert.Equal(KernelError.MissingCapability, denied.Error);
        Assert.Equal(KernelError.StaleHandle,
            kernel.ValidateCapability(component.Process, Assert.Single(component.Capabilities), CapabilityRights.Read).Error);
    }

    [Fact]
    public void CheckpointStorageHonorsExactProcessHierarchyLimitAndDoesNotLeakFailedCharge()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 896, 8906);
        var component = kernel.AdmitComponent(
            Plan("checkpoint-child-limit", 806, 8006, 1, checkpointBytes: 8)).Value!;

        var denied = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability,
            component.Identity, new byte[9]);

        Assert.Equal(KernelError.BudgetExceeded, denied.Error);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!,
            ServiceBudgetDimension.CheckpointStorageBytes).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!,
            ServiceBudgetDimension.CheckpointStorageBytes).Used);
        Assert.True(kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability,
            component.Identity, new byte[8]).IsSuccess);
        Assert.Equal(8UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!,
            ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    private static ComponentAdmissionPlan Plan(
        string name,
        ulong processId,
        ulong domainId,
        ulong generation,
        CapabilityRequirementV1? capability = null,
        bool includeGrant = true,
        IEnumerable<PlatformRequirementV1>? platform = null,
        ulong checkpointBytes = 4096)
    {
        var required = capability is { } exact ? new[] { exact } : [];
        var process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry", required);
        var resources = required.Select(ComponentResourceRequirementV1.ForCapability).ToArray();
        var manifest = new ServiceManifestV1(new(name), new("1"), Digest(Image), process,
            platformRequirements: platform,
            resourceRequirements: resources,
            budgetRequests:
            [
                new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.ExternalOperations, 4),
                new(ServiceBudgetDimension.CheckpointStorageBytes, checkpointBytes),
            ],
            checkpointPolicy: new(ServiceCheckpointMode.PlannedReplacementOnly));
        var grants = capability is { } grant && includeGrant
            ? new[] { new ComponentCapabilityGrant(new(domainId), grant) }
            : [];
        return new(manifest, Image, grants);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static BudgetUsage Usage(BudgetAccountSnapshot account, ServiceBudgetDimension dimension) =>
        Assert.Single(account.Usage, usage => usage.Dimension == dimension);

    private static (ProcessHandle Process, CapabilityId Capability) Admin(
        RuntimeKernel kernel, ulong processId, ulong domainId)
    {
        var process = TestFixtures.Create(kernel, processId, domainId).Handle;
        var capability = kernel.MintCapability(new(domainId), process, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        return (process, capability);
    }
}
