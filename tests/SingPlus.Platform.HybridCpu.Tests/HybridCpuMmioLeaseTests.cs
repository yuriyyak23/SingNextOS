using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuMmioLeaseTests
{
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
            PlatformFeatureAvailability.Executable,
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
