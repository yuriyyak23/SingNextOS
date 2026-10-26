using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformProviderConformanceMatrixTests
{
    [Theory]
    [InlineData("host")]
    [InlineData("hybridcpu")]
    public void DomainLeaseNegativeMatrixIsProviderIndependent(string kind)
    {
        IPlatformAuthorityProvider provider = Provider(kind);
        PlatformDomainIdentity subject = Subject(1301, 13010);
        PlatformAuthorityResult<PlatformProviderDomainLease> lease = provider.BindDomain(subject);
        Assert.True(lease.IsSuccess, lease.Message);
        Assert.Equal(PlatformAuthorityStatus.Denied, provider.BindDomain(subject).Status);

        PlatformProviderDomainLease stale = lease.Value! with
        {
            Generation = new PlatformProviderLeaseGeneration(lease.Value.Generation.Value + 1),
        };
        Assert.Equal(PlatformAuthorityStatus.Stale, provider.RevokeDomain(stale).Status);

        PlatformProviderDomainLease wrong = lease.Value with { Subject = Subject(1302, 13020) };
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, provider.RevokeDomain(wrong).Status);

        Assert.True(provider.RevokeDomain(lease.Value).IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Revoked, provider.RevokeDomain(lease.Value).Status);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("fault-host")]
    [InlineData("hybridcpu")]
    public void OwnedRegionNegativeMatrixIsProviderIndependent(string kind)
    {
        IPlatformAuthorityProvider provider = Provider(kind);
        PlatformDomainIdentity subject = Subject(1311, 13110);
        PlatformAuthorityResult<PlatformProviderDomainLease> domain = provider.BindDomain(subject);
        Assert.True(domain.IsSuccess, domain.Message);
        PlatformRegionIdentity region = new PlatformRegionIdentity(
            new RegionHandle(new RegionId(71), new RegionGeneration(3)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration),
            4096);

        Assert.Equal(
            PlatformAuthorityStatus.Denied,
            provider.MapOwnedRegion(domain.Value!, region, PlatformMemoryAccess.None).Status);

        PlatformRegionIdentity foreign = region with { Owner = new RegionOwner(new DomainId(9999), subject.ProcessGeneration) };
        Assert.Equal(
            PlatformAuthorityStatus.WrongDomain,
            provider.MapOwnedRegion(domain.Value!, foreign, PlatformMemoryAccess.Read).Status);
        Assert.True(provider.RevokeDomain(domain.Value!).IsSuccess);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("hybridcpu")]
    public void ActiveMappingProcessTeardownDrainsBeforeLocalReclaim(string kind)
    {
        IPlatformAuthorityProvider provider = Provider(kind);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1321, 13210);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId capability = kernel.MintCapability(
            process.DomainId,
            owner,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);

        KernelResult terminate = kernel.TerminateProcess(owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(owner).Error);
        Assert.Equal(
            RegionState.Released,
            kernel.Regions.Snapshot().Single(item => item.Handle.RegionId == region.Handle.RegionId).State);
    }

    [Fact]
    public void ProviderClaimsStayAtTheStrongestImplementedClass()
    {
        HostPlatformAuthorityProvider host = new HostPlatformAuthorityProvider();
        PlatformFeatureManifest hostManifest = host.QueryFeatures();
        Claim(hostManifest, PlatformFeatureFamily.NeutralDomains, PlatformFeatureAvailability.RuntimeAdmission);
        Claim(hostManifest, PlatformFeatureFamily.OwnedRegionMapping, PlatformFeatureAvailability.RuntimeAdmission);
        Claim(hostManifest, PlatformFeatureFamily.ExplicitMemoryVisibility, PlatformFeatureAvailability.ModelOnly);
        Claim(hostManifest, PlatformFeatureFamily.Dsc1BulkCompute, PlatformFeatureAvailability.ModelOnly);
        Claim(hostManifest, PlatformFeatureFamily.VirtualizationDomains, PlatformFeatureAvailability.ModelOnly);
        Claim(hostManifest, PlatformFeatureFamily.ExecutionPolicy, PlatformFeatureAvailability.ModelOnly);
        Assert.IsAssignableFrom<IPlatformMemoryVisibilityProvider>(host);
        Assert.IsAssignableFrom<IPlatformDsc1ComputeProvider>(host);
        Assert.IsAssignableFrom<IPlatformVirtualizationProvider>(host);
        Assert.IsAssignableFrom<IPlatformExecutionPolicyProvider>(host);

        HybridCpuPlatformAuthorityProvider hybrid = new HybridCpuPlatformAuthorityProvider();
        PlatformFeatureManifest hybridManifest = hybrid.QueryFeatures();
        foreach (PlatformFeatureFamily family in new[]
                 {
                     PlatformFeatureFamily.NeutralDomains,
                     PlatformFeatureFamily.OwnedRegionMapping,
                     PlatformFeatureFamily.IoDomainBinding,
                     PlatformFeatureFamily.DmaMapping,
                     PlatformFeatureFamily.ExplicitMemoryVisibility,
                     PlatformFeatureFamily.MmioMapping,
                     PlatformFeatureFamily.IrqBinding,
                 })
            Claim(hybridManifest, family, PlatformFeatureAvailability.RuntimeAdmission);

        Assert.IsAssignableFrom<IPlatformOwnedRegionMappingProvider>(hybrid);
        Assert.IsAssignableFrom<IPlatformDeviceLeaseProvider>(hybrid);
        Assert.IsAssignableFrom<IPlatformDmaGrantProvider>(hybrid);
        Assert.IsAssignableFrom<IPlatformRegionVisibilityProvider>(hybrid);
        Assert.IsAssignableFrom<IPlatformRegionAcquireProvider>(hybrid);
        Assert.IsAssignableFrom<IPlatformMmioLeaseProvider>(hybrid);
        Assert.IsAssignableFrom<IPlatformIrqBindingProvider>(hybrid);
        Assert.DoesNotContain(
            hybridManifest.Features,
            feature => feature.Availability is PlatformFeatureAvailability.Executable or PlatformFeatureAvailability.ProductionSecure);
    }

    [Fact]
    public void ChildScaffoldPresenceCannotPromoteBlockedFamilies()
    {
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider();
        PlatformFeatureManifest manifest = provider.QueryFeatures();
        Assert.IsAssignableFrom<IPlatformChildDomainProvider>(provider);
        Assert.IsAssignableFrom<IPlatformGuestMemoryProvider>(provider);
        Assert.IsAssignableFrom<IPlatformVirtualEventProvider>(provider);
        Assert.IsAssignableFrom<IPlatformVirtualIoProvider>(provider);
        Assert.IsNotAssignableFrom<IPlatformVirtualTrapProvider>(provider);
        foreach (PlatformFeatureFamily family in new[]
                 {
                     PlatformFeatureFamily.VirtualizationDomains,
                     PlatformFeatureFamily.NestedDomains,
                     PlatformFeatureFamily.PlatformEvidence,
                     PlatformFeatureFamily.SecureDomains,
                     PlatformFeatureFamily.ChildDomainLifecycle,
                     PlatformFeatureFamily.ChildGuestMemory,
                     PlatformFeatureFamily.ChildEventDelivery,
                     PlatformFeatureFamily.ChildTrapDelivery,
                     PlatformFeatureFamily.BoundedVirtualIo,
                 })
            Unavailable(manifest, family);

        HybridCpuChildDomainAdapterStatus status = provider.QueryChildDomainAdapterStatus();
        Assert.Equal(HybridCpuChildDomainAdapterReadiness.ExternalBlocked, status.Readiness);
        Assert.True(status.HasVersionedExternalChildContract);
        Assert.True(status.HasParentChildSubset);
        Assert.True(status.HasGuestMemory);
        Assert.True(status.HasVirtualEvents);
        Assert.True(status.HasVirtualTraps);
        Assert.False(status.HasVirtualIo);
        Assert.True(status.HasDefinitiveClose);
    }

    [Fact]
    public void FaultInjectionProviderPinsReclaimWhenDrainClosureIsAmbiguous()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            regionRevocationFailure: PlatformAuthorityStatus.Faulted);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1331, 13310);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId capability = kernel.MintCapability(
            process.DomainId,
            owner,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
            owner, binding, capability, region.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);

        KernelResult terminated = kernel.TerminateProcess(owner);

        Assert.Equal(KernelError.PlatformFaulted, terminated.Error);
        Assert.Equal(1, provider.RevokeRegionMappingCallCount);
        Assert.Equal(0, provider.RevokeDomainCallCount);
        Assert.Equal(RegionState.Owned,
            kernel.Regions.Snapshot().Single(item => item.Handle.RegionId == region.Handle.RegionId).State);
    }

    private static IPlatformAuthorityProvider Provider(string kind) => kind switch
    {
        "host" => new HostPlatformAuthorityProvider(),
        "fault-host" => new HostPlatformAuthorityProvider(
            regionRevocationFailure: PlatformAuthorityStatus.Faulted),
        "hybridcpu" => new HybridCpuPlatformAuthorityProvider(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PlatformDomainIdentity Subject(ulong process, ulong domain) =>
        new(new DomainId(domain), new ProcessHandle(new ProcessId(process), 1));

    private static void Claim(PlatformFeatureManifest manifest, PlatformFeatureFamily family, PlatformFeatureAvailability availability)
    {
        PlatformFeatureDescriptor feature = manifest.Resolve(family);
        Assert.True(feature.ContractVersion > 0);
        Assert.Equal(availability, feature.Availability);
    }

    private static void Unavailable(PlatformFeatureManifest manifest, PlatformFeatureFamily family)
    {
        PlatformFeatureDescriptor feature = manifest.Resolve(family);
        Assert.Equal(0u, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.Unavailable, feature.Availability);
    }

}
