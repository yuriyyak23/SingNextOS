using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDsc1FailClosedTests
{
    [Fact]
    public void HybridCpuProviderReportsDsc1UnavailableAndDoesNotImplementModelContract()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(
            new NeutralDomainRuntimeFacade());
        var feature = provider.QueryFeatures().Resolve(
            PlatformFeatureFamily.Dsc1BulkCompute);

        Assert.Equal(0u, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.Unavailable, feature.Availability);
        Assert.DoesNotContain(
            typeof(IPlatformDsc1ComputeProvider),
            provider.GetType().GetInterfaces());
    }

    [Fact]
    public void HybridCpuSubmissionFailsClosedWithoutPublishingHostFallback()
    {
        var neutralRuntime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(neutralRuntime);
        var kernel = new RuntimeKernel(provider);
        var manifest = new SingProcessManifestV1(
            new ProcessId(701),
            new DomainId(710),
            1,
            "hybrid-dsc1-fail-closed",
            ExecutionRole.Sip,
            MemoryProfile.SipRegion);
        var process = kernel.CreateProcess(manifest).Value!;
        var subject = new ProcessHandle(process.ProcessId, process.Generation);
        var source = kernel.AllocateBuffer<byte>(subject, 32).Value!;
        var destination = kernel.AllocateBuffer<byte>(subject, 32).Value!;
        source.Span.Fill(0x31);
        destination.Span.Fill(0xA4);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var sourceCapability = kernel.MintCapability(
            process.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(source.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var destinationCapability = kernel.MintCapability(
            process.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(destination.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Write).Value!.CapabilityId;
        var computeCapability = new Dsc1ComputeCapability(
            kernel.MintCapability(
                process.DomainId,
                subject,
                ResourceKind.Compute,
                CapabilityResourceIds.Dsc1Copy,
                CapabilityRights.Execute).Value!.CapabilityId);
        var sourceMapping = kernel.MapPlatformOwnedRegion(
            subject,
            binding,
            sourceCapability,
            source.Handle,
            PlatformMemoryAccess.Read).Value!;
        var destinationMapping = kernel.MapPlatformOwnedRegion(
            subject,
            binding,
            destinationCapability,
            destination.Handle,
            PlatformMemoryAccess.Write).Value!;

        var rejected = kernel.SubmitPlatformDsc1Copy(
            subject,
            binding,
            computeCapability,
            source,
            new PlatformDsc1RegionRange(sourceMapping, 0, source.Length),
            destination,
            new PlatformDsc1RegionRange(destinationMapping, 0, destination.Length));

        Assert.False(rejected.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, rejected.Error);
        Assert.All(destination.Span.ToArray(), value => Assert.Equal((byte)0xA4, value));
        source.Span[0] = 0x42;
        Assert.Equal(2, neutralRuntime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, neutralRuntime.ActiveBindingCount);

        Assert.True(kernel.RevokePlatformRegionMapping(subject, sourceMapping).IsSuccess);
        Assert.True(kernel.RevokePlatformRegionMapping(subject, destinationMapping).IsSuccess);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
    }
}
