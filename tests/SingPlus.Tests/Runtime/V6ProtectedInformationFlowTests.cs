using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class V6ProtectedInformationFlowTests
{
    [Fact]
    public void InvalidatedUnreleasedUsePinsProtectedLabelChanges()
    {
        var context = Create();
        var label = context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted)).Value!;
        var capability = context.Kernel.MintCapability(new(8710), context.Process, ResourceKind.MemoryRegion,
            CapabilityResourceIds.InformationFlowTransition(context.First.Handle.RegionId),
            CapabilityRights.Configure, resourceGeneration: context.First.Handle.Generation.Value).Value!.CapabilityId;
        var inputUse = context.Kernel.AcquireRegionUse(context.Process, context.First.Handle,
            RegionUseMode.ReadOnly, new(0, 8)).Value!;
        var outputUse = context.Kernel.AcquireRegionUse(context.Process, context.Output.Handle,
            RegionUseMode.ReadOnly, new(0, 8)).Value!;
        var unlabeledUse = context.Kernel.AcquireRegionUse(context.Process, context.Unlabeled.Handle,
            RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(context.Kernel.InvalidateRegionUse(context.Process, inputUse.Handle).IsSuccess);
        Assert.True(context.Kernel.InvalidateRegionUse(context.Process, outputUse.Handle).IsSuccess);
        Assert.True(context.Kernel.InvalidateRegionUse(context.Process, unlabeledUse.Handle).IsSuccess);

        Assert.Equal(KernelError.RegionUseConflict,
            context.Kernel.TransitionV6ProtectedRegionLabel(context.Process, label,
                new(1, InformationFlowTransitionKindV1.Endorse, label.Label,
                    Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated)), capability).Error);
        Assert.Equal(KernelError.RegionUseConflict,
            context.Kernel.PropagateV6ProtectedRegionLabel(context.Process,
                [context.First.Handle], context.Output.Handle).Error);
        Assert.Equal(KernelError.RegionUseConflict,
            context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.Unlabeled.Handle,
                label.Label).Error);
        Assert.Equal(label, context.Kernel.Regions.QueryProtectedLabel(context.First.Handle, context.Owner).Value);

        Assert.True(context.Kernel.ReleaseRegionUse(context.Process, inputUse.Handle).IsSuccess);
        Assert.True(context.Kernel.ReleaseRegionUse(context.Process, outputUse.Handle).IsSuccess);
        Assert.True(context.Kernel.ReleaseRegionUse(context.Process, unlabeledUse.Handle).IsSuccess);
        Assert.True(context.Kernel.PropagateV6ProtectedRegionLabel(context.Process,
            [context.First.Handle], context.Output.Handle).IsSuccess);
    }

    [Fact]
    public void ProtectedPropagationUsesJoinAndRejectsUnlabeledInput()
    {
        var context = Create();
        Assert.True(context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Trusted)).IsSuccess);
        Assert.True(context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.Second.Handle,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted)).IsSuccess);

        var propagated = context.Kernel.PropagateV6ProtectedRegionLabel(context.Process,
            [context.First.Handle, context.Second.Handle], context.Output.Handle);

        Assert.True(propagated.IsSuccess, propagated.Message);
        Assert.Equal(Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted), propagated.Value!.Label);
        Assert.Equal(KernelError.PlatformDenied, context.Kernel.PropagateV6ProtectedRegionLabel(context.Process,
            [context.Unlabeled.Handle], context.Output.Handle).Error);
    }

    [Fact]
    public void DeclassificationWithoutExactCapabilityFails()
    {
        var context = Create();
        var label = context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated)).Value!;
        var wrong = context.Kernel.MintCapability(new(8710), context.Process, ResourceKind.MemoryRegion,
            CapabilityResourceIds.InformationFlowTransition(context.Second.Handle.RegionId),
            CapabilityRights.Configure, resourceGeneration: context.Second.Handle.Generation.Value).Value!.CapabilityId;
        var transition = new InformationFlowTransitionV1(1, InformationFlowTransitionKindV1.Declassify,
            label.Label, Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Validated));

        var result = context.Kernel.TransitionV6ProtectedRegionLabel(context.Process, label, transition, wrong);

        Assert.Equal(KernelError.WrongCapabilityResource, result.Error);
        Assert.Equal(label, context.Kernel.Regions.QueryProtectedLabel(context.First.Handle, context.Owner).Value);
    }

    [Fact]
    public void ExactCapabilityAuthorizesOneExplicitTransitionAndStaleBindingFails()
    {
        var context = Create();
        var label = context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted)).Value!;
        var capability = context.Kernel.MintCapability(new(8710), context.Process, ResourceKind.MemoryRegion,
            CapabilityResourceIds.InformationFlowTransition(context.First.Handle.RegionId),
            CapabilityRights.Configure, resourceGeneration: context.First.Handle.Generation.Value).Value!.CapabilityId;
        var transition = new InformationFlowTransitionV1(1, InformationFlowTransitionKindV1.Endorse,
            label.Label, Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated));

        var changed = context.Kernel.TransitionV6ProtectedRegionLabel(context.Process, label, transition, capability);

        Assert.True(changed.IsSuccess, changed.Message);
        Assert.Equal(label.LabelGeneration + 1, changed.Value!.LabelGeneration);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.TransitionV6ProtectedRegionLabel(context.Process, label, transition, capability).Error);
        Assert.False(changed.Value.AuthorizesRegionAccess);
    }

    [Fact]
    public void RevokedCapabilityCannotAuthorizeTransitionAndGateRemainsOff()
    {
        var context = Create();
        var label = context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted)).Value!;
        var capability = context.Kernel.MintCapability(new(8710), context.Process, ResourceKind.MemoryRegion,
            CapabilityResourceIds.InformationFlowTransition(context.First.Handle.RegionId),
            CapabilityRights.Configure, resourceGeneration: context.First.Handle.Generation.Value).Value!.CapabilityId;
        Assert.True(context.Kernel.RevokeCapability(capability).IsSuccess);

        var result = context.Kernel.TransitionV6ProtectedRegionLabel(context.Process, label,
            new(1, InformationFlowTransitionKindV1.Endorse, label.Label,
                Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated)), capability);

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.False(V6FeatureGates.IsEnabled("V6-IFC"));
    }

    [Fact]
    public void OwnershipTransferCarriesLabelAndAdvancesItsBindingGeneration()
    {
        var context = Create();
        var initial = context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Validated)).Value!;
        var target = TestFixtures.Create(context.Kernel, 8720, 8730).Handle;

        var moved = context.Kernel.TransferRegion(context.Process, target, context.First);

        Assert.True(moved.IsSuccess, moved.Message);
        var current = context.Kernel.Regions.QueryProtectedLabel(moved.Value!.Handle,
            new(new(8730), target.Generation)).Value!;
        Assert.Equal(initial.Label, current.Label);
        Assert.Equal(initial.LabelGeneration + 1, current.LabelGeneration);
        Assert.Equal(moved.Value.Handle, current.Region);
    }

    [Fact]
    public void ActiveRegionUsePinsProtectedLabelAgainstTransition()
    {
        var context = Create();
        var label = context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted)).Value!;
        var capability = context.Kernel.MintCapability(new(8710), context.Process, ResourceKind.MemoryRegion,
            CapabilityResourceIds.InformationFlowTransition(context.First.Handle.RegionId),
            CapabilityRights.Configure, resourceGeneration: context.First.Handle.Generation.Value).Value!.CapabilityId;
        var use = context.Kernel.AcquireRegionUse(context.Process, context.First.Handle,
            RegionUseMode.ExclusiveRead, new(0, 8)).Value!;

        var denied = context.Kernel.TransitionV6ProtectedRegionLabel(context.Process, label,
            new(1, InformationFlowTransitionKindV1.Endorse, label.Label,
                Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated)), capability);

        Assert.Equal(KernelError.RegionUseConflict, denied.Error);
        Assert.Equal(label, context.Kernel.Regions.QueryProtectedLabel(context.First.Handle, context.Owner).Value);
        Assert.True(context.Kernel.ReleaseRegionUse(context.Process, use.Handle).IsSuccess);
    }

    [Fact]
    public void ActiveOutputUsePinsProtectedLabelAgainstPropagation()
    {
        var context = Create();
        Assert.True(context.Kernel.AttachV6ProtectedRegionLabel(context.Process, context.First.Handle,
            Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Validated)).IsSuccess);
        var use = context.Kernel.AcquireRegionUse(context.Process, context.Output.Handle,
            RegionUseMode.ExclusiveWrite, new(0, 8)).Value!;

        var denied = context.Kernel.PropagateV6ProtectedRegionLabel(context.Process,
            [context.First.Handle], context.Output.Handle);

        Assert.Equal(KernelError.RegionUseConflict, denied.Error);
        Assert.Equal(KernelError.PlatformBindingNotFound,
            context.Kernel.Regions.QueryProtectedLabel(context.Output.Handle, context.Owner).Error);
        Assert.True(context.Kernel.ReleaseRegionUse(context.Process, use.Handle).IsSuccess);
    }

    private static Context Create()
    {
        var kernel = new RuntimeKernel();
        var process = TestFixtures.Create(kernel, 8700, 8710).Handle;
        return new(kernel, process, new(new(8710), process.Generation),
            kernel.AllocateBuffer<byte>(process, 8).Value!, kernel.AllocateBuffer<byte>(process, 8).Value!,
            kernel.AllocateBuffer<byte>(process, 8).Value!, kernel.AllocateBuffer<byte>(process, 8).Value!);
    }

    private static DataLabelV1 Label(ConfidentialityClassV1 confidentiality, IntegrityClassV1 integrity) =>
        new(1, confidentiality, integrity);

    private sealed record Context(RuntimeKernel Kernel, ProcessHandle Process, RegionOwner Owner,
        OwnedBuffer<byte> First, OwnedBuffer<byte> Second, OwnedBuffer<byte> Output, OwnedBuffer<byte> Unlabeled);
}
