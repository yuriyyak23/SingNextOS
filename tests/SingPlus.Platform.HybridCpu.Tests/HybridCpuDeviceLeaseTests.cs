using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDeviceLeaseTests
{
    [Fact]
    public void CapabilityBackedSingDeviceLeaseOwnsExactNeutralHybridCpuLifetime()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            61,
            610,
            1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = kernel.MintCapability(
            new DomainId(610),
            subject,
            ResourceKind.Device,
            "device/net0",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;

        var features = kernel.QueryPlatformFeatures();
        Assert.Equal(PlatformDeviceLeaseContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.IoDomainBinding).ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.IoDomainBinding).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);

        var lease = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure);

        Assert.True(lease.IsSuccess, lease.Message);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal("device/net0", lease.Value!.Device.ResourceId);
        Assert.NotEqual(typeof(PlatformDeviceLeaseId), typeof(NeutralDeviceLeaseHandle));
        Assert.NotEqual(typeof(PlatformDeviceLeaseGeneration), typeof(NeutralDeviceLeaseEpoch));
        Assert.NotEqual(typeof(CapabilityId), typeof(NeutralDeviceLeaseHandle));

        var earlyDomainClose = kernel.RevokePlatformDomain(subject, binding);
        Assert.False(earlyDomainClose.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, earlyDomainClose.Error);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);

        var closeDevice = kernel.RevokePlatformDevice(subject, lease.Value);
        Assert.True(closeDevice.IsSuccess, closeDevice.Message);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var closeDomain = kernel.RevokePlatformDomain(subject, binding);
        Assert.True(closeDomain.IsSuccess, closeDomain.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);

        var staleDevice = kernel.RevokePlatformDevice(subject, lease.Value);
        Assert.False(staleDevice.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, staleDevice.Error);
    }

    [Fact]
    public void CapabilityRevokeClosesRealNeutralDeviceLeaseBeforeDomainClosure()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            62,
            620,
            1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = kernel.MintCapability(
            new DomainId(620),
            subject,
            ResourceKind.Device,
            "device/storage0",
            CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);

        var revoke = kernel.RevokeCapability(capability);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void ProcessTeardownClosesRealNeutralDeviceAuthorityBeforeDomainAndExit()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            63,
            630,
            1);
        Assert.True(kernel.AdmitProcess(subject).IsSuccess);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = kernel.MintCapability(
            new DomainId(630),
            subject,
            ResourceKind.Device,
            "device/controller0",
            CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);
        Assert.True(kernel.StartProcess(subject).IsSuccess);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }
}
