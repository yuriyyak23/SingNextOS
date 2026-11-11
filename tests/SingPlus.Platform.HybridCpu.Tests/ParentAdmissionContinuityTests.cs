using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class ParentAdmissionContinuityTests
{
    private static (HybridCpuPlatformAuthorityProvider Provider, ChildClosureTupleTests.RootCloseRuntimeProxy Proxy,
        PlatformProviderDomainLease Parent, PlatformProviderDeviceLease Device, PlatformProviderOwnedRegionMapping Mapping) Create()
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        PlatformProviderDeviceLease device = provider.BindDevice(parent, new("parent-source"), PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(parent.Subject.DomainId, parent.Subject.ProcessGeneration), 4096);
        PlatformProviderOwnedRegionMapping mapping = provider.MapOwnedRegionSlice(parent, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        return (provider, proxy, parent, device, mapping);
    }

    private static Action LoseParent(HybridCpuPlatformAuthorityProvider provider, PlatformProviderDomainLease parent, bool absent)
    {
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object original = owners[parent.LeaseId]!;
        if (absent) owners.Remove(parent.LeaseId);
        else owners[parent.LeaseId] = Activator.CreateInstance(original.GetType(), parent,
            original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        // Test arrangement only, not a durable recovery route.
        return () => owners[parent.LeaseId] = original;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublishedSourcesRefuseLostParentBeforeDownstreamEffectsOrCleanup(bool absent)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device, PlatformProviderOwnedRegionMapping mapping) = Create();
        Action restore = LoseParent(provider, parent, absent);
        Assert.False(provider.BindInterrupt(device, new("irq", PlatformInterruptTrigger.Edge)).IsSuccess);
        Assert.False(provider.MapMmio(device, new("mmio", 4096), new(0, 64), PlatformMmioAccess.Read).IsSuccess);
        Assert.False(provider.BindDmaGrant(new PlatformDmaGrantRequest(device, mapping.Lease, mapping.Slice, new(0, 64), PlatformDmaDirection.DeviceReadsMemory)).IsSuccess);
        Assert.False(provider.PrepareRegionMappingForConsumer(new(mapping.Lease, mapping.Slice,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence)).IsSuccess);
        Assert.False(provider.BeginRegionMappingRevocation(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.False(provider.RevokeRegionMapping(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.False(provider.RevokeDevice(device).IsSuccess);
        Assert.Equal(0, proxy.IrqBindCalls);
        Assert.Equal(0, proxy.MmioMapCalls);
        Assert.Equal(0, proxy.DmaCalls);
        Assert.Equal(0, proxy.VisibilityCalls);
        Assert.Equal(0, proxy.MappingCloseCalls);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, proxy.Runtime.ActiveDeviceLeaseCount);
        restore();
        Assert.True(provider.RevokeRegionMapping(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClosedMappingRefusesForeignOrAbsentParentBeforeAcquire(bool absent)
    {
        (HybridCpuPlatformAuthorityProvider? provider, ChildClosureTupleTests.RootCloseRuntimeProxy? proxy, PlatformProviderDomainLease parent, PlatformProviderDeviceLease device, PlatformProviderOwnedRegionMapping mapping) = Create();
        Assert.True(provider.RevokeRegionMapping(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Action restore = LoseParent(provider, parent, absent);
        PlatformRegionAcquireRequest request = new PlatformRegionAcquireRequest(mapping.Lease, mapping.Slice,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryAcquireRequirement.AcquisitionFence);
        Assert.False(provider.AcquireRegionMappingFromConsumer(request).IsSuccess);
        Assert.Equal(0, proxy.AcquireCalls);
        restore();
        Assert.True(provider.AcquireRegionMappingFromConsumer(request).IsSuccess);
        Assert.Equal(1, proxy.AcquireCalls);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
    }
}
