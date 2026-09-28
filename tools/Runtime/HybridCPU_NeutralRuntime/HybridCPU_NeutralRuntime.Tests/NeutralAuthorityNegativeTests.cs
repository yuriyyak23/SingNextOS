using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class NeutralAuthorityNegativeTests
{
    [Fact]
    public void UnsupportedDomainProfileIsRejected()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        Assert.Equal(NeutralDomainBindDecision.UnsupportedProfile, runtime.Bind((NeutralDomainProfile)999).Decision);
    }

    [Fact]
    public void DomainPublicDecisionsPreserveLookupClassification()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var bind = runtime.Bind(NeutralDomainProfile.OrdinaryService);
        Assert.Equal(NeutralDomainBindDecision.Bound, bind.Decision);
        Assert.Equal(NeutralExecutionTransitionDecision.Transitioned, runtime.TransitionExecution(bind.Lease, NeutralExecutionTransition.Start).Decision);
        Assert.Equal(NeutralDomainCloseDecision.NotFound, runtime.Close(new(new(999), new(1))).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Stale, runtime.Close(bind.Lease with { Epoch = new(2) }).Decision);
    }

    [Fact]
    public void DomainRejectsFabricatedStaleRevokedAndInvalidTransition()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var lease = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        Assert.Equal(NeutralRuntimeImplementationProfile.ModelOnly, runtime.ImplementationProfile);
        Assert.Equal(NeutralExecutionTransitionDecision.NotFound, runtime.TransitionExecution(new(new(999), new(1)), NeutralExecutionTransition.Start).Decision);
        Assert.Equal(NeutralExecutionTransitionDecision.Stale, runtime.TransitionExecution(lease with { Epoch = new(2) }, NeutralExecutionTransition.Start).Decision);
        Assert.Equal(NeutralExecutionTransitionDecision.InvalidTransition, runtime.TransitionExecution(lease, NeutralExecutionTransition.Resume).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(lease).Decision);
        Assert.Equal(NeutralExecutionTransitionDecision.Revoked, runtime.TransitionExecution(lease, NeutralExecutionTransition.Start).Decision);
    }

    [Fact]
    public void MappingRejectsBadAuthorityAndRequiresClosedExactLeaseForAcquire()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var fabricatedDomain = new NeutralDomainBindingLease(new(999), new(1));
        Assert.Equal(NeutralOwnedRegionMapDecision.NotFound, runtime.MapOwnedRegion(fabricatedDomain, new(0, 1, NeutralMemoryAccess.Read)).Decision);
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        Assert.Equal(NeutralOwnedRegionMapDecision.InvalidAccess, runtime.MapOwnedRegion(domain, new(0, 1, NeutralMemoryAccess.None)).Decision);
        Assert.Equal(NeutralOwnedRegionMapDecision.InvalidRange, runtime.MapOwnedRegion(domain, new(0, 0, NeutralMemoryAccess.Read)).Decision);
        var mapping = runtime.MapOwnedRegion(domain, new(0, 16, NeutralMemoryAccess.Read)).Lease;
        Assert.Equal(NeutralOwnedRegionAcquireDecision.NotClosed, runtime.AcquireOwnedRegionVisibility(mapping, NeutralMemoryAcquireRequirement.AcquisitionFence).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Stale, runtime.CloseOwnedRegionMapping(mapping with { Epoch = new(2) }).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
        var acquire = runtime.AcquireOwnedRegionVisibility(mapping, NeutralMemoryAcquireRequirement.AcquisitionFence);
        Assert.Equal(NeutralOwnedRegionAcquireDecision.Satisfied, acquire.Decision);
        Assert.Equal(NeutralMemoryAcquireRequirement.AcquisitionFence, acquire.Requirement);
        Assert.Equal(NeutralOwnedRegionAcquireDecision.NotFound, runtime.AcquireOwnedRegionVisibility(mapping with { Handle = new(999) }, NeutralMemoryAcquireRequirement.AcquisitionFence).Decision);
    }

    [Fact]
    public void ClosedMappingAndDomainRemainPreciselyClassified()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mapping = runtime.MapOwnedRegion(domain, new(0, 16, NeutralMemoryAccess.Read)).Lease;
        Assert.Equal(NeutralOwnedRegionCloseDecision.NotFound, runtime.CloseOwnedRegionMapping(mapping with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Revoked, runtime.CloseOwnedRegionMapping(mapping).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(domain).Decision);
        Assert.Equal(NeutralOwnedRegionAcquireDecision.RevokedDomain, runtime.AcquireOwnedRegionVisibility(mapping, NeutralMemoryAcquireRequirement.AcquisitionFence).Decision);
    }

    [Fact]
    public void MappingOperationsPreserveLookupAndRequirementClassification()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        Assert.Equal(NeutralOwnedRegionMapDecision.Stale, runtime.MapOwnedRegion(domain with { Epoch = new(2) }, new(0, 1, NeutralMemoryAccess.Read)).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(domain).Decision);
        Assert.Equal(NeutralOwnedRegionMapDecision.Revoked, runtime.MapOwnedRegion(domain, new(0, 1, NeutralMemoryAccess.Read)).Decision);

        domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mappingResult = runtime.MapOwnedRegion(domain, new(0, 16, NeutralMemoryAccess.Read));
        Assert.Equal(NeutralOwnedRegionMapDecision.Mapped, mappingResult.Decision);
        var mapping = mappingResult.Lease;
        Assert.Equal(NeutralOwnedRegionVisibilityDecision.Satisfied, runtime.PrepareOwnedRegionVisibility(mapping, NeutralMemoryVisibilityRequirement.PublicationFence).Decision);
        Assert.Equal(NeutralOwnedRegionVisibilityDecision.NotFound, runtime.PrepareOwnedRegionVisibility(mapping with { Handle = new(999) }, NeutralMemoryVisibilityRequirement.PublicationFence).Decision);
        Assert.Equal(NeutralOwnedRegionVisibilityDecision.Stale, runtime.PrepareOwnedRegionVisibility(mapping with { Epoch = new(2) }, NeutralMemoryVisibilityRequirement.PublicationFence).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
        Assert.Equal(NeutralOwnedRegionVisibilityDecision.Revoked, runtime.PrepareOwnedRegionVisibility(mapping, NeutralMemoryVisibilityRequirement.PublicationFence).Decision);
        Assert.Equal(NeutralOwnedRegionAcquireDecision.Stale, runtime.AcquireOwnedRegionVisibility(mapping with { Epoch = new(2) }, NeutralMemoryAcquireRequirement.AcquisitionFence).Decision);
        Assert.Equal(NeutralOwnedRegionAcquireDecision.Unsupported, runtime.AcquireOwnedRegionVisibility(mapping, (NeutralMemoryAcquireRequirement)999).Decision);
    }

    [Fact]
    public void DeviceRejectsInvalidDuplicateAndStaleParent()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        Assert.Equal(NeutralDeviceBindDecision.InvalidDevice, runtime.BindDevice(domain, new(" "), NeutralDeviceRights.Read).Decision);
        Assert.Equal(NeutralDeviceBindDecision.InvalidRights, runtime.BindDevice(domain, new("device"), NeutralDeviceRights.None).Decision);
        Assert.Equal(NeutralDeviceBindDecision.Stale, runtime.BindDevice(domain with { Epoch = new(2) }, new("device"), NeutralDeviceRights.Read).Decision);
        Assert.Equal(NeutralDeviceBindDecision.Bound, runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Decision);
        Assert.Equal(NeutralDeviceBindDecision.AlreadyBound, runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Write).Decision);
    }

    [Fact]
    public void DeviceCloseClassifiesNotFoundAndRevoked()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Lease;
        Assert.Equal(NeutralDeviceCloseDecision.NotFound, runtime.CloseDevice(device with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralDeviceCloseDecision.Closed, runtime.CloseDevice(device).Decision);
        Assert.Equal(NeutralDeviceCloseDecision.Revoked, runtime.CloseDevice(device).Decision);
    }

    [Fact]
    public void DeviceBindClassifiesMissingAndRevokedDomains()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        Assert.Equal(NeutralDeviceBindDecision.NotFound, runtime.BindDevice(new(new(999), new(1)), new("device"), NeutralDeviceRights.Read).Decision);
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(domain).Decision);
        Assert.Equal(NeutralDeviceBindDecision.Revoked, runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Decision);
    }

    [Fact]
    public void MmioRejectsContainmentDuplicateAndStaleDevice()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read | NeutralDeviceRights.Write).Lease;
        var region = new NeutralMmioRegionIdentity("bar0", 128);
        Assert.Equal(NeutralMmioMapDecision.InvalidRange, runtime.MapMmio(device, region, new(127, 2), NeutralMmioAccess.Read).Decision);
        Assert.Equal(NeutralMmioMapDecision.InvalidAccess, runtime.MapMmio(device, region, new(0, 4), NeutralMmioAccess.None).Decision);
        Assert.Equal(NeutralMmioMapDecision.Stale, runtime.MapMmio(device with { Epoch = new(2) }, region, new(0, 4), NeutralMmioAccess.Read).Decision);
        Assert.Equal(NeutralMmioMapDecision.Mapped, runtime.MapMmio(device, region, new(0, 4), NeutralMmioAccess.Read).Decision);
        Assert.Equal(NeutralMmioMapDecision.AlreadyMapped, runtime.MapMmio(device, region, new(0, 4), NeutralMmioAccess.Read).Decision);
        Assert.Equal(NeutralDeviceCloseDecision.ActiveDependents, runtime.CloseDevice(device).Decision);
    }

    [Fact]
    public void MmioCloseAndRegionIdentityAreFailClosed()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Lease;
        Assert.Equal(NeutralMmioMapDecision.InvalidRegion, runtime.MapMmio(device, new("", 16), new(0, 1), NeutralMmioAccess.Read).Decision);
        var mmio = runtime.MapMmio(device, new("bar", 16), new(0, 1), NeutralMmioAccess.Read).Lease;
        Assert.Equal(NeutralMmioCloseDecision.NotFound, runtime.CloseMmio(mmio with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralMmioCloseDecision.Stale, runtime.CloseMmio(mmio with { Epoch = new(2) }).Decision);
        Assert.Equal(NeutralMmioCloseDecision.Closed, runtime.CloseMmio(mmio).Decision);
        Assert.Equal(NeutralMmioCloseDecision.Revoked, runtime.CloseMmio(mmio).Decision);
    }

    [Fact]
    public void MmioMapClassifiesMissingAndRevokedDevices()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var missing = new NeutralDeviceLease(default, new("missing"), NeutralDeviceRights.Read, new(999), new(1));
        Assert.Equal(NeutralMmioMapDecision.NotFound, runtime.MapMmio(missing, new("bar", 16), new(0, 1), NeutralMmioAccess.Read).Decision);
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Lease;
        Assert.Equal(NeutralDeviceCloseDecision.Closed, runtime.CloseDevice(device).Decision);
        Assert.Equal(NeutralMmioMapDecision.Revoked, runtime.MapMmio(device, new("bar", 16), new(0, 1), NeutralMmioAccess.Read).Decision);
    }

    [Fact]
    public void InterruptSequenceIsMonotonicAndPendingCloseIsFailClosed()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var weakDevice = runtime.BindDevice(domain, new("weak"), NeutralDeviceRights.Read).Lease;
        Assert.Equal(NeutralInterruptBindDecision.InsufficientDeviceRights, runtime.BindInterrupt(weakDevice, new("irq", NeutralInterruptTrigger.Edge)).Decision);
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Configure).Lease;
        var irq = runtime.BindInterrupt(device, new("irq", NeutralInterruptTrigger.Edge)).Lease;
        Assert.Equal(NeutralInterruptBindDecision.AlreadyBound, runtime.BindInterrupt(device, new("irq", NeutralInterruptTrigger.Edge)).Decision);
        var first = runtime.SignalInterrupt(irq);
        Assert.Equal(NeutralInterruptCompleteDecision.Completed, runtime.CompleteInterruptDelivery(irq, first.Sequence).Decision);
        var second = runtime.SignalInterrupt(irq);
        Assert.True(second.Sequence.Value > first.Sequence.Value);
        Assert.Equal(NeutralInterruptCloseDecision.PendingDelivery, runtime.CloseInterrupt(irq).Decision);
        Assert.Equal(NeutralInterruptPollDecision.Observed, runtime.PollInterrupt(irq).Decision);
        Assert.Equal(NeutralInterruptCompleteDecision.Completed, runtime.CompleteInterruptDelivery(irq, second.Sequence).Decision);
        Assert.Equal(NeutralInterruptCloseDecision.Closed, runtime.CloseInterrupt(irq).Decision);
        Assert.Equal(NeutralInterruptSignalDecision.Revoked, runtime.SignalInterrupt(irq).Decision);
    }

    [Fact]
    public void InterruptRejectsInvalidStaleAndFabricatedLeases()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Configure).Lease;
        Assert.Equal(NeutralInterruptBindDecision.InvalidSource, runtime.BindInterrupt(device, new("", NeutralInterruptTrigger.Edge)).Decision);
        var irq = runtime.BindInterrupt(device, new("irq", NeutralInterruptTrigger.Level)).Lease;
        Assert.Equal(NeutralInterruptSignalDecision.Stale, runtime.SignalInterrupt(irq with { Epoch = new(2) }).Decision);
        Assert.Equal(NeutralInterruptPollDecision.NotFound, runtime.PollInterrupt(irq with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralInterruptCompleteDecision.NoPendingDelivery, runtime.CompleteInterruptDelivery(irq, new(0)).Decision);
    }

    [Fact]
    public void InterruptOperationsPreserveEveryLookupClassification()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var missingDevice = new NeutralDeviceLease(default, new("missing"), NeutralDeviceRights.Configure, new(999), new(1));
        Assert.Equal(NeutralInterruptBindDecision.NotFound, runtime.BindInterrupt(missingDevice, new("irq", NeutralInterruptTrigger.Edge)).Decision);
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Configure).Lease;
        Assert.Equal(NeutralInterruptBindDecision.Stale, runtime.BindInterrupt(device with { Epoch = new(2) }, new("irq", NeutralInterruptTrigger.Edge)).Decision);
        var irqResult = runtime.BindInterrupt(device, new("irq", NeutralInterruptTrigger.Edge));
        Assert.Equal(NeutralInterruptBindDecision.Bound, irqResult.Decision);
        var irq = irqResult.Lease;
        Assert.Equal(NeutralInterruptSignalDecision.NotFound, runtime.SignalInterrupt(irq with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralInterruptPollDecision.Stale, runtime.PollInterrupt(irq with { Epoch = new(2) }).Decision);
        Assert.Equal(NeutralInterruptCompleteDecision.NotFound, runtime.CompleteInterruptDelivery(irq with { Handle = new(999) }, default).Decision);
        Assert.Equal(NeutralInterruptCompleteDecision.Stale, runtime.CompleteInterruptDelivery(irq with { Epoch = new(2) }, default).Decision);
        Assert.Equal(NeutralInterruptCloseDecision.Stale, runtime.CloseInterrupt(irq with { Epoch = new(2) }).Decision);
        Assert.Equal(NeutralInterruptCloseDecision.NotFound, runtime.CloseInterrupt(irq with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralInterruptCloseDecision.Closed, runtime.CloseInterrupt(irq).Decision);
        Assert.Equal(NeutralInterruptCloseDecision.Revoked, runtime.CloseInterrupt(irq).Decision);
        Assert.Equal(NeutralDeviceCloseDecision.Closed, runtime.CloseDevice(device).Decision);
        Assert.Equal(NeutralInterruptBindDecision.Revoked, runtime.BindInterrupt(device, new("irq2", NeutralInterruptTrigger.Edge)).Decision);
    }

    [Fact]
    public void DmaRejectsCrossDomainDuplicateInvalidDirectionAndStaleGrant()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain1 = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var domain2 = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mapping1 = runtime.MapOwnedRegion(domain1, new(0, 64, NeutralMemoryAccess.Read | NeutralMemoryAccess.Write)).Lease;
        var mapping2 = runtime.MapOwnedRegion(domain2, new(0, 64, NeutralMemoryAccess.Read)).Lease;
        var device = runtime.BindDevice(domain1, new("device"), NeutralDeviceRights.Configure | NeutralDeviceRights.Read | NeutralDeviceRights.Write).Lease;
        Assert.Equal(NeutralDmaGrantDecision.WrongDomain, runtime.BindDmaGrant(device, mapping2, new(0, 1), NeutralDmaDirection.DeviceReadsMemory).Decision);
        Assert.Equal(NeutralDmaGrantDecision.InvalidRange, runtime.BindDmaGrant(device, mapping1, new(64, 1), NeutralDmaDirection.DeviceReadsMemory).Decision);
        Assert.Equal(NeutralDmaGrantDecision.InvalidDirection, runtime.BindDmaGrant(device, mapping1, new(0, 1), (NeutralDmaDirection)999).Decision);
        var grant = runtime.BindDmaGrant(device, mapping1, new(0, 16), NeutralDmaDirection.Bidirectional).Grant;
        Assert.Equal(NeutralDmaGrantDecision.AlreadyGranted, runtime.BindDmaGrant(device, mapping1, new(0, 16), NeutralDmaDirection.Bidirectional).Decision);
        Assert.Equal(NeutralDmaGrantCloseDecision.Stale, runtime.CloseDmaGrant(grant with { Epoch = new(2) }).Decision);
    }

    [Fact]
    public void DmaVisibilityUsesFreshConsumableCycles()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mapping = runtime.MapOwnedRegion(domain, new(0, 64, NeutralMemoryAccess.Write)).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Configure | NeutralDeviceRights.Write).Lease;
        var grant = runtime.BindDmaGrant(device, mapping, new(0, 64), NeutralDmaDirection.DeviceWritesMemory).Grant;
        Assert.Equal(NeutralDmaAcquireDecision.NotPrepared, runtime.AcquireDmaVisibility(grant).Decision);
        var first = runtime.PrepareDmaVisibility(grant);
        Assert.Equal(NeutralDmaAcquireDecision.Acquired, runtime.AcquireDmaVisibility(grant).Decision);
        Assert.Equal(NeutralDmaAcquireDecision.AlreadyAcquired, runtime.AcquireDmaVisibility(grant).Decision);
        var second = runtime.PrepareDmaVisibility(grant);
        Assert.True(second.Evidence.Cycle.Value > first.Evidence.Cycle.Value);
        var acquired = runtime.AcquireDmaVisibility(grant);
        Assert.Equal(second.Evidence.Cycle, acquired.Evidence.Cycle);
        Assert.Equal(NeutralDmaAcquireDecision.Stale, runtime.AcquireDmaVisibility(grant with { Epoch = new(2) }).Decision);
    }

    [Fact]
    public void DmaVisibilityClassifiesNotRequiredNotFoundAndRevoked()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mapping = runtime.MapOwnedRegion(domain, new(0, 16, NeutralMemoryAccess.Read)).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Configure | NeutralDeviceRights.Read).Lease;
        var grant = runtime.BindDmaGrant(device, mapping, new(0, 16), NeutralDmaDirection.DeviceReadsMemory).Grant;
        Assert.Equal(NeutralDmaAcquireDecision.NotRequired, runtime.AcquireDmaVisibility(grant).Decision);
        Assert.Equal(NeutralDmaPrepareDecision.NotFound, runtime.PrepareDmaVisibility(grant with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed, runtime.CloseDmaGrant(grant).Decision);
        Assert.Equal(NeutralDmaGrantCloseDecision.Revoked, runtime.CloseDmaGrant(grant).Decision);
        Assert.Equal(NeutralDmaAcquireDecision.Revoked, runtime.AcquireDmaVisibility(grant).Decision);
    }

    [Fact]
    public void DmaOperationsPreserveLookupClassification()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var mapping = runtime.MapOwnedRegion(domain, new(0, 16, NeutralMemoryAccess.Read)).Lease;
        var device = runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Configure | NeutralDeviceRights.Read).Lease;
        var missingDevice = device with { Handle = new(999) };
        Assert.Equal(NeutralDmaGrantDecision.NotFound, runtime.BindDmaGrant(missingDevice, mapping, new(0, 1), NeutralDmaDirection.DeviceReadsMemory).Decision);
        Assert.Equal(NeutralDmaGrantDecision.Stale, runtime.BindDmaGrant(device with { Epoch = new(2) }, mapping, new(0, 1), NeutralDmaDirection.DeviceReadsMemory).Decision);
        var grant = runtime.BindDmaGrant(device, mapping, new(0, 1), NeutralDmaDirection.DeviceReadsMemory).Grant;
        Assert.Equal(NeutralDmaGrantCloseDecision.NotFound, runtime.CloseDmaGrant(grant with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralDmaPrepareDecision.Stale, runtime.PrepareDmaVisibility(grant with { Epoch = new(2) }).Decision);
        Assert.Equal(NeutralDmaAcquireDecision.NotFound, runtime.AcquireDmaVisibility(grant with { Handle = new(999) }).Decision);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed, runtime.CloseDmaGrant(grant).Decision);
        Assert.Equal(NeutralDmaPrepareDecision.Revoked, runtime.PrepareDmaVisibility(grant).Decision);

        var device2 = runtime.BindDevice(domain, new("device2"), NeutralDeviceRights.Configure | NeutralDeviceRights.Read).Lease;
        Assert.Equal(NeutralDeviceCloseDecision.Closed, runtime.CloseDevice(device2).Decision);
        Assert.Equal(NeutralDmaGrantDecision.Revoked, runtime.BindDmaGrant(device2, mapping, new(1, 1), NeutralDmaDirection.DeviceReadsMemory).Decision);
    }

    [Fact]
    public void AuthorityAllocationExhaustionReturnsTypedFaultWithoutZeroIdentity()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        SetCounter(runtime, "_nextHandle", ulong.MaxValue);
        Assert.Equal(NeutralDomainBindDecision.Faulted, runtime.Bind(NeutralDomainProfile.OrdinaryService).Decision);

        runtime = new NeutralDomainRuntimeFacade();
        var domain = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        SetCounter(runtime, "_nextResource", ulong.MaxValue);
        Assert.Equal(NeutralDeviceBindDecision.Faulted, runtime.BindDevice(domain, new("device"), NeutralDeviceRights.Read).Decision);
    }

    private static void SetCounter(NeutralDomainRuntimeFacade runtime, string name, ulong value)
    {
        var field = typeof(NeutralDomainRuntimeFacade).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(runtime, value);
    }
}
