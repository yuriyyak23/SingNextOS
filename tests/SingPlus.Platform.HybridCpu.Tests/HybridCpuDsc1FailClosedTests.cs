using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDsc1FailClosedTests
{
    [Fact]
    public void HybridCpuProviderReportsDsc1AdmissionOnlyAndDoesNotImplementModelContract()
    {
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(
            new NeutralDomainRuntimeFacade());
        PlatformFeatureDescriptor feature = provider.QueryFeatures().Resolve(
            PlatformFeatureFamily.Dsc1BulkCompute);

        Assert.Equal(PlatformOriginalDsc1CopyBackingContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.DoesNotContain(
            typeof(IPlatformDsc1ComputeProvider),
            provider.GetType().GetInterfaces());
    }

    [Fact]
    public void HybridCpuSubmissionFailsClosedWithoutPublishingHostFallback()
    {
        NeutralDomainRuntimeFacade neutralRuntime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(neutralRuntime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        SingProcessManifestV1 manifest = new SingProcessManifestV1(
            new ProcessId(701),
            new DomainId(710),
            1,
            "hybrid-dsc1-fail-closed",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        SingProcess process = kernel.CreateProcess(manifest).Value!;
        ProcessHandle subject = new ProcessHandle(process.ProcessId, process.Generation);
        OwnedBuffer<byte> source = kernel.AllocateBuffer<byte>(subject, 32).Value!;
        OwnedBuffer<byte> destination = kernel.AllocateBuffer<byte>(subject, 32).Value!;
        source.Span.Fill(0x31);
        destination.Span.Fill(0xA4);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId sourceCapability = kernel.MintCapability(
            process.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(source.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        CapabilityId destinationCapability = kernel.MintCapability(
            process.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(destination.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Write).Value!.CapabilityId;
        Dsc1ComputeCapability computeCapability = new Dsc1ComputeCapability(
            kernel.MintCapability(
                process.DomainId,
                subject,
                ResourceKind.Compute,
                CapabilityResourceIds.Dsc1Copy,
                CapabilityRights.Execute).Value!.CapabilityId);
        PlatformRegionMapping sourceMapping = kernel.MapPlatformOwnedRegion(
            subject,
            binding,
            sourceCapability,
            source.Handle,
            PlatformMemoryAccess.Read).Value!;
        PlatformRegionMapping destinationMapping = kernel.MapPlatformOwnedRegion(
            subject,
            binding,
            destinationCapability,
            destination.Handle,
            PlatformMemoryAccess.Write).Value!;

        KernelResult<PlatformDsc1CopySubmission> rejected = kernel.SubmitPlatformDsc1Copy(
            subject,
            binding,
            computeCapability,
            source,
            new PlatformDsc1RegionRange(sourceMapping, 0, source.Length),
            destination,
            new PlatformDsc1RegionRange(destinationMapping, 0, destination.Length));

        Assert.False(rejected.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, rejected.Error);
        Assert.Equal(2, neutralRuntime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, neutralRuntime.ActiveBindingCount);

        Assert.False(kernel.RevokePlatformRegionMapping(subject, sourceMapping).IsSuccess);
        Assert.False(kernel.RevokePlatformRegionMapping(subject, destinationMapping).IsSuccess);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(2, neutralRuntime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, neutralRuntime.ActiveBindingCount);
    }
}
