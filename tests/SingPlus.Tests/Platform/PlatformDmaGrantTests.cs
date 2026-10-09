using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaGrantTests
{
    [Theory]
    [InlineData("absent")][InlineData("replacement")][InlineData("pending")]
    public void DmaCleanupSourceChangeRefusesProviderEffectAndMappingRelease(string fault)
    {
        var scenario = CreateScenario(1336, 1436,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        var kernel = scenario.Kernel;
        var provider = scenario.Provider;
        var grant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_dmaGrants", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[grant.GrantId]!;
        provider.QueryObserver = () =>
        {
            provider.QueryObserver = null;
            if (fault == "absent") records.Remove(grant.GrantId);
            if (fault == "replacement") records[grant.GrantId] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (fault == "pending") original.GetType().GetProperty("ClosureInFlight")!.SetValue(original, false);
        };
        Assert.False(kernel.RevokePlatformDma(scenario.Subject, grant).IsSuccess);
        Assert.Equal(0, provider.DmaRevokeCalls);
        Assert.Null(original.GetType().GetProperty("KnownClosureGrant")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        var mapping = kernel.QueryPlatformRegionMappingLifecycle(scenario.Subject, scenario.Mapping.Mapping);
        Assert.True(mapping.IsSuccess);
        Assert.False(mapping.Value.LocalReservationReleased);
        Assert.True(kernel.Processes.Resolve(scenario.Subject).IsSuccess);
        Assert.False(kernel.RevokePlatformDma(scenario.Subject, grant).IsSuccess);
        Assert.Equal(0, provider.DmaRevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectCapabilityOwnerRevokeRefusesDmaPublication(bool mappingSource)
    {
        var scenario = CreateScenario(1335, 1435,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        var kind = mappingSource ? ResourceKind.MemoryRegion : ResourceKind.Device;
        var source = scenario.Kernel.CapabilityAuthority.InspectionSnapshot().Single(x => x.Descriptor.ResourceKind == kind).Descriptor.CapabilityId;
        scenario.Provider.AfterBind = () => Assert.True(scenario.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, scenario.Kernel.BindPlatformDma(scenario.Subject,
            scenario.Device, scenario.Mapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory).Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
    }
    [Fact]
    public async Task PausedDmaAdmissionBlocksConcurrentParentsBeforePublication()
    {
        var scenario = CreateScenario(1336, 1436,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = false;
        scenario.Provider.AfterBind = () => { entered.Set(); released = release.Wait(TimeSpan.FromSeconds(5)); };
        var admission = Task.Run(() => scenario.Kernel.BindPlatformDma(scenario.Subject,
            scenario.Device, scenario.Mapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingActive, scenario.Kernel.BindPlatformDma(scenario.Subject,
                scenario.Device, scenario.Mapping, 0, 32, PlatformDmaDirection.DeviceReadsMemory).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, scenario.Kernel.TerminateProcess(scenario.Subject).Error);
            Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
            Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        }
        finally { release.Set(); }
        Assert.False((await admission).IsSuccess);
        Assert.True(released);
        Assert.Equal(1, scenario.Provider.DmaBindCalls);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    public void RefusedDmaPublicationRetainsCleanupForActualOwnerRetry(int consumer, int fault)
    {
        var scenario = CreateScenario(1333, 1433,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        var kernel = scenario.Kernel;
        var provider = scenario.Provider;
        var capability = kernel.CapabilityAuthority.InspectionSnapshot().Single(x =>
            x.Descriptor.ResourceKind == ResourceKind.Device).Descriptor.CapabilityId;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_dmaCompletionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var outsideBind = false;
        var outsideCleanup = false;
        KernelResult nested = default;
        provider.AfterBind = () =>
        {
            provider.AfterBind = null;
            outsideBind = !Monitor.IsEntered(kernelGate) && !Monitor.IsEntered(bridgeGate);
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
        };
        provider.BeforeRevoke = () =>
        {
            outsideCleanup = !Monitor.IsEntered(kernelGate) && !Monitor.IsEntered(bridgeGate);
            nested = kernel.RevokePlatformDevice(scenario.Subject, scenario.Device);
            Assert.False(kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
                0, 16, PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
            if (fault == 0) provider.ThrowQuery = true;
            if (fault == 1) provider.Incarnation++;
            if (fault == 2) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowAfterRevoke = fault == 3;
        var refused = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory);
        Assert.False(refused.IsSuccess);
        Assert.True(outsideBind);
        Assert.True(outsideCleanup);
        Assert.Equal(KernelError.PlatformBindingDraining, nested.Error);
        Assert.Equal(1, provider.DmaBindCalls);
        Assert.Equal(1, provider.DmaRevokeCalls);
        provider.ThrowQuery = false;
        provider.ThrowAfterRevoke = false;
        provider.Incarnation = 1;
        provider.BeforeRevoke = null;
        var retry = consumer == 0 ? kernel.RevokeCapability(capability)
            : consumer == 1 ? kernel.RevokePlatformDevice(scenario.Subject, scenario.Device)
            : kernel.TerminateProcess(scenario.Subject);
        Assert.Equal(fault == 0, retry.IsSuccess);
        Assert.Equal(1, provider.DmaRevokeCalls);
        if (consumer != 2) Assert.Equal(fault == 0, kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(fault != 0, kernel.Processes.Resolve(scenario.Subject).IsSuccess);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 2)]
    [InlineData(5, 0)] [InlineData(5, 1)] [InlineData(5, 2)]
    public void MalformedDmaCleanupCannotUnpinOriginalInvocation(int mutation, int cleanup)
    {
        var scenario = CreateScenario(1334, 1434,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        scenario.Provider.ReturnMalformedGrant = true;
        scenario.Provider.MalformedGrantCase = mutation;
        if (cleanup == 1) scenario.Provider.DmaRevokeStatus = PlatformAuthorityStatus.Faulted;
        scenario.Provider.ThrowAfterRevoke = cleanup == 2;
        var originalPinObserved = false;
        scenario.Provider.BeforeRevoke = () =>
        {
            var pins = (global::System.Collections.IEnumerable)typeof(PlatformAuthorityBridge)
                .GetField("_dmaSubmissionFaultPins", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(scenario.Kernel.PlatformAuthority)!;
            originalPinObserved = pins.Cast<object>().Any();
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).Error);
        };
        Assert.Equal(KernelError.PlatformFaulted, scenario.Kernel.BindPlatformDma(scenario.Subject,
            scenario.Device, scenario.Mapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory).Error);
        Assert.True(originalPinObserved);
        scenario.Provider.BeforeRevoke = null;
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.False(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).IsSuccess);
        Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.True(scenario.Kernel.Processes.Resolve(scenario.Subject).IsSuccess);
    }
    [Fact]
    public async Task ProcessDrainAllowsConcurrentObservationWithoutDuplicateRevoke()
    {
        var scenario = CreateScenario(1332, 1432,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        Assert.True(scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = false;
        scenario.Provider.BeforeRevoke = () => { entered.Set(); released = release.Wait(TimeSpan.FromSeconds(5)); };
        var closure = Task.Run(() => scenario.Kernel.TerminateProcess(scenario.Subject));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.TerminateProcess(scenario.Subject).Error);
            var observed = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
            Assert.True(observed.IsSuccess);
            Assert.False(observed.Value!.LocalReclaimCompleted);
            var queried = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
            Assert.True(queried.IsSuccess);
            Assert.False(queried.Value!.LocalReclaimCompleted);
            Assert.False(scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
                0, 32, PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
            Assert.Equal(0, scenario.Provider.Log.Count(x => x == "revoke-device"));
        }
        finally { release.Set(); }
        Assert.True((await closure).IsSuccess);
        Assert.True(released);
        Assert.Equal(1, scenario.Provider.DmaBindCalls);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(1, scenario.Provider.Log.Count(x => x == "revoke-device"));
        Assert.False(scenario.Kernel.Processes.Resolve(scenario.Subject).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ProcessDrainBlocksNewDmaAndRetainsKnownCleanup(int fault)
    {
        var scenario = CreateScenario(1331, 1431,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        var kernel = scenario.Kernel;
        var provider = scenario.Provider;
        var grant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
        var gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var outside = false;
        KernelResult nested = default;
        KernelResult<PlatformDmaGrant> newGrant = default;
        provider.BeforeRevoke = () =>
        {
            outside = !Monitor.IsEntered(gate);
            nested = kernel.TerminateProcess(scenario.Subject);
            newGrant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
                0, 32, PlatformDmaDirection.DeviceReadsMemory);
            if (fault == 0) provider.ThrowQuery = true;
            if (fault == 1) provider.Incarnation++;
            if (fault == 2) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowAfterRevoke = fault == 3;
        Assert.False(kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.True(outside);
        Assert.Equal(KernelError.PlatformBindingDraining, nested.Error);
        Assert.False(newGrant.IsSuccess);
        Assert.Equal(1, provider.DmaBindCalls);
        Assert.Equal(0, provider.Log.Count(x => x == "revoke-device"));
        provider.ThrowQuery = false;
        provider.ThrowAfterRevoke = false;
        provider.Incarnation = 1;
        Assert.Equal(fault == 0, kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(1, provider.DmaRevokeCalls);
        Assert.Equal(fault == 0 ? 1 : 0, provider.Log.Count(x => x == "revoke-device"));
        Assert.Equal(fault != 0, kernel.Processes.Resolve(scenario.Subject).IsSuccess);
    }
    [Fact]
    public async Task DeviceParentDrainAllowsConcurrentObservationWithoutDuplicateRevoke()
    {
        var scenario = CreateScenario(1332, 1432,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        Assert.True(scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = false;
        scenario.Provider.BeforeRevoke = () => { entered.Set(); released = release.Wait(TimeSpan.FromSeconds(5)); };
        var closure = Task.Run(() => scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).Error);
            Assert.False(scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
                0, 32, PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
            Assert.Equal(0, scenario.Provider.Log.Count(x => x == "revoke-device"));
        }
        finally { release.Set(); }
        Assert.True((await closure).IsSuccess);
        Assert.True(released);
        Assert.Equal(1, scenario.Provider.DmaBindCalls);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(1, scenario.Provider.Log.Count(x => x == "revoke-device"));
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DeviceParentDrainBlocksNewDmaAndRetainsKnownCleanup(int fault)
    {
        var scenario = CreateScenario(1331, 1431,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        var kernel = scenario.Kernel;
        var provider = scenario.Provider;
        var grant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
        var gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var outside = false;
        KernelResult nested = default;
        KernelResult<PlatformDmaGrant> newGrant = default;
        provider.BeforeRevoke = () =>
        {
            outside = !Monitor.IsEntered(gate);
            nested = kernel.RevokePlatformDevice(scenario.Subject, scenario.Device);
            newGrant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
                0, 32, PlatformDmaDirection.DeviceReadsMemory);
            if (fault == 0) provider.ThrowQuery = true;
            if (fault == 1) provider.Incarnation++;
            if (fault == 2) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowAfterRevoke = fault == 3;
        Assert.False(kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).IsSuccess);
        Assert.True(outside);
        Assert.Equal(KernelError.PlatformBindingDraining, nested.Error);
        Assert.False(newGrant.IsSuccess);
        Assert.Equal(1, provider.DmaBindCalls);
        Assert.Equal(0, provider.Log.Count(x => x == "revoke-device"));
        provider.ThrowQuery = false;
        provider.ThrowAfterRevoke = false;
        provider.Incarnation = 1;
        Assert.Equal(fault == 0, kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).IsSuccess);
        Assert.Equal(1, provider.DmaRevokeCalls);
        Assert.Equal(fault == 0 ? 1 : 0, provider.Log.Count(x => x == "revoke-device"));
        Assert.Equal(fault == 0, kernel.TerminateProcess(scenario.Subject).IsSuccess);
    }
    [Fact]
    public async Task MappingParentDrainAllowsConcurrentObservationWithoutDuplicateRevoke()
    {
        var scenario = CreateScenario(1332, 1432,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        Assert.True(scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = false;
        scenario.Provider.BeforeRevoke = () => { entered.Set(); released = release.Wait(TimeSpan.FromSeconds(5)); };
        var closure = Task.Run(() => scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining,
                scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).Error);
            Assert.False(scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
                0, 32, PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
            Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        }
        finally { release.Set(); }
        Assert.True((await closure).IsSuccess);
        Assert.True(released);
        Assert.Equal(1, scenario.Provider.DmaBindCalls);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(1, scenario.Provider.MappingRevokeCalls);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MappingParentDrainBlocksNewDmaAndRetainsKnownCleanup(int fault)
    {
        var scenario = CreateScenario(1331, 1431,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        var kernel = scenario.Kernel;
        var provider = scenario.Provider;
        var grant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
        var gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var outside = false;
        KernelResult nested = default;
        KernelResult<PlatformDmaGrant> newGrant = default;
        provider.BeforeRevoke = () =>
        {
            outside = !Monitor.IsEntered(gate);
            nested = kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping);
            newGrant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
                0, 32, PlatformDmaDirection.DeviceReadsMemory);
            if (fault == 0) provider.ThrowQuery = true;
            if (fault == 1) provider.Incarnation++;
            if (fault == 2) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowAfterRevoke = fault == 3;
        Assert.False(kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.True(outside);
        Assert.Equal(KernelError.PlatformBindingDraining, nested.Error);
        Assert.False(newGrant.IsSuccess);
        Assert.Equal(1, provider.DmaBindCalls);
        Assert.Equal(0, provider.MappingRevokeCalls);
        provider.ThrowQuery = false;
        provider.ThrowAfterRevoke = false;
        provider.Incarnation = 1;
        Assert.Equal(fault == 0, kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        Assert.Equal(1, provider.DmaRevokeCalls);
        Assert.Equal(fault == 0 ? 1 : 0, provider.MappingRevokeCalls);
        Assert.Equal(fault == 0, kernel.TerminateProcess(scenario.Subject).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OrdinaryDmaClosureRetainsKnownReplyOutsideOwnerLocks(int fault)
    {
        var scenario = CreateScenario(1330, 1430,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure, PlatformMemoryAccess.Read);
        var kernel = scenario.Kernel;
        var provider = scenario.Provider;
        var grant = kernel.BindPlatformDma(scenario.Subject, scenario.Device, scenario.Mapping,
            0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_dmaCompletionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var outside = false;
        var queriesOutside = true;
        provider.QueryObserver = () => queriesOutside &= !Monitor.IsEntered(bridgeGate) && !Monitor.IsEntered(kernelGate);
        KernelResult nested = default;
        provider.BeforeRevoke = () =>
        {
            outside = !Monitor.IsEntered(bridgeGate) && !Monitor.IsEntered(kernelGate);
            nested = kernel.RevokePlatformDma(scenario.Subject, grant);
            if (fault == 0) provider.ThrowQuery = true;
            if (fault == 1) provider.Incarnation++;
            if (fault == 2) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowAfterRevoke = fault == 3;
        Assert.False(kernel.RevokePlatformDma(scenario.Subject, grant).IsSuccess);
        Assert.True(outside);
        Assert.True(queriesOutside);
        Assert.Equal(KernelError.PlatformBindingDraining, nested.Error);
        Assert.Equal(1, provider.DmaRevokeCalls);
        provider.ThrowQuery = false;
        provider.ThrowAfterRevoke = false;
        provider.Incarnation = 1;
        Assert.Equal(fault == 0, kernel.RevokePlatformDma(scenario.Subject, grant).IsSuccess);
        Assert.Equal(1, provider.DmaRevokeCalls);
        Assert.Equal(fault == 0, kernel.TerminateProcess(scenario.Subject).IsSuccess);
        if (fault != 0) Assert.True(kernel.Processes.Resolve(scenario.Subject).IsSuccess);
    }
    [Theory]
    [InlineData(PlatformDmaDirection.DeviceReadsMemory)]
    [InlineData(PlatformDmaDirection.DeviceWritesMemory)]
    public void PublishedMappingDamageDeniesNewDmaGrantBeforeProvider(PlatformDmaDirection direction)
    {
        var scenario = CreateScenario(1320, 1390,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var process = scenario.Kernel.Processes.Resolve(scenario.Subject).Value!;
        var owner = new RegionOwner(process.DomainId, scenario.Subject.Generation);
        var region = scenario.Mapping.Mapping.Region;
        Assert.True(scenario.Kernel.Regions.QuarantineSubrange(region, owner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(96, 8))).IsSuccess);

        var grant = scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device,
            scenario.Mapping, 32, 128, direction);
        Assert.Equal(KernelError.Quarantined, grant.Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);
        Assert.True(scenario.Kernel.Regions.HasPlatformMappingReservation(region, owner));
        Assert.Single(scenario.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void ExactDeviceAndMappingMaterializeBoundedAdmissionOnlyGrant()
    {
        var scenario = CreateScenario(1301, 1310,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        var result = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            32,
            128,
            PlatformDmaDirection.DeviceReadsMemory);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(scenario.Device, result.Value!.DeviceLease);
        Assert.Equal(scenario.Mapping, result.Value.Mapping);
        Assert.Equal(new PlatformDmaRange(32, 128), result.Value.Range);
        Assert.Equal(PlatformDmaDirection.DeviceReadsMemory, result.Value.Direction);
        Assert.Equal(1, scenario.Provider.DmaBindCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.DoesNotContain(
            typeof(RuntimeKernel).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            static method => method.Name.Contains("SubmitDma", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingDeviceOrRegionAuthorityIsRejectedBeforeProviderDmaAdmission()
    {
        var scenario = CreateScenario(1302, 1320,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);

        var missingDevice = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            default,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(KernelError.PlatformBindingNotFound, missingDevice.Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);

        var missingMapping = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            default,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(KernelError.PlatformBindingNotFound, missingMapping.Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);
    }

    [Fact]
    public void InvalidRangeDirectionAndAccessFailBeforeProviderDmaAdmission()
    {
        var scenario = CreateScenario(1303, 1330,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);

        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.BindPlatformDma(
                scenario.Subject,
                scenario.Device,
                scenario.Mapping,
                240,
                32,
                PlatformDmaDirection.DeviceReadsMemory).Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);

        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.BindPlatformDma(
                scenario.Subject,
                scenario.Device,
                scenario.Mapping,
                0,
                32,
                (PlatformDmaDirection)0xff).Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);

        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.BindPlatformDma(
                scenario.Subject,
                scenario.Device,
                scenario.Mapping,
                0,
                32,
                PlatformDmaDirection.DeviceWritesMemory).Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);
    }

    [Fact]
    public void DeviceRightsMustCoverConfigureAndDmaDirection()
    {
        var scenario = CreateScenario(1304, 1340,
            PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        var result = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, scenario.Provider.DmaBindCalls);
    }

    [Fact]
    public void MappingRevokeClosesDmaGrantBeforeMappingClosure()
    {
        var scenario = CreateScenario(1305, 1350,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        var grant = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).Value!;

        var revoke = scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject,
            scenario.Mapping);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(
            new[] { "bind-dma", "revoke-dma", "revoke-mapping" },
            scenario.Provider.Log.Where(static entry =>
                entry is "bind-dma" or "revoke-dma" or "revoke-mapping").ToArray());
        Assert.Equal(
            KernelError.PlatformBindingRevoked,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, grant).Error);
    }

    [Fact]
    public void DeviceRevokeClosesDmaGrantBeforeDeviceClosure()
    {
        var scenario = CreateScenario(1306, 1360,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        Assert.True(scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);

        var revoke = scenario.Kernel.RevokePlatformDevice(
            scenario.Subject,
            scenario.Device);

        Assert.True(revoke.IsSuccess, revoke.Message);
        var relevant = scenario.Provider.Log.Where(static entry =>
            entry is "bind-dma" or "revoke-dma" or "revoke-device").ToArray();
        Assert.Equal(new[] { "bind-dma", "revoke-dma", "revoke-device" }, relevant);
    }

    [Fact]
    public void DmaRevokeFaultPinsMappingClosureAndKeepsReservationLive()
    {
        var scenario = CreateScenario(1307, 1370,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        scenario.Provider.DmaRevokeStatus = PlatformAuthorityStatus.Faulted;
        Assert.True(scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            scenario.Mapping,
            0,
            32,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);

        var revoke = scenario.Kernel.RevokePlatformRegionMapping(
            scenario.Subject,
            scenario.Mapping);

        Assert.Equal(KernelError.PlatformFaulted, revoke.Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(PlatformExternalClosureState.Active,
            scenario.Kernel.QueryPlatformRegionMappingLifecycle(
                scenario.Subject,
                scenario.Mapping.Mapping).Value!.PlatformClosure);
    }

    [Fact]
    public void ExplicitNotAcceptedGrantClosureCanRetryWithoutReclaimingEarly()
    {
        var scenario = CreateScenario(1311, 1410,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        var grant = scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device,
            scenario.Mapping, 0, 32, PlatformDmaDirection.DeviceReadsMemory).Value!;
        scenario.Provider.DmaRevokeStatus = PlatformAuthorityStatus.NotAccepted;

        Assert.False(scenario.Kernel.RevokePlatformDma(scenario.Subject, grant).IsSuccess);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
        scenario.Provider.DmaRevokeStatus = null;
        Assert.True(scenario.Kernel.RevokePlatformDma(scenario.Subject, grant).IsSuccess);
        Assert.Equal(3, scenario.Provider.DmaRevokeCalls);
        Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).IsSuccess);
    }

    [Fact]
    public void DmaFeatureIsAdmissionOnlyAndPublicGrantContainsNoProviderOrHardwareIdentity()
    {
        var scenario = CreateScenario(1308, 1380,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        var feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaGrantContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        var surface = new[]
        {
            typeof(PlatformDmaDirection),
            typeof(PlatformDmaRange),
            typeof(PlatformDmaGrantId),
            typeof(PlatformDmaGrantGeneration),
            typeof(PlatformDmaGrant),
        };
        var forbidden = new[]
        {
            "PlatformProvider",
            "Neutral",
            "Physical",
            "BusAddress",
            "Iommu",
            "PageTable",
            "Pte",
            "Descriptor",
            "ScatterGather",
            "Queue",
            "Vmcs",
            "Vmx",
            "Lane",
            "Opcode",
        };
        foreach (var type in surface)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FailedDmaAdmissionCannotDiscardPossibleProviderGrant()
    {
        var scenario = CreateScenario(1309, 1390,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        scenario.Provider.DmaBindStatus = PlatformAuthorityStatus.Faulted;

        var bind = scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device,
            scenario.Mapping, 0, 32, PlatformDmaDirection.DeviceReadsMemory);

        Assert.False(bind.IsSuccess);
        Assert.Equal(1, scenario.Provider.DmaBindCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).Error);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedProviderGrantCleanupCannotProveOriginalMappingClosure(bool cleanupFails)
    {
        var scenario = CreateScenario(cleanupFails ? 1310UL : 1311UL,
            cleanupFails ? 1400UL : 1410UL,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        scenario.Provider.ReturnMalformedGrant = true;
        if (cleanupFails) scenario.Provider.DmaRevokeStatus = PlatformAuthorityStatus.Faulted;

        var bind = scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device,
            scenario.Mapping, 0, 32, PlatformDmaDirection.DeviceReadsMemory);

        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        var revoke = scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping);
        Assert.False(revoke.IsSuccess);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
    }

    [Fact]
    public void RejectedProviderGrantRevokedStatusDoesNotProveCleanup()
    {
        var scenario = CreateScenario(1312, 1420,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            PlatformMemoryAccess.Read);
        scenario.Provider.ReturnMalformedGrant = true;
        scenario.Provider.DmaRevokeStatus = PlatformAuthorityStatus.Revoked;

        var bind = scenario.Kernel.BindPlatformDma(scenario.Subject, scenario.Device,
            scenario.Mapping, 0, 32, PlatformDmaDirection.DeviceReadsMemory);

        Assert.Equal(KernelError.PlatformFaulted, bind.Error);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).Error);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).IsSuccess);
    }

    private static Scenario CreateScenario(
        ulong processId,
        ulong domainId,
        PlatformDeviceRights deviceRights,
        PlatformMemoryAccess mappingAccess)
    {
        var provider = new DmaProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma0",
            ToCapabilityRights(deviceRights));
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;

        var region = kernel.AllocateBuffer<byte>(subject, 512).Value!;
        var regionRights = CapabilityRights.Map;
        if ((mappingAccess & PlatformMemoryAccess.Read) != 0) regionRights |= CapabilityRights.Read;
        if ((mappingAccess & PlatformMemoryAccess.Write) != 0) regionRights |= CapabilityRights.Write;
        var regionCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            regionRights);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            regionCapability,
            region.Handle,
            64,
            256,
            mappingAccess).Value!;

        return new Scenario(kernel, provider, subject, device, mapping);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        var result = CapabilityRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0) result |= CapabilityRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0) result |= CapabilityRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0) result |= CapabilityRights.Configure;
        return result;
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        DmaProvider Provider,
        ProcessHandle Subject,
        PlatformDeviceLease Device,
        PlatformOwnedRegionSliceMapping Mapping);

    private sealed class DmaProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionRevocationProvider,
        IPlatformDmaGrantProvider,
        IPlatformProviderIncarnationSource
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformOperationId, PlatformCompletionReceipt> _operations = [];
        private PlatformProviderDomainLease? _domain;
        private ulong _nextDevice = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextOperation = 1;

        public int DmaBindCalls { get; private set; }
        public Action? BeforeRevoke { get; set; }
        public Action? AfterBind { get; set; }
        public ulong Incarnation { get; set; } = 1;
        public bool ThrowQuery { get; set; }
        public Action? QueryObserver { get; set; }
        public bool ThrowAfterRevoke { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                QueryObserver?.Invoke();
                return ThrowQuery ? throw new InvalidOperationException("Late DMA source query failure") : new(Incarnation);
            }
        }
        public int DmaRevokeCalls { get; private set; }
        public int MappingRevokeCalls { get; private set; }
        public PlatformAuthorityStatus? DmaRevokeStatus { get; set; }
        public PlatformAuthorityStatus? DmaBindStatus { get; set; }
        public bool ReturnMalformedGrant { get; set; }
        public int MalformedGrantCase { get; set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("dma-admission-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaGrantContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(1),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domain = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            var lease = new PlatformProviderDeviceLease(
                new PlatformProviderDeviceLeaseId(_nextDevice++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                device,
                rights);
            _devices.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            if (_grants.Values.Any(grant => grant.DeviceLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "DMA grant remains live.");
            Log.Add("revoke-device");
            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            var mapped = MapOwnedRegionSlice(
                domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return mapped.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(mapped.Status, mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (_domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain.");
            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            var mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, mapped);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapped);
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
            PlatformDmaGrantRequest request)
        {
            DmaBindCalls++;
            Log.Add("bind-dma");
            if (DmaBindStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(status,
                    "Injected ambiguous DMA grant admission failure.");
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) ||
                !_mappings.TryGetValue(request.MappingLease.MappingId, out var mapped) ||
                mapped.Slice != request.MappingSlice)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Exact device or mapping is not live.");
            }
            var grant = new PlatformProviderDmaGrant(
                new PlatformProviderDmaGrantId(_nextGrant++),
                new PlatformProviderLeaseGeneration(1),
                request.DeviceLease,
                request.MappingLease,
                request.Range,
                request.Direction);
            _grants.Add(grant.GrantId, grant);
            AfterBind?.Invoke();
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(
                !ReturnMalformedGrant ? grant : MalformedGrantCase switch
                {
                    1 => grant with { GrantId = new(0) },
                    2 => grant with { DeviceLease = grant.DeviceLease with { Generation = new(2) } },
                    3 => grant with { MappingLease = grant.MappingLease with { Generation = new(2) } },
                    4 => grant with { Range = new(0, 32) },
                    5 => grant with { Direction = PlatformDmaDirection.DeviceWritesMemory },
                    _ => grant with { Generation = new(0) },
                });
        }

        public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
        {
            BeforeRevoke?.Invoke();
            DmaRevokeCalls++;
            Log.Add("revoke-dma");
            if (DmaRevokeStatus is { } status)
                return PlatformAuthorityResult.Fail(status, "Injected DMA grant revoke fault.");
            _grants.Remove(grant.GrantId);
            if (ThrowAfterRevoke) throw new InvalidOperationException("DMA grant removed but reply lost");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (_grants.Values.Any(grant => grant.MappingLease == mapping))
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }
            MappingRevokeCalls++;
            Log.Add("revoke-mapping");
            _mappings.Remove(mapping.MappingId);
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            var receipt = new PlatformCompletionReceipt(
                operation.OperationId,
                operation.Generation,
                operation.DomainLease,
                PlatformCompletionState.Closed);
            _operations.Add(operation.OperationId, receipt);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation) =>
            _operations.TryGetValue(operation.OperationId, out var receipt)
                ? PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(receipt)
                : PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown operation.");
    }
}
