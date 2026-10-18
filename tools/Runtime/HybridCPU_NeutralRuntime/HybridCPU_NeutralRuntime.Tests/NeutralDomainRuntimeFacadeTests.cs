using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class NeutralDomainRuntimeFacadeTests
{
    [Fact]
    public void OrdinaryServiceBindsAndClosesWithExactLease()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var bind = runtime.Bind(NeutralDomainProfile.OrdinaryService);

        Assert.True(bind.IsBound);
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(bind.Lease).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Revoked, runtime.Close(bind.Lease).Decision);
    }

    [Fact]
    public void ExecutionLifecycleIsFailClosed()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var lease = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;

        Assert.Equal(NeutralExecutionState.Running,
            runtime.TransitionExecution(lease, NeutralExecutionTransition.Start).State);
        Assert.Equal(NeutralExecutionTransitionDecision.InvalidTransition,
            runtime.TransitionExecution(lease, NeutralExecutionTransition.Start).Decision);
        Assert.Equal(NeutralExecutionState.Parked,
            runtime.TransitionExecution(lease, NeutralExecutionTransition.Park).State);
        Assert.Equal(NeutralExecutionState.Running,
            runtime.TransitionExecution(lease, NeutralExecutionTransition.Resume).State);
    }

    [Fact]
    public void DmaRangeIsRelativeToMappingSlice()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mapping = runtime.MapOwnedRegion(domain, new(128, 512, NeutralMemoryAccess.Read)).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Configure | NeutralDeviceRights.Read).Lease;

        var result = runtime.BindDmaGrant(device, mapping, new(32, 128), NeutralDmaDirection.DeviceReadsMemory);

        Assert.Equal(NeutralDmaGrantDecision.Granted, result.Decision);
    }

    [Fact]
    public void ClosedDmaGrantCannotAcquirePreparedVisibility()
    {
        var (runtime, _, mapping, device) = CreateParents(NeutralMemoryAccess.Write, NeutralDeviceRights.Configure | NeutralDeviceRights.Write);
        var grant = runtime.BindDmaGrant(device, mapping, new(0, 64), NeutralDmaDirection.DeviceWritesMemory).Grant;
        Assert.Equal(NeutralDmaPrepareDecision.Prepared, runtime.PrepareDmaVisibility(grant).Decision);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed, runtime.CloseDmaGrant(grant).Decision);

        Assert.Equal(NeutralDmaAcquireDecision.Revoked, runtime.AcquireDmaVisibility(grant).Decision);
    }

    [Fact]
    public void InterruptStartsIdleAndCannotCompleteAfterClose()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("irq-device"), NeutralDeviceRights.Configure).Lease;
        var interrupt = runtime.BindInterrupt(device, new("irq", NeutralInterruptTrigger.Edge)).Lease;

        Assert.Equal(NeutralInterruptPollDecision.NoDelivery, runtime.PollInterrupt(interrupt).Decision);
        var signal = runtime.SignalInterrupt(interrupt);
        Assert.Equal(NeutralInterruptSignalDecision.Signaled, signal.Decision);
        Assert.Equal(NeutralInterruptSignalDecision.AlreadyPending, runtime.SignalInterrupt(interrupt).Decision);
        Assert.Equal(NeutralInterruptCompleteDecision.WrongSequence, runtime.CompleteInterruptDelivery(interrupt, new(signal.Sequence.Value + 1)).Decision);
        Assert.Equal(NeutralInterruptPollDecision.Observed, runtime.PollInterrupt(interrupt).Decision);
        Assert.Equal(NeutralInterruptCloseDecision.PendingDelivery, runtime.CloseInterrupt(interrupt).Decision);
        Assert.Equal(NeutralInterruptCompleteDecision.Completed, runtime.CompleteInterruptDelivery(interrupt, signal.Sequence).Decision);
        Assert.Equal(NeutralInterruptCloseDecision.Closed, runtime.CloseInterrupt(interrupt).Decision);
        Assert.Equal(NeutralInterruptCompleteDecision.Revoked, runtime.CompleteInterruptDelivery(interrupt, signal.Sequence).Decision);
        Assert.Equal(NeutralInterruptPollDecision.Revoked, runtime.PollInterrupt(interrupt).Decision);
        Assert.Equal(NeutralInterruptSignalDecision.Revoked, runtime.SignalInterrupt(interrupt).Decision);
    }

    [Fact]
    public void ExistingHandleWithWrongEpochIsStale()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Lease;
        var stale = device with { Epoch = new(device.Epoch.Value + 1) };

        Assert.Equal(NeutralDeviceCloseDecision.Stale, runtime.CloseDevice(stale).Decision);
    }

    [Fact]
    public void ParentCloseIsDeniedWithoutChangingState()
    {
        var (runtime, domain, mapping, device) = CreateParents(NeutralMemoryAccess.Read, NeutralDeviceRights.Configure | NeutralDeviceRights.Read);
        var grant = runtime.BindDmaGrant(device, mapping, new(0, 64), NeutralDmaDirection.DeviceReadsMemory).Grant;

        Assert.Equal(NeutralOwnedRegionCloseDecision.ActiveDependents, runtime.CloseOwnedRegionMapping(mapping).Decision);
        Assert.Equal(NeutralDeviceCloseDecision.ActiveDependents, runtime.CloseDevice(device).Decision);
        Assert.Equal(NeutralDomainCloseDecision.ActiveDependents, runtime.Close(domain).Decision);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed, runtime.CloseDmaGrant(grant).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
        Assert.Equal(NeutralDeviceCloseDecision.Closed, runtime.CloseDevice(device).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(domain).Decision);
    }

    [Fact]
    public void MmioRequiresMatchingDeviceRights()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Lease;

        Assert.Equal(NeutralMmioMapDecision.InsufficientDeviceRights,
            runtime.MapMmio(device, new("bar0", 4096), new(0, 4), NeutralMmioAccess.Write).Decision);
    }

    [Theory]
    [InlineData(NeutralDmaDirection.DeviceReadsMemory, NeutralDeviceRights.Configure, NeutralMemoryAccess.Read, NeutralDmaGrantDecision.InsufficientDeviceRights)]
    [InlineData(NeutralDmaDirection.DeviceReadsMemory, NeutralDeviceRights.Configure | NeutralDeviceRights.Read, NeutralMemoryAccess.Write, NeutralDmaGrantDecision.InsufficientMappingAccess)]
    [InlineData(NeutralDmaDirection.DeviceWritesMemory, NeutralDeviceRights.Configure | NeutralDeviceRights.Write, NeutralMemoryAccess.Read, NeutralDmaGrantDecision.InsufficientMappingAccess)]
    [InlineData(NeutralDmaDirection.Bidirectional, NeutralDeviceRights.Configure | NeutralDeviceRights.Read, NeutralMemoryAccess.Read | NeutralMemoryAccess.Write, NeutralDmaGrantDecision.InsufficientDeviceRights)]
    public void DmaDirectionRequiresExactRights(NeutralDmaDirection direction, NeutralDeviceRights rights, NeutralMemoryAccess access, NeutralDmaGrantDecision expected)
    {
        var (runtime, _, mapping, device) = CreateParents(access, rights);
        Assert.Equal(expected, runtime.BindDmaGrant(device, mapping, new(0, 64), direction).Decision);
    }

    [Fact]
    public void VisibilityEvidenceDoesNotSubstituteRequirements()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var nonCoherent = runtime.MapOwnedRegion(domain, new(0, 64, NeutralMemoryAccess.Read, NeutralMemoryCoherenceModel.NonCoherent)).Lease;
        var coherent = runtime.MapOwnedRegion(domain, new(0, 64, NeutralMemoryAccess.Read, NeutralMemoryCoherenceModel.Coherent)).Lease;

        var publication = runtime.PrepareOwnedRegionVisibility(nonCoherent, NeutralMemoryVisibilityRequirement.PublicationFence);
        Assert.Equal(NeutralMemoryVisibilityRequirement.PublicationFence, publication.Requirement);
        Assert.Equal(NeutralMemoryVisibilityOutcome.PublicationFenceSatisfied, publication.Outcome);
        Assert.Equal(NeutralOwnedRegionVisibilityDecision.Unsupported, runtime.PrepareOwnedRegionVisibility(nonCoherent, NeutralMemoryVisibilityRequirement.CoherentAccess).Decision);
        Assert.Equal(NeutralMemoryVisibilityOutcome.Coherent, runtime.PrepareOwnedRegionVisibility(coherent, NeutralMemoryVisibilityRequirement.CoherentAccess).Outcome);
        Assert.Equal(NeutralOwnedRegionVisibilityDecision.Unsupported, runtime.PrepareOwnedRegionVisibility(coherent, NeutralMemoryVisibilityRequirement.CacheMaintenance).Decision);
    }

    [Fact]
    public void NegativeRangesAreRejected()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        Assert.Equal(NeutralOwnedRegionMapDecision.InvalidRange, runtime.MapOwnedRegion(domain, new(-1, 64, NeutralMemoryAccess.Read)).Decision);
        Assert.Equal(NeutralOwnedRegionMapDecision.InvalidRange, runtime.MapOwnedRegion(domain, new(long.MaxValue, 1, NeutralMemoryAccess.Read)).Decision);
    }

    private static (NeutralDomainRuntimeFacade Runtime, NeutralDomainBindingLease Domain, NeutralOwnedRegionMappingLease Mapping, NeutralDeviceLease Device) CreateParents(NeutralMemoryAccess access, NeutralDeviceRights rights)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mapping = runtime.MapOwnedRegion(domain, new(0, 512, access)).Lease;
        var device = runtime.BindDevice(domain, new("device"), rights).Lease;
        return (runtime, domain, mapping, device);
    }
}
