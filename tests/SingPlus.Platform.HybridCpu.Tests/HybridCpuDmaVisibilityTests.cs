using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDmaVisibilityTests
{
    [Theory]
    [InlineData(false, false)][InlineData(true, false)]
    [InlineData(false, true)][InlineData(true, true)]
    public void ForeignOrAbsentCycleRouteCannotConsumeOriginalKernelVisibility(bool absent, bool acquire)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 91, 910, 1);
        (PlatformDeviceLease device, PlatformOwnedRegionSliceMapping mapping) = CreateAuthorities(kernel, subject);
        PlatformDmaGrant grant = kernel.BindPlatformDma(subject, device, mapping, 0, 64, PlatformDmaDirection.Bidirectional).Value!;
        Assert.True(kernel.PreparePlatformDmaForDevice(subject, grant).IsSuccess);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary states = (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaVisibilityStates", flags)!.GetValue(provider)!;
        object key = states.Keys.Cast<object>().Single();
        object original = states[key]!;
        if (absent) states.Remove(key);
        else states[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.False(acquire ? kernel.AcquirePlatformDmaForCpu(subject, grant).IsSuccess : kernel.PreparePlatformDmaForDevice(subject, grant).IsSuccess);
        Assert.Equal(1, proxy.DmaPrepareCalls);
        Assert.Equal(0, proxy.DmaAcquireCalls);
        Assert.False((bool)original.GetType().GetProperty("Acquired")!.GetValue(original)!);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        states[key] = original;
        IDictionary grants = (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaGrants", flags)!.GetValue(provider)!;
        object owner = grants.Values.Cast<object>().Single();
        PlatformProviderDmaGrant providerGrant = (PlatformProviderDmaGrant)owner.GetType().GetProperty("Grant")!.GetValue(owner)!;
        Assert.True(provider.AcquireDmaGrantVisibility(providerGrant).IsSuccess);
        Assert.True((bool)original.GetType().GetProperty("Acquired")!.GetValue(original)!);
        Assert.Equal(1, proxy.DmaAcquireCalls);
        Assert.False(provider.AcquireDmaGrantVisibility(providerGrant).IsSuccess);
        Assert.Equal(1, proxy.DmaAcquireCalls);
        Assert.True(provider.RevokeDmaGrant(providerGrant).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveDmaGrantCount);
    }

    [Fact]
    public void RealProviderPreparesAndAcquiresExactNonCoherentDmaCycle()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 91, 910, 1);
        (PlatformDeviceLease device, PlatformOwnedRegionSliceMapping mapping) = CreateAuthorities(kernel, subject);
        PlatformDmaGrant grant = kernel.BindPlatformDma(
            subject,
            device,
            mapping,
            32,
            128,
            PlatformDmaDirection.Bidirectional).Value!;

        KernelResult<PlatformDmaPrepareEvidence> prepare = kernel.PreparePlatformDmaForDevice(subject, grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        Assert.True(prepare.Value!.IsSatisfied);
        Assert.Equal(PlatformMemoryVisibilityRequirement.PublicationFence, prepare.Value.Requirement);
        Assert.Equal(1, runtime.ActiveDmaGrantCount);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);

        KernelResult<PlatformDmaAcquireEvidence> acquire = kernel.AcquirePlatformDmaForCpu(subject, grant);
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
        NeutralDomainRuntimeFacade writerRuntime = new NeutralDomainRuntimeFacade();
        RuntimeKernel writerKernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(writerRuntime));
        (SingProcess _, ProcessHandle writerSubject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(writerKernel, 92, 920, 1);
        (PlatformDeviceLease writerDevice, PlatformOwnedRegionSliceMapping writerMapping) = CreateAuthorities(writerKernel, writerSubject);
        PlatformDmaGrant writerGrant = writerKernel.BindPlatformDma(
            writerSubject,
            writerDevice,
            writerMapping,
            0,
            64,
            PlatformDmaDirection.DeviceWritesMemory).Value!;
        Assert.Equal(
            KernelError.PlatformDenied,
            writerKernel.AcquirePlatformDmaForCpu(writerSubject, writerGrant).Error);

        NeutralDomainRuntimeFacade readerRuntime = new NeutralDomainRuntimeFacade();
        RuntimeKernel readerKernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(readerRuntime));
        (SingProcess _, ProcessHandle readerSubject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(readerKernel, 93, 930, 1);
        (PlatformDeviceLease readerDevice, PlatformOwnedRegionSliceMapping readerMapping) = CreateAuthorities(readerKernel, readerSubject);
        PlatformDmaGrant readerGrant = readerKernel.BindPlatformDma(
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
    public void OriginalCopyV5HasManagedBackingSurfaceWithoutExecutablePromotion()
    {
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        PlatformFeatureDescriptor dma = provider.QueryFeatures().Resolve(PlatformFeatureFamily.DmaMapping);

        Assert.Equal(PlatformDmaCopySubmissionContract.ContractVersion, dma.ContractVersion);
        Assert.Equal(5U, dma.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, dma.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, dma.Availability);

        string[] providerMethods = typeof(HybridCpuPlatformAuthorityProvider)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(static method => method.Name)
            .ToArray();
        Assert.Contains(nameof(IPlatformDmaVisibilityProvider.PrepareDmaGrantVisibility), providerMethods);
        Assert.Contains(nameof(IPlatformDmaVisibilityProvider.AcquireDmaGrantVisibility), providerMethods);
        Assert.Contains(nameof(IPlatformDmaCopyBackingProvider.SubmitDmaCopy),providerMethods);
        Assert.IsAssignableFrom<IPlatformOriginalDmaCopyProvider>(provider);
        Assert.IsAssignableFrom<IPlatformDmaCompletionProvider>(provider);
        Assert.False(provider is IPlatformDmaSubmissionProvider); // No unbound single-leg effect.
    }

    private static (PlatformDeviceLease Device, PlatformOwnedRegionSliceMapping Mapping)
        CreateAuthorities(RuntimeKernel kernel, ProcessHandle subject)
    {
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma-visibility0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;

        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 1024).Value!;
        CapabilityId regionCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        PlatformOwnedRegionSliceMapping mapping = kernel.MapPlatformOwnedRegionSlice(
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
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }
}
