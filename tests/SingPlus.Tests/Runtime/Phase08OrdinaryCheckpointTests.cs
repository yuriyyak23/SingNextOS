using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Platform.Host;
using SingPlus.Platform;

namespace SingPlus.Tests.Runtime;

public sealed class Phase08OrdinaryCheckpointTests
{
    private static readonly byte[] Image = [0x08, 0x88];

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedCheckpointRefundRetainsExactStoragePinWhenBudgetOwnerRefuses(bool reconcile)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 895, 8905);
        var component = kernel.AdmitComponent(Plan("checkpoint-refund-refusal", 805, 8005, 1)).Value!;
        CheckpointInspectionRecord original = default!;
        kernel.BeforeCheckpointPublicationQualificationHook = () =>
        {
            original = Assert.Single(kernel.CheckpointInspectionSnapshot());
            Assert.True(kernel.Budgets.QuarantineLease(component.Process, original.StorageReservation).IsSuccess);
            if (reconcile) Assert.True(kernel.Budgets.ReconcileLease(component.Process, original.StorageReservation).IsSuccess);
            Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
        };

        var failed = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]);
        Assert.Equal(KernelError.CapabilityRevoked, failed.Error);
        var retained = Assert.Single(kernel.CheckpointInspectionSnapshot());
        Assert.Equal(original.StorageReservation, retained.StorageReservation);
        Assert.Equal(original.Handle, retained.Handle);
        Assert.Equal(CheckpointLifecycleState.Failed, retained.State);
        Assert.Equal(reconcile ? BudgetReservationState.Reconciled : BudgetReservationState.Quarantined,
            kernel.Budgets.Query(retained.StorageReservation).Value!.State);
        Assert.Equal(8UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        var fresh = kernel.MintCapability(new(8905), admin.Process, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.Equal(KernelError.InvalidTransition,
            kernel.DeleteOrdinaryCheckpoint(admin.Process, fresh, retained.Handle).Error);
        Assert.Equal(retained, Assert.Single(kernel.CheckpointInspectionSnapshot()));
        Assert.Equal(8UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointChargesAndSnapshotsOneSelectionWithFreshAdministration(bool revoke)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 899, 8909);
        var component = kernel.AdmitComponent(Plan("checkpoint-input-tuple", 809, 8009, 1)).Value!;
        var buffer = kernel.AllocateBuffer<byte>(component.Process, 16).Value!;
        byte[] logical = [1, 2];
        var reads = 0;
        var selected = new ChangingSelection(() =>
        {
            reads++;
            logical[0] = 9;
            if (revoke) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            return reads == 1 ? [new CheckpointBufferSource(buffer)] : [];
        });
        var result = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability,
            component.Identity, logical, selected);
        Assert.Equal(1, reads);
        if (revoke)
        {
            Assert.Equal(KernelError.CapabilityRevoked, result.Error);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
            Assert.Empty(kernel.CheckpointInspectionSnapshot());
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Single(result.Value!.Regions);
            Assert.Equal(new byte[] { 1, 2 }, result.Value.LogicalState);
            Assert.Equal(18UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
            Assert.True(kernel.DeleteOrdinaryCheckpoint(admin.Process, admin.Capability, result.Value.Handle).IsSuccess);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementCapturesSelectionAndRefusesRevokedAdministrationBeforeDrain(bool revoke)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 897, 8907);
        var original = kernel.AdmitComponent(Plan("replacement-input", 807, 8007, 1)).Value!;
        var buffer = kernel.AllocateBuffer<byte>(original.Process, 4).Value!;
        buffer.Span.Fill(7);
        byte[] logical = [1, 2];
        var reads = 0;
        var selection = new ChangingSelection(() =>
        {
            reads++;
            logical[0] = 9;
            if (revoke) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            return reads == 1 ? [new(buffer)] : [];
        });
        var restored = kernel.CheckpointDrainAndRestore(admin.Process, admin.Capability,
            original.Identity, logical, selection, Plan("replacement-input", 807, 8007, 2));
        Assert.Equal(1, reads);
        if (revoke)
        {
            Assert.Equal(KernelError.CapabilityRevoked, restored.Error);
            Assert.Equal(original, kernel.QueryComponent(original.Identity).Value!);
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, buffer.Span.ToArray());
            Assert.Empty(kernel.CheckpointInspectionSnapshot());
            Assert.Equal(0UL, Usage(kernel.QueryBudget(original.ProcessBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        }
        else
        {
            Assert.True(restored.IsSuccess, restored.Message);
            Assert.Equal(new byte[] { 1, 2 }, restored.Value!.Receipt.LogicalState);
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, Assert.Single(restored.Value.RestoredBuffers).Span.ToArray());
            Assert.Equal(6UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
            Assert.True(kernel.DeleteOrdinaryCheckpoint(admin.Process, admin.Capability, restored.Value.Receipt.Checkpoint).IsSuccess);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalCheckpointPublicationRefusesRevokedAdministrationOrRetiredSourceAndRefunds(bool retireSource)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 896, 8906);
        var component = kernel.AdmitComponent(Plan("checkpoint-final-admission", 806, 8006, 1)).Value!;
        kernel.BeforeCheckpointPublicationQualificationHook = () =>
        {
            Assert.Equal(8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
            if (retireSource) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
            else Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
        };
        var result = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]);
        Assert.False(result.IsSuccess);
        if (!retireSource) Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Empty(kernel.CheckpointInspectionSnapshot());
        Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        var failed = kernel.QueryOrdinaryCheckpoint(component.Process, new(new(1), new(1)));
        if (!retireSource) Assert.Equal(CheckpointLifecycleState.Failed, failed.Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreUsesCapturedImageAndRechecksAdministrationAfterEnumeration(bool revoke)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 895, 8905);
        var component = kernel.AdmitComponent(Plan("restore-input", 805, 8005, 1)).Value!;
        var buffer = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        buffer.Span.Fill(7);
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability,
            component.Identity, new byte[] { 1, 2 }, [new(buffer)]).Value!;
        var drained = kernel.DrainComponent(component.Identity).Value!;
        Assert.True(drained.Reclaimable);
        var originalRegions = image.Regions.ToArray();
        var reads = 0;
        var supplied = image with { Regions = new ChangingImageRegions(() =>
        {
            reads++;
            image.LogicalState[0] = 9;
            if (revoke) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            return reads == 1 ? originalRegions : [];
        }) };
        var restored = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability,
            supplied, Plan("restore-input", 805, 8005, 2));
        Assert.Equal(1, reads);
        if (revoke)
        {
            Assert.Equal(KernelError.CapabilityRevoked, restored.Error);
            Assert.Equal(drained, kernel.QueryComponent(component.Identity).Value!);
        }
        else
        {
            Assert.True(restored.IsSuccess, restored.Message);
            Assert.Equal(new byte[] { 1, 2 }, restored.Value!.Receipt.LogicalState);
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, Assert.Single(restored.Value.RestoredBuffers).Span.ToArray());
        }
        Assert.Equal(6UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    private sealed class ChangingImageRegions(Func<CheckpointRegionImage[]> read) : IReadOnlyList<CheckpointRegionImage>
    {
        public int Count => 1;
        public CheckpointRegionImage this[int index] => throw new NotSupportedException();
        public IEnumerator<CheckpointRegionImage> GetEnumerator() => ((IEnumerable<CheckpointRegionImage>)read()).GetEnumerator();
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteFinalAdmissionRetainsImageAndChargeUntilAuthorizedConsumer(bool terminateAdmin)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 894, 8904);
        var cleanup = Admin(kernel, 893, 8903);
        var component = kernel.AdmitComponent(Plan("delete-final", 804, 8004, 1)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        kernel.BeforeCheckpointDeleteQualificationHook = () =>
        {
            if (terminateAdmin) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
        };
        var refused = kernel.DeleteOrdinaryCheckpoint(admin.Process, admin.Capability, image.Handle);
        Assert.False(refused.IsSuccess);
        if (!terminateAdmin) Assert.Equal(KernelError.CapabilityRevoked, refused.Error);
        Assert.Equal(8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        Assert.Equal(CheckpointLifecycleState.Committed, kernel.QueryOrdinaryCheckpoint(component.Process, image.Handle).Value!.State);
        kernel.BeforeCheckpointDeleteQualificationHook = null;
        Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        Assert.Empty(kernel.CheckpointInspectionSnapshot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RestoreRetirementRequiresCurrentAdministrationAndCommittedCheckpoint(int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 892, 8902);
        var cleanup = Admin(kernel, 891, 8901);
        var component = kernel.AdmitComponent(Plan("restore-retire", 802, 8002, 1)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        var drained = kernel.DrainComponent(component.Identity).Value!;
        Assert.True(drained.Reclaimable);
        kernel.BeforeCheckpointRestoreRetirementQualificationHook = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        };
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability,
            image, Plan("restore-retire", 802, 8002, 2));
        Assert.False(refused.IsSuccess);
        Assert.Equal(drained, kernel.QueryComponent(component.Identity).Value!);
        Assert.True(kernel.QueryBudget(component.ProcessBudget).IsSuccess);
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        if (fault == 0) Assert.Equal(KernelError.CapabilityRevoked, refused.Error);
        if (fault == 2) Assert.Equal(KernelError.CheckpointInvalid, refused.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RestoreAllocationRefusesLateSourceChangeWithoutNewComponentOrProcess(int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 890, 8900);
        var cleanup = Admin(kernel, 889, 8899);
        var component = kernel.AdmitComponent(Plan("restore-allocation", 800, 8000, 1)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        kernel.BeforeComponentAdmissionCommitQualificationHook = () =>
        {
            Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(component.Identity).Error);
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        };
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability,
            image, Plan("restore-allocation", 800, 8000, 2));
        Assert.False(refused.IsSuccess);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(component.Identity).Error);
        Assert.False(kernel.Processes.Resolve(new(component.Process.ProcessId, 2)).IsSuccess);
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        if (fault == 0) Assert.Equal(KernelError.CapabilityRevoked, refused.Error);
        if (fault == 2) Assert.Equal(KernelError.CheckpointInvalid, refused.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RestoreCreationRefusalCleansOnlyUncreatedHierarchyAndPreservesForeignIncarnation(int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 888, 8898);
        var cleanup = Admin(kernel, 887, 8897);
        var component = kernel.AdmitComponent(Plan("restore-create", 798, 7998, 1)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        var next = Plan("restore-create", 798, 7998, 2);
        ComponentLifecycleSnapshot? allocated = null;
        kernel.BeforeComponentProcessCreationQualificationHook = () =>
        {
            allocated = kernel.QueryComponent(component.Identity).Value!;
            Assert.True(kernel.QueryBudget(allocated.ProcessBudget).IsSuccess);
            if (fault == 3) Assert.True(kernel.CreateProcess(next.Manifest.Process).IsSuccess);
            if (fault is 0 or 3) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        };
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image, next);
        Assert.False(refused.IsSuccess);
        Assert.NotNull(allocated);
        if (fault == 3)
        {
            Assert.True(kernel.Processes.Resolve(allocated.Process).IsSuccess);
            Assert.Equal(ComponentLifecycleState.Faulted, kernel.QueryComponent(component.Identity).Value!.State);
            Assert.True(kernel.QueryBudget(allocated.ProcessBudget).IsSuccess);
        }
        else
        {
            Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(component.Identity).Error);
            Assert.False(kernel.Processes.Resolve(allocated.Process).IsSuccess);
            Assert.False(kernel.QueryBudget(allocated.ProcessBudget).IsSuccess);
        }
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void RestoreCreatedAndGrantCommitRefusalUsesActualTeardownWithoutUsableAuthority(bool atGrant, int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 886, 8896);
        var cleanup = Admin(kernel, 885, 8895);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "restore-grant", CapabilityRights.Read);
        var component = kernel.AdmitComponent(Plan("restore-created-grant", 796, 7996, 1, requirement)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        var next = Plan("restore-created-grant", 796, 7996, 2, requirement);
        Action interfere = () =>
        {
            var fresh = kernel.QueryComponent(component.Identity).Value!;
            Assert.Empty(fresh.Capabilities);
            Assert.True(kernel.Processes.Resolve(fresh.Process).IsSuccess);
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        };
        if (atGrant) kernel.BeforeComponentGrantMintQualificationHook = interfere;
        else kernel.BeforeComponentCreatedPublicationQualificationHook = interfere;
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image, next);
        Assert.False(refused.IsSuccess);
        var failed = kernel.QueryComponent(component.Identity).Value!;
        Assert.True(failed.Reclaimable);
        Assert.Empty(failed.Capabilities);
        Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RestoreProcessAdmissionChecksSourceAndRequiredGrantIndependentlyAndReclaimsIssuedFacts(int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 884, 8894);
        var cleanup = Admin(kernel, 883, 8893);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "restore-admit", CapabilityRights.Read);
        var component = kernel.AdmitComponent(Plan("restore-admit", 794, 7994, 1, requirement)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        CapabilityId issued = default;
        kernel.BeforeComponentProcessAdmissionQualificationHook = () =>
        {
            var fresh = kernel.QueryComponent(component.Identity).Value!;
            issued = Assert.Single(fresh.Capabilities);
            Assert.True(kernel.ValidateCapability(fresh.Process, issued, CapabilityRights.Read).IsSuccess);
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else if (fault == 2) Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
            else Assert.True(kernel.RevokeCapability(issued).IsSuccess);
        };
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability,
            image, Plan("restore-admit", 794, 7994, 2, requirement));
        Assert.False(refused.IsSuccess);
        var failed = kernel.QueryComponent(component.Identity).Value!;
        Assert.True(failed.Reclaimable);
        Assert.Empty(failed.Capabilities);
        Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        Assert.False(kernel.CapabilityAuthority.Validate(issued, new(7994), 2, CapabilityRights.Read).IsSuccess);
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void RestorePlatformBindingChecksSourceAndClosesLateExactReceipt(bool late, int fault)
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var admin = Admin(kernel, 882, 8892);
        var cleanup = Admin(kernel, 881, 8891);
        var component = kernel.AdmitComponent(Plan("restore-binding", 792, 7992, 1)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        var next = Plan("restore-binding", 792, 7992, 2, platform:
            [new(ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion,
                ComponentPlatformFeatureAvailability.RuntimeAdmission)], requirePlatformDomain: true);
        Action interfere = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        };
        if (late) kernel.BeforePlatformBindingAttachmentQualificationHook = interfere;
        else kernel.BeforeComponentPlatformBindingQualificationHook = interfere;
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image, next);
        Assert.False(refused.IsSuccess);
        Assert.Equal(late ? 1 : 0, provider.BindDomainCallCount);
        Assert.Equal(late ? 1 : 0, provider.RevokeDomainCallCount);
        var failed = kernel.QueryComponent(component.Identity).Value!;
        Assert.True(failed.Reclaimable);
        Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(failed.Identity, failed.Process).IsSuccess);
        Assert.False(kernel.Budgets.HasProcessAccount(failed.Process));
        Assert.Equal(late ? 1 : 0, provider.RevokeDomainCallCount);
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RestoreServiceRegistrationUsesCurrentCheckpointSourceAndExactNewProvider(int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 880, 8890);
        var cleanup = Admin(kernel, 879, 8889);
        var component = kernel.AdmitComponent(Plan("restore-service", 790, 7990, 1)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        var baseline = Plan("restore-service", 790, 7990, 2);
        var contract = new ServiceContractIdentity("RestoreService", "1", "restore-service-digest");
        var provided = new ProvidedServiceManifestV1("restore-service-endpoint", contract);
        var protocol = new ProtocolDefinitionV1(contract.Name, contract.Digest, "Idle", ["Done"],
            [new ProtocolMessageDescriptorV1(1, "Invoke")], [new ProtocolTransitionV1(1, "Idle", "Done")]);
        var manifest = new ServiceManifestV1(baseline.Manifest.Identity, baseline.Manifest.Version,
            baseline.Manifest.ImageDigest, baseline.Manifest.Process, [provided],
            budgetRequests: baseline.Manifest.BudgetRequests, checkpointPolicy: baseline.Manifest.CheckpointPolicy);
        var next = new ComponentAdmissionPlan(manifest, Image,
            providedServices: [new ComponentProvidedServiceRegistration(provided, protocol)]);
        kernel.BeforeComponentServiceRegistrationQualificationHook = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else if (fault == 2) Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        };
        var restored = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image, next);
        if (fault == 3)
        {
            Assert.True(restored.IsSuccess, restored.Message);
            var endpoint = kernel.ResolveByServiceName(provided.ServiceName).Value!;
            Assert.Equal(restored.Value!.Component.Process, kernel.Services.Resolve(endpoint).Value!.Provider);
        }
        else
        {
            Assert.False(restored.IsSuccess);
            Assert.Equal(KernelError.ServiceNotFound, kernel.ResolveByServiceName(provided.ServiceName).Error);
            var failed = kernel.QueryComponent(component.Identity).Value!;
            Assert.True(failed.Reclaimable);
            Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        }
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    public void RestoreDependencySessionChecksSourceServiceAndGrantWithoutOptionalSourceDowngrade(int fault, bool optional)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 878, 8888);
        var cleanup = Admin(kernel, 877, 8887);
        var provider = TestFixtures.Create(kernel, 876, 8886).Handle;
        Assert.True(kernel.AdmitProcess(provider).IsSuccess);
        var contract = new ServiceContractIdentity("RestoreDependency", "1", "restore-dependency-digest");
        var protocol = new ProtocolDefinitionV1(contract.Name, contract.Digest, "Idle", ["Done"],
            [new ProtocolMessageDescriptorV1(1, "Invoke")], [new ProtocolTransitionV1(1, "Idle", "Done")]);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "restore-session", CapabilityRights.Read);
        var endpoint = kernel.RegisterService(provider, "restore-dependency", contract, protocol,
            requiredCapabilities: [requirement]).Value!;
        var component = kernel.AdmitComponent(Plan("restore-session", 788, 7988, 1, requirement)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        var baseline = Plan("restore-session", 788, 7988, 2, requirement);
        var manifest = new ServiceManifestV1(baseline.Manifest.Identity, baseline.Manifest.Version,
            baseline.Manifest.ImageDigest, baseline.Manifest.Process,
            dependencies: [new(contract, optional ? ServiceDependencyKind.Optional : ServiceDependencyKind.Hard)],
            budgetRequests: baseline.Manifest.BudgetRequests, checkpointPolicy: baseline.Manifest.CheckpointPolicy);
        var next = new ComponentAdmissionPlan(manifest, Image, baseline.Grants);
        kernel.BeforeComponentDependencySessionQualificationHook = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else if (fault == 2) Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
            else if (fault == 3) Assert.True(kernel.RevokeCapability(Assert.Single(kernel.QueryComponent(component.Identity).Value!.Capabilities)).IsSuccess);
            else if (fault == 4) Assert.True(kernel.SetServiceAvailability(endpoint.Service, ServiceAvailability.Unavailable).IsSuccess);
        };
        var restored = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image, next);
        if (fault == 5)
        {
            Assert.True(restored.IsSuccess, restored.Message);
            Assert.Single(restored.Value!.Component.Sessions);
            Assert.Single(kernel.Processes.Resolve(provider).Value!.Channels);
        }
        else
        {
            Assert.False(restored.IsSuccess);
            var failed = kernel.QueryComponent(component.Identity).Value!;
            Assert.True(failed.Reclaimable);
            Assert.Empty(failed.Sessions);
            Assert.Empty(kernel.Processes.Resolve(provider).Value!.Channels);
            Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        }
        Assert.Equal(fault == 2 ? 0UL : 8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    private sealed class ChangingSelection(Func<CheckpointBufferSource[]> read) : IReadOnlyList<CheckpointBufferSource>
    {
        public int Count => 1;
        public CheckpointBufferSource this[int index] => throw new NotSupportedException();
        public IEnumerator<CheckpointBufferSource> GetEnumerator() => ((IEnumerable<CheckpointBufferSource>)read()).GetEnumerator();
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointStorageCannotBeRefundedByObservedHandleBeforeActualDelete(bool retireSource)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 898, 8908);
        var component = kernel.AdmitComponent(Plan("checkpoint-release-provenance", 808, 8008, 1)).Value!;
        var checkpoint = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]);
        Assert.True(checkpoint.IsSuccess, checkpoint.Message);
        var reservation = Assert.Single(kernel.Budgets.InspectionSnapshot(), r =>
            r.Owner == component.Process && r.Lifetime == BudgetReservationLifetime.CheckpointImage).Reservation;
        Assert.Equal(KernelError.InvalidTransition, kernel.ReleaseBudget(component.Process, reservation).Error);
        Assert.Equal(8UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        if (retireSource) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
        Assert.Equal(8UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        var stale = reservation with { Generation = new(reservation.Generation.Value + 1) };
        Assert.Equal(BudgetReservationState.Stale, kernel.ReleaseBudget(component.Process, stale).Value!.State);
        Assert.Equal(KernelError.WrongRegionOwner, kernel.ReleaseBudget(component.Process with { Generation = 2 }, reservation).Error);
        Assert.True(kernel.DeleteOrdinaryCheckpoint(admin.Process, admin.Capability, checkpoint.Value!.Handle).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
        Assert.True(kernel.DeleteOrdinaryCheckpoint(admin.Process, admin.Capability, checkpoint.Value!.Handle).IsSuccess);
    }

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

    [Fact]
    public void LiveResourceLeaseBlocksCheckpointAndOldHandleCannotBindAfterFreshRestore()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 897, 8907);
        var component = kernel.AdmitComponent(Plan("resource-checkpoint", 807, 8007, 1)).Value!;
        var lease = kernel.Budgets.Reserve(component.Process,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 5)],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Value!.Reservation;

        var blocked = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability,
            component.Identity, new byte[] { 1 });
        Assert.Equal(KernelError.CheckpointBlocked, blocked.Error);
        var failed = kernel.QueryOrdinaryCheckpoint(admin.Process, new(new(1), new(1)),
            admin.Capability).Value!;
        Assert.Contains(failed.Resources, item => item.Kind == CheckpointResourceKind.Budget &&
            item.Classification == CheckpointResourceClassification.NonCheckpointable &&
            item.Correlation.Contains($":{lease.Generation.Value}", StringComparison.Ordinal));

        Assert.True(kernel.Budgets.CancelLeasePreSubmit(component.Process, lease).IsSuccess);
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability,
            component.Identity, new byte[] { 2 }).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        var restored = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image,
            Plan("resource-checkpoint", 807, 8007, 2)).Value!;

        Assert.False(kernel.Budgets.BindLease(restored.Receipt.RestoredProcess, lease).IsSuccess);
        Assert.False(restored.Receipt.ReusedCapability);
        Assert.False(restored.Receipt.ReusedExternalAuthority);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void RestoreStartupSourceRefusalUsesActualTeardown(int boundary, int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 874, 8884);
        var cleanup = Admin(kernel, 873, 8883);
        var component = kernel.AdmitComponent(Plan("restore-start", 784, 7984, 1)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        Action interfere = () =>
        {
            var fresh = kernel.QueryComponent(component.Identity).Value!;
            Assert.Equal(boundary == 2 ? ProcessState.Running : ProcessState.Admitted,
                kernel.Processes.Resolve(fresh.Process).Value!.State);
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
        };
        if (boundary == 0) kernel.BeforeComponentStartingPublicationQualificationHook = interfere;
        else if (boundary == 1) kernel.BeforeComponentExecutionStartQualificationHook = interfere;
        else kernel.BeforeComponentRunningPublicationQualificationHook = interfere;
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image,
            Plan("restore-start", 784, 7984, 2));
        Assert.False(refused.IsSuccess);
        var failed = kernel.QueryComponent(component.Identity).Value!;
        Assert.True(failed.Reclaimable);
        Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        Assert.Equal(fault == 2 ? 0UL : 8UL,
            Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RestoreReceiptRefusalReclaimsExactAllocatedTargetWithoutReturningBuffers(int fault)
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 872, 8882);
        var cleanup = Admin(kernel, 871, 8881);
        var component = kernel.AdmitComponent(Plan("restore-receipt", 782, 7982, 1)).Value!;
        var original = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        original.Span.Fill(7);
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity,
            new byte[8], [new(original)]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        ProcessHandle target = default;
        kernel.BeforeCheckpointRestoreReceiptQualificationHook = () =>
        {
            target = kernel.QueryComponent(component.Identity).Value!.Process;
            Assert.Single(kernel.Processes.Resolve(target).Value!.Regions);
            if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
            else if (fault == 2) Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
            else Assert.True(kernel.TerminateProcess(target).IsSuccess);
        };
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image,
            Plan("restore-receipt", 782, 7982, 2));
        Assert.False(refused.IsSuccess);
        Assert.Null(refused.Value);
        Assert.True(kernel.QueryComponent(component.Identity).Value!.Reclaimable);
        Assert.True(kernel.Processes.ResolveTerminalState(target).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(fault == 2 ? 0UL : 12UL,
            Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void RestoreRegionSourceChecksBeforeAllocationAndAfterOutsideLockObservation(bool late, int fault)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var admin = Admin(kernel, 870, 8880);
        var cleanup = Admin(kernel, 869, 8879);
        var component = kernel.AdmitComponent(Plan("restore-region", 780, 7980, 1)).Value!;
        var original = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        original.Span.Fill(9);
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.Capability, component.Identity,
            new byte[8], [new(original)]).Value!;
        Assert.True(kernel.DrainComponent(component.Identity).Value!.Reclaimable);
        var calls = 0;
        kernel.BeforeCheckpointRestoreRegionQualificationHook = () =>
        {
            var target = kernel.QueryComponent(component.Identity).Value!.Process;
            Action interfere = () =>
            {
                calls++;
                Assert.Equal(late ? 1 : 0, kernel.Processes.Resolve(target).Value!.Regions.Count);
                if (fault == 0) Assert.True(kernel.RevokeCapability(admin.Capability).IsSuccess);
                else if (fault == 1) Assert.True(kernel.TerminateProcess(admin.Process).IsSuccess);
                else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup.Process, cleanup.Capability, image.Handle).IsSuccess);
            };
            if (!late) interfere();
            else
            {
                Assert.True(kernel.Traces.Start(target, 8, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
                clock.Next = () =>
                {
                    var worker = Task.Run(interfere);
                    Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Observation must run outside owner locks.");
                    worker.GetAwaiter().GetResult();
                };
            }
        };
        var refused = kernel.RestoreOrdinaryCheckpoint(admin.Process, admin.Capability, image,
            Plan("restore-region", 780, 7980, 2));
        Assert.False(refused.IsSuccess);
        Assert.Equal(1, calls);
        Assert.True(kernel.QueryComponent(component.Identity).Value!.Reclaimable);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(fault == 2 ? 0UL : 12UL,
            Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.CheckpointStorageBytes).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicBufferAllocationObservationRetainsFactAndAllowsActualTeardownOutsideLock(bool terminate)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var component = kernel.AdmitComponent(Plan("buffer-observation", 778, 7978, 1)).Value!;
        Assert.True(kernel.Traces.Start(component.Process, 8, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        var calls = 0;
        clock.Next = () =>
        {
            calls++;
            var worker = Task.Run(() =>
            {
                Assert.Single(kernel.Processes.Resolve(component.Process).Value!.Regions);
                if (terminate) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
                else Assert.True(kernel.QueryBudget(component.ProcessBudget).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Budget observation must run outside allocation owner locks.");
            worker.GetAwaiter().GetResult();
        };
        var allocated = kernel.AllocateBuffer<byte>(component.Process, 4);
        Assert.Equal(1, calls);
        Assert.Equal(!terminate, allocated.IsSuccess);
        if (terminate)
        {
            Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            allocated.Value!.Span.Fill(3);
            Assert.Equal(new byte[] { 3, 3, 3, 3 }, allocated.Value.Span.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicRegionAllocationObservationRetainsFactAndAllowsActualTeardownOutsideLock(bool terminate)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var component = kernel.AdmitComponent(Plan("region-observation", 776, 7976, 1)).Value!;
        Assert.True(kernel.Traces.Start(component.Process, 8, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        var calls = 0;
        clock.Next = () =>
        {
            calls++;
            var worker = Task.Run(() =>
            {
                Assert.Single(kernel.Processes.Resolve(component.Process).Value!.Regions);
                if (terminate) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
                else Assert.True(kernel.QueryBudget(component.ProcessBudget).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Budget observation must run outside allocation owner locks.");
            worker.GetAwaiter().GetResult();
        };
        var allocated = kernel.AllocateRegion(component.Process, 123);
        Assert.Equal(1, calls);
        Assert.Equal(!terminate, allocated.IsSuccess);
        if (terminate)
        {
            Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            Assert.Equal(123, allocated.Value!.Value);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DirectTransferObservationSeesCommittedOwnershipAndActualTargetTeardown(bool scalar, bool terminate)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var source = kernel.AdmitComponent(Plan("transfer-source", 774, 7974, 1)).Value!;
        var target = kernel.AdmitComponent(Plan("transfer-target", 773, 7973, 1)).Value!;
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(source.Process, 123).Value! : null;
        Assert.True(kernel.Traces.Start(target.Process, 8, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        var calls = 0;
        clock.Next = () =>
        {
            calls++;
            var worker = Task.Run(() =>
            {
                Assert.Empty(kernel.Processes.Resolve(source.Process).Value!.Regions);
                Assert.Single(kernel.Processes.Resolve(target.Process).Value!.Regions);
                Assert.Equal(0UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
                if (terminate) Assert.True(kernel.TerminateProcess(target.Process).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Transfer observation must run outside owner locks.");
            worker.GetAwaiter().GetResult();
        };
        if (scalar)
        {
            var moved = kernel.TransferRegion(source.Process, target.Process, region!);
            Assert.Equal(!terminate, moved.IsSuccess);
            if (!terminate) Assert.Equal(123, moved.Value!.Value);
            Assert.False(kernel.TransferRegion(source.Process, target.Process, region!).IsSuccess);
        }
        else
        {
            var moved = kernel.TransferRegion(source.Process, target.Process, buffer!);
            Assert.Equal(!terminate, moved.IsSuccess);
            if (!terminate) Assert.Equal(4, moved.Value!.Span.Length);
            Assert.False(kernel.TransferRegion(source.Process, target.Process, buffer!).IsSuccess);
        }
        Assert.Equal(1, calls);
        Assert.Equal(terminate ? 0UL : 4UL,
            Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalAdmissionObservationSeesOperationChargeAndAllowsActualTeardown(bool terminate)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var component = kernel.AdmitComponent(Plan("external-observation", 772, 7972, 1)).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        var preparation = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 4))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        Assert.NotNull(preparation);
        Assert.True(kernel.Traces.Start(component.Process, 8, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        var calls = 0;
        clock.Next = () =>
        {
            calls++;
            var worker = Task.Run(() =>
            {
                Assert.Equal(ExternalOperationState.Admitted,
                    kernel.QueryExternalOperation(component.Process, preparation.Operation).Value!.State);
                Assert.Equal(1UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.ExternalOperations).Used);
                if (terminate) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Admission observation must run outside owner locks.");
            worker.GetAwaiter().GetResult();
        };
        var admitted = kernel.AdmitExternalOperation(component.Process, preparation.Operation, new(1, 1));
        Assert.Equal(1, calls);
        Assert.Equal(!terminate, admitted.IsSuccess);
        Assert.Equal(terminate ? 0UL : 1UL,
            Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.ExternalOperations).Used);
        Assert.False(kernel.AdmitExternalOperation(component.Process, preparation.Operation, new(1, 1)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalAdmissionLifecycleTraceRechecksOwnerAfterCallback(bool terminate)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var component = kernel.AdmitComponent(Plan("external-publication", 770, 7970, 1)).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        var preparation = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 4))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        Assert.NotNull(preparation);
        Assert.True(kernel.Traces.Start(component.Process, 8, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        var calls = 0;
        clock.Next = () =>
        {
            calls++;
            clock.Next = () =>
            {
                calls++;
                var worker = Task.Run(() =>
                {
                    Assert.Equal(ExternalOperationState.Admitted,
                        kernel.QueryExternalOperation(component.Process, preparation.Operation).Value!.State);
                    if (terminate) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
                });
                Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Lifecycle trace must run outside owner locks.");
                worker.GetAwaiter().GetResult();
            };
        };
        var admitted = kernel.AdmitExternalOperation(component.Process, preparation.Operation, new(1, 1));
        Assert.Equal(2, calls);
        Assert.Equal(!terminate, admitted.IsSuccess);
        Assert.Equal(terminate ? 0UL : 1UL,
            Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.ExternalOperations).Used);
        Assert.False(kernel.AdmitExternalOperation(component.Process, preparation.Operation, new(1, 1)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalPreparationTraceRechecksActualProcessBeforeReturningSnapshot(bool terminate)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var component = kernel.AdmitComponent(Plan("external-preparation", 768, 7968, 1)).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        Assert.True(kernel.Traces.Start(component.Process, 8, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        var calls = 0;
        clock.Next = () =>
        {
            calls++;
            var worker = Task.Run(() =>
            {
                if (terminate) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
                else Assert.True(kernel.Processes.Resolve(component.Process).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Preparation trace must run outside owner locks.");
            worker.GetAwaiter().GetResult();
        };
        var prepared = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 4))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged);
        Assert.Equal(1, calls);
        Assert.Equal(!terminate, prepared.IsSuccess);
        if (terminate)
        {
            Assert.Null(prepared.Value);
            Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            Assert.Equal(ExternalOperationState.Prepared,
                kernel.QueryExternalOperation(component.Process, prepared.Value!.Operation).Value!.State);
            Assert.True(kernel.AdmitExternalOperation(component.Process, prepared.Value.Operation, new(1, 1)).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalPreparationCapturesOneInputOutsideGateAndChecksPrincipalBeforeEffect(bool terminate)
    {
        var kernel = new RuntimeKernel();
        var component = kernel.AdmitComponent(Plan("external-capture", 766, 7966, 1)).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        var reads = 0;
        var requests = new ChangingOperationUses(() =>
        {
            reads++;
            var worker = Task.Run(() =>
            {
                if (terminate) Assert.True(kernel.TerminateProcess(component.Process).IsSuccess);
                else Assert.True(kernel.Processes.Resolve(component.Process).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Caller input capture must run outside owner locks.");
            worker.GetAwaiter().GetResult();
            return reads == 1 ? [new(input.Handle, RegionUseMode.ReadOnly, new(0, 4))] : [];
        });
        var prepared = kernel.PrepareExternalOperation(component.Process, requests,
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged);
        Assert.Equal(1, reads);
        Assert.Equal(!terminate, prepared.IsSuccess);
        if (!terminate)
        {
            Assert.Single(prepared.Value!.RegionUses);
            Assert.True(kernel.AdmitExternalOperation(component.Process, prepared.Value.Operation, new(1, 1)).IsSuccess);
        }
        else
        {
            Assert.Null(prepared.Value);
            var fresh = TestFixtures.Create(kernel, 765, 7965).Handle;
            var freshInput = kernel.AllocateBuffer<byte>(fresh, 4).Value!;
            var next = kernel.PrepareExternalOperation(fresh,
                [new(freshInput.Handle, RegionUseMode.ReadOnly, new(0, 4))],
                ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
            Assert.Equal(1UL, next.Operation.OperationId.Value);
        }
    }

    private sealed class ChangingOperationUses(Func<OperationRegionUseRequest[]> read) : IReadOnlyList<OperationRegionUseRequest>
    {
        public int Count => 1;
        public OperationRegionUseRequest this[int index] => throw new InvalidOperationException("Input must be captured by enumeration.");
        public IEnumerator<OperationRegionUseRequest> GetEnumerator() => ((IEnumerable<OperationRegionUseRequest>)read()).GetEnumerator();
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubmissionTraceCancellationPreservesOriginalBindingPinsAndCharge(bool cancel)
    {
        var clock = new RestoreObservationClock();
        var kernel = new RuntimeKernel(null, clock);
        var component = kernel.AdmitComponent(Plan("submission-observation", 764, 7964, 1)).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 4).Value!;
        var preparation = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 4))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        Assert.True(kernel.AdmitExternalOperation(component.Process, preparation.Operation, new(1, 1)).IsSuccess);
        Assert.True(kernel.Traces.Start(component.Process, 16, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        OperationBinding? original = null;
        var calls = 0;
        clock.Next = () =>
        {
            calls++;
            var worker = Task.Run(() =>
            {
                original = kernel.QueryExternalOperation(component.Process, preparation.Operation).Value!.Binding;
                Assert.NotNull(original);
                if (cancel) Assert.True(kernel.CancelExternalOperation(component.Process, preparation.Operation, false).IsSuccess);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Submission observation must run outside owner locks.");
            worker.GetAwaiter().GetResult();
        };
        var submitted = kernel.RecordExternalOperationSubmission(component.Process, preparation.Operation, new(1, 1));
        Assert.Equal(1, calls);
        Assert.Equal(!cancel, submitted.IsSuccess);
        var actual = kernel.QueryExternalOperation(component.Process, preparation.Operation).Value!;
        Assert.Equal(original, actual.Binding);
        Assert.False(kernel.RecordExternalOperationSubmission(component.Process, preparation.Operation, new(1, 1)).IsSuccess);
        Assert.False(kernel.ReleaseRegion(component.Process, input).IsSuccess);
        Assert.Equal(1UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.ExternalOperations).Used);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    private sealed class RestoreObservationClock : TimeProvider
    {
        public Action? Next;
        public override long GetTimestamp()
        {
            var callback = Next;
            Next = null;
            callback?.Invoke();
            return base.GetTimestamp();
        }
    }

    private static ComponentAdmissionPlan Plan(
        string name,
        ulong processId,
        ulong domainId,
        ulong generation,
        CapabilityRequirementV1? capability = null,
        bool includeGrant = true,
        IEnumerable<PlatformRequirementV1>? platform = null,
        ulong checkpointBytes = 4096,
        bool requirePlatformDomain = false)
    {
        var required = capability is { } exact ? new[] { exact } : [];
        var process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry", required);
        var resources = required.Select(ComponentResourceRequirementV1.ForCapability).ToArray();
        if (requirePlatformDomain) resources = [..resources, ComponentResourceRequirementV1.ForPlatformAuthorityDomain()];
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
