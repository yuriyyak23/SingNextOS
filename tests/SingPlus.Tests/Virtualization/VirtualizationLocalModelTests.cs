using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Virtualization;

public sealed class VirtualizationLocalModelTests
{
    [Fact]
    public async Task HostModelDomainCompletesLifecycleWithMappingAndEvents()
    {
        Scenario scenario = CreateScenario();
        KernelResult<VirtualDomainAuthoritySet> vm = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability, new VirtualDomainProfile(2, 4096));
        Assert.True(vm.IsSuccess, vm.Message);
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        OwnedBuffer<byte> region = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 256).Value!;
        CapabilityId regionCapability = scenario.Kernel.MintCapability(scenario.Domain, scenario.Owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapping = scenario.Kernel.MapGuestRegion(scenario.Owner, vm.Value.Domain, vm.Value.MemoryCapability, regionCapability, region.Handle, new GuestAddressRange(0, 256), GuestMemoryAccess.Read | GuestMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.Equal(KernelError.PlatformBindingActive, scenario.Kernel.ReleaseRegion(scenario.Owner, region).Error);

        Assert.True(scenario.Kernel.StartVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ExecuteCapability).IsSuccess);
        Assert.True(scenario.Kernel.ParkVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ExecuteCapability).IsSuccess);
        Assert.True(scenario.Kernel.ResumeVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ExecuteCapability).IsSuccess);
        KernelEventEndpoint endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Owner).Value!;
        Assert.True(scenario.Kernel.InjectVirtualEvent(scenario.Owner, vm.Value.Domain, vm.Value.EventCapability, endpoint).IsSuccess);
        KernelResult<KernelEvent> delivered = await scenario.Kernel.WaitForKernelEventAsync(scenario.Owner, endpoint);
        Assert.True(delivered.IsSuccess);
        Assert.Equal(VirtualizationResourceIds.Events(vm.Value.Domain.DomainId), delivered.Value!.SourceResourceId);

        Assert.True(scenario.Kernel.DestroyVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.VirtualDomainNotFound, scenario.Kernel.InjectVirtualEvent(scenario.Owner, vm.Value.Domain, vm.Value.EventCapability, endpoint).Error);
        Assert.Equal(1, scenario.Provider.RevokeVirtualDomainCallCount);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Owner, region).IsSuccess);
    }

    [Fact]
    public void CreateRequiresExactCapabilityBeforeProviderCall()
    {
        Scenario scenario = CreateScenario();
        CapabilityId wrong = scenario.Kernel.MintCapability(scenario.Domain, scenario.Owner, ResourceKind.KernelService, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<VirtualDomainAuthoritySet> denied = scenario.Kernel.CreateVirtualDomain(scenario.Owner, wrong, new VirtualDomainProfile(1, 4096));
        Assert.Equal(KernelError.WrongCapabilityResource, denied.Error);
        Assert.Equal(0, scenario.Provider.CreateVirtualDomainCallCount);
    }

    [Fact]
    public async Task WaiterCancellationDoesNotDestroyVirtualDomain()
    {
        Scenario scenario = CreateScenario();
        VirtualDomainAuthoritySet vm = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability, new VirtualDomainProfile(1, 4096)).Value;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Owner).Value!;
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task<KernelResult<KernelEvent>> wait = scenario.Kernel.WaitForKernelEventAsync(scenario.Owner, endpoint, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
        Assert.Equal(VirtualDomainState.Configured, scenario.Kernel.QueryVirtualDomain(scenario.Owner, vm.Domain).Value);
    }

    [Fact]
    public void StaleDomainAndMappingAreRejectedWithoutReclaim()
    {
        Scenario scenario = CreateScenario();
        VirtualDomainAuthoritySet vm = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability, new VirtualDomainProfile(1, 4096)).Value;
        VirtualDomainHandle stale = vm.Domain with { Generation = new VirtualDomainGeneration(2) };
        Assert.Equal(KernelError.StaleGeneration, scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, stale, vm.ConfigureCapability).Error);
        Assert.Equal(1, scenario.Provider.CreateVirtualDomainCallCount);
    }

    [Fact]
    public void FeatureClaimsRemainModelOnlyAndVmxUnavailable()
    {
        Scenario scenario = CreateScenario();
        Assert.Equal(PlatformFeatureAvailability.ModelOnly, scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.VmxCompatibility).Availability);
        Assert.NotEqual(typeof(VirtualDomainHandle), typeof(ProcessHandle));
    }

    private static Scenario CreateScenario()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        DomainId domain = new DomainId(9400);
        ProcessHandle owner = TestFixtures.Create(kernel, 940, domain.Value).Handle;
        CapabilityId create = kernel.MintCapability(domain, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        return new Scenario(kernel, provider, owner, domain, create);
    }

    private sealed record Scenario(RuntimeKernel Kernel, HostPlatformAuthorityProvider Provider, ProcessHandle Owner, DomainId Domain, CapabilityId CreateCapability);
}
