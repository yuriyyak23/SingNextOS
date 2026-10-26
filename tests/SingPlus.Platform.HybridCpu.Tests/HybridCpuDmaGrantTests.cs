using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDmaGrantTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ForeignDmaOwnerCannotConsumeOriginalKernelClosureOrRegion(bool completed)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 81, 810, 1);
        (PlatformDeviceLease device, PlatformOwnedRegionSliceMapping mapping) = CreateAuthorities(kernel, subject);
        RegionDescriptor region = Assert.Single(kernel.Regions.Snapshot());
        PlatformDmaGrant grant = kernel.BindPlatformDma(subject, device, mapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary records = (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaGrants", flags)!.GetValue(provider)!;
        object original = records.Values.Cast<object>().Single();
        PlatformProviderDmaGrant providerGrant = (PlatformProviderDmaGrant)original.GetType().GetProperty("Grant")!.GetValue(original)!;
        object copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (completed)
        {
            (SingProcess _, ProcessHandle otherSubject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 82, 820, 1);
            (PlatformDeviceLease otherDevice, PlatformOwnedRegionSliceMapping otherMapping) = CreateAuthorities(kernel, otherSubject);
            PlatformDmaGrant otherGrant = kernel.BindPlatformDma(otherSubject, otherDevice, otherMapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
            object otherRecord = records.Values.Cast<object>().Single(x => !ReferenceEquals(x, original));
            Assert.True(kernel.RevokePlatformDma(otherSubject, otherGrant).IsSuccess);
            copy.GetType().GetProperty("CloseObservation")!.SetValue(copy,
                otherRecord.GetType().GetProperty("CloseObservation")!.GetValue(otherRecord));
            Assert.True(kernel.TerminateProcess(otherSubject).IsSuccess);
        }
        records[providerGrant.GrantId] = copy;
        int closeCalls = proxy.DmaCloseCalls;
        Assert.False(kernel.RevokePlatformDma(subject, grant).IsSuccess);
        Assert.Equal(closeCalls, proxy.DmaCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveDmaGrantCount);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        records[providerGrant.GrantId] = original;
        Assert.True(provider.RevokeDmaGrant(providerGrant).IsSuccess);
        Assert.Equal(closeCalls + 1, proxy.DmaCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveDmaGrantCount);
        Assert.False(kernel.RevokePlatformDma(subject, grant).IsSuccess);
    }

    [Fact]
    public void ExactDeviceAndMappedRegionMaterializeNeutralDmaGrantWithoutSubmit()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 81, 810, 1);
        (PlatformDeviceLease device, PlatformOwnedRegionSliceMapping mapping) = CreateAuthorities(kernel, subject);

        KernelResult<PlatformDmaGrant> grant = kernel.BindPlatformDma(
            subject,
            device,
            mapping,
            32,
            128,
            PlatformDmaDirection.DeviceReadsMemory);

        Assert.True(grant.IsSuccess, grant.Message);
        Assert.Equal(1, runtime.ActiveDmaGrantCount);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.NotEqual(typeof(PlatformDmaGrantId), typeof(NeutralDmaGrantHandle));
        Assert.NotEqual(typeof(PlatformProviderDmaGrantId), typeof(NeutralDmaGrantHandle));

        Assert.True(kernel.RevokePlatformRegionMapping(subject, mapping).IsSuccess);
        Assert.Equal(0, runtime.ActiveDmaGrantCount);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
    }

    [Fact]
    public void HybridCpuAdvertisesDmaGrantAsAdmissionOnlyNotExecutable()
    {
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        PlatformFeatureDescriptor dma = provider.QueryFeatures().Resolve(PlatformFeatureFamily.DmaMapping);

        Assert.Equal(PlatformDmaCopySubmissionContract.ContractVersion, dma.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, dma.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, dma.Availability);
    }

    [Fact]
    public void ProcessTeardownClosesNeutralDmaBeforeDeviceMappingAndDomain()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess? process, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 82, 820, 1);
        (PlatformDeviceLease device, PlatformOwnedRegionSliceMapping mapping) = CreateAuthorities(kernel, subject);
        Assert.True(kernel.BindPlatformDma(
            subject,
            device,
            mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
        Assert.Equal(1, runtime.ActiveDmaGrantCount);

        KernelResult terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveDmaGrantCount);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
    }

    private static (PlatformDeviceLease Device, PlatformOwnedRegionSliceMapping Mapping)
        CreateAuthorities(RuntimeKernel kernel, ProcessHandle subject)
    {
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma0",
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
