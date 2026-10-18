using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Fact]
    public void NativeIrqCloseMustFenceOriginalRealmAtProducingOwnerDuringKernelReclaim()
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var irqCap = kernel.MintCapability(root.Subject.DomainId,owner,ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/native-dma","irq",IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
        var endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        var binding = kernel.BindPlatformInterrupt(owner,device,irqCap,endpoint);
        Assert.True(binding.IsSuccess,binding.Message);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_irqBindings",flags)!.GetValue(provider)!;
        var original = Assert.Single(records.Values.Cast<object>());
        var nativeIrq = (NeutralInterruptLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
        var foreign = new NeutralDomainRuntimeFacade(); var foreignRoot = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var foreignDevice = foreign.BindDevice(foreignRoot,nativeIrq.DeviceLease.Device,nativeIrq.DeviceLease.Rights).Lease;
        var foreignIrq = foreign.BindInterrupt(foreignDevice,nativeIrq.Source).Lease;
        Assert.Equal(nativeIrq,foreignIrq);
        spy.BeforeOperation = name => { if (name == "CloseInterrupt") { spy.BeforeOperation = null; spy.Inner = foreign; } };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1,foreign.ActiveInterruptLeaseCount); Assert.Equal(1,native.ActiveInterruptLeaseCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.Inner = native;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0,native.ActiveInterruptLeaseCount); Assert.Equal(1,foreign.ActiveInterruptLeaseCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void NativeMmioCloseMustFenceOriginalRealmAtProducingOwnerDuringKernelReclaim()
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var mmioCap = kernel.MintCapability(root.Subject.DomainId,owner,ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/native-dma","registers",128),CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mmio = kernel.BindPlatformMmio(owner,device,mmioCap,0,64,PlatformMmioAccess.Read);
        Assert.True(mmio.IsSuccess,mmio.Message);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_mmioLeases",flags)!.GetValue(provider)!;
        var original = Assert.Single(records.Values.Cast<object>());
        var nativeMmio = (NeutralMmioLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
        var foreign = new NeutralDomainRuntimeFacade(); var foreignRoot = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var foreignDevice = foreign.BindDevice(foreignRoot,nativeMmio.DeviceLease.Device,nativeMmio.DeviceLease.Rights).Lease;
        var foreignMmio = foreign.MapMmio(foreignDevice,nativeMmio.Region,nativeMmio.Range,nativeMmio.Access).Lease;
        Assert.Equal(nativeMmio,foreignMmio);
        spy.BeforeOperation = name => { if (name == "CloseMmio") { spy.BeforeOperation = null; spy.Inner = foreign; } };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1,foreign.ActiveMmioLeaseCount); Assert.Equal(1,native.ActiveMmioLeaseCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.Inner = native;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0,native.ActiveMmioLeaseCount); Assert.Equal(1,foreign.ActiveMmioLeaseCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData("map","lost-reply")] [InlineData("map","source")] [InlineData("map","query-lost")] [InlineData("map","reentry")]
    [InlineData("close","lost-reply")] [InlineData("close","source")] [InlineData("close","query-lost")] [InlineData("close","reentry")] [InlineData("close","backend")]
    [InlineData("map","native-owner")] [InlineData("close","native-owner")] [InlineData("close","bridge-owner")] [InlineData("close","bridge-aba")]
    [InlineData("map","malformed")] [InlineData("close","malformed")]
    public void OriginalNativeMmioOutcomeDrivesExactKernelReclaimWithoutRepeatedEffect(string operation,string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var mmioCap = kernel.MintCapability(root.Subject.DomainId,owner,ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/native-dma","registers",128),CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(native)!;
        var nativeRoutes = (IDictionary)registry.GetType().GetField("Mmio",flags)!.GetValue(registry)!;
        var bridgeRoutes = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_mmioLeases",flags)!.GetValue(kernel.PlatformAuthority)!;
        object? nativeOwner = null; object? bridgeOwner = null; object? nativeKey = null; object? bridgeKey = null;
        var pinned = fault is "backend" or "bridge-aba" or "malformed";
        spy.MalformedMmio = fault == "malformed" && operation == "map";
        var maps = 0; var closes = 0;
        void Inject(string name)
        {
            if (name == "MapMmio") maps++; if (name == "CloseMmio") closes++;
            if (name != (operation == "map" ? "MapMmio" : "CloseMmio")) return;
            if (fault is "lost-reply" or "query-lost") throw new IOException("Original MMIO reply lost after effect.");
            if (fault == "source") spy.ThrowSource = true;
            if (fault == "backend") Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (fault == "reentry") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            if (fault == "native-owner")
            { nativeKey = nativeRoutes.Keys.Cast<object>().Single(); nativeOwner = nativeRoutes[nativeKey]!; nativeRoutes[nativeKey] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(nativeOwner,null)!; }
            if (fault is "bridge-owner" or "bridge-aba")
            {
                bridgeKey = bridgeRoutes.Keys.Cast<object>().Single(); bridgeOwner = bridgeRoutes[bridgeKey]!;
                if (fault == "bridge-owner") bridgeRoutes[bridgeKey] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(bridgeOwner,null)!;
                else { var leaseProperty = bridgeOwner.GetType().GetProperty("ProviderLease")!; leaseProperty.SetValue(bridgeOwner,leaseProperty.GetValue(bridgeOwner)); }
            }
        }
        if (operation == "map") spy.AfterOperation = Inject;
        var admitted = kernel.BindPlatformMmio(owner,device,mmioCap,0,64,PlatformMmioAccess.Read);
        Assert.Equal(operation == "close",admitted.IsSuccess);
        if (operation == "close") { maps = 1; spy.MalformedMmio = fault == "malformed"; spy.AfterOperation = Inject; }
        if (operation == "close") Assert.Equal(fault == "reentry",kernel.TerminateProcess(owner).IsSuccess);
        spy.AfterOperation = name => { if (name == "CloseMmio") closes++; if (fault == "query-lost" && name == (operation == "map" ? "ResolveMmioMap" : "ResolveMmioClose")) throw new IOException("Original MMIO lookup unavailable."); };
        spy.ThrowSource = false;
        if (nativeOwner is not null) nativeRoutes[nativeKey!] = nativeOwner;
        if (bridgeOwner is not null && fault == "bridge-owner") bridgeRoutes[bridgeKey!] = bridgeOwner;
        if (fault == "query-lost")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            spy.AfterOperation = name => { if (name == "CloseMmio") closes++; };
        }
        if (!(operation == "close" && fault == "reentry")) { var result = kernel.TerminateProcess(owner); if (pinned) Assert.False(result.IsSuccess); else Assert.True(result.IsSuccess,result.Message); }
        Assert.Equal(1,maps); Assert.Equal(operation == "map" && fault == "malformed" ? 0 : 1,closes); Assert.Equal(operation == "map" && fault == "malformed" ? 1 : 0,native.ActiveMmioLeaseCount);
        Assert.Equal(pinned ? RegionState.Owned : RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(pinned ? 512UL : 0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void NativeDeviceCloseMustFenceOriginalRealmAtProducingOwnerDuringKernelReclaim()
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var devices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases",flags)!.GetValue(provider)!;
        var original = Assert.Single(devices.Values.Cast<object>());
        var nativeDevice = (NeutralDeviceLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
        var foreign = new NeutralDomainRuntimeFacade();
        var foreignRoot = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var foreignDevice = foreign.BindDevice(foreignRoot,nativeDevice.Device,nativeDevice.Rights).Lease;
        Assert.Equal(nativeDevice,foreignDevice);
        spy.BeforeOperation = name => { if (name == "CloseDevice") { spy.BeforeOperation = null; spy.Inner = foreign; } };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1,foreign.ActiveDeviceLeaseCount); Assert.Equal(1,native.ActiveDeviceLeaseCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.Inner = native;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0,native.ActiveDeviceLeaseCount); Assert.Equal(1,foreign.ActiveDeviceLeaseCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(NeutralDeviceCloseDecision.Closed,foreign.CloseDevice(foreignDevice).Decision);
    }

    [Theory]
    [InlineData("lost-reply")] [InlineData("source")] [InlineData("reentry")]
    [InlineData("owner")] [InlineData("query-lost")] [InlineData("backend")]
    public void NativeOriginalDeviceCloseRetainsOneEffectThroughActualReclaim(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var devices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases",flags)!.GetValue(provider)!;
        var original = Assert.Single(devices.Values.Cast<object>());
        var nativeDevice = (NeutralDeviceLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
        var calls = 0;
        var queryFails = fault == "query-lost";
        spy.AfterOperation = name =>
        {
            if (name == "ResolveDeviceClose" && queryFails) throw new IOException("Original lookup reply lost.");
            if (name != "CloseDevice") return;
            calls++;
            if (fault is "lost-reply" or "query-lost") throw new IOException("Original Close reply lost after effect.");
            if (fault == "source") spy.ThrowSource = true;
            if (fault == "owner") devices[nativeDeviceKey()] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null)!;
            if (fault == "backend") Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (fault == "reentry") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        };
        object nativeDeviceKey() => devices.Keys.Cast<object>().Single();
        var first = kernel.TerminateProcess(owner);
        Assert.Equal(fault == "reentry",first.IsSuccess);
        if (!first.IsSuccess)
        {
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        spy.AfterOperation = null; spy.ThrowSource = false;
        if (fault == "owner") devices[devices.Keys.Cast<object>().Single()] = original;
        if (fault == "query-lost")
        {
            spy.AfterOperation = name => { if (name == "ResolveDeviceClose") throw new IOException("Still unavailable."); };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            spy.AfterOperation = null;
        }
        if (!first.IsSuccess) Assert.Equal(fault != "backend",kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1,calls); Assert.Equal(0,native.ActiveDeviceLeaseCount);
        Assert.Equal(fault == "backend" ? RegionState.Owned : RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(fault == "backend" ? 512UL : 0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData("lost-reply")] [InlineData("source")] [InlineData("reentry")] [InlineData("query-lost")]
    public void UnpublishedOriginalNativeDeviceBindIsResolvedOnlyForCleanup(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var binds = 0; var closes = 0;
        spy.AfterOperation = name =>
        {
            if (name == "CloseDevice") closes++;
            if (name != "BindDevice") return;
            binds++;
            if (fault is "lost-reply" or "query-lost") throw new IOException("Bind reply lost after effect.");
            if (fault == "source") spy.ThrowSource = true;
            if (fault == "reentry") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        };
        Assert.False(kernel.BindPlatformDevice(owner,root,cap,PlatformDeviceRights.Read | PlatformDeviceRights.Configure).IsSuccess);
        spy.AfterOperation = name => { if (name == "CloseDevice") closes++; if (fault == "query-lost" && name == "ResolveDeviceBind") throw new IOException("Original lookup unavailable."); };
        spy.ThrowSource = false;
        if (fault == "query-lost")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            spy.AfterOperation = name => { if (name == "CloseDevice") closes++; };
        }
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1,binds); Assert.Equal(1,closes); Assert.Equal(0,native.ActiveDeviceLeaseCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData("prepare","lost-reply")]
    [InlineData("prepare","source")]
    [InlineData("prepare","native-owner")]
    [InlineData("prepare","cycle-aba")]
    [InlineData("prepare","reentry")]
    [InlineData("prepare","malformed")]
    [InlineData("prepare","backend")]
    [InlineData("prepare","query-lost")]
    [InlineData("prepare","query-owner")]
    [InlineData("acquire","lost-reply")]
    [InlineData("acquire","source")]
    [InlineData("acquire","native-owner")]
    [InlineData("acquire","cycle-aba")]
    [InlineData("acquire","reentry")]
    [InlineData("acquire","malformed")]
    [InlineData("acquire","backend")]
    [InlineData("acquire","query-lost")]
    [InlineData("acquire","query-owner")]
    public void OriginalNativeDmaVisibilityConsumesExactGrantClosureAndKeepsIndependentPins(string operation,string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var memoryCap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Write | CapabilityRights.Map).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, root, memoryCap, region.Handle, 0, 128, PlatformMemoryAccess.Write).Value!;
        var admitted = kernel.BindPlatformDma(owner,device,mapping,0,64,PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(admitted.IsSuccess,admitted.Message);
        if (operation == "acquire") Assert.True(kernel.PreparePlatformDmaForDevice(owner,admitted.Value!).IsSuccess);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(native)!;
        var grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        var nativeKey = Assert.Single(grants.Keys.Cast<object>()); var nativeOwner = grants[nativeKey]!;
        var method = operation == "prepare" ? "PrepareDmaVisibility" : "AcquireDmaVisibility";
        spy.MalformedVisibility = fault == "malformed";
        spy.AfterOperation = name =>
        {
            if (name == "ResolveDmaVisibility" && fault == "query-owner")
            { spy.AfterOperation = null; grants[nativeKey] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(nativeOwner,null)!; }
            if (name == "ResolveDmaVisibility" && fault == "query-lost") throw new IOException("Original visibility query reply lost.");
            if (name != method) return;
            if (fault == "lost-reply") throw new IOException("Original visibility reply lost after effect.");
            if (fault == "source") spy.ThrowSource = true;
            if (fault == "native-owner") grants[nativeKey] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(nativeOwner,null)!;
            if (fault == "cycle-aba")
            {
                var state = nativeOwner.GetType().GetProperty("Visibility")!.GetValue(nativeOwner)!;
                var acquired = state.GetType().GetProperty("Acquired")!; var before = acquired.GetValue(state);
                acquired.SetValue(state,before); // Equal write advances producing source revision.
            }
            if (fault == "backend") Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (fault == "reentry")
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.False(kernel.PreparePlatformDmaForDevice(owner,admitted.Value!).IsSuccess);
                Assert.False(kernel.AcquirePlatformDmaForCpu(owner,admitted.Value!).IsSuccess);
            }
        };
        var success = operation == "prepare" ? kernel.PreparePlatformDmaForDevice(owner,admitted.Value!).IsSuccess : kernel.AcquirePlatformDmaForCpu(owner,admitted.Value!).IsSuccess;
        Assert.False(success); // Reentrant Terminate starts process drain; known visibility cannot publish a new effect.
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.AfterOperation = null; spy.ThrowSource = false; spy.MalformedVisibility = false; grants[nativeKey] = nativeOwner;
        if (!success)
        {
            Assert.False(kernel.PreparePlatformDmaForDevice(owner,admitted.Value!).IsSuccess);
            Assert.False(kernel.AcquirePlatformDmaForCpu(owner,admitted.Value!).IsSuccess);
        }
        var closed = kernel.TerminateProcess(owner);
        Assert.Equal(fault != "backend",closed.IsSuccess);
        Assert.Equal(1,spy.DmaPrepareCalls);
        Assert.Equal(operation == "acquire" ? 1 : 0,spy.DmaAcquireCalls);
        Assert.Equal(fault == "backend" ? 0 : 1,spy.DmaCloseCalls);
        Assert.Equal(fault == "backend" ? 1 : 0,native.ActiveDmaGrantCount);
        Assert.Equal(fault == "backend" ? RegionState.Owned : RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(fault == "backend" ? 512UL : 0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeDmaVisibilityMustFenceOriginalRealmAtProducingOwner(bool acquire)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var memoryCap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Write | CapabilityRights.Map).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, root, memoryCap, region.Handle, 0, 128, PlatformMemoryAccess.Write).Value!;
        var admitted = kernel.BindPlatformDma(owner,device,mapping,0,64,PlatformDmaDirection.DeviceWritesMemory);
        Assert.True(admitted.IsSuccess,admitted.Message);
        if (acquire) Assert.True(kernel.PreparePlatformDmaForDevice(owner,admitted.Value!).IsSuccess);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var grants = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_dmaGrants",flags)!.GetValue(provider)!;
        var original = Assert.Single(grants.Values.Cast<object>());
        var nativeGrant = (NeutralDmaGrant)original.GetType().GetProperty("HybridCpuGrant")!.GetValue(original)!;
        var foreign = new NeutralDomainRuntimeFacade();
        var foreignRoot = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var foreignDevice = foreign.BindDevice(foreignRoot,nativeGrant.DeviceLease.Device,nativeGrant.DeviceLease.Rights).Lease;
        var foreignMapping = foreign.MapOwnedRegion(foreignRoot,nativeGrant.MappingLease.Slice).Lease;
        var foreignGrant = foreign.BindDmaGrant(foreignDevice,foreignMapping,nativeGrant.Range,nativeGrant.Direction).Grant;
        Assert.Equal(nativeGrant,foreignGrant);
        if (acquire) Assert.True(foreign.PrepareDmaVisibility(foreignGrant).IsPrepared);
        var registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(foreign)!;
        var foreignGrants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        var foreignState = foreignGrants[foreignGrant.Handle]!;
        spy.BeforeOperation = name => { if (name == (acquire ? "AcquireDmaVisibility" : "PrepareDmaVisibility")) { spy.BeforeOperation = null; spy.Inner = foreign; } };
        var success = acquire ? kernel.AcquirePlatformDmaForCpu(owner,admitted.Value!).IsSuccess : kernel.PreparePlatformDmaForDevice(owner,admitted.Value!).IsSuccess;
        var visibility = foreignState.GetType().GetProperty("Visibility")!.GetValue(foreignState);
        if (acquire) Assert.False((bool)visibility!.GetType().GetProperty("Acquired")!.GetValue(visibility)!);
        else Assert.Null(visibility);
        Assert.False(success);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.Inner = native;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0,native.ActiveDmaGrantCount); Assert.Equal(1,foreign.ActiveDmaGrantCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed,foreign.CloseDmaGrant(foreignGrant).Decision);
    }

    [Theory]
    [InlineData("realm")] [InlineData("source")] [InlineData("mapping")] [InlineData("device-factory")]
    public void OriginalDmaBindNoEffectKeepsForeignGrantAndConsumesWithoutSyntheticClose(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var memoryCap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Read | CapabilityRights.Map).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, root, memoryCap, region.Handle, 0, 128, PlatformMemoryAccess.Read).Value!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var devices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", flags)!.GetValue(provider)!;
        var mappings = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerMappings", flags)!.GetValue(provider)!;
        var deviceRecord = Assert.Single(devices.Values.Cast<object>()); var mapRecord = Assert.Single(mappings.Values.Cast<object>());
        var nativeDevice = (NeutralDeviceLease)deviceRecord.GetType().GetProperty("HybridCpuLease")!.GetValue(deviceRecord)!;
        var nativeMap = (NeutralOwnedRegionMappingLease)mapRecord.GetType().GetProperty("HybridCpuLease")!.GetValue(mapRecord)!;
        var foreign = new NeutralDomainRuntimeFacade();
        var foreignRoot = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var foreignDevice = foreign.BindDevice(foreignRoot, nativeDevice.Device, nativeDevice.Rights).Lease;
        var foreignMap = foreign.MapOwnedRegion(foreignRoot, nativeMap.Slice).Lease;
        var foreignGrant = foreign.BindDmaGrant(foreignDevice,foreignMap,new(0,64),NeutralDmaDirection.DeviceReadsMemory).Grant;
        var mapKey = mappings.Keys.Cast<object>().Single();
        var factoryProperty = deviceRecord.GetType().GetProperty("AdmissionSource")!;
        var originalFactory = factoryProperty.GetValue(deviceRecord)!;
        spy.AfterOperation = name =>
        {
            if(name != "BindDmaGrant" || fault is not ("mapping" or "device-factory")) return;
            Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",flags)!.GetValue(provider)!));
            spy.Inner = native;
            if(fault == "mapping") mappings[mapKey] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(mapRecord,null);
            else factoryProperty.SetValue(deviceRecord,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(originalFactory,null));
        };
        spy.BeforeOperation = name => { if (name == "BindDmaGrant") { spy.BeforeOperation = null; spy.Inner = foreign; } };
        Assert.False(kernel.BindPlatformDma(owner,device,mapping,0,64,PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
        var admission = Assert.Single(((global::System.Collections.IList)deviceRecord.GetType().GetProperty("OriginalDmaAdmissions")!.GetValue(deviceRecord)!).Cast<object>());
        Assert.False((bool)admission.GetType().GetProperty("NoEffectConsumed")!.GetValue(admission)!);
        if(fault != "realm")
        {
            spy.ThrowSource = fault == "source";
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(0,spy.DmaCloseCalls); Assert.Equal(1,spy.DmaBindCalls);
            spy.ThrowSource = false;
        }
        Assert.Equal(0,native.ActiveDmaGrantCount); Assert.Equal(1,foreign.ActiveDmaGrantCount);
        Assert.Equal(0,spy.DmaCloseCalls);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.AfterOperation = null;
        mappings[mapKey] = mapRecord; factoryProperty.SetValue(deviceRecord,originalFactory);
        spy.Inner = native;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1,foreign.ActiveDmaGrantCount); Assert.Equal(0,spy.DmaCloseCalls); Assert.Equal(1,spy.DmaBindCalls);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed,foreign.CloseDmaGrant(foreignGrant).Decision);
    }

    [Theory]
    [InlineData("bind-lost")] [InlineData("bind-source")] [InlineData("bind-owner")]
    [InlineData("close-lost")] [InlineData("close-source")] [InlineData("close-owner")]
    [InlineData("close-reentry")] [InlineData("close-malformed")] [InlineData("bind-malformed")]
    [InlineData("bind-history-lost")]
    public void NativeOriginalDmaOutcomesReachKernelReclaimWithoutRepeatedEffect(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var memoryCap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Read | CapabilityRights.Map).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, root, memoryCap, region.Handle, 0, 128, PlatformMemoryAccess.Read).Value!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var devices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", flags)!.GetValue(provider)!;
        var deviceKey = Assert.Single(devices.Keys.Cast<object>()); var originalDevice = devices[deviceKey]!;
        var grants = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_dmaGrants", flags)!.GetValue(provider)!;
        object? grantKey = null; object? originalGrant = null;
        spy.MalformedDmaBind = fault == "bind-malformed"; spy.MalformedDmaClose = fault == "close-malformed";
        spy.AfterOperation = name =>
        {
            if (name == "BindDmaGrant" && fault.StartsWith("bind-"))
            {
                if (fault == "bind-source") spy.ThrowSource = true;
                if (fault == "bind-owner") devices[deviceKey] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(originalDevice, null)!;
                if (fault is "bind-lost" or "bind-history-lost") throw new IOException("Original native DMA Bind reply lost after effect.");
            }
            if (name == "CloseDmaGrant" && fault.StartsWith("close-"))
            {
                if (fault == "close-source") spy.ThrowSource = true;
                if (fault == "close-owner") grants[grantKey!] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(originalGrant, null)!;
                if (fault == "close-lost") throw new IOException("Original native DMA Close reply lost after effect.");
                if (fault == "close-reentry") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            }
        };
        var admitted = kernel.BindPlatformDma(owner, device, mapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(fault.StartsWith("close-"), admitted.IsSuccess);
        if (grants.Count != 0) { grantKey = Assert.Single(grants.Keys.Cast<object>()); originalGrant = grants[grantKey]!; }
        if (fault.StartsWith("bind-")) spy.AfterOperation = null;
        if (fault.StartsWith("bind-"))
        {
            var history = (IList)originalDevice.GetType().GetProperty("OriginalDmaAdmissions")!.GetValue(originalDevice)!;
            var fact = Assert.Single(history.Cast<object>());
            var originalRequest = (PlatformOriginalDmaGrantBindRequest)fact.GetType().GetProperty("OriginalRequest")!.GetValue(fact)!;
            Assert.False(provider.ResolveDmaGrantBind(new(originalRequest.Request)).IsSuccess); // Equal inputs are not original request.
            if (fault == "bind-history-lost") history.Clear();
        }

        var first = kernel.TerminateProcess(owner);
        Assert.Equal(fault == "close-reentry" || fault == "bind-lost", first.IsSuccess);
        if (!first.IsSuccess)
        {
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        spy.AfterOperation = null; spy.ThrowSource = false; devices[deviceKey] = originalDevice;
        if (originalGrant is not null) grants[grantKey!] = originalGrant;
        var terminal = first.IsSuccess ? first : kernel.TerminateProcess(owner);
        var malformed = fault.EndsWith("malformed") || fault == "bind-history-lost";
        Assert.Equal(!malformed, terminal.IsSuccess);
        Assert.Equal(1, spy.DmaBindCalls);
        Assert.Equal(fault is "bind-malformed" or "bind-history-lost" ? 0 : 1, spy.DmaCloseCalls);
        Assert.Equal(malformed ? RegionState.Owned : RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(malformed ? 512UL : 0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (malformed) { Assert.False(kernel.TerminateProcess(owner).IsSuccess); Assert.Equal(fault is "bind-malformed" or "bind-history-lost" ? 0 : 1, spy.DmaCloseCalls); }
    }

    [Theory]
    [InlineData("realm")] [InlineData("source")] [InlineData("mapping")]
    public void NativeDmaCloseMustFenceOriginalRealmAtProducingOwnerDuringKernelReclaim(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 768, 1110);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "native-original-dma",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.Device, "device/native-dma",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var memoryCap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Read | CapabilityRights.Map).Value!.CapabilityId;
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, root, memoryCap, region.Handle, 0, 128, PlatformMemoryAccess.Read).Value!;
        var grant = kernel.BindPlatformDma(owner, device, mapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(grant.IsSuccess, grant.Message);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var devices = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", flags)!.GetValue(provider)!;
        var mappings = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerMappings", flags)!.GetValue(provider)!;
        var originalDevice = (NeutralDeviceLease)Assert.Single(devices.Values.Cast<object>()).GetType().GetProperty("HybridCpuLease")!.GetValue(Assert.Single(devices.Values.Cast<object>()))!;
        var originalMapping = (NeutralOwnedRegionMappingLease)Assert.Single(mappings.Values.Cast<object>()).GetType().GetProperty("HybridCpuLease")!.GetValue(Assert.Single(mappings.Values.Cast<object>()))!;
        var replacement = new NeutralDomainRuntimeFacade();
        var foreignRoot = replacement.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var foreignDevice = replacement.BindDevice(foreignRoot, originalDevice.Device, originalDevice.Rights).Lease;
        var foreignMapping = replacement.MapOwnedRegion(foreignRoot, originalMapping.Slice).Lease;
        Assert.Equal(originalDevice, foreignDevice); Assert.Equal(originalMapping, foreignMapping);
        var foreignGrant = replacement.BindDmaGrant(foreignDevice, foreignMapping, new(0, 64), NeutralDmaDirection.DeviceReadsMemory).Grant;
        var mapKey = mappings.Keys.Cast<object>().Single(); var mapOwner = mappings[mapKey]!;
        spy.AfterOperation = name =>
        {
            if(name != "CloseDmaGrant") return;
            Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate",flags)!.GetValue(provider)!));
            if(fault == "source") spy.ThrowSource = true;
            if(fault == "mapping")
            {
                spy.Inner = native;
                mappings[mapKey] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(mapOwner,null);
            }
        };
        spy.BeforeOperation = name => { if (name == "CloseDmaGrant") { spy.BeforeOperation = null; spy.Inner = replacement; } };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        var providerGrants = (IDictionary)provider.GetType().GetField("_dmaGrants",flags)!.GetValue(provider)!;
        var originalGrant = Assert.Single(providerGrants.Values.Cast<object>());
        var retainedClose = originalGrant.GetType().GetProperty("NativeClose")!.GetValue(originalGrant);
        Assert.NotNull(retainedClose);
        Assert.False((bool)retainedClose!.GetType().GetProperty("NoEffectConsumed")!.GetValue(retainedClose)!);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess); Assert.Equal(1,spy.DmaCloseCalls);
        Assert.Equal(1, replacement.ActiveDmaGrantCount);
        Assert.Equal(1, native.ActiveDmaGrantCount);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.AfterOperation = null; spy.ThrowSource = false; mappings[mapKey] = mapOwner;
        spy.Inner = native;
        var resumed = kernel.TerminateProcess(owner);
        Assert.True(resumed.IsSuccess || kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(2,spy.DmaCloseCalls);
        Assert.Equal(0, native.ActiveDmaGrantCount); Assert.Equal(1, replacement.ActiveDmaGrantCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed, replacement.CloseDmaGrant(foreignGrant).Decision);
    }

    [Theory]
    [InlineData("source")] [InlineData("owner")] [InlineData("lost-reply")]
    [InlineData("reentry")] [InlineData("malformed")]
    [InlineData("provider-reply-lost")]
    [InlineData("lost-reply-source")]
    public void OriginalRootCloseRetainsOutcomeAndConsumesWithoutRepeatingEffect(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var wrapped = DispatchProxy.Create<IAdmissionProvider, AdmissionSpy>();
        var wrapper = (AdmissionSpy)wrapped;
        wrapper.Inner = provider;
        wrapper.ThrowRootReply = fault == "provider-reply-lost";
        var kernel = new RuntimeKernel(fault == "provider-reply-lost" ? wrapped : provider);
        var (_, owner) = TestFixtures.Create(kernel, 764, 1106);
        var admin = TestFixtures.Create(kernel, 97, 997).Handle;
        var cap = kernel.MintCapability(new DomainId(997), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, cap, owner, "original-close", [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        Assert.True(kernel.BindPlatformDomain(owner).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var roots = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains", flags)!.GetValue(provider)!;
        var key = Assert.Single(roots.Keys.Cast<object>());
        var original = roots[key]!;
        var gate = typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", flags)!.GetValue(provider)!;
        spy.AfterOperation = name =>
        {
            if (name != "Close") return;
            Assert.False(Monitor.IsEntered(gate));
            if (fault == "source") spy.ThrowSource = true;
            if (fault == "owner") roots[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (fault is "lost-reply" or "lost-reply-source") throw new IOException("Original root Close reply lost after effect.");
            if (fault == "reentry") Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        };
        spy.MalformedRootClose = fault == "malformed";
        var first = kernel.TerminateProcess(owner);
        Assert.Equal(fault == "reentry", first.IsSuccess);
        Assert.Equal(0, native.ActiveBindingCount);
        Assert.Equal(1, spy.RootCloseCalls);
        if (fault != "reentry")
        {
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        spy.AfterOperation = null;
        wrapper.ThrowRootReply = false;
        if (fault == "lost-reply-source")
        {
            spy.ThrowSource = true;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            var bridgeRoots = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
            var bridgeRoot = Assert.Single(bridgeRoots.Values.Cast<object>());
            Assert.True((bool)bridgeRoot.GetType().GetProperty("ParentRevokeMayHaveEffect")!.GetValue(bridgeRoot)!);
        }
        spy.ThrowSource = false;
        roots[key] = original;
        if (fault != "reentry") Assert.Equal(fault != "malformed", kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, spy.RootCloseCalls);
        Assert.Equal(fault == "malformed" ? RegionState.Owned : RegionState.Released,
            kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(fault == "malformed" ? 1024UL : 0UL,
            kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void NativeRootCleanupMustFenceOriginalRealmAtTheProducingOwner()
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 763, 1105);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        Assert.True(kernel.BindPlatformDomain(owner).IsSuccess);
        var replacement = new NeutralDomainRuntimeFacade();
        var foreignRoot = replacement.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        spy.BeforeOperation = name => { if (name == "Close") { spy.BeforeOperation = null; spy.Inner = replacement; } };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, replacement.ActiveBindingCount); // Equal lease must not authorize foreign Close.
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        spy.Inner = native;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, native.ActiveBindingCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(NeutralDomainCloseDecision.Closed, replacement.Close(foreignRoot).Decision);
    }

    [Theory]
    [InlineData("bind")] [InlineData("map")]
    public void NativeAdmissionSourceMustBeFencedAtTheProducingOwner(string operation)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        var foreignRoot = operation == "bind" ? native.Bind(NeutralDomainProfile.OrdinaryService).Lease : default;
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 762, 1104);
        var admin = TestFixtures.Create(kernel, 98, 998).Handle;
        var budgetCap = kernel.MintCapability(new DomainId(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "fenced-admission",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var replacement = new NeutralDomainRuntimeFacade();
        if (operation == "bind") spy.BeforeOperation = name => { if (name == "Bind") { spy.BeforeOperation = null; spy.Inner = replacement; } };
        var root = kernel.BindPlatformDomain(owner);
        if (operation == "bind") Assert.False(root.IsSuccess);
        else
        {
            Assert.True(root.IsSuccess, root.Message);
            var replacementRoot = replacement.Bind(NeutralDomainProfile.OrdinaryService).Lease;
            var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
            var parent = Assert.Single(records.Values.Cast<object>());
            Assert.Equal(parent.GetType().GetProperty("HybridCpuLease")!.GetValue(parent), replacementRoot);
            spy.BeforeOperation = name => { if (name == "MapOwnedRegion") { spy.BeforeOperation = null; spy.Inner = replacement; } };
            var cap = kernel.MintCapability(root.Value!.Subject.DomainId, owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
            Assert.False(kernel.MapPlatformOwnedRegionSlice(owner, root.Value, cap, region.Handle, 16, 128, PlatformMemoryAccess.Read).IsSuccess);
        }
        Assert.Equal(operation == "bind" ? 0 : 1, replacement.ActiveBindingCount);
        Assert.Equal(0, replacement.ActiveOwnedRegionMappingCount);
        Assert.Equal(operation == "map" ? 128UL : 0UL,
            kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        spy.Inner = native;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, spy.CloseCalls);
        Assert.Equal(operation == "bind" ? 0 : 1, spy.RootCloseCalls);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        if (operation == "bind") Assert.True(native.Close(foreignRoot).Decision == NeutralDomainCloseDecision.Closed);
    }

    [Theory]
    [InlineData("bind", "none")] [InlineData("map", "none")]
    [InlineData("bind", "owner")] [InlineData("map", "owner")]
    [InlineData("bind", "bridge-owner")] [InlineData("map", "bridge-owner")]
    [InlineData("bind", "source")] [InlineData("map", "source")]
    [InlineData("bind", "unknown")] [InlineData("map", "unknown")]
    [InlineData("bind", "overlap")] [InlineData("map", "overlap")]
    [InlineData("bind", "process-owner")] [InlineData("map", "process-owner")]
    [InlineData("bind", "record-owner")] [InlineData("map", "record-owner")]
    [InlineData("bind", "foreign-query")] [InlineData("map", "foreign-query")]
    public void KnownUnpublishedAdmissionCleanupUsesOriginalRequestAndReclaims(string operation, string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var adapter = DispatchProxy.Create<IAdmissionProvider, AdmissionSpy>();
        var admissionSpy = (AdmissionSpy)adapter;
        admissionSpy.Inner = provider;
        var kernel = new RuntimeKernel(fault == "foreign-query" ? adapter : provider);
        var (_, owner) = TestFixtures.Create(kernel, 761, 1103);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "unpublished-admission",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        PlatformDomainBindRequest? bindRequest = null;
        PlatformOwnedRegionMapRequest? mapRequest = null;
        spy.AfterOperation = name =>
        {
            if (name != (operation == "bind" ? "Bind" : "MapOwnedRegion")) return;
            var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField(operation == "bind" ? "_originalRootBinds" : "_domains", flags)!.GetValue(provider)!;
            var original = Assert.Single(records.Values.Cast<object>());
            if (operation == "bind") bindRequest = (PlatformDomainBindRequest)original.GetType().GetProperty("Request")!.GetValue(original)!;
            else
            {
                var mapOwner = original.GetType().GetProperty("OwnedMapAdmissionOwner")!.GetValue(original)!;
                mapRequest = (PlatformOwnedRegionMapRequest)mapOwner.GetType().GetProperty("Request")!.GetValue(mapOwner)!;
            }
            Assert.NotEqual(Guid.Empty, operation == "bind" ? bindRequest!.Value.InvocationId : mapRequest!.Value.InvocationId);
            if (fault == "unknown") throw new IOException("Original admission response lost after native effect.");
            spy.ThrowSource = true;
        };
        var root = kernel.BindPlatformDomain(owner);
        if (operation == "bind") Assert.False(root.IsSuccess);
        else
        {
            Assert.True(root.IsSuccess, root.Message);
            var cap = kernel.MintCapability(root.Value!.Subject.DomainId, owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
            Assert.False(kernel.MapPlatformOwnedRegionSlice(owner, root.Value, cap, region.Handle, 16, 128, PlatformMemoryAccess.Read).IsSuccess);
        }
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(operation == "map" ? 128UL : 0UL,
            kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        spy.ThrowSource = false;
        spy.AfterOperation = null;
        var resolver = (IPlatformOriginalAdmissionResolutionProvider)provider;
        if (operation == "bind") Assert.False(resolver.ResolveDomainBind(bindRequest!.Value with { InvocationId = Guid.NewGuid() }).IsSuccess);
        else Assert.False(resolver.ResolveOwnedRegionMap(mapRequest!.Value with { Slice = mapRequest.Value.Slice with { Length = 129 } }).IsSuccess);
        Assert.Equal(0, spy.CloseCalls);
        Assert.Equal(0, spy.RootCloseCalls);
        if (fault == "unknown")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            if (operation == "bind") Assert.False(resolver.BindDomain(bindRequest!.Value).IsSuccess);
            else Assert.False(resolver.MapOwnedRegionSlice(mapRequest!.Value).IsSuccess);
            Assert.Equal(1, spy.BindCalls);
            Assert.Equal(operation == "map" ? 1 : 0, spy.MapCalls);
            Assert.Equal(0, spy.CloseCalls);
            Assert.Equal(0, spy.RootCloseCalls);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
            Assert.Equal(operation == "map" ? 128UL : 0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            return;
        }
        Action? restore = null;
        if (fault == "foreign-query")
        {
            admissionSpy.ForeignQuery = true;
            restore = () => admissionSpy.ForeignQuery = false;
        }
        if (fault is not ("none" or "foreign-query")) spy.OnSourceRead = () =>
        {
            spy.OnSourceRead = null;
            Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", flags)!.GetValue(provider)!));
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!));
            if (fault == "overlap")
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                var overlapping = Task.Run(() => kernel.TerminateProcess(owner));
                Assert.True(overlapping.Wait(TimeSpan.FromSeconds(6)));
                Assert.False(overlapping.Result.IsSuccess);
            }
            else if (fault == "source") { spy.Inner = new NeutralDomainRuntimeFacade(); restore = () => spy.Inner = native; }
            else if ((fault is "owner" or "record-owner") && operation == "map")
            {
                var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains", flags)!.GetValue(provider)!;
                var parent = Assert.Single(records.Values.Cast<object>());
                var mapOwner = parent.GetType().GetProperty("OwnedMapAdmissionOwner")!.GetValue(parent)!;
                var target = fault == "record-owner" ? mapOwner : parent;
                var property = target.GetType().GetProperty(fault == "record-owner" ? "CleanupMapping" : "FailedOwnedMap")!;
                var original = property.GetValue(target)!;
                property.SetValue(target, typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null));
                restore = () => property.SetValue(target, original);
            }
            else if (fault == "record-owner")
            {
                var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalRootBinds", flags)!.GetValue(provider)!;
                var source = Assert.Single(records.Values.Cast<object>());
                var property = source.GetType().GetProperty("PublishedDomain")!;
                var original = property.GetValue(source)!;
                property.SetValue(source, typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null));
                restore = () => property.SetValue(source, original);
            }
            else
            {
                var records = fault == "process-owner"
                    ? (IDictionary)typeof(RuntimeKernel).GetField("_processTeardowns", flags)!.GetValue(kernel)!
                    : fault == "bridge-owner"
                        ? (IDictionary)typeof(PlatformAuthorityBridge).GetField(operation == "bind" ? "_unresolvedDomainBinds" : "_mappings", flags)!.GetValue(kernel.PlatformAuthority)!
                        : (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalRootBinds", flags)!.GetValue(provider)!;
                var key = Assert.Single(records.Keys.Cast<object>());
                var original = records[key]!;
                records[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                restore = () => records[key] = original;
            }
        };
        if (fault is not ("none" or "overlap"))
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0, spy.CloseCalls);
            Assert.Equal(0, spy.RootCloseCalls);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
            Assert.Equal(operation == "map" ? 128UL : 0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.NotNull(restore);
            restore!();
        }
        var completed = kernel.TerminateProcess(owner);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(1, spy.BindCalls);
        Assert.Equal(operation == "map" ? 1 : 0, spy.MapCalls);
        Assert.Equal(operation == "map" ? 1 : 0, spy.CloseCalls);
        Assert.Equal(1, spy.RootCloseCalls);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        if (operation == "bind") Assert.False(resolver.BindDomain(bindRequest!.Value).IsSuccess);
        else Assert.False(resolver.MapOwnedRegionSlice(mapRequest!.Value).IsSuccess);
        Assert.Equal(1, spy.BindCalls);
        Assert.Equal(operation == "map" ? 1 : 0, spy.MapCalls);
    }

    [Theory]
    [InlineData("close")] [InlineData("map")] [InlineData("bind")]
    [InlineData("root-close")] [InlineData("unavailable")] [InlineData("root-close-owner")] [InlineData("root-close-next-throw")]
    public void OriginalNativeRealmCannotBeReplacedByEqualTupleBeforeEffect(string operation)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 756, 1098);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCapability = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCapability, owner, "native-realm-continuity",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 2048), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, root, cap, region.Handle, 16, 128, PlatformMemoryAccess.Read).Value!;
        var mapRegion = operation == "map" ? kernel.AllocateBuffer<byte>(owner, 1024).Value! : region;
        var mapCap = operation == "map" ? kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(mapRegion.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId : cap;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originals = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderMappings", flags)!.GetValue(provider)!;
        var original = Assert.Single(originals.Values.Cast<object>());
        var lease = (NeutralOwnedRegionMappingLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
        var replacement = new NeutralDomainRuntimeFacade();
        var foreignRoot = replacement.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        Assert.Equal(lease.DomainLease, foreignRoot);
        if (!operation.StartsWith("root-close", StringComparison.Ordinal))
            Assert.Equal(lease, replacement.MapOwnedRegion(foreignRoot, lease.Slice).Lease);
        else Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        var closes = spy.CloseCalls;
        var maps = spy.MapCalls;
        var binds = spy.BindCalls;
        spy.Inner = replacement;
        if (operation == "unavailable") spy.ThrowSource = true;
        var kernelRoots = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        var originalKernelRoot = kernelRoots[root.Subject]!;
        if (operation == "root-close-owner") spy.OnSourceRead = () => kernelRoots.Remove(root.Subject);
        var other = TestFixtures.Create(kernel, 757, 1099).Handle;

        if (operation is "close" or "unavailable") Assert.False(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        if (operation == "map") Assert.False(kernel.MapPlatformOwnedRegionSlice(owner, root, mapCap, mapRegion.Handle, 256, 64, PlatformMemoryAccess.Read).IsSuccess);
        if (operation == "bind") Assert.False(kernel.BindPlatformDomain(other).IsSuccess);
        if (operation.StartsWith("root-close", StringComparison.Ordinal)) Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(closes, spy.CloseCalls);
        Assert.Equal(maps, spy.MapCalls);
        Assert.Equal(binds, spy.BindCalls);
        Assert.Equal(0, spy.RootCloseCalls);
        Assert.Equal(operation.StartsWith("root-close", StringComparison.Ordinal) ? 0UL : 128UL,
            kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(operation.StartsWith("root-close", StringComparison.Ordinal) ? 0 : 1, replacement.ActiveOwnedRegionMappingCount);

        spy.ThrowSource = false;
        spy.Inner = native;
        spy.OnSourceRead = null;
        kernelRoots[root.Subject] = originalKernelRoot;
        if (operation == "root-close-next-throw")
        {
            spy.AfterOperation = name => { if (name == "Close") throw new IOException("Next original root Close response lost."); };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            spy.AfterOperation = null;
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, spy.RootCloseCalls);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
            return;
        }
        if (operation is "close" or "unavailable") Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        if (operation == "map")
        {
            var retried = kernel.MapPlatformOwnedRegionSlice(owner, root, mapCap, mapRegion.Handle, 256, 64, PlatformMemoryAccess.Read);
            Assert.True(retried.IsSuccess, retried.Message);
        }
        if (operation == "bind") Assert.True(kernel.BindPlatformDomain(other).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.TerminateProcess(other).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(operation == "map" ? 2 : 1, spy.CloseCalls);
        Assert.Equal(0, spy.ResolveCalls);
    }

    [Theory]
    [InlineData("bind")] [InlineData("map")] [InlineData("acquire")]
    public void OriginalNativeOutcomePrecedesLateSourceObservationAndCannotBecomeNoEffect(string operation)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 759, 1101);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", flags)!.GetValue(provider)!;
        var observed = false;
        spy.AfterOperation = name =>
        {
            if (name == (operation == "bind" ? "Bind" : operation == "map" ? "MapOwnedRegion" : "AcquireOwnedRegionVisibility"))
            {
                observed = true;
                spy.ThrowSource = true;
            }
        };
        spy.OnSourceRead = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            if (!observed) return;
            var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField(
                operation == "bind" ? "_originalRootBinds" : operation == "map" ? "_domains" : "_originalProviderMappings", flags)!.GetValue(provider)!;
            var record = Assert.Single(records.Values.Cast<object>());
            Assert.NotNull(record.GetType().GetProperty(operation == "bind" ? "Observation" : operation == "map" ? "FailedOwnedMap" : "AcquireObservation")!.GetValue(record));
        };
        var root = kernel.BindPlatformDomain(owner);
        if (operation == "bind")
        {
            Assert.False(root.IsSuccess);
            Assert.Equal(1, spy.BindCalls);
        }
        else
        {
            Assert.True(root.IsSuccess, root.Message);
            var cap = kernel.MintCapability(root.Value!.Subject.DomainId, owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
            var mapping = kernel.MapPlatformOwnedRegionSlice(owner, root.Value, cap, region.Handle,
                operation == "acquire" ? 0L : 16L, operation == "acquire" ? 1024L : 128L,
                operation == "acquire" ? PlatformMemoryAccess.Read | PlatformMemoryAccess.Write : PlatformMemoryAccess.Read);
            if (operation == "map") Assert.False(mapping.IsSuccess);
            else
            {
                Assert.True(mapping.IsSuccess, mapping.Message);
                var (_, target) = TestFixtures.Create(kernel, 760, 1102);
                Assert.False(kernel.MovePlatformOwnedBuffer(owner, target, region, mapping.Value!).IsSuccess);
                Assert.True(observed, "Actual kernel MOVE must reach native acquire.");
                spy.ThrowSource = false;
                spy.OnSourceRead = null;
                spy.AfterOperation = null;
                Assert.False(kernel.MovePlatformOwnedBuffer(owner, target, region, mapping.Value!).IsSuccess);
                Assert.True(region.IsValid);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
                Assert.Equal(1, spy.CloseCalls);
                return;
            }
        }
        Assert.True(observed);
        spy.ThrowSource = false;
        spy.OnSourceRead = null;
        spy.AfterOperation = null;
        if (operation == "bind") Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
        else Assert.False(kernel.MapPlatformOwnedRegionSlice(owner, root.Value!,
            kernel.MintCapability(root.Value!.Subject.DomainId, owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId,
            region.Handle, 16, 128, PlatformMemoryAccess.Read).IsSuccess);
        var cleanup = kernel.TerminateProcess(owner);
        Assert.True(cleanup.IsSuccess, cleanup.Message);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(operation == "bind" ? 0 : 1, spy.MapCalls);
        Assert.Equal(1, spy.RootCloseCalls);
    }

    [Theory]
    [InlineData("unavailable")] [InlineData("restart")] [InlineData("owner")]
    [InlineData("close-owner")] [InlineData("overlap")]
    public void DirectNativeCloseRetainsOutcomeBeforeFreshSourceAndFinalOwnerCommit(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 755, 1097);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCapability = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCapability, owner, "direct-native-close",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, root, cap, region.Handle, 16, 128, PlatformMemoryAccess.Read).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originals = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderMappings", flags)!.GetValue(provider)!;
        var original = Assert.Single(originals.Values.Cast<object>());
        var roots = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_activeSubjects", flags)!.GetValue(provider)!;
        var rootKey = Assert.Single(roots.Keys.Cast<object>());
        var originalRoot = roots[rootKey]!;
        var gate = typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", flags)!.GetValue(provider)!;
        var closed = false;
        var finalSourceReads = 0;
        spy.OnSourceRead = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            if (!closed) return;
            finalSourceReads++;
            Assert.NotNull(original.GetType().GetProperty("CloseObservation")!.GetValue(original));
            Assert.False(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
            if (fault == "overlap" && finalSourceReads == 1)
            {
                var teardown = Task.Run(() => kernel.TerminateProcess(owner));
                Assert.True(teardown.Wait(TimeSpan.FromSeconds(5)), "Teardown must not block on the provider callback lock.");
                Assert.False(teardown.Result.IsSuccess);
            }
            if (fault == "owner") roots[rootKey] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(originalRoot, null)!;
        };
        spy.AfterClose = () =>
        {
            closed = true;
            if (fault is "unavailable" or "overlap") spy.ThrowSource = true;
            if (fault == "restart") spy.Inner = new NeutralDomainRuntimeFacade();
            if (fault == "close-owner") roots[rootKey] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(originalRoot, null)!;
        };

        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        Assert.True(finalSourceReads > 0);
        Assert.NotNull(original.GetType().GetProperty("CloseObservation")!.GetValue(original));
        Assert.False(kernel.ObservePlatformRegionMappingRevocation(owner, mapped.Mapping).IsSuccess);
        Assert.Equal(128UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);

        spy.OnSourceRead = null;
        spy.AfterClose = null;
        spy.ThrowSource = false;
        spy.Inner = native;
        roots[rootKey] = originalRoot;
        Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapped.Mapping).IsSuccess);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        spy.ThrowSource = true; // Exact consumed lifetime must not require fresh active source again.
        Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapped.Mapping).IsSuccess);
        spy.ThrowSource = false;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(1, spy.CloseCalls);
        Assert.Equal(0, spy.ResolveCalls); // The direct original result was already known.
    }

    [Theory]
    [InlineData("exact")] [InlineData("source")] [InlineData("foreign")] [InlineData("late-source")]
    public void ActualKernelResolvesLostNativeCloseOnlyThroughSameOriginalIncarnationAndOwner(string fault)
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade();
        spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 754, 1096);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCapability = kernel.MintCapability(new DomainId(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCapability, owner, "native-original-close",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var root = kernel.BindPlatformDomain(owner).Value!;
        var cap = kernel.MintCapability(root.Subject.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, root, cap, region.Handle, 16, 128, PlatformMemoryAccess.Read).Value!;
        spy.ThrowAfterClose = true;
        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        spy.ThrowAfterClose = false;
        Assert.Equal(1, spy.CloseCalls);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originals = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderMappings", flags)!.GetValue(provider)!;
        var original = Assert.Single(originals.Values.Cast<object>());
        var routes = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerMappings", flags)!.GetValue(provider)!;
        var key = Assert.Single(routes.Keys.Cast<object>());
        var gate = typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", flags)!.GetValue(provider)!;
        spy.BeforeResolve = () => Assert.False(Monitor.IsEntered(gate));
        if (fault == "source") spy.Inner = new NeutralDomainRuntimeFacade();
        if (fault == "foreign") routes[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (fault == "late-source") spy.AfterResolve = () => spy.ThrowSource = true;
        var result = kernel.ObservePlatformRegionMappingRevocation(owner, mapped.Mapping);
        Assert.Equal(fault == "exact", result.IsSuccess);
        Assert.Equal(fault == "exact" ? 0UL : 128UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        if (fault == "late-source")
        {
            Assert.NotNull(original.GetType().GetProperty("NeutralCloseResolutionObservation")!.GetValue(original));
            spy.ThrowSource = false;
            spy.AfterResolve = null;
            Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapped.Mapping).IsSuccess);
        }
        Assert.Equal(fault is "exact" or "late-source", kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(fault is "exact" or "late-source" ? RegionState.Released : RegionState.Owned,
            kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(1, spy.CloseCalls);
        Assert.Equal(fault is "exact" or "late-source" ? 1 : 0, spy.ResolveCalls);
    }

    public interface IResolvingNeutralRuntime : INeutralDomainRuntime, INeutralOriginalDomainCloseProvider, INeutralOriginalDmaVisibilityProvider, INeutralOriginalDmaCopyProvider, INeutralOriginalMmioProvider, INeutralOriginalIrqProvider { }

    [Fact]
    public void ActualUnderlyingCloseLostReplyRemainsUnknownAndCannotRepeat()
    {
        var runtime = DispatchProxy.Create<INeutralDomainRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        spy.Inner = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(9), new ProcessHandle(new ProcessId(9), 1));
        var root = provider.BindDomain(subject).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(10), new RegionGeneration(1)),
            new(subject.DomainId, subject.ProcessGeneration), 1024);
        var mapping = provider.MapOwnedRegionSlice(root, new(region, 0, 1024, PlatformMemoryAccess.Read)).Value!.Lease;
        var request = new PlatformRegionRevocationRequest(Guid.NewGuid(), mapping, PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        spy.ThrowAfterClose = true;
        Assert.ThrowsAny<Exception>(() => provider.BeginRegionMappingRevocation(request));
        spy.ThrowAfterClose = false;
        Assert.False(provider.ResolveRegionMappingRevocation(request).IsSuccess);
        Assert.False(provider.BeginRegionMappingRevocation(request).IsSuccess);
        Assert.False(provider.RevokeDomain(root).IsSuccess);
        Assert.Equal(1, spy.CloseCalls);
    }

    [Theory]
    [InlineData("exact")] [InlineData("absent")] [InlineData("foreign")]
    [InlineData("wrong-key")] [InlineData("wrong-generation")] [InlineData("unknown")]
    [InlineData("root-foreign")] [InlineData("root-closed")] [InlineData("owner-lost")]
    public void ActualProviderResolvesOnlySameOriginalClosedRequestWithoutAnotherClose(string fault)
    {
        var runtime = DispatchProxy.Create<INeutralDomainRuntime, RuntimeSpy>();
        var spy = (RuntimeSpy)runtime;
        spy.Inner = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(7), new ProcessHandle(new ProcessId(7), 1));
        var root = provider.BindDomain(subject).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(8), new RegionGeneration(1)),
            new(subject.DomainId, subject.ProcessGeneration), 1024);
        var mapped = provider.MapOwnedRegionSlice(root, new(region, 0, 1024, PlatformMemoryAccess.Read));
        Assert.True(mapped.IsSuccess, mapped.Message);
        var request = new PlatformRegionRevocationRequest(Guid.NewGuid(), mapped.Value!.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        var begun = provider.BeginRegionMappingRevocation(request);
        Assert.True(begun.IsSuccess, begun.Message);
        Assert.Equal(1, spy.CloseCalls);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerMappings", flags)!.GetValue(provider)!;
        var original = routes[request.Mapping.MappingId]!;
        var operations = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_providerOperations", flags)!.GetValue(provider)!;
        operations.Clear(); // Lost returned ticket/index; exact original owner survives.
        if (fault == "absent") routes.Remove(request.Mapping.MappingId);
        if (fault == "foreign") routes[request.Mapping.MappingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (fault == "wrong-key") request = request with { InvocationId = Guid.NewGuid() };
        if (fault == "wrong-generation") request = request with { Mapping = request.Mapping with { Generation = new(2) } };
        if (fault == "unknown")
        {
            original.GetType().GetProperty("CloseObservation")!.SetValue(original, null);
            original.GetType().GetProperty("CloseUncertain")!.SetValue(original, true);
        }
        if (fault == "root-foreign")
        {
            var roots = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_activeSubjects", flags)!.GetValue(provider)!;
            roots[subject] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(roots[subject], null)!;
        }
        if (fault == "owner-lost")
            ((IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderMappings", flags)!.GetValue(provider)!).Remove(request.Mapping.MappingId);
        if (fault == "root-closed") Assert.True(provider.RevokeDomain(root).IsSuccess);
        var resolution = provider.ResolveRegionMappingRevocation(request);
        Assert.Equal(fault is "exact" or "absent" or "root-closed", resolution.IsSuccess);
        if (resolution.IsSuccess)
        {
            Assert.Equal(request, resolution.Value!.Request);
            Assert.Equal(begun.Value!, resolution.Value!.Ticket);
            Assert.True(provider.ObserveCompletion(resolution.Value!.Ticket.Operation).Value!.ProvesClosure);
            Assert.True(provider.ResolveRegionMappingRevocation(request).IsSuccess);
        }
        Assert.False(provider.BeginRegionMappingRevocation(request).IsSuccess);
        Assert.Equal(1, spy.CloseCalls);
    }

    public class RuntimeSpy : DispatchProxy
    {
        public INeutralDomainRuntime Inner { get; set; } = null!;
        public int CloseCalls { get; private set; }
        public int MapCalls { get; private set; }
        public int BindCalls { get; private set; }
        public int RootCloseCalls { get; private set; }
        public int DmaBindCalls { get; private set; }
        public int DmaCloseCalls { get; private set; }
        public int DmaPrepareCalls { get; private set; }
        public int DmaAcquireCalls { get; private set; }
        public bool MalformedVisibility { get; set; }
        public bool MalformedMmio { get; set; }
        public bool MalformedDmaBind { get; set; }
        public bool MalformedDmaClose { get; set; }
        public bool MalformedRootClose { get; set; }
        public bool ThrowAfterClose { get; set; }
        public bool ThrowSource { get; set; }
        public int ResolveCalls { get; private set; }
        public Action? BeforeResolve { get; set; }
        public Action? AfterResolve { get; set; }
        public Action? AfterClose { get; set; }
        public Action? OnSourceRead { get; set; }
        public Action<string>? AfterOperation { get; set; }
        public Action<string>? BeforeOperation { get; set; }
        public Action<string,object?[]?>? BeforeInvocation { get; set; }
        public Func<object?,object?>? RewriteObservation { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_MappingClosureIncarnation") OnSourceRead?.Invoke();
            if (method!.Name == "get_MappingClosureIncarnation" && ThrowSource) throw new IOException("Native source unavailable");
            if (method.Name == "ResolveOwnedRegionMappingClose") { ResolveCalls++; BeforeResolve?.Invoke(); }
            if (method!.Name == "CloseOwnedRegionMapping") CloseCalls++;
            if (method.Name == "MapOwnedRegion") MapCalls++;
            if (method.Name == "Bind") BindCalls++;
            if (method.Name == "Close") RootCloseCalls++;
            if (method.Name == "BindDmaGrant") DmaBindCalls++;
            if (method.Name == "CloseDmaGrant") DmaCloseCalls++;
            if (method.Name == "PrepareDmaVisibility") DmaPrepareCalls++;
            if (method.Name == "AcquireDmaVisibility") DmaAcquireCalls++;
            BeforeOperation?.Invoke(method.Name);
            BeforeInvocation?.Invoke(method.Name,args);
            var result = method.Invoke(Inner, args);
            if (RewriteObservation is not null) result = RewriteObservation(result);
            if (method.Name == "Close" && MalformedRootClose && result is NeutralOriginalDomainCloseObservation raw)
                result = raw with { Request = raw.Request with { InvocationId = Guid.NewGuid() } };
            if (method.Name == "BindDmaGrant" && MalformedDmaBind && result is NeutralOriginalDmaGrantObservation dmaBind)
                result = dmaBind with { Request = dmaBind.Request with { InvocationId = Guid.NewGuid() } };
            if (method.Name == "CloseDmaGrant" && MalformedDmaClose && result is NeutralOriginalDmaGrantCloseObservation dmaClose)
                result = dmaClose with { Request = dmaClose.Request with { InvocationId = Guid.NewGuid() } };
            if (MalformedVisibility && result is NeutralOriginalDmaVisibilityObservation visibility)
                result = visibility with { Request = visibility.Request with { InvocationId = Guid.NewGuid() } };
            if (MalformedMmio && result is NeutralOriginalMmioMapObservation mmioMap)
                result = mmioMap with { Request = mmioMap.Request with { InvocationId = Guid.NewGuid() } };
            if (MalformedMmio && result is NeutralOriginalMmioCloseObservation mmioClose)
                result = mmioClose with { Request = mmioClose.Request with { InvocationId = Guid.NewGuid() } };
            AfterOperation?.Invoke(method.Name);
            if (method.Name == "ResolveOwnedRegionMappingClose") AfterResolve?.Invoke();
            if (method.Name == "CloseOwnedRegionMapping") AfterClose?.Invoke();
            if (method.Name == "CloseOwnedRegionMapping" && ThrowAfterClose) throw new IOException("Original Close response lost after actual effect");
            return result;
        }
    }

    public interface IAdmissionProvider : IPlatformOriginalAdmissionResolutionProvider, IPlatformFeatureProvider, IPlatformRegionRevocationResolutionProvider, IPlatformOriginalDomainClosureProvider { }

    public class AdmissionSpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner { get; set; } = null!;
        public bool ForeignQuery { get; set; }
        public bool ThrowRootReply { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            if (ThrowRootReply && method.Name == "RevokeDomain") throw new IOException("Published original provider Close reply lost.");
            if (ForeignQuery && result is PlatformAuthorityResult<PlatformDomainBindResolution> bind && bind.IsSuccess)
                return PlatformAuthorityResult<PlatformDomainBindResolution>.Ok(bind.Value with { Request = bind.Value.Request with { InvocationId = Guid.NewGuid() } });
            if (ForeignQuery && result is PlatformAuthorityResult<PlatformOwnedRegionMapResolution> map && map.IsSuccess)
                return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Ok(map.Value with { Request = map.Value.Request with { InvocationId = Guid.NewGuid() } });
            return result;
        }
    }
}
