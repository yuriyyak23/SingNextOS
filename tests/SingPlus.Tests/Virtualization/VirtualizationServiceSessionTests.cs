using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Virtualization;

public sealed class VirtualizationServiceSessionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeDrainRetainsOriginalResultAndStopsNewDispatchUntilPublicationFinishes(bool terminate)
    {
        HostPlatformAuthorityProvider provider =new HostPlatformAuthorityProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,2951,29510).Handle; ProcessHandle service =TestFixtures.Create(kernel,2952,29520).Handle;
        OwnedBuffer<byte> independent =kernel.AllocateBuffer<byte>(service,16).Value!;independent.Span.Fill(99);
        CapabilityId capability =kernel.MintCapability(new(29510),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"drain-result",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        ValueTask<VirtualDomainAuthorityResponse> reply =client.CreateAsync(new(capability,new(1,4096)));
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            Assert.Equal(KernelError.SessionDraining,kernel.CloseSession(caller,session).Error);
            throw new IOException("Original result pending while native drain is requested");
        };
        Assert.Throws<IOException>(()=>host.ProcessNext());
        Assert.True(host.HasPendingResult);Assert.False(reply.IsCompleted);
        KernelResult blocked =terminate ? kernel.TerminateProcess(caller) : kernel.CloseSession(caller,session);
        if(terminate)
        {
            Assert.True(blocked.IsSuccess,blocked.Message);
            await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
            Assert.False(host.HasPendingResult);Assert.Equal(1,provider.CreateVirtualDomainCallCount);
            Assert.Equal(1,provider.RevokeVirtualDomainCallCount);
            Assert.Equal(KernelError.SessionDraining,host.ProcessNext().Error);
            Assert.Equal((byte)99,independent.Span[0]);
            Assert.True(kernel.ReleaseRegion(service,independent).IsSuccess);
            return;
        }
        Assert.False(blocked.IsSuccess);Assert.Equal(1,provider.CreateVirtualDomainCallCount);Assert.Equal(0,provider.RevokeVirtualDomainCallCount);
        Assert.Equal((byte)99,independent.Span[0]);
        Assert.True(host.AdvancePendingResult().IsSuccess);
        VirtualDomainAuthoritySet authority =(await reply).Authority;
        Assert.Equal(KernelError.SessionDraining,host.ProcessNext().Error);
        Assert.False(host.HasPendingResult);Assert.Equal(1,provider.CreateVirtualDomainCallCount);
        if(terminate)
        {
            KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
            Assert.Equal(1,provider.RevokeVirtualDomainCallCount);
        }
        else
        {
            Assert.True(kernel.CloseSession(caller,session).IsSuccess);
            Assert.Equal(0,provider.RevokeVirtualDomainCallCount);
            Assert.True(kernel.DestroyVirtualDomain(caller,authority.Domain,authority.ConfigureCapability).IsSuccess);
            Assert.Equal(1,provider.RevokeVirtualDomainCallCount);
        }
        Assert.Equal((byte)99,independent.Span[0]);
        Assert.True(kernel.ReleaseRegion(service,independent).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SuccessfulCreateResultSurvivesPublicationThrowAndOriginalOnlyContinuation(bool committed)
    {
        HostPlatformAuthorityProvider provider =new HostPlatformAuthorityProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,2941,29410).Handle; ProcessHandle service =TestFixtures.Create(kernel,2942,29420).Handle;
        CapabilityId capability =kernel.MintCapability(new(29410),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"result-continuation",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        ValueTask<VirtualDomainAuthorityResponse> pending =client.CreateAsync(new(capability,new(1,1<<20)));
        BindingFlags flags =global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        FieldInfo field =typeof(RuntimeVirtualizationServiceHost).GetField("_pendingResult",flags)!;
        object? originalPayload=null;
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            Assert.Equal(1,provider.CreateVirtualDomainCallCount);
            object retained =field.GetValue(host)!;
            originalPayload=retained.GetType().GetField("Payload",flags)!.GetValue(retained);
            if(committed)
            {
                ChannelEndpointHandle endpoint =kernel.EndpointSessions.ResolveForService(session,service).Value!.ServiceEndpoint;
                Assert.True(kernel.PublishResponse(service,endpoint,1,originalPayload).IsSuccess);
            }
            throw new IOException("Original create result publication acknowledgement lost");
        };
        Assert.Throws<IOException>(()=>host.ProcessNext());
        Assert.True(host.HasPendingResult);Assert.Equal(1,provider.CreateVirtualDomainCallCount);
        object original =field.GetValue(host)!;
        field.SetValue(host,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
        Assert.False(host.AdvancePendingResult().IsSuccess);Assert.True(host.HasPendingResult);
        field.SetValue(host,original);
        Assert.True(host.AdvancePendingResult().IsSuccess);
        VirtualDomainAuthoritySet authority =(await pending).Authority;
        Assert.Equal(((VirtualDomainAuthorityResponse)originalPayload!).Authority,authority);
        Assert.False(host.HasPendingResult);Assert.Equal(1,provider.CreateVirtualDomainCallCount);
        Assert.Equal(0,provider.RevokeVirtualDomainCallCount); // Publication is not domain closure.
        Assert.False(host.AdvancePendingResult().IsSuccess);
        ValueTask configuring =client.ConfigureAsync(new(authority.Domain,authority.ConfigureCapability));
        Assert.True(host.ProcessNext().IsSuccess);await configuring;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,4096).Value!;buffer.Span.Fill(99);
        CapabilityId memory =kernel.MintCapability(new(29410),caller,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
        ValueTask<GuestRegionMappingResponse> mappingTask =client.MapGuestRegionAsync(new(authority.Domain,authority.MemoryCapability,memory,buffer.Handle,new(0x2000,4096),GuestMemoryAccess.Read|GuestMemoryAccess.Write));
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Map result retained before response");
        };
        Assert.Throws<IOException>(()=>host.ProcessNext());Assert.True(host.HasPendingResult);
        Assert.Equal((byte)99,buffer.Span[0]);
        KernelResult mapContinuation =host.AdvancePendingResult();
        Assert.True(mapContinuation.IsSuccess,$"{mapContinuation.Error}: {mapContinuation.Message}"); GuestRegionMapping mapping =(await mappingTask).Mapping;
        Assert.Equal((byte)99,buffer.Span[0]); // ModelOnly projection is not executable backing closure.
        ValueTask starting =client.StartAsync(new(authority.Domain,authority.ExecuteCapability));
        kernel.SessionInvocationSettlementReservedHook=()=>
        {
            kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Start null response retained");
        };
        Assert.Throws<IOException>(()=>host.ProcessNext());Assert.True(host.HasPendingResult);
        Assert.True(host.AdvancePendingResult().IsSuccess);await starting;
        Assert.Equal(1,provider.CreateVirtualDomainCallCount);
        ValueTask parking =client.ParkAsync(new(authority.Domain,authority.ExecuteCapability));
        Assert.True(host.ProcessNext().IsSuccess);await parking;
        ValueTask closing =client.CloseGuestRegionMappingAsync(new(authority.Domain,authority.MemoryCapability,mapping.Mapping));
        Assert.True(host.ProcessNext().IsSuccess);await closing;
        Assert.True(kernel.ReleaseRegion(caller,buffer).IsSuccess);
        Assert.True(kernel.DestroyVirtualDomain(caller,authority.Domain,authority.ConfigureCapability).IsSuccess);
        Assert.Equal(1,provider.RevokeVirtualDomainCallCount);
        Assert.True(kernel.CloseSession(caller,session).IsSuccess);
    }

    [Fact]
    public async Task AcceptedModelCreateCapacityRefusalConsumesOriginalNoEffect()
    {
        HostPlatformAuthorityProvider provider =new HostPlatformAuthorityProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider,ulong.MaxValue);
        ProcessHandle caller =TestFixtures.Create(kernel,2921,29210).Handle; ProcessHandle service =TestFixtures.Create(kernel,2922,29220).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;
        CapabilityId capability =kernel.MintCapability(new(29210),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"capacity-refusal",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        ValueTask<VirtualDomainAuthorityResponse> pending =client.CreateAsync(new(capability,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,provider.CreateVirtualDomainCallCount);Assert.Equal(0,provider.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    [Theory]
    [InlineData(0,4096)]
    [InlineData(257,4096)]
    [InlineData(1,0)]
    public async Task AcceptedCreateProfileRefusalHasOwnNoEffectAndReclaimsCaller(int processors,long bytes)
    {
        HostPlatformAuthorityProvider provider =new HostPlatformAuthorityProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,2911,29110).Handle; ProcessHandle service =TestFixtures.Create(kernel,2912,29120).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;
        CapabilityId capability =kernel.MintCapability(new(29110),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"create-refusal",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        ValueTask<VirtualDomainAuthorityResponse> pending =client.CreateAsync(new(capability,new(processors,bytes)));
        Assert.Equal(KernelError.PlatformDenied,host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(0,provider.CreateVirtualDomainCallCount);Assert.Equal(0,provider.RevokeVirtualDomainCallCount);
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    [Fact]
    public async Task FailedModelCreatePublicationClosesOriginalInvocationAfterModelCleanup()
    {
        HostPlatformAuthorityProvider provider =new HostPlatformAuthorityProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,2901,29010).Handle; ProcessHandle service =TestFixtures.Create(kernel,2902,29020).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;
        CapabilityId capability =kernel.MintCapability(new(29010),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"model-failure",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
        kernel.BeforeVirtualAuthorityPublicationQualificationHook=()=>throw new IOException("Model publication interrupted");
        ValueTask<VirtualDomainAuthorityResponse> pending =client.CreateAsync(new(capability,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        kernel.BeforeVirtualAuthorityPublicationQualificationHook=null;
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,provider.CreateVirtualDomainCallCount);Assert.Equal(1,provider.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    [Fact]
    public async Task DiscoverySessionAndTypedClientDriveCompleteLocalLifecycle()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        ProcessHandle service = TestFixtures.Create(kernel, 1200, 12000).Handle;
        ProcessHandle caller = TestFixtures.Create(kernel, 1201, 12010).Handle;
        CapabilityId createCapability = kernel.MintCapability(new DomainId(12010), caller, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = IVirtualizationServiceProtocol.CreateDefinition();
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "virtualization", contract, protocol, IVirtualizationServiceResponseProtocol.Definition, [requirement]).Value!;
        ServiceEndpointDescriptor discovered = Assert.Single(kernel.ResolveByContract(contract).Value!);
        Assert.Equal(descriptor, discovered);
        EndpointSessionHandle session = kernel.OpenSession(caller, discovered, [createCapability]).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        IVirtualizationService client = IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

        Task<VirtualDomainAuthorityResponse> createTask = client.CreateAsync(new CreateVirtualDomainRequest(createCapability, new VirtualDomainProfile(2, 1 << 20))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        VirtualDomainAuthoritySet authority = (await createTask).Authority;

        await Dispatch(client.ConfigureAsync(new VirtualDomainCommand(authority.Domain, authority.ConfigureCapability)), host);
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(caller, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(new DomainId(12010), caller, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        Task<GuestRegionMappingResponse> mapTask = client.MapGuestRegionAsync(new MapGuestRegionRequest(authority.Domain, authority.MemoryCapability, regionCapability, region.Handle, new GuestAddressRange(0x2000, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Write)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        GuestRegionMapping mapping = (await mapTask).Mapping;
        await Dispatch(client.StartAsync(new VirtualDomainCommand(authority.Domain, authority.ExecuteCapability)), host);
        KernelEventEndpoint eventEndpoint = kernel.CreateKernelEventEndpoint(caller).Value!;
        await Dispatch(client.InjectEventAsync(new VirtualEventCommand(authority.Domain, authority.EventCapability, eventEndpoint)), host);
        Task<VirtualEventResponse> waitTask = client.WaitEventAsync(new WaitVirtualEventRequest(authority.Domain, eventEndpoint)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        KernelEvent delivered = (await waitTask).Event;
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        (RuntimeKernel? kernel, ProcessHandle owner, VirtualDomainAuthoritySet authority, OwnedBuffer<byte> _, GuestRegionMapping _) = CreateMappedDomain(provider);

        KernelResult terminated = kernel.TerminateProcess(owner);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(1, provider.RevokeVirtualDomainCallCount);
    }

    [Fact]
    public void AmbiguousProviderRevokeQuarantinesDomainAndForbidsProcessReclaim()
    {
        RevokeFaultProvider provider = new RevokeFaultProvider();
        (RuntimeKernel? kernel, ProcessHandle owner, VirtualDomainAuthoritySet authority, OwnedBuffer<byte>? region, GuestRegionMapping _) = CreateMappedDomain(provider);

        KernelResult terminated = kernel.TerminateProcess(owner);

        Assert.Equal(KernelError.PlatformFaulted, terminated.Error);
        Assert.Equal(VirtualDomainState.Quarantined, kernel.QueryVirtualDomain(owner, authority.Domain).Value);
        Assert.Equal(KernelError.InvalidTransition, kernel.ReleaseRegion(owner, region).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
    }

    private static async Task Dispatch(ValueTask pending, RuntimeVirtualizationServiceHost host)
    {
        Task task = pending.AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static (RuntimeKernel Kernel, ProcessHandle Owner, VirtualDomainAuthoritySet Authority, SingPlus.Sip.OwnedBuffer<byte> Region, GuestRegionMapping Mapping) CreateMappedDomain(IPlatformAuthorityProvider provider)
    {
        RuntimeKernel kernel = new RuntimeKernel(provider);
        ProcessHandle owner = TestFixtures.Create(kernel, 1210, 12100).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(12100), owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new VirtualDomainProfile(1, 65536)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(new DomainId(12100), owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        GuestRegionMapping mapping = kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability, regionCapability, region.Handle, new GuestAddressRange(0, 4096), GuestMemoryAccess.Read).Value!;
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
