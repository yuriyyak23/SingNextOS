using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Virtualization;

public sealed class VirtualizationServiceSessionTests
{
    [Fact]
    public async Task DiscoverySessionAndTypedClientDriveCompleteLocalLifecycle()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var service = TestFixtures.Create(kernel, 1200, 12000).Handle;
        var caller = TestFixtures.Create(kernel, 1201, 12010).Handle;
        var createCapability = kernel.MintCapability(new DomainId(12010), caller, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var protocol = IVirtualizationServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var requirement = new CapabilityRequirementV1(ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure);
        var descriptor = kernel.RegisterService(service, "virtualization", contract, protocol, IVirtualizationServiceResponseProtocol.Definition, [requirement]).Value!;
        var discovered = Assert.Single(kernel.ResolveByContract(contract).Value!);
        Assert.Equal(descriptor, discovered);
        var session = kernel.OpenSession(caller, discovered, [createCapability]).Value;
        var host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

        var createTask = client.CreateAsync(new CreateVirtualDomainRequest(createCapability, new VirtualDomainProfile(2, 1 << 20))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var authority = (await createTask).Authority;

        await Dispatch(client.ConfigureAsync(new VirtualDomainCommand(authority.Domain, authority.ConfigureCapability)), host);
        var region = kernel.AllocateBuffer<byte>(caller, 4096).Value!;
        var regionCapability = kernel.MintCapability(new DomainId(12010), caller, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapTask = client.MapGuestRegionAsync(new MapGuestRegionRequest(authority.Domain, authority.MemoryCapability, regionCapability, region.Handle, new GuestAddressRange(0x2000, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Write)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var mapping = (await mapTask).Mapping;
        await Dispatch(client.StartAsync(new VirtualDomainCommand(authority.Domain, authority.ExecuteCapability)), host);
        var eventEndpoint = kernel.CreateKernelEventEndpoint(caller).Value!;
        await Dispatch(client.InjectEventAsync(new VirtualEventCommand(authority.Domain, authority.EventCapability, eventEndpoint)), host);
        var waitTask = client.WaitEventAsync(new WaitVirtualEventRequest(authority.Domain, eventEndpoint)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var delivered = (await waitTask).Event;
        Assert.Equal(KernelEventClass.ExternalSignal, delivered.EventClass);
        Assert.Equal(VirtualizationResourceIds.Events(authority.Domain.DomainId), delivered.SourceResourceId);
        await Dispatch(client.ParkAsync(new VirtualDomainCommand(authority.Domain, authority.ExecuteCapability)), host);
        await Dispatch(client.ResumeAsync(new VirtualDomainCommand(authority.Domain, authority.ExecuteCapability)), host);
        await Dispatch(client.ParkAsync(new VirtualDomainCommand(authority.Domain, authority.ExecuteCapability)), host);
        await Dispatch(client.CloseGuestRegionMappingAsync(new CloseGuestRegionMappingRequest(authority.Domain, authority.MemoryCapability, mapping.Mapping)), host);
        await Dispatch(client.DestroyAsync(new VirtualDomainCommand(authority.Domain, authority.ConfigureCapability)), host);

        Assert.Equal(1, provider.CreateVirtualDomainCallCount);
        Assert.Equal(1, provider.RevokeVirtualDomainCallCount);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesVirtualDomainBeforeRegionReclaim()
    {
        var provider = new HostPlatformAuthorityProvider();
        var (kernel, owner, authority, _, _) = CreateMappedDomain(provider);

        var terminated = kernel.TerminateProcess(owner);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(1, provider.RevokeVirtualDomainCallCount);
    }

    [Fact]
    public void AmbiguousProviderRevokeQuarantinesDomainAndForbidsProcessReclaim()
    {
        var provider = new RevokeFaultProvider();
        var (kernel, owner, authority, region, _) = CreateMappedDomain(provider);

        var terminated = kernel.TerminateProcess(owner);

        Assert.Equal(KernelError.PlatformFaulted, terminated.Error);
        Assert.Equal(VirtualDomainState.Quarantined, kernel.QueryVirtualDomain(owner, authority.Domain).Value);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.ReleaseRegion(owner, region).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
    }

    private static async Task Dispatch(ValueTask pending, RuntimeVirtualizationServiceHost host)
    {
        var task = pending.AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static (RuntimeKernel Kernel, ProcessHandle Owner, VirtualDomainAuthoritySet Authority, SingPlus.Sip.OwnedBuffer<byte> Region, GuestRegionMapping Mapping) CreateMappedDomain(IPlatformAuthorityProvider provider)
    {
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 1210, 12100).Handle;
        var create = kernel.MintCapability(new DomainId(12100), owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var authority = kernel.CreateVirtualDomain(owner, create, new VirtualDomainProfile(1, 65536)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var regionCapability = kernel.MintCapability(new DomainId(12100), owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability, regionCapability, region.Handle, new GuestAddressRange(0, 4096), GuestMemoryAccess.Read).Value!;
        return (kernel, owner, authority, region, mapping);
    }

    private sealed class RevokeFaultProvider : IPlatformAuthorityProvider, IPlatformVirtualizationProvider, IPlatformFeatureProvider
    {
        private readonly HostPlatformAuthorityProvider _inner = new();
        public PlatformProviderDescriptor Descriptor => _inner.Descriptor;
        public PlatformFeatureManifest QueryFeatures() => _inner.QueryFeatures();
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) => _inner.BindDomain(subject);
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => _inner.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access) => _inner.MapOwnedRegion(domainLease, region, access);
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => _inner.RevokeRegionMapping(mapping, policy);
        public PlatformAuthorityResult<PlatformProviderVirtualDomainLease> CreateVirtualDomain(PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile) => _inner.CreateVirtualDomain(owner, profile);
        public PlatformAuthorityResult TransitionVirtualDomain(PlatformProviderVirtualDomainLease lease, PlatformVirtualDomainTransition transition) => _inner.TransitionVirtualDomain(lease, transition);
        public PlatformAuthorityResult RevokeVirtualDomain(PlatformProviderVirtualDomainLease lease) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Injected ambiguous VM revoke.");
    }
}
