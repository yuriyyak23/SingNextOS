using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuTwoDomainMoveTests
{
    [Fact]
    public void RealNeutralWritableMappingClosesAcquiresMovesAndPublishesTarget()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle source) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 51, 510, 1);
        (SingProcess _, ProcessHandle target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 52, 520, 1);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(source, 1024).Value!;
        PlatformDomainBinding sourceBinding = kernel.BindPlatformDomain(source).Value!;
        PlatformDomainBinding targetBinding = kernel.BindPlatformDomain(target).Value!;
        CapabilityId sourceCapability = MintRegionCapability(
            kernel,
            source,
            buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        CapabilityId targetCapability = MintRegionCapability(
            kernel,
            target,
            buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        PlatformOwnedRegionSliceMapping sourceMapping = kernel.MapPlatformOwnedRegionSlice(
            source,
            sourceBinding,
            sourceCapability,
            buffer.Handle,
            64,
            512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;

        KernelResult<PlatformOwnedBufferMoveResult<byte>> moved = kernel.MovePlatformOwnedBuffer(
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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(
            new DomainId(530),
            new ProcessHandle(new ProcessId(531), 3));
        PlatformProviderDomainLease domain = provider.BindDomain(subject).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(
            new RegionHandle(new RegionId(77), new RegionGeneration(9)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration),
            4096);
        PlatformRegionSlice slice = new PlatformRegionSlice(
            region,
            128,
            512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        PlatformProviderOwnedRegionMapping mapping = provider.MapOwnedRegionSlice(domain, slice).Value!;

        PlatformAuthorityResult<PlatformRegionAcquireResult> early = provider.AcquireRegionMappingFromConsumer(
            new PlatformRegionAcquireRequest(
                mapping.Lease,
                slice,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryAcquireRequirement.AcquisitionFence));
        Assert.Equal(PlatformAuthorityStatus.Denied, early.Status);

        PlatformAuthorityResult<PlatformRegionRevocationTicket> revoke = provider.BeginRegionMappingRevocation(
            mapping.Lease,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        Assert.True(revoke.IsSuccess, revoke.Message);
        PlatformAuthorityResult<PlatformCompletionReceipt> receipt = provider.ObserveCompletion(revoke.Value!.Operation);
        Assert.True(receipt.IsSuccess, receipt.Message);
        Assert.True(receipt.Value!.ProvesClosure);

        PlatformProviderRegionMappingLease stale = mapping.Lease with
        {
            Generation = new PlatformProviderLeaseGeneration(
                mapping.Lease.Generation.Value + 1),
        };
        PlatformAuthorityResult<PlatformRegionAcquireResult> staleAcquire = provider.AcquireRegionMappingFromConsumer(
            new PlatformRegionAcquireRequest(
                stale,
                slice,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryAcquireRequirement.AcquisitionFence));
        Assert.Equal(PlatformAuthorityStatus.Stale, staleAcquire.Status);

        PlatformAuthorityResult<PlatformRegionAcquireResult> acquired = provider.AcquireRegionMappingFromConsumer(
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
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        KernelResult<CapabilityDescriptorV1> capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }
}
