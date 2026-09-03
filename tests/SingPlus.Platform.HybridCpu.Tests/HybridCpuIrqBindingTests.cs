using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuIrqBindingTests
{
    [Fact]
    public void ExactCapabilityBackedInterruptRouteMaterializesInNeutralRuntime()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 81, 810, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/net0",
            CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        var irqCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/net0", "rx-ready", IrqTriggerMode.Edge),
            CapabilityRights.Signal);
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;

        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint);

        Assert.True(route.IsSuccess, route.Message);
        Assert.Equal(
            new PlatformInterruptSourceIdentity("rx-ready", PlatformInterruptTrigger.Edge),
            route.Value!.Source);
        Assert.Equal(endpoint, route.Value.EventEndpoint);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveInterruptLeaseCount);

        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, runtime.ActiveInterruptLeaseCount);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
    }

    [Fact]
    public void HybridCpuProviderAdvertisesIrqBindingWithoutDmaClaim()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var features = provider.QueryFeatures();

        Assert.Equal(
            PlatformIrqBindingContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.IrqBinding).ContractVersion);
        Assert.Equal(
            PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.IrqBinding).Availability);
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
    }

    [Fact]
    public void ProcessTeardownClosesRealInterruptRouteBeforeDeviceAndDomain()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 82, 820, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/controller0",
            CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        var irqCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/controller0", "notify", IrqTriggerMode.Level),
            CapabilityRights.Signal);
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        Assert.True(kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).IsSuccess);
        Assert.Equal(1, runtime.ActiveInterruptLeaseCount);

        var terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveInterruptLeaseCount);
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
