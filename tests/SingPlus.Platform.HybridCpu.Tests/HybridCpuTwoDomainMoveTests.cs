using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuTwoDomainMoveTests
{
    [Fact]
    public void RealNeutralWritableMappingClosesAcquiresMovesAndPublishesTarget()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, source) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 51, 510, 1);
        var (_, target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 52, 520, 1);
        var buffer = kernel.AllocateBuffer<byte>(source, 1024).Value!;
        var sourceBinding = kernel.BindPlatformDomain(source).Value!;
        var targetBinding = kernel.BindPlatformDomain(target).Value!;
        var sourceCapability = MintRegionCapability(
            kernel,
            source,
            buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        var targetCapability = MintRegionCapability(
            kernel,
            target,
            buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var sourceMapping = kernel.MapPlatformOwnedRegionSlice(
            source,
            sourceBinding,
            sourceCapability,
            buffer.Handle,
            64,
            512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;

        var moved = kernel.MovePlatformOwnedBuffer(
            source,
            target,
            buffer,
            sourceMapping,
            new PlatformMoveTargetMappingRequest(
                targetBinding,
                targetCapability,
                PlatformMemoryAccess.Read));

        Assert.True(moved.IsSuccess, moved.Message);
        Assert.False(buffer.IsValid);
        Assert.True(moved.Value!.Buffer.IsValid);
        Assert.Equal(new RegionGeneration(2), moved.Value.Buffer.Handle.Generation);
        Assert.Equal(
            PlatformMoveTargetExposureState.ExactMappedAndPublished,
            moved.Value.TargetExposure);
        Assert.NotNull(moved.Value.TargetMapping);
        Assert.True(moved.Value.TargetPublication!.Value.IsSatisfied);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(2, runtime.ActiveBindingCount);
        Assert.Equal(
            PlatformRegionAcquireContract.ContractVersion,
            provider.QueryFeatures()
                .Resolve(PlatformFeatureFamily.ExplicitMemoryVisibility)
                .ContractVersion);

        Assert.True(kernel.RevokePlatformRegionMapping(
            target,
            moved.Value.TargetMapping!.Value).IsSuccess);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.True(kernel.RevokePlatformDomain(source, sourceBinding).IsSuccess);
        Assert.True(kernel.RevokePlatformDomain(target, targetBinding).IsSuccess);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void ProviderAcquireRequiresExactClosedMappingAndRejectsStaleGeneration()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(530), 3);
        var domain = provider.BindDomain(subject).Value!;
        var region = new PlatformRegionIdentity(
            new RegionHandle(new RegionId(77), new RegionGeneration(9)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration),
            4096);
        var slice = new PlatformRegionSlice(
            region,
            128,
            512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var mapping = provider.MapOwnedRegionSlice(domain, slice).Value!;

        var early = provider.AcquireRegionMappingFromConsumer(
            new PlatformRegionAcquireRequest(
                mapping.Lease,
                slice,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryAcquireRequirement.AcquisitionFence));
        Assert.Equal(PlatformAuthorityStatus.Denied, early.Status);

        var revoke = provider.BeginRegionMappingRevocation(
            mapping.Lease,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        Assert.True(revoke.IsSuccess, revoke.Message);
        var receipt = provider.ObserveCompletion(revoke.Value!.Operation);
        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.True(receipt.Value!.ProvesClosure);

        var stale = mapping.Lease with
        {
            Generation = new PlatformProviderLeaseGeneration(
                mapping.Lease.Generation.Value + 1),
        };
        var staleAcquire = provider.AcquireRegionMappingFromConsumer(
            new PlatformRegionAcquireRequest(
                stale,
                slice,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryAcquireRequirement.AcquisitionFence));
        Assert.Equal(PlatformAuthorityStatus.Stale, staleAcquire.Status);

        var acquired = provider.AcquireRegionMappingFromConsumer(
            new PlatformRegionAcquireRequest(
                mapping.Lease,
                slice,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryAcquireRequirement.AcquisitionFence));
        Assert.True(acquired.IsSuccess, acquired.Message);
        Assert.True(acquired.Value!.IsSatisfied);
        Assert.Equal(
            PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied,
            acquired.Value.Outcome);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
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
