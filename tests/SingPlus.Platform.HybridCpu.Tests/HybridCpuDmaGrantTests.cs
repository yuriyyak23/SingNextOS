using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDmaGrantTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ForeignDmaOwnerCannotConsumeOriginalKernelClosureOrRegion(bool completed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 81, 810, 1);
        var (device, mapping) = CreateAuthorities(kernel, subject);
        var region = Assert.Single(kernel.Regions.Snapshot());
        var grant = kernel.BindPlatformDma(subject, device, mapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_dmaGrants", flags)!.GetValue(provider)!;
        var original = records.Values.Cast<object>().Single();
        var providerGrant = (PlatformProviderDmaGrant)original.GetType().GetProperty("Grant")!.GetValue(original)!;
        var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (completed)
        {
            var (_, otherSubject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 82, 820, 1);
            var (otherDevice, otherMapping) = CreateAuthorities(kernel, otherSubject);
            var otherGrant = kernel.BindPlatformDma(otherSubject, otherDevice, otherMapping, 0, 64, PlatformDmaDirection.DeviceReadsMemory).Value!;
            var otherRecord = records.Values.Cast<object>().Single(x => !ReferenceEquals(x, original));
            Assert.True(kernel.RevokePlatformDma(otherSubject, otherGrant).IsSuccess);
            copy.GetType().GetProperty("CloseObservation")!.SetValue(copy,
                otherRecord.GetType().GetProperty("CloseObservation")!.GetValue(otherRecord));
            Assert.True(kernel.TerminateProcess(otherSubject).IsSuccess);
        }
        records[providerGrant.GrantId] = copy;
        var closeCalls = proxy.DmaCloseCalls;
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
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 81, 810, 1);
        var (device, mapping) = CreateAuthorities(kernel, subject);

        var grant = kernel.BindPlatformDma(
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
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var dma = provider.QueryFeatures().Resolve(PlatformFeatureFamily.DmaMapping);

        Assert.Equal(PlatformDmaCopySubmissionContract.ContractVersion, dma.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, dma.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, dma.Availability);
    }

    [Fact]
    public void ProcessTeardownClosesNeutralDmaBeforeDeviceMappingAndDomain()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 82, 820, 1);
        var (device, mapping) = CreateAuthorities(kernel, subject);
        Assert.True(kernel.BindPlatformDma(
            subject,
            device,
            mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);
        Assert.Equal(1, runtime.ActiveDmaGrantCount);

        var terminate = kernel.TerminateProcess(subject);

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
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/dma0",
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
