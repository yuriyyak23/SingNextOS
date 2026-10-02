using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlType3MemoryProviderTests
{
    [Fact]
    public void CxlLiveReceiptsHoldExactDeviceUntilAllPlacementsClose()
    {
        var s = CreateScenario();
        var other = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        var first = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var second = s.Memory.Place(s.Owner, other.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.True(s.Memory.Close(first.PlacementId).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.True(s.Memory.Close(second.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
        Assert.Equal(1, s.Platform.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CxlCreationCallbackCannotCloseParentBeforeLateReceipt(bool memoryStage, bool capabilityRevoke)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model), fabricFactory: model => fabric = new(model));
        var capability = DeviceCapability(s);
        var closure = KernelResult.Ok();
        Action callback = () => closure = capabilityRevoke ? s.Kernel.RevokeCapability(capability)
            : s.Kernel.RevokePlatformDevice(s.Handle, s.Lease);
        if (memoryStage) memory!.BindHook = callback;
        else fabric!.BindHook = callback;
        var result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformBindingActive, closure.Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(!capabilityRevoke, result.IsSuccess);
        if (capabilityRevoke) Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        var exited = s.Kernel.TerminateProcess(s.Handle);
        Assert.True(exited.IsSuccess, $"{exited.Error}: {exited.Message}; {s.Kernel.QueryProcessTeardown(s.Handle).Value}");
        Assert.Equal(KernelError.InvalidRegionState, s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(1, s.Platform.DeviceRevokeCalls);
    }

    [Fact]
    public async Task CxlPendingReceiptBlocksConcurrentParentDeviceClosure()
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        memory!.BindHook = () => { entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var pending = Task.Run(() => s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
            Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        }
        finally { resume.Set(); }
        var result = await pending;
        Assert.True(result.IsSuccess, result.Message);
        Assert.True(s.Memory.Close(result.Value!.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Fact]
    public void CxlFreshDeviceCapabilityAdmissionRejectsDirectOwnerRevocation()
    {
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(fabricFactory: model => fabric = new(model));
        Assert.True(s.Kernel.CapabilityAuthority.Revoke(DeviceCapability(s)).IsSuccess);
        var result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, fabric!.BindCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Fact]
    public void CxlRevokedSourceForbidsNewMemoryEffectButAllowsExactClosureRetry()
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var binding = s.Model.QueryMemory(placed.MemoryBindingId).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokeCapability(DeviceCapability(s)).Error);
        Assert.Equal(KernelError.CapabilityRevoked, s.Bridge.BindMemory(s.Owner, binding.BackingLease, binding.FabricBinding).Error);
        Assert.Equal(1, memory!.BindCalls);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.True(s.Memory.Close(placed.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokeCapability(DeviceCapability(s)).IsSuccess);
        Assert.Equal(1, s.Platform.DeviceRevokeCalls);
    }

    [Fact]
    public void CxlPendingChildBlocksFabricReleaseUntilReceiptTracking()
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        var release = KernelResult.Ok();
        memory!.BindHook = () =>
        {
            var backing = s.Kernel.Regions.InspectionSnapshot().Single(item => item.Region.Handle == s.Buffer.Handle).BackingLease!;
            var binding = s.Model.QueryMemory(new(1)).Value!;
            Assert.Equal(backing.Handle, binding.BackingLease);
            release = s.Bridge.ReleaseFabric(binding.FabricBinding);
        };
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformBindingActive, release.Error);
        Assert.True(placed.IsSuccess, placed.Message);
        Assert.True(s.Memory.Close(placed.Value!.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    private static CapabilityId DeviceCapability(Scenario s) => s.Kernel.CapabilityAuthority.InspectionSnapshot()
        .Single(item => item.Descriptor.ResourceKind == ResourceKind.Device && item.Descriptor.ResourceId == s.Lease.Device.ResourceId)
        .Descriptor.CapabilityId;

    [Fact]
    public void CxlMalformedChildReceiptCannotUnpinOriginalDeviceOrFabric()
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        memory!.RewriteReceipt = binding => binding with { FabricBinding = binding.FabricBinding with { BindingId = new(binding.FabricBinding.BindingId.Value + 100) } };
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.ExternalEffectUncontained, placed.Error);
        var actual = s.Model.QueryMemory(new(1)).Value!;
        Assert.Equal(KernelError.ExternalEffectUncontained, s.Bridge.ReleaseFabric(actual.FabricBinding).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CxlResetBeforeOrDuringClosureCannotReleaseParentReservation(bool duringClosure)
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        if (duringClosure) memory!.AfterReleaseHook = () => Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        else Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        var close = s.Memory.Close(placed.PlacementId);
        Assert.Equal(duringClosure ? KernelError.ExternalEffectUncontained : KernelError.StaleGeneration, close.Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CxlStaleParentGenerationOrSubjectHasNoEffectOrRetainedReservation(bool wrongSubject)
    {
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(fabricFactory: model => fabric = new(model));
        var lease = wrongSubject ? s.Lease with
        {
            DomainBinding = s.Lease.DomainBinding with { Subject = s.Subject with { Process = s.Handle with { Generation = s.Handle.Generation + 1 } } }
        } : s.Lease with { Generation = new(s.Lease.Generation.Value + 1) };
        var result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(wrongSubject ? KernelError.WrongPlatformDomain : KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, fabric!.BindCalls);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        var retry = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner);
        Assert.True(retry.IsSuccess);
        Assert.True(s.Kernel.Regions.ReleaseBacking(retry.Value!.Handle, s.Owner).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NotAcceptedBackingCreationRequiresExactFreshTuple(bool memoryStage, bool reset)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        Action reject = () =>
        {
            if (reset) Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);
        };
        if (memoryStage) memory!.RejectBind = reject;
        else fabric!.RejectBind = reject;
        var result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(reset ? KernelError.ExternalEffectUncontained : KernelError.PlatformUnavailable, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        if (reset)
        {
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
            Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        }
        else
        {
            var retry = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner);
            Assert.True(retry.IsSuccess);
            Assert.True(s.Kernel.Regions.ReleaseBacking(retry.Value!.Handle, s.Owner).IsSuccess);
            Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        }
    }

    [Fact]
    public void NotAcceptedMemoryCreationWithFailedTupleObservationRetainsBacking()
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        memory!.RejectBind = () => fabric!.QueryHook = () => throw new InvalidOperationException("tuple observation lost");
        var result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        fabric!.QueryHook = null;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
    }

    [Fact]
    public async Task PendingMemoryCreationBlocksConcurrentProcessReclaimUntilReceiptTracking()
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        memory!.BindHook = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var creation = Task.Run(() => s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.TerminateProcess(s.Handle).Error);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.ExternalEffectUncontained, (await creation).Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.True(s.Kernel.ObserveProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetDuringBackingCreationBalancesAdmissionWithoutInventingClosure(bool memoryStage)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        var reset = false;
        Action callback = () => reset = s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess;
        if (memoryStage) memory!.BindHook = callback;
        else fabric!.BindHook = callback;
        var result = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(reset);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessExitInsideBackingCreationRetainsLateReceiptForActualTeardown(bool memoryStage)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        var exit = KernelResult.Ok();
        Action callback = () => exit = s.Kernel.TerminateProcess(s.Handle);
        if (memoryStage) memory!.BindHook = callback;
        else fabric!.BindHook = callback;
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.PlatformBindingDraining, exit.Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, placed.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
        var retried = s.Kernel.ObserveProcessTeardown(s.Handle);
        Assert.True(retried.IsSuccess);
        Assert.True(retried.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void ClosureAdmissionInsideMemoryCreationCannotLosePendingReceipt()
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        var closure = KernelResult.Ok();
        memory!.BindHook = () =>
        {
            var lease = s.Kernel.Regions.InspectionSnapshot().Single(item => item.Region.Handle == s.Buffer.Handle).BackingLease!;
            closure = s.Kernel.Regions.BeginBackingClosure(lease.Handle, s.Owner);
        };
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);
        Assert.Equal(KernelError.PlatformBindingDraining, closure.Error);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.True(s.Memory.Close(placed.Value!.PlacementId).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackingClosureBlocksNewMappingAndBackingEffectsUntilExactRelease(bool fails)
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var binding = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        memory!.ReleaseHook = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        s.Model.MemoryReleaseFails = fails;
        var close = Task.Run(() => s.Memory.Close(placement.PlacementId));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability, s.Buffer.Handle,
                    PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Error);
            Assert.False(s.Kernel.Regions.HasPlatformMappingReservation(s.Buffer.Handle, s.Owner));
            Assert.Equal(KernelError.PlatformBindingDraining,
                s.Bridge.BindMemory(s.Owner, binding.BackingLease, binding.FabricBinding).Error);
        }
        finally { resume.Set(); }
        Assert.Equal(!fails, (await close).IsSuccess);
        memory.ReleaseHook = null;
        if (fails)
        {
            Assert.Equal(KernelError.PlatformBindingDraining,
                s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability, s.Buffer.Handle,
                    PlatformMemoryAccess.Read).Error);
            s.Model.MemoryReleaseFails = false;
            Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        }
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability,
            s.Buffer.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongOwnerOrStaleBackingClosureAdmissionCannotPartiallyDenyMapping(bool stale)
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var binding = s.Model.QueryMemory(placement.MemoryBindingId).Value!;
        var handle = stale ? binding.BackingLease with { Generation = binding.BackingLease.Generation + 1 } : binding.BackingLease;
        var owner = stale ? s.Owner : new RegionOwner(new(9999), s.Handle.Generation);
        Assert.False(s.Kernel.Regions.BeginBackingClosure(handle, owner).IsSuccess);
        Assert.True(s.Kernel.Regions.ValidateBacking(binding.BackingLease, s.Owner).IsSuccess);
        Assert.True(s.Kernel.Regions.ReservePlatformMapping(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Memory.Close(placement.PlacementId).Error);
        Assert.True(s.Kernel.Regions.ReleasePlatformMappingReservation(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MemoryClosureSurvivesFabricFailureForDirectAndTeardownRetry(bool throws, bool teardown)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        if (throws) fabric!.UnbindHook = () => throw new InvalidOperationException("fabric closure failure");
        else s.Model.FabricUnbindFails = true;
        Assert.False(teardown ? s.Kernel.TerminateProcess(s.Handle).IsSuccess : s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(1, memory!.ReleaseCalls);
        Assert.Equal(1, fabric!.UnbindCalls);
        fabric.UnbindHook = null;
        s.Model.FabricUnbindFails = false;
        if (teardown)
        {
            Assert.False(s.Kernel.QueryProcessTeardown(s.Handle).Value.LocalReclaimCompleted);
            var retried = s.Kernel.ObserveProcessTeardown(s.Handle);
            Assert.True(retried.IsSuccess);
            Assert.True(retried.Value.LocalReclaimCompleted);
        }
        else Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(2, fabric.UnbindCalls);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostSuccessfulClosureReplyNeverBecomesClosureFromNotFound(bool memoryReply)
    {
        CallbackMemoryProvider? memory = null;
        CallbackFabricProvider? fabric = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model),
            fabricFactory: model => fabric = new(model));
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Action lost = () => throw new InvalidOperationException("successful provider closure reply lost");
        if (memoryReply) memory!.AfterReleaseHook = lost;
        else fabric!.AfterUnbindHook = lost;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        memory!.AfterReleaseHook = null;
        fabric!.AfterUnbindHook = null;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(memoryReply ? 0 : 1, fabric.UnbindCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedClosureBalancesInterlockAndRetainsBackingUntilRealRetry(bool throws)
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        if (throws) memory!.ReleaseHook = () => throw new InvalidOperationException("closure exception");
        else s.Model.MemoryReleaseFails = true;
        Assert.False(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive,
            s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
        memory!.ReleaseHook = null;
        s.Model.MemoryReleaseFails = false;
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
        Assert.Equal(2, memory.ReleaseCalls);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
        var replacement = s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner);
        Assert.True(replacement.IsSuccess);
        Assert.True(s.Kernel.Regions.ReleaseBacking(replacement.Value!.Handle, s.Owner).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentHealthClosePreservesReleasedWithoutLateDamage()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var health = Task.Run(() => s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner,
            new CallbackHealthProvider(s.Model, () =>
            {
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            })));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
            Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Refresh(placement.PlacementId).Value!.State);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.StaleGeneration, (await health).Error);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public async Task OverlappingHealthObservationsCommitOnePlacementConsequence()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        using var entered = new CountdownEvent(2);
        using var resume = new ManualResetEventSlim();
        var provider = new CallbackHealthProvider(s.Model, () =>
        {
            entered.Signal();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, provider))).ToArray();
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); }
        finally { resume.Set(); }
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, item => item.IsSuccess);
        Assert.Single(results, item => item.Error == KernelError.StaleGeneration);
        Assert.Single(s.Kernel.Regions.SnapshotDamage());
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public async Task PendingCloseRejectsOverlapOutsidePlacementLock()
    {
        CallbackMemoryProvider? memory = null;
        var s = CreateScenario(memoryFactory: model => memory = new(model));
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        memory!.ReleaseHook = () =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var close = Task.Run(() => s.Memory.Close(placement.PlacementId));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Memory.Close(placement.PlacementId).Error);
            Assert.Equal(CxlMemoryPlacementState.Draining, s.Memory.Refresh(placement.PlacementId).Value!.State);
            Assert.Equal(KernelError.InvalidTransition,
                s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model).Error);
        }
        finally { resume.Set(); }
        Assert.True((await close).IsSuccess);
        Assert.Equal(1, memory.ReleaseCalls);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public void ClosingPlacementDuringHealthCallbackCannotResurrectReleasedPlacement()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        var closed = false;
        var result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner,
            new CallbackHealthProvider(s.Model, () => closed = s.Memory.Close(placement.PlacementId).IsSuccess));
        Assert.True(closed);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);
    }

    private sealed class CallbackHealthProvider(ICxlType3HealthEvidenceProvider provider, Action callback)
        : ICxlType3HealthEvidenceProvider
    {
        public PlatformAuthorityResult<CxlType3HealthObservation> QueryHealth(CxlType3HealthObservationRequest request)
        {
            var observation = provider.QueryHealth(request);
            callback();
            return observation;
        }
    }

    [Fact]
    public void ModelHealthEvidenceQuarantinesOnlyTheExactCxlBackedSubrange()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var overlapping = s.Kernel.AcquireRegionUse(s.Handle, s.Buffer.Handle,
            RegionUseMode.ReadOnly, new(8, 8)).Value!;
        var disjoint = s.Kernel.AcquireRegionUse(s.Handle, s.Buffer.Handle,
            RegionUseMode.ReadOnly, new(40, 8)).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);

        var damage = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model);

        Assert.True(damage.IsSuccess, damage.Message);
        Assert.Equal(new RegionUseRange(8, 8), damage.Value!.Range);
        Assert.Equal("cxl-type3-model", damage.Value.FailureDomain.ProviderId);
        Assert.Equal(CxlMemoryPlacementState.Quarantined,
            s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.StaleGeneration,
            s.Kernel.ValidateRegionUse(s.Handle, overlapping.Handle).Error);
        Assert.True(s.Kernel.ValidateRegionUse(s.Handle, disjoint.Handle).IsSuccess);
    }

    [Fact]
    public void DamagedRegionCannotBePlacedAgainAfterOldType3BackingCloses()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)).IsSuccess);
        Assert.True(s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model).IsSuccess);
        Assert.True(s.Memory.Close(placement.PlacementId).IsSuccess);

        var replacement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.Equal(KernelError.Quarantined, replacement.Error);
        Assert.Single(s.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void DeviceRebindRejectsPreviouslyInjectedCxlHealthObservation()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Failed, ProviderFaultClassV1.Omission, new(0, 16)).IsSuccess);
        Assert.True(s.Model.Rebind(s.Endpoint.EndpointId).IsSuccess);

        var result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, s.Model);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void DeviceRebindDuringHealthCallbackCannotCommitStaleRegionDamage()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.InjectHealthFault(placement.MemoryBindingId,
            ProviderHealthStateV1.Failed, ProviderFaultClassV1.Omission, new(0, 16)).IsSuccess);

        var result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner,
            new RebindingHealthProvider(s.Model, s.Endpoint.EndpointId));

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void MalformedCxlHealthTupleCannotReachRegionAuthority()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var provider = new MalformedHealthProvider();

        var result = s.Memory.ObserveAndQuarantine(placement.PlacementId, s.Owner, provider);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Empty(s.Kernel.Regions.SnapshotDamage());
        Assert.Equal(CxlMemoryPlacementState.Active,
            s.Memory.Query(placement.PlacementId).Value!.State);
    }

    [Fact]
    public void CxlHealthObservationIsEvidenceOnly()
    {
        var request = new CxlType3HealthObservationRequest(new("endpoint"), new(1), new(2), new(3), new(4), new(5));
        var observation = new CxlType3HealthObservation(request, new(1,
            new("provider", "domain", 1, 5), 1, ProviderHealthStateV1.Degraded,
            ProviderFaultClassV1.Omission, new(0, 1)));

        Assert.False(observation.AuthorizesRegionMutation);
        Assert.False(observation.AuthorizesReclaim);
    }

    [Fact]
    public void PlacementPolicyExplicitlyFallsBackOrFailsWhenCapacityIsMissing()
    {
        var model = new CxlType3ModelProvider();
        var planner = new CxlType3PlacementPlanner(model, model);
        var preferred = Intent(CxlMemoryPlacementPreference.CxlPreferred);
        var required = Intent(CxlMemoryPlacementPreference.CxlRequired);

        var fallback = planner.Plan(preferred, [new("missing")]);
        var failure = planner.Plan(required, [new("missing")]);

        Assert.True(fallback.IsSuccess);
        Assert.Equal(CxlMemoryPlacementKind.Local, fallback.Value!.Kind);
        Assert.False(failure.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, failure.Error);
    }

    [Fact]
    public void ModelPlacesAnOrdinaryOwnedBufferAndCloseRestoresNormalReclaim()
    {
        var s = CreateScenario();
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.DoesNotContain(typeof(CxlMemoryPlacementSnapshot).Assembly.GetExportedTypes(), type => type.Name == "CxlRegion");

        var blocked = s.Kernel.ReleaseRegion(s.Handle, s.Buffer);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, blocked.Error);
        Assert.True(s.Memory.Close(placed.Value!.PlacementId).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Handle, s.Buffer).IsSuccess);
    }

    [Fact]
    public void HotRemoveMarksControlledMigrationWithoutDanglingOrRevokingOwnership()
    {
        var s = CreateScenario();
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        Assert.True(s.Model.HotRemove(s.Endpoint.EndpointId).IsSuccess);

        var refresh = s.Memory.Refresh(placed.PlacementId);
        Assert.False(refresh.IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired, s.Memory.Query(placed.PlacementId).Value!.State);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, s.Memory.Close(placed.PlacementId).Error);
        Assert.Equal(CxlMemoryPlacementState.Quarantined, s.Memory.Query(placed.PlacementId).Value!.State);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReserveBacking(s.Buffer.Handle, s.Owner).Error);
    }

    [Fact]
    public void HotRemoveInvalidatesOnlyAffectedEndpointBindings()
    {
        var s = CreateScenario(twoEndpoints: true);
        var first = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var secondBuffer = s.Kernel.AllocateBuffer<byte>(s.Handle, 32).Value!;
        var endpoint2 = s.Model.QueryEndpoint(new("type3-1")).Value!;
        var second = s.Memory.Place(s.Owner, secondBuffer.Handle, s.Subject, s.Lease2!.Value, endpoint2, CxlMemoryPersistence.Volatile).Value!;

        Assert.True(s.Model.HotRemove(s.Endpoint.EndpointId).IsSuccess);
        Assert.False(s.Memory.Refresh(first.PlacementId).IsSuccess);
        Assert.True(s.Memory.Refresh(second.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.Active, s.Memory.Query(second.PlacementId).Value!.State);
        Assert.True(s.Memory.Close(second.PlacementId).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Handle, s.Lease2!.Value).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(second.PlacementId).Value!.State);
    }

    [Fact]
    public void CapacityGenerationAndCoherenceClaimsAreExplicit()
    {
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("e"), new("device:e"), 128).IsSuccess);
        var before = model.QueryEndpoint(new("e")).Value!;
        Assert.Equal(CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, before.Features);
        Assert.DoesNotContain(typeof(ICxlCoherentAccessProvider), model.GetType().GetInterfaces());
        Assert.True(model.Rebind(new("e")).IsSuccess);
        var after = model.QueryEndpoint(new("e")).Value!;
        Assert.NotEqual(before.DeviceGeneration, after.DeviceGeneration);
    }

    [Fact]
    public void CxlBackedRegionStillUsesExistingPlatformMappingForDeviceAccess()
    {
        var s = CreateScenario();
        var placed = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placed.IsSuccess, placed.Message);

        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Handle, s.Domain, s.RegionCapability,
            s.Buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.DoesNotContain(typeof(IPlatformDmaGrantProvider), s.Model.GetType().GetInterfaces());
    }

    [Fact]
    public void StaleDestructiveHandlesCannotDeleteCurrentFabricOrMemoryGeneration()
    {
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("exact"), new("device:exact"), 1024).IsSuccess);
        var endpoint = model.QueryEndpoint(new("exact")).Value!;
        var first = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var second = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var firstBacking = new RegionBackingLeaseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), 64);
        var secondBacking = new RegionBackingLeaseDescriptor(new(new(2), 1), new(new(2), new(1)), new(new(1), 1), 64);
        var firstMemory = model.BindMemory(first, firstBacking).Value!;
        var secondMemory = model.BindMemory(second, secondBacking).Value!;
        var ticket = model.BeginReconfiguration(first).Value!;
        var replacement = model.CompleteReconfiguration(ticket).Value!;

        Assert.Equal(PlatformAuthorityStatus.Stale, model.Unbind(first).Status);
        Assert.Equal(replacement, model.Query(first.BindingId).Value);
        Assert.Equal(new CxlFabricBindingGeneration(1), model.Query(second.BindingId).Value!.Generation);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            model.ReleaseMemory(firstMemory).Status);
        Assert.Equal(new CxlMemoryBindingGeneration(2), model.QueryMemory(firstMemory.BindingId).Value!.Generation);
        Assert.Equal(new CxlMemoryBindingGeneration(1), model.QueryMemory(secondMemory.BindingId).Value!.Generation);
        Assert.True(model.ReleaseMemory(model.QueryMemory(firstMemory.BindingId).Value!).IsSuccess);
        Assert.True(model.ReleaseMemory(secondMemory).IsSuccess);
    }

    [Fact]
    public void EffectCreatingModelRejectionsUseNotAcceptedOnlyBeforeEffect()
    {
        var model = new CxlType3ModelProvider();
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            model.Bind(new(new("missing"), new(1), 64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Status);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,
            model.AssignPoolCapacity(new("missing"), new("missing"), 64).Status);

        Assert.True(model.RegisterEndpoint(new("pre-effect"), new("device:pre-effect"), 128).IsSuccess);
        var endpoint = model.QueryEndpoint(new("pre-effect")).Value!;
        var fabric = model.Bind(new(endpoint.EndpointId, endpoint.DeviceGeneration, 64,
            CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var staleFabric = fabric with { Generation = new(fabric.Generation.Value + 1) };
        var backing = new RegionBackingLeaseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), 64);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted, model.BindMemory(staleFabric, backing).Status);
        Assert.Equal(fabric, model.Query(fabric.BindingId).Value);
    }

    [Fact]
    public void ProcessTeardownClosesLiveType3PlacementBeforeRegionReclaim()
    {
        var s = CreateScenario();
        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;

        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);

        Assert.Equal(CxlMemoryPlacementState.Released, s.Memory.Query(placement.PlacementId).Value!.State);
        Assert.Equal(KernelError.InvalidRegionState, s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).Error);
    }

    [Fact]
    public void Type3BackedInputAndOutputComposeWithPlannerAndType2StagedExecution()
    {
        var s = CreateScenario();
        var output = s.Kernel.AllocateBuffer<byte>(s.Handle, 64).Value!;
        var inputPlacement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var outputPlacement = s.Memory.Place(s.Owner, output.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile).Value!;
        var candidate = new ComputeProviderCandidate(new("type2-over-type3"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
            1024, 1, 1, true, false);
        var intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(s.Buffer.Handle, new(0, 64)), new(output.Handle, new(0, 64)),
            ComputePublicationPreference.StagedRequired, false, false);
        var plan = s.Kernel.PlanCompute(s.Handle, intent, new(true, true, true), [candidate]);
        Assert.True(plan.IsSuccess, plan.Message);

        var control = s.Kernel.AllocateBuffer<byte>(s.Handle, 8).Value!;
        var controlUse = s.Kernel.AcquireRegionUse(s.Handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 8)).Value!;
        var bridge = new CxlAuthorityBridge(s.Kernel, s.Model, s.Model, s.Model, s.Model,
            new UnsupportedCoherent(), new EvidenceOnly());
        var fabric = bridge.BindFabric(s.Owner, controlUse.Handle, s.Subject, s.Lease,
            new(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration, 8, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var accelerator = new CxlType2ModelAccelerator();
        var manager = new CxlFabricManagerAuthority(s.Kernel, s.Model);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        var execution = service.Submit(s.Handle, plan.Value!, [candidate], s.Subject, s.Lease, s.Endpoint, fabric, 1);
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.True(service.CompleteVisiblePublish(s.Handle, execution.Value!, [candidate], () => s.Buffer.Span.CopyTo(output.Span)).IsSuccess);

        Assert.True(s.Memory.Close(inputPlacement.PlacementId).IsSuccess);
        Assert.True(s.Memory.Close(outputPlacement.PlacementId).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MalformedFabricOrMemoryBinding_CompensationFailure_RemainsTrackedForTeardown(bool fabricFailure)
    {
        var s = CreateScenario();
        if (fabricFailure)
        {
            s.Model.ReturnMalformedFabricBinding = true;
            s.Model.FabricUnbindFails = true;
        }
        else
        {
            s.Model.ReturnMalformedMemoryBinding = true;
            s.Model.MemoryReleaseFails = true;
        }

        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease, s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmbiguousFabricOrMemoryAcceptance_QuarantinesBackingAndBlocksReclaim(bool fabricAcceptance)
    {
        var s = CreateScenario();
        if (fabricAcceptance)
            s.Model.FabricAcceptanceAmbiguous = true;
        else
            s.Model.MemoryAcceptanceAmbiguous = true;

        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderExceptionAfterFabricOrMemoryAcceptance_QuarantinesBacking(bool fabricAcceptance)
    {
        var s = CreateScenario();
        if (fabricAcceptance)
            s.Model.FabricThrowsAfterAcceptance = true;
        else
            s.Model.MemoryThrowsAfterAcceptance = true;

        var placement = s.Memory.Place(s.Owner, s.Buffer.Handle, s.Subject, s.Lease,
            s.Endpoint, CxlMemoryPersistence.Volatile);

        Assert.False(placement.IsSuccess);
        Assert.False(s.Kernel.Regions.HasPendingBackingCreations(s.Owner));
        Assert.Equal(KernelError.ExternalEffectUncontained, placement.Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.RevokePlatformDevice(s.Handle, s.Lease).Error);
        Assert.Equal(0, s.Platform.DeviceRevokeCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Buffer.Handle, s.Owner).IsSuccess);
    }

    private static CxlMemoryPlacementIntent Intent(CxlMemoryPlacementPreference preference) =>
        new(64, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive, preference, 10, 0);

    private static Scenario CreateScenario(bool twoEndpoints = false,
        Func<CxlType3ModelProvider, ICxlMemoryProvider>? memoryFactory = null,
        Func<CxlType3ModelProvider, ICxlFabricProvider>? fabricFactory = null)
    {
        var platform = new AuthorityProvider();
        var kernel = new RuntimeKernel(platform);
        var (_, handle) = TestFixtures.Create(kernel, 951, 952);
        var process = kernel.Processes.Resolve(handle).Value!;
        var owner = new RegionOwner(process.DomainId, handle.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, handle);
        var domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        PlatformDeviceLease Lease(string id)
        {
            var cap = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, id,
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
            return kernel.BindPlatformDevice(handle, domain, cap.CapabilityId,
                PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        }
        var lease = Lease("device:type3-0");
        PlatformDeviceLease? lease2 = null;
        var model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("type3-0"), lease.Device, 1024, latencyClass: 2, bandwidthClass: 5).IsSuccess);
        if (twoEndpoints)
        {
            lease2 = Lease("device:type3-1");
            Assert.True(model.RegisterEndpoint(new("type3-1"), lease2.Value.Device, 1024, latencyClass: 3, bandwidthClass: 4).IsSuccess);
        }
        var bridge = new CxlAuthorityBridge(kernel, model, model, fabricFactory?.Invoke(model) ?? model, memoryFactory?.Invoke(model) ?? model,
            new UnsupportedCoherent(), new EvidenceOnly());
        var memory = new CxlType3MemoryAuthority(kernel, bridge);
        var buffer = kernel.AllocateBuffer<byte>(handle, 64).Value!;
        var regionCapability = kernel.MintCapability(process.DomainId, handle, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Map).Value!.CapabilityId;
        return new(kernel, handle, owner, subject, domain, buffer, regionCapability, lease, lease2, model,
            model.QueryEndpoint(new("type3-0")).Value!, memory, bridge, platform);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle, RegionOwner Owner,
        PlatformDomainIdentity Subject, PlatformDomainBinding Domain, OwnedBuffer<byte> Buffer, CapabilityId RegionCapability, PlatformDeviceLease Lease,
        PlatformDeviceLease? Lease2, CxlType3ModelProvider Model, CxlEndpointSnapshot Endpoint,
        CxlType3MemoryAuthority Memory, CxlAuthorityBridge Bridge, AuthorityProvider Platform);

    private sealed class CallbackMemoryProvider(CxlType3ModelProvider model) : ICxlMemoryProvider
    {
        internal Func<CxlMemoryBinding, CxlMemoryBinding>? RewriteReceipt { get; set; }
        internal int BindCalls { get; private set; }
        internal Action? RejectBind { get; set; }
        internal Action? BindHook { get; set; }
        internal Action? ReleaseHook { get; set; }
        internal Action? AfterReleaseHook { get; set; }
        internal int ReleaseCalls { get; private set; }
        public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId endpoint) => model.QueryCapacity(endpoint);
        public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabric, RegionBackingLeaseDescriptor backing)
        {
            BindCalls++;
            if (RejectBind is { } reject)
            {
                reject();
                return PlatformAuthorityResult<CxlMemoryBinding>.Fail(PlatformAuthorityStatus.NotAccepted, "zero-effect rejection");
            }
            var result = model.BindMemory(fabric, backing);
            BindHook?.Invoke();
            if (result.IsSuccess && RewriteReceipt is { } rewrite)
                return PlatformAuthorityResult<CxlMemoryBinding>.Ok(rewrite(result.Value!));
            return result;
        }
        public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId binding) => model.QueryMemory(binding);
        public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding)
        {
            ReleaseCalls++;
            ReleaseHook?.Invoke();
            var result = model.ReleaseMemory(binding);
            if (result.IsSuccess) AfterReleaseHook?.Invoke();
            return result;
        }
    }

    private sealed class CallbackFabricProvider(CxlType3ModelProvider model) : ICxlFabricProvider
    {
        internal int BindCalls { get; private set; }
        internal Action? RejectBind { get; set; }
        internal Action? QueryHook { get; set; }
        internal Action? BindHook { get; set; }
        internal Action? UnbindHook { get; set; }
        internal Action? AfterUnbindHook { get; set; }
        internal int UnbindCalls { get; private set; }
        public PlatformAuthorityResult<CxlFabricBinding> Bind(CxlFabricBindingRequest request)
        {
            BindCalls++;
            if (RejectBind is { } reject)
            {
                reject();
                return PlatformAuthorityResult<CxlFabricBinding>.Fail(PlatformAuthorityStatus.NotAccepted, "zero-effect rejection");
            }
            var result = model.Bind(request);
            BindHook?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlFabricBinding> Query(CxlFabricBindingId binding)
        {
            QueryHook?.Invoke();
            return model.Query(binding);
        }
        public PlatformAuthorityResult Unbind(CxlFabricBinding binding)
        {
            UnbindCalls++;
            UnbindHook?.Invoke();
            var result = model.Unbind(binding);
            if (result.IsSuccess) AfterUnbindHook?.Invoke();
            return result;
        }
    }

    private sealed class UnsupportedCoherent : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding f, RegionUseDescriptor u) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding b) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class EvidenceOnly : ICxlSecurityEvidenceProvider
    {
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId id, CxlDeviceGeneration generation) =>
            PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }

    private sealed class MalformedHealthProvider : ICxlType3HealthEvidenceProvider
    {
        public PlatformAuthorityResult<CxlType3HealthObservation> QueryHealth(
            CxlType3HealthObservationRequest request) =>
            PlatformAuthorityResult<CxlType3HealthObservation>.Ok(new(
                request with { MemoryGeneration = new(request.MemoryGeneration.Value + 1) },
                new(1, new("malformed", "domain", request.DeviceGeneration.Value,
                        request.MemoryGeneration.Value), 1, ProviderHealthStateV1.Degraded,
                    ProviderFaultClassV1.Omission, new(0, 1))));
    }

    private sealed class RebindingHealthProvider(CxlType3ModelProvider model, CxlEndpointId endpointId)
        : ICxlType3HealthEvidenceProvider
    {
        public PlatformAuthorityResult<CxlType3HealthObservation> QueryHealth(
            CxlType3HealthObservationRequest request)
        {
            var observation = model.QueryHealth(request);
            if (!model.Rebind(endpointId).IsSuccess)
                throw new InvalidOperationException("The model rebind fault injection failed.");
            return observation;
        }
    }

    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        internal int DeviceRevokeCalls { get; private set; }
        private ulong _nextDevice = 1;
        public PlatformProviderDescriptor Descriptor { get; } = new(new("type3-test"), 1,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), subject));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease d, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(_nextDevice++), new(1), d, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            DeviceRevokeCalls++;
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(new(new(1), new(1), d, r, a));
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => PlatformAuthorityResult.Ok();
    }
}
