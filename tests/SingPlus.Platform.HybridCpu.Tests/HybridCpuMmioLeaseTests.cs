using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuMmioLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignMmioOwnerCannotConsumeOriginalKernelClosure(bool completed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 71, 710, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(kernel, subject, ResourceKind.Device, "device/uart0",
            CapabilityRights.Read | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(subject, binding, deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var mmioCapability = Mint(kernel, subject, ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/uart0", "registers", 4096), CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.BindPlatformMmio(subject, device, mmioCapability, 0, 64, PlatformMmioAccess.Read).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_mmioLeases", flags)!.GetValue(provider)!;
        var original = records.Values.Cast<object>().Single();
        var lease = (PlatformProviderMmioLease)original.GetType().GetProperty("Lease")!.GetValue(original)!;
        var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (completed)
        {
            var other = provider.MapMmio(lease.DeviceLease, new("other-registers", 4096), new(0, 64), PlatformMmioAccess.Read).Value!;
            Assert.True(provider.RevokeMmio(other).IsSuccess);
            var otherRecord = records[other.LeaseId]!;
            copy.GetType().GetProperty("CloseObservation")!.SetValue(copy,
                otherRecord.GetType().GetProperty("CloseObservation")!.GetValue(otherRecord));
        }
        records[lease.LeaseId] = copy;
        var calls = proxy.MmioCloseCalls;
        Assert.False(kernel.RevokePlatformMmio(subject, mapping).IsSuccess);
        Assert.Equal(calls, proxy.MmioCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveMmioLeaseCount);
        Assert.False(provider.RevokeDevice(lease.DeviceLease).IsSuccess);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        records[lease.LeaseId] = original;
        Assert.True(provider.RevokeMmio(lease).IsSuccess);
        Assert.Equal(calls + 1, proxy.MmioCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveMmioLeaseCount);
        Assert.False(kernel.RevokePlatformMmio(subject, mapping).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(1, proxy.Runtime.ActiveDeviceLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostMmioRouteCannotHideOriginalDeviceDependency(bool copiedClosed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(710), new ProcessHandle(new ProcessId(71), 1))).Value!;
        var device = provider.BindDevice(parent, new("device/uart0"), PlatformDeviceRights.Read).Value!;
        var mapping = provider.MapMmio(device, new("registers", 4096), new(0, 64), PlatformMmioAccess.Read).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_mmioLeases", flags)!.GetValue(provider)!;
        var original = records[mapping.LeaseId]!;
        if (copiedClosed)
        {
            var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            copy.GetType().GetProperty("Revoked")!.SetValue(copy, true);
            records[mapping.LeaseId] = copy;
        }
        else records.Remove(mapping.LeaseId);
        Assert.False(provider.RevokeDevice(device).IsSuccess);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveMmioLeaseCount);
        records[mapping.LeaseId] = original;
        Assert.True(provider.RevokeMmio(mapping).IsSuccess);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Fact]
    public void ExactCapabilityBackedMmioLeaseMaterializesInNeutralRuntime()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 71, 710, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/uart0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var mmioCapability = Mint(
            kernel,
            subject,
            ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/uart0", "uart0/registers", 4096),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);

        var mmio = kernel.BindPlatformMmio(
            subject,
            device,
            mmioCapability,
            128,
            64,
            PlatformMmioAccess.Read | PlatformMmioAccess.Write);

        Assert.True(mmio.IsSuccess, mmio.Message);
        Assert.Equal(new PlatformMmioRegionIdentity("uart0/registers", 4096), mmio.Value!.Region);
        Assert.Equal(new PlatformMmioRange(128, 64), mmio.Value.Range);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveMmioLeaseCount);

        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, runtime.ActiveMmioLeaseCount);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
    }

    [Fact]
    public void HybridCpuProviderAdvertisesMmioWithDmaAdmissionOnly()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var features = provider.QueryFeatures();

        Assert.Equal(
            PlatformMmioLeaseContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.MmioMapping).ContractVersion);
        Assert.Equal(
            PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.MmioMapping).Availability);
        Assert.Equal(
            PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
        Assert.NotEqual(
            PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
    }

    [Fact]
    public void ProcessTeardownClosesRealMmioBeforeDeviceAndDomain()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 72, 720, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/controller0",
            CapabilityRights.Read | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var mmioCapability = Mint(
            kernel,
            subject,
            ResourceKind.MmioRegion,
            CapabilityResourceIds.MmioRegion("device/controller0", "controller0/registers", 1024),
            CapabilityRights.Map | CapabilityRights.Read);
        Assert.True(kernel.BindPlatformMmio(
            subject,
            device,
            mmioCapability,
            0,
            128,
            PlatformMmioAccess.Read).IsSuccess);
        Assert.Equal(1, runtime.ActiveMmioLeaseCount);

        var terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveMmioLeaseCount);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
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
}
