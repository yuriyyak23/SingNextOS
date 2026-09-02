using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuOwnedRegionMappingTests
{
    [Fact]
    public void ExactSlicePublishesFenceAndClosesBeforeOwnershipCanMove()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 41, 410, 1);
        var (_, target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 42, 420, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);

        var mapped = kernel.MapPlatformOwnedRegionSlice(
            owner,
            binding,
            capability,
            region.Handle,
            offset: 64,
            length: 512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(64, mapped.Value!.Offset);
        Assert.Equal(512, mapped.Value.Length);
        Assert.NotEqual(typeof(PlatformProviderRegionMappingId), typeof(NeutralOwnedRegionMappingHandle));
        Assert.NotEqual(typeof(PlatformProviderLeaseGeneration), typeof(NeutralOwnedRegionMappingEpoch));

        var coherent = kernel.PreparePlatformRegionMappingForConsumer(
            owner,
            mapped.Value,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.CoherentAccess);
        Assert.Equal(KernelError.PlatformUnsupported, coherent.Error);

        var fence = kernel.PreparePlatformRegionMappingForConsumer(
            owner,
            mapped.Value,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        Assert.True(fence.IsSuccess, fence.Message);
        Assert.True(fence.Value!.IsSatisfied);
        Assert.Equal(
            PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            fence.Value.Outcome);

        Assert.Equal(
            KernelError.PlatformBindingActive,
            kernel.TransferRegion(owner, target, region).Error);

        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value).IsSuccess);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);

        var moved = kernel.TransferRegion(owner, target, region);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(new RegionGeneration(2), moved.Value!.Handle.Generation);
    }

    [Fact]
    public void DirectProviderRequiresExactMappingGenerationAndOwner()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(430), 3);
        var domain = provider.BindDomain(subject).Value!;
        var region = new PlatformRegionIdentity(
            new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration),
            2048);
        var slice = new PlatformRegionSlice(region, 128, 256, PlatformMemoryAccess.Read);
        var mapping = provider.MapOwnedRegionSlice(domain, slice);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);

        var stale = mapping.Value!.Lease with
        {
            Generation = new PlatformProviderLeaseGeneration(
                mapping.Value.Lease.Generation.Value + 1),
        };
        var visibility = provider.PrepareRegionMappingForConsumer(
            new PlatformRegionVisibilityRequest(
                stale,
                slice,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence));

        Assert.Equal(PlatformAuthorityStatus.Stale, visibility.Status);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.True(provider.RevokeRegionMapping(
            mapping.Value.Lease,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesExactMappingBeforeHybridCpuDomainAndLocalExit()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 44, 440, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        Assert.True(kernel.MapPlatformOwnedRegionSlice(
            owner,
            binding,
            capability,
            region.Handle,
            16,
            128,
            PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var terminate = kernel.TerminateProcess(owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(owner).Error);
    }

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }
}
