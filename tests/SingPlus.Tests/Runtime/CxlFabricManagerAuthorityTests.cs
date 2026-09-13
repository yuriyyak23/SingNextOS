using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class CxlFabricManagerAuthorityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ReconfigurationDrainsEveryTrackedLifecycleStage(int stage)
    {
        var s = CreateScenario();
        var operation = PrepareOperation(s);
        OperationBinding? operationBinding = null;
        if (stage >= 1) Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).IsSuccess);
        if (stage >= 2) operationBinding = s.Kernel.RecordExternalOperationSubmission(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).Value!;
        if (stage >= 3) Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Handle, new(operationBinding!.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        if (stage >= 4) Assert.True(s.Kernel.RecordExternalOperationVisibility(s.Handle, new(operationBinding!.Value, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        if (stage >= 5) Assert.True(s.Kernel.PublishExternalOperation(s.Handle, operation.Operation,
            new(1, 1, s.Binding.Generation.Value), new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);
        var closureInvoked = false;
        Func<KernelResult>? providerClosure = stage >= 2
            ? () => { closureInvoked = true; return KernelResult.Ok(); }
            : null;
        Assert.True(s.Manager.TrackOperation(s.Binding, s.Handle, operation.Operation, providerClosure).IsSuccess);

        var draining = s.Manager.BeginReconfiguration(s.Binding);
        Assert.True(draining.IsSuccess, draining.Message);
        Assert.Equal(CxlFabricResourceState.Draining, draining.Value!.State);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.QueryExternalOperation(s.Handle, operation.Operation).Value!.State);
        Assert.False(s.Manager.ValidateAdmission(s.Binding).IsSuccess);
        Assert.Equal(stage >= 2, closureInvoked);
    }

    [Fact]
    public void PostSubmitTrackingWithoutProviderClosureProofIsRejected()
    {
        var s = CreateScenario();
        var operation = PrepareOperation(s);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationSubmission(s.Handle, operation.Operation, new(1, 1, s.Binding.Generation.Value)).IsSuccess);

        var tracked = s.Manager.TrackOperation(s.Binding, s.Handle, operation.Operation);

        Assert.False(tracked.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, tracked.Error);
    }

    [Fact]
    public void OldOpaqueBindingNeverReopensAfterReplacement()
    {
        var s = CreateScenario();
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        var replacement = s.Manager.CompleteReconfiguration(s.Binding.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        Assert.NotEqual(s.Binding.Generation, replacement.Value!.Binding.Generation);
        Assert.Equal(KernelError.StaleGeneration, s.Manager.ValidateAdmission(s.Binding).Error);
        Assert.True(s.Manager.ValidateAdmission(replacement.Value.Binding).IsSuccess);
    }

    [Fact]
    public void UnrelatedEndpointGenerationRemainsValid()
    {
        var s = CreateScenario(twoBindings: true);
        Assert.True(s.Manager.BeginReconfiguration(s.Binding).IsSuccess);
        Assert.True(s.Manager.ValidateAdmission(s.OtherBinding!).IsSuccess);
        Assert.Equal(new CxlFabricBindingGeneration(1), s.OtherBinding!.Generation);
    }

    [Fact]
    public void PoolAssignmentPinsNormalOwnershipUntilExplicitRelease()
    {
        var s = CreateScenario();
        Assert.True(s.Model.RegisterPool(new("pool-a"), 1024).IsSuccess);
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        var allocation = s.Manager.AssignPool(new("pool-a"), s.Endpoint.EndpointId, buffer.Handle, s.Owner);
        Assert.True(allocation.IsSuccess, allocation.Message);
        var (_, target) = TestFixtures.Create(s.Kernel, 993, 994);
        Assert.Equal(KernelError.RegionUseConflict, s.Kernel.TransferRegion(s.Handle, target, buffer).Error);
        Assert.True(s.Manager.ReleasePool(allocation.Value!).IsSuccess);
        Assert.True(s.Kernel.TransferRegion(s.Handle, target, buffer).IsSuccess);
    }

    [Fact]
    public void StalePoolRelease_DoesNotDeleteCurrentAssignmentOrChangeCapacity()
    {
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("pool-endpoint"), new("device:pool-endpoint"), 1024).IsSuccess);
        Assert.True(model.RegisterPool(new("pool-exact"), 256).IsSuccess);
        var assignment = model.AssignPoolCapacity(new("pool-exact"), new("pool-endpoint"), 64).Value!;
        var before = model.QueryPool(new("pool-exact")).Value!;
        var stale = assignment with { Generation = assignment.Generation + 1 };

        Assert.Equal(PlatformAuthorityStatus.Stale, model.ReleasePoolCapacity(stale).Status);
        Assert.Equal(before, model.QueryPool(new("pool-exact")).Value);
        Assert.True(model.ReleasePoolCapacity(assignment).IsSuccess);
        Assert.Equal(256, model.QueryPool(new("pool-exact")).Value!.AvailableCapacityBytes);
    }

    [Fact]
    public void AmbiguousPoolAssignmentAcceptanceKeepsRegionPinnedAndBlocksReclaim()
    {
        var s = CreateScenario();
        Assert.True(s.Model.RegisterPool(new("pool-ambiguous"), 256).IsSuccess);
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        s.Model.PoolAcceptanceAmbiguous = true;

        var allocation = s.Manager.AssignPool(new("pool-ambiguous"), s.Endpoint.EndpointId, buffer.Handle, s.Owner);

        Assert.False(allocation.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, allocation.Error);
        Assert.Contains(s.Kernel.Regions.SnapshotUses(), use => use.Region == buffer.Handle && use.State == RegionUseState.Active);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(buffer.Handle, s.Owner).IsSuccess);
    }

    [Fact]
    public void StaleReconfigurationTicket_DoesNotConsumeCurrentTicket()
    {
        var s = CreateScenario();
        var ticket = s.Model.BeginReconfiguration(s.Binding).Value!;
        var stale = ticket with { ReplacementGeneration = new(ticket.ReplacementGeneration.Value + 1) };

        Assert.Equal(PlatformAuthorityStatus.Stale, s.Model.CompleteReconfiguration(stale).Status);
        var completed = s.Model.CompleteReconfiguration(ticket);
        Assert.True(completed.IsSuccess);
        Assert.Equal(ticket.ReplacementGeneration, completed.Value!.Generation);
    }

    [Fact]
    public void EndpointGenerationChangeDuringReconfiguration_DoesNotRollbackGeneration()
    {
        var s = CreateScenario();
        var ticket = s.Model.BeginReconfiguration(s.Binding).Value!;
        Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        var current = s.Model.Query(s.Binding.BindingId).Value!;

        Assert.Equal(PlatformAuthorityStatus.Stale, s.Model.CompleteReconfiguration(ticket).Status);
        Assert.Equal(current, s.Model.Query(s.Binding.BindingId).Value);
        Assert.True(current.Generation.Value > s.Binding.Generation.Value);
        Assert.Equal(CxlFabricResourceState.Bound, s.Model.QueryResource(s.Binding.BindingId).Value!.State);
    }

    [Fact]
    public void PeerAccessRequiresBothRouteSupportAndPlatformIsolation()
    {
        var s = CreateScenario(twoBindings: true);
        s.Model.PeerAccessSupported = true;
        var noIsolation = s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding!.EndpointId, 16, false), s.Binding, s.OtherBinding);
        Assert.Equal(KernelError.PlatformDenied, noIsolation.Error);
        s.Model.PeerAccessSupported = false;
        var noRoute = s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding!.EndpointId, 16, true), s.Binding, s.OtherBinding);
        Assert.Equal(KernelError.PlatformUnsupported, noRoute.Error);
        s.Model.PeerAccessSupported = true;
        Assert.True(s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding!.EndpointId, 16, true), s.Binding, s.OtherBinding).IsSuccess);
        Assert.Equal(KernelError.PlatformDenied,
            s.Manager.ValidatePeerAccess(new(s.Endpoint.EndpointId, s.OtherBinding.EndpointId, 16, true)).Error);
    }

    private static OperationPreparation PrepareOperation(Scenario s) =>
        s.Kernel.PrepareExternalOperation(s.Handle,
            [new(s.OperationInput.Handle, RegionUseMode.ReadOnly, new(0, 8)),
             new(s.OperationOutput.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;

    private static Scenario CreateScenario(bool twoBindings = false)
    {
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("fm-0"), new("device:fm-0"), 1024).IsSuccess);
        var endpoint = model.QueryEndpoint(new("fm-0")).Value!;
        var binding = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 128,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        CxlFabricBinding? other = null;
        if (twoBindings)
        {
            Assert.True(model.RegisterEndpoint(new("fm-1"), new("device:fm-1"), 1024).IsSuccess);
            var e = model.QueryEndpoint(new("fm-1")).Value!;
            other = model.Bind(new(e.EndpointId, e.DeviceGeneration, 128, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        }
        var kernel = new RuntimeKernel();
        var (_, handle) = TestFixtures.Create(kernel, 991, 992);
        var process = kernel.Processes.Resolve(handle).Value!;
        var manager = new CxlFabricManagerAuthority(kernel, model);
        Assert.True(manager.Register(binding).IsSuccess);
        if (other is not null) Assert.True(manager.Register(other).IsSuccess);
        return new(kernel, handle, new(process.DomainId, handle.Generation), model, manager, endpoint, binding, other,
            kernel.AllocateBuffer<byte>(handle, 8).Value!, kernel.AllocateBuffer<byte>(handle, 8).Value!);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle, RegionOwner Owner,
        CxlType3ModelProvider Model, CxlFabricManagerAuthority Manager, CxlEndpointSnapshot Endpoint,
        CxlFabricBinding Binding, CxlFabricBinding? OtherBinding,
        SingPlus.Sip.OwnedBuffer<byte> OperationInput, SingPlus.Sip.OwnedBuffer<byte> OperationOutput);
}
