using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Virtualization;

public sealed class VirtualizationLocalModelTests
{
    [Fact]
    public async Task HostModelDomainCompletesLifecycleWithMappingAndEvents()
    {
        var scenario = CreateScenario();
        var vm = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability, new VirtualDomainProfile(2, 4096));
        Assert.True(vm.IsSuccess, vm.Message);
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        var region = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 256).Value!;
        var regionCapability = scenario.Kernel.MintCapability(scenario.Domain, scenario.Owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapGuestRegion(scenario.Owner, vm.Value.Domain, vm.Value.MemoryCapability, regionCapability, region.Handle, new GuestAddressRange(0, 256), GuestMemoryAccess.Read | GuestMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.Equal(KernelError.PlatformBindingActive, scenario.Kernel.ReleaseRegion(scenario.Owner, region).Error);

        Assert.True(scenario.Kernel.StartVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ExecuteCapability).IsSuccess);
        Assert.True(scenario.Kernel.ParkVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ExecuteCapability).IsSuccess);
        Assert.True(scenario.Kernel.ResumeVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ExecuteCapability).IsSuccess);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Owner).Value!;
        Assert.True(scenario.Kernel.InjectVirtualEvent(scenario.Owner, vm.Value.Domain, vm.Value.EventCapability, endpoint).IsSuccess);
        var delivered = await scenario.Kernel.WaitForKernelEventAsync(scenario.Owner, endpoint);
        Assert.True(delivered.IsSuccess);
        Assert.Equal(VirtualizationResourceIds.Events(vm.Value.Domain.DomainId), delivered.Value!.SourceResourceId);

        Assert.Equal(KernelError.PlatformBindingDraining, scenario.Kernel.DestroyVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ConfigureCapability).Error);
        Assert.Equal(KernelError.InvalidTransition, scenario.Kernel.InjectVirtualEvent(scenario.Owner, vm.Value.Domain, vm.Value.EventCapability, endpoint).Error);
        Assert.True(scenario.Kernel.CloseGuestRegionMapping(scenario.Owner, vm.Value.Domain, vm.Value.MemoryCapability, mapping.Value.Mapping).IsSuccess);
        Assert.True(scenario.Kernel.DestroyVirtualDomain(scenario.Owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        Assert.Equal(1, scenario.Provider.RevokeVirtualDomainCallCount);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Owner, region).IsSuccess);
    }

    [Fact]
    public void CreateRequiresExactCapabilityBeforeProviderCall()
    {
        var scenario = CreateScenario();
        var wrong = scenario.Kernel.MintCapability(scenario.Domain, scenario.Owner, ResourceKind.KernelService, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var denied = scenario.Kernel.CreateVirtualDomain(scenario.Owner, wrong, new VirtualDomainProfile(1, 4096));
        Assert.Equal(KernelError.WrongCapabilityResource, denied.Error);
        Assert.Equal(0, scenario.Provider.CreateVirtualDomainCallCount);
    }

    [Fact]
    public async Task WaiterCancellationDoesNotDestroyVirtualDomain()
    {
        var scenario = CreateScenario();
        var vm = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability, new VirtualDomainProfile(1, 4096)).Value;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Owner).Value!;
        using var cancellation = new CancellationTokenSource();
        var wait = scenario.Kernel.WaitForKernelEventAsync(scenario.Owner, endpoint, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
        Assert.Equal(VirtualDomainState.Configured, scenario.Kernel.QueryVirtualDomain(scenario.Owner, vm.Domain).Value);
    }

    [Fact]
    public void StaleDomainAndMappingAreRejectedWithoutReclaim()
    {
        var scenario = CreateScenario();
        var vm = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability, new VirtualDomainProfile(1, 4096)).Value;
        var stale = vm.Domain with { Generation = new VirtualDomainGeneration(2) };
        Assert.Equal(KernelError.StaleGeneration, scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, stale, vm.ConfigureCapability).Error);
        Assert.Equal(1, scenario.Provider.CreateVirtualDomainCallCount);
    }

    [Fact]
    public void FeatureClaimsRemainModelOnlyAndVmxUnavailable()
    {
        var scenario = CreateScenario();
        Assert.Equal(PlatformFeatureAvailability.ModelOnly, scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.VmxCompatibility).Availability);
        Assert.NotEqual(typeof(VirtualDomainHandle), typeof(ProcessHandle));
    }

    private static Scenario CreateScenario()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var domain = new DomainId(9400);
        var owner = TestFixtures.Create(kernel, 940, domain.Value).Handle;
        var create = kernel.MintCapability(domain, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        return new Scenario(kernel, provider, owner, domain, create);
    }

    private sealed record Scenario(RuntimeKernel Kernel, HostPlatformAuthorityProvider Provider, ProcessHandle Owner, DomainId Domain, CapabilityId CreateCapability);
}
