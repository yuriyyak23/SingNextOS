using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDmaVisibilityTests
{
    [Fact]
    public void RealProviderPreparesAndAcquiresExactNonCoherentDmaCycle()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 91, 910, 1);
        var (device, mapping) = CreateAuthorities(kernel, subject);
        var grant = kernel.BindPlatformDma(
            subject,
            device,
            mapping,
            32,
            128,
            PlatformDmaDirection.Bidirectional).Value!;

        var prepare = kernel.PreparePlatformDmaForDevice(subject, grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(prepare.Value!.IsSatisfied);
        Assert.Equal(PlatformMemoryVisibilityRequirement.PublicationFence, prepare.Value.Requirement);
        Assert.Equal(1, runtime.ActiveDmaGrantCount);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);

        var acquire = kernel.AcquirePlatformDmaForCpu(subject, grant);
        Assert.True(acquire.IsSuccess, acquire.Message);
        Assert.True(acquire.Value!.IsSatisfied);
        Assert.Equal(prepare.Value.Cycle, acquire.Value.Cycle);
        Assert.Equal(PlatformMemoryAcquireRequirement.AcquisitionFence, acquire.Value.Requirement);
        Assert.Equal(1, runtime.ActiveDmaGrantCount);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);

        Assert.NotEqual(typeof(PlatformDmaVisibilityCycle), typeof(PlatformProviderDmaVisibilityCycle));
        Assert.NotEqual(typeof(PlatformDmaVisibilityCycle), typeof(NeutralDmaVisibilityCycle));
        Assert.NotEqual(typeof(PlatformProviderDmaVisibilityCycle), typeof(NeutralDmaVisibilityCycle));
    }

    [Fact]
    public void RealProviderRequiresPrepareBeforePostWriteAcquireAndReadOnlyNeedsNoAcquire()
    {
        var writerRuntime = new NeutralDomainRuntimeFacade();
        var writerKernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(writerRuntime));
        var (_, writerSubject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(writerKernel, 92, 920, 1);
        var (writerDevice, writerMapping) = CreateAuthorities(writerKernel, writerSubject);
        var writerGrant = writerKernel.BindPlatformDma(
            writerSubject,
            writerDevice,
            writerMapping,
            0,
            64,
            PlatformDmaDirection.DeviceWritesMemory).Value!;
        Assert.Equal(
            KernelError.PlatformDenied,
            writerKernel.AcquirePlatformDmaForCpu(writerSubject, writerGrant).Error);

        var readerRuntime = new NeutralDomainRuntimeFacade();
        var readerKernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(readerRuntime));
        var (_, readerSubject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(readerKernel, 93, 930, 1);
        var (readerDevice, readerMapping) = CreateAuthorities(readerKernel, readerSubject);
        var readerGrant = readerKernel.BindPlatformDma(
            readerSubject,
            readerDevice,
            readerMapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).Value!;
        Assert.True(readerKernel.PreparePlatformDmaForDevice(readerSubject, readerGrant).IsSuccess);
        Assert.Equal(
            KernelError.PlatformDenied,
            readerKernel.AcquirePlatformDmaForCpu(readerSubject, readerGrant).Error);
    }

    [Fact]
    public void DmaVisibilityV2IsRuntimeAdmissionAndStillHasNoSubmitSurface()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var dma = provider.QueryFeatures().Resolve(PlatformFeatureFamily.DmaMapping);

        Assert.Equal(PlatformDmaVisibilityContract.ContractVersion, dma.ContractVersion);
        Assert.Equal(2U, dma.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, dma.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, dma.Availability);

        var providerMethods = typeof(HybridCpuPlatformAuthorityProvider)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(static method => method.Name)
            .ToArray();
        Assert.Contains(nameof(IPlatformDmaVisibilityProvider.PrepareDmaGrantVisibility), providerMethods);
        Assert.Contains(nameof(IPlatformDmaVisibilityProvider.AcquireDmaGrantVisibility), providerMethods);
        Assert.DoesNotContain(providerMethods, static name =>
            name.Contains("SubmitDma", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("CompleteDma", StringComparison.OrdinalIgnoreCase));
    }

    private static (PlatformDeviceLease Device, PlatformOwnedRegionSliceMapping Mapping)
        CreateAuthorities(RuntimeKernel kernel, ProcessHandle subject)
    {
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-visibility0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;

        var buffer = kernel.AllocateBuffer<byte>(subject, 1024).Value!;
        var regionCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            regionCapability,
            buffer.Handle,
            128,
            512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        return (device, mapping);
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
