using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;
namespace SingPlus.Tests.Virtualization;

public sealed class OriginalModelCreateProducerTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ImmediateOriginalNoEffectRequiresRetainedRawAfterSourceCallback(bool copied)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IRuntime proxy = DispatchProxy.Create<IRuntime, Spy>(); Spy spy = (Spy)(object)proxy; spy.Lose = false; spy.Query = "exact";
        typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId", flags)!.SetValue(spy.Inner, ulong.MaxValue);
        RuntimeKernel kernel = new RuntimeKernel(proxy); ProcessHandle caller = TestFixtures.Create(kernel, 3121, 31210).Handle; ProcessHandle service = TestFixtures.Create(kernel, 3122, 31220).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(31220), service, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, budgetCap, caller, "immediate-no-effect", [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 8).Value!; foreign.Span.Fill(0x7D);
        CapabilityId cap = kernel.MintCapability(new(31210), caller, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "immediate-no-effect", new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, []).Value; RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        object? fact = null; PlatformOriginalModelCreateObservation? raw = null;
        spy.AfterSource = () =>
        {
            IDictionary pending = (global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_pendingVirtualDomainCreates", flags)!.GetValue(kernel.PlatformAuthority)!;
            if (pending.Count != 1) return;
            object current = pending.Values.Cast<object>().Single();
            if (current.GetType().GetProperty("Observation")!.GetValue(current) is null) return;
            fact = current; FieldInfo field = current.GetType().GetField("OriginalObservation")!;
            raw = (PlatformOriginalModelCreateObservation)field.GetValue(current)!;
            field.SetValue(current, copied ? raw with { } : null);
        };
        ValueTask<VirtualDomainAuthorityResponse> reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, caller, session)).CreateAsync(new(cap, new(1, 4096)));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.NotNull(fact); Assert.NotNull(raw);
        IDictionary unresolved = (global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_unresolvedVirtualDomainCreates", flags)!.GetValue(kernel.PlatformAuthority)!;
        Assert.Single(unresolved); Assert.Equal(1, spy.Inner.CreateVirtualDomainCallCount); Assert.Equal(0, spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.AfterSource = null; fact!.GetType().GetField("OriginalObservation")!.SetValue(fact, raw);
        KernelResult closed = kernel.TerminateProcess(caller); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, spy.Inner.CreateVirtualDomainCallCount); Assert.Equal(0, spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7D, x));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void DeliveredModelRequiresOriginalCreationRawUntilLocalClosure(bool copied, bool destroy)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(); RuntimeKernel kernel = new RuntimeKernel(provider);
        ProcessHandle caller = TestFixtures.Create(kernel, 3111, 31110).Handle; ProcessHandle admin = TestFixtures.Create(kernel, 3112, 31120).Handle;
        CapabilityId configure = kernel.MintCapability(new(31120), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, configure, caller, "delivered-model-raw", [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(admin, 8).Value!; foreign.Span.Fill(0x7D);
        CapabilityId cap = kernel.MintCapability(new(31110), caller, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(caller, cap, new(1, 4096)).Value!;
        IDictionary owners = (global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_originalVirtualDomainOwners", flags)!.GetValue(kernel.PlatformAuthority)!;
        object record = Assert.Single(owners.Values.Cast<object>());
        object fact = record.GetType().GetProperty("CreateInvocation")!.GetValue(record)!;
        FieldInfo field = fact.GetType().GetField("OriginalObservation")!;
        PlatformOriginalModelCreateObservation original = (PlatformOriginalModelCreateObservation)field.GetValue(fact)!;
        field.SetValue(fact, copied ? original with { } : null);
        KernelResult refused = destroy ? kernel.DestroyVirtualDomain(caller, domain.Domain, domain.ConfigureCapability) : kernel.ConfigureVirtualDomain(caller, domain.Domain, domain.ConfigureCapability);
        Assert.False(refused.IsSuccess); Assert.Equal(0, provider.RevokeVirtualDomainCallCount);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        field.SetValue(fact, original);
        KernelResult closed = kernel.TerminateProcess(caller); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, provider.CreateVirtualDomainCallCount); Assert.Equal(1, provider.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7D, x));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task GeneratedModelResolutionRetainsCanonicalObservationAcrossSourceCallback(bool noEffect, bool copied)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IRuntime proxy = DispatchProxy.Create<IRuntime, Spy>(); Spy spy = (Spy)(object)proxy; RuntimeKernel kernel = new RuntimeKernel(proxy);
        if (noEffect) typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId", flags)!.SetValue(spy.Inner, ulong.MaxValue);
        ProcessHandle caller = TestFixtures.Create(kernel, 3101, 31010).Handle; ProcessHandle service = TestFixtures.Create(kernel, 3102, 31020).Handle;
        CapabilityId admin = kernel.MintCapability(new(31020), service, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, caller, "model-raw", [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 8).Value!; foreign.Span.Fill(0x7D);
        CapabilityId cap = kernel.MintCapability(new(31010), caller, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "model-raw", new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, []).Value; RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        ValueTask<VirtualDomainAuthorityResponse> reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, caller, session)).CreateAsync(new(cap, new(1, 4096)));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        IDictionary routes = (global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_unresolvedVirtualDomainCreates", flags)!.GetValue(kernel.PlatformAuthority)!;
        object fact = Assert.Single(routes.Values.Cast<object>()); FieldInfo field = fact.GetType().GetField("OriginalObservation")!;
        spy.Query = "exact";
        spy.AfterSource = () =>
        {
            if (field.GetValue(fact) is PlatformOriginalModelCreateObservation raw)
                field.SetValue(fact, copied ? raw with { } : null);
        };
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);
        Assert.Equal(0, spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        spy.AfterSource = null;
        KernelResult closed = kernel.TerminateProcess(caller); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, spy.Inner.CreateVirtualDomainCallCount); Assert.Equal(noEffect ? 0 : 1, spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7D, x));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task GeneratedModelCleanupRequiresOriginalCreationConsumer(bool noEffect, bool substituted)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IRuntime proxy = DispatchProxy.Create<IRuntime, Spy>(); Spy spy = (Spy)(object)proxy; RuntimeKernel kernel = new RuntimeKernel(proxy);
        if (noEffect) typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId", flags)!.SetValue(spy.Inner, ulong.MaxValue);
        ProcessHandle caller = TestFixtures.Create(kernel, 3091, 30910).Handle; ProcessHandle service = TestFixtures.Create(kernel, 3092, 30920).Handle;
        CapabilityId admin = kernel.MintCapability(new(30920), service, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, caller, "model-consumer", [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 8).Value!; foreign.Span.Fill(0x7D);
        CapabilityId cap = kernel.MintCapability(new(30910), caller, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "model-consumer", new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, []).Value; RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        ValueTask<VirtualDomainAuthorityResponse> reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, caller, session)).CreateAsync(new(cap, new(1, 4096)));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        IDictionary routes = (global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_unresolvedVirtualDomainCreates", flags)!.GetValue(kernel.PlatformAuthority)!;
        object fact = Assert.Single(routes.Values.Cast<object>());
        PropertyInfo property = fact.GetType().GetProperty(noEffect ? "LocalCreationNoEffectConsumer" : "LocalCreationClosureConsumer")!;
        object? original = property.GetValue(fact); Assert.NotNull(original);
        property.SetValue(fact, substituted ? new Func<KernelResult>(() => KernelResult.Ok()) : null);
        spy.Query = "exact";
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);
        Assert.Equal(0, spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        property.SetValue(fact, original);
        KernelResult closed = kernel.TerminateProcess(caller); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, spy.Inner.CreateVirtualDomainCallCount); Assert.Equal(noEffect ? 0 : 1, spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7D, x));
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task SourceCallbackCannotReplaceCapturedModelCreateRequest(bool copied,bool afterEffect)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)proxy;spy.Lose=false;spy.Query="exact";
        RuntimeKernel kernel =new RuntimeKernel(proxy); ProcessHandle caller =TestFixtures.Create(kernel,3081,30810).Handle; ProcessHandle service =TestFixtures.Create(kernel,3082,30820).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        ProcessHandle foreignOwner =TestFixtures.Create(kernel,3083,30830).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30810),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol(); ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"request-source",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        int changed =0;Action? restore=null;
        spy.AfterSource=()=>
        {
            IDictionary pending =(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_pendingVirtualDomainCreates",flags)!.GetValue(kernel.PlatformAuthority)!;
            if(pending.Count==0)return;
            object fact =Assert.Single(pending.Values.Cast<object>());
            if((bool)fact.GetType().GetProperty("EffectMayHaveOccurred")!.GetValue(fact)!!=afterEffect)return;
            spy.AfterSource=null;changed++;
            FieldInfo field =fact.GetType().GetField("<OriginalRequest>k__BackingField",flags)!;
            PlatformOriginalModelCreateRequest original =(PlatformOriginalModelCreateRequest)field.GetValue(fact)!;
            restore=()=>field.SetValue(fact,original);
            field.SetValue(fact,copied?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null):new PlatformOriginalModelCreateRequest(original.Owner,original.Profile));
        };
        ValueTask<VirtualDomainAuthorityResponse> reply =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.Equal(1,changed);Assert.Equal(afterEffect?1:0,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        if(afterEffect)
        {
            Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            restore!(); KernelResult closed =kernel.TerminateProcess(service);Assert.True(closed.IsSuccess,closed.Message);
            Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);Assert.Equal(0x48,buffer.Span[0]);
        }
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);if(!afterEffect)Assert.True(kernel.TerminateProcess(service).IsSuccess);
        Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeliveredModelDualIndexCopyCannotAdmitConfigureOrDestroy(bool destroy)
    {
        HostPlatformAuthorityProvider provider =new HostPlatformAuthorityProvider(); RuntimeKernel kernel =new RuntimeKernel(provider);
        ProcessHandle caller =TestFixtures.Create(kernel,3071,30710).Handle; ProcessHandle admin =TestFixtures.Create(kernel,3072,30720).Handle;
        CapabilityId budgetCap =kernel.MintCapability(new(30720),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,budgetCap,caller,"delivered-owner",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(admin,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30710),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        VirtualDomainAuthoritySet other =kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        PlatformAuthorityBridge bridge =kernel.PlatformAuthority;
        IDictionary owners =(global::System.Collections.IDictionary)bridge.GetType().GetField("_originalVirtualDomainOwners",flags)!.GetValue(bridge)!;
        IDictionary routes =(global::System.Collections.IDictionary)bridge.GetType().GetField("_virtualDomainBindings",flags)!.GetValue(bridge)!;
        object key =owners.Keys.Cast<object>().OrderBy(k=>((PlatformAuthorityBridge.VirtualDomainBindingId)k).Value).First();
        object original =owners[key]!; object copy =typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null)!;
        owners[key]=copy;routes[key]=copy;
        KernelResult refused =destroy?kernel.DestroyVirtualDomain(caller,domain.Domain,domain.ConfigureCapability):kernel.ConfigureVirtualDomain(caller,domain.Domain,domain.ConfigureCapability);
        Assert.False(refused.IsSuccess);Assert.Equal(0,provider.RevokeVirtualDomainCallCount);Assert.Equal(64UL,Used());
        Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.True(kernel.ConfigureVirtualDomain(caller,other.Domain,other.ConfigureCapability).IsSuccess);
        owners[key]=original;routes[key]=original;
        Assert.True(kernel.DestroyVirtualDomain(caller,domain.Domain,domain.ConfigureCapability).IsSuccess);Assert.Equal(1,provider.RevokeVirtualDomainCallCount);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(2,provider.RevokeVirtualDomainCallCount);Assert.Equal(0UL,Used());
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(admin).IsSuccess);
    }

    [Theory]
    [InlineData("missing")] [InlineData("copy")] [InlineData("foreign")]
    public void RecoveredOwnerRoutingCannotReplacePartialOriginalClosure(string fault)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)proxy; RuntimeKernel kernel =new RuntimeKernel(proxy);
        ProcessHandle caller =TestFixtures.Create(kernel,3061,30610).Handle; ProcessHandle admin =TestFixtures.Create(kernel,3062,30620).Handle;
        CapabilityId foreignCap =kernel.MintCapability(new(30620),admin,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=false;spy.Query="exact"; VirtualDomainAuthoritySet live =kernel.CreateVirtualDomain(admin,foreignCap,new(1,4096)).Value!;
        CapabilityId budgetCap =kernel.MintCapability(new(30620),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,budgetCap,caller,"recovered-owner",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(admin,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30610),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=true;spy.Query="lost";Assert.False(kernel.CreateVirtualDomain(caller,cap,new(1,4096)).IsSuccess);
        spy.Query="exact";spy.AfterRevoke=()=>{spy.AfterRevoke=null;spy.ThrowSource=true;};
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);Assert.Equal(64UL,Used());
        spy.ThrowSource=false;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        PlatformAuthorityBridge bridge =kernel.PlatformAuthority;
        IDictionary owners =(global::System.Collections.IDictionary)bridge.GetType().GetField("_originalVirtualDomainOwners",flags)!.GetValue(bridge)!;
        IDictionary routes =(global::System.Collections.IDictionary)bridge.GetType().GetField("_virtualDomainBindings",flags)!.GetValue(bridge)!;
        object key =owners.Keys.Cast<object>().Single(k=>((PlatformAuthorityBridge.VirtualDomainBinding)owners[k]!.GetType().GetProperty("Binding")!.GetValue(owners[k])!).Owner==caller);
        object original =owners[key]!; object foreignModel =owners.Values.Cast<object>().Single(v=>!ReferenceEquals(v,original));
        if(fault=="missing"){owners.Remove(key);routes.Remove(key);}
        else { object? replacement =fault=="copy"?typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null):foreignModel;owners[key]=replacement;routes[key]=replacement;}
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        if(fault=="missing"){Assert.False(owners.Contains(key));Assert.False(routes.Contains(key));}
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.ConfigureVirtualDomain(admin,live.Domain,live.ConfigureCapability).IsSuccess);
        owners[key]=original;routes[key]=original;
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(2,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(admin).IsSuccess);Assert.Equal(2,spy.Inner.RevokeVirtualDomainCallCount);
    }

    [Theory]
    [InlineData("raw-copy")] [InlineData("raw-foreign")] [InlineData("missing")] [InlineData("fact-copy")] [InlineData("fact-foreign")]
    public async Task GeneratedCleanupRejectsReplacedProviderRawAndPreservesSameCallerLiveModel(string fault)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)proxy; RuntimeKernel kernel =new RuntimeKernel(proxy);
        ProcessHandle caller =TestFixtures.Create(kernel,3051,30510).Handle; ProcessHandle service =TestFixtures.Create(kernel,3052,30520).Handle;
        ProcessHandle foreignOwner =TestFixtures.Create(kernel,3053,30530).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30510),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=false;spy.Query="exact"; VirtualDomainAuthoritySet live =kernel.CreateVirtualDomain(caller,cap,new(1,4096)).Value!;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        IDictionary facts =(global::System.Collections.IDictionary)spy.Inner.GetType().GetField("_modelCreates",flags)!.GetValue(spy.Inner)!;
        object liveFact =Assert.Single(facts.Values.Cast<object>());
        PlatformOriginalModelCreateObservation liveRaw =(PlatformOriginalModelCreateObservation)liveFact.GetType().GetField("Raw")!.GetValue(liveFact)!;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"raw-custody",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        spy.Lose=true;spy.Query="lost";
        ValueTask<VirtualDomainAuthorityResponse> reply =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        object fact =facts.Values.Cast<object>().Single(f=>!ReferenceEquals(f,liveFact)); FieldInfo field =fact.GetType().GetField("Raw")!;
        PlatformOriginalModelCreateObservation original =(PlatformOriginalModelCreateObservation)field.GetValue(fact)!;
        if(fault=="missing")facts.Remove(original.Request);
        else if(fault=="fact-copy")facts[original.Request]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(fact,null);
        else if(fault=="fact-foreign")facts[original.Request]=liveFact;
        else field.SetValue(fact,fault=="raw-foreign"?original with {Outcome=liveRaw.Outcome}:original with {});
        spy.Lose=false;spy.Query="exact";
        CapabilityId otherCap =kernel.MintCapability(new(30530),foreignOwner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(foreignOwner,otherCap,new(1,4096)).IsSuccess);
        PlatformAuthorityResult<PlatformProviderVirtualDomainLease> legacy =spy.Inner.CreateVirtualDomain(original.Request.Owner,original.Request.Profile);
        Assert.Equal(PlatformAuthorityStatus.NotAccepted,legacy.Status);
        Assert.Equal(2,spy.Inner.CreateVirtualDomainCallCount);
        spy.Query="exact";Assert.False(kernel.TerminateProcess(service).IsSuccess);
        Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.True(kernel.ConfigureVirtualDomain(caller,live.Domain,live.ConfigureCapability).IsSuccess);
        facts[original.Request]=fact;field.SetValue(fact,original); KernelResult closed =kernel.TerminateProcess(service);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(2,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.True(kernel.QueryVirtualDomain(caller,live.Domain).IsSuccess);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(2,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void QuerySourceSubstitutionRetainsOriginalAndIndependentLiveModel(bool noEffect)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)proxy; RuntimeKernel kernel =new RuntimeKernel(proxy);
        ProcessHandle caller =TestFixtures.Create(kernel,3041,30410).Handle; ProcessHandle foreignOwner =TestFixtures.Create(kernel,3042,30420).Handle;
        CapabilityId foreignCap =kernel.MintCapability(new(30420),foreignOwner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        spy.Lose=false;spy.Query="exact"; VirtualDomainAuthoritySet live =kernel.CreateVirtualDomain(foreignOwner,foreignCap,new(1,4096)).Value!;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        CapabilityId budgetCap =kernel.MintCapability(new(30420),foreignOwner,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(foreignOwner,budgetCap,caller,"source-substitution",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        spy.Lose=true;spy.Query="lost";
        CapabilityId cap =kernel.MintCapability(new(30410),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(caller,cap,new(1,4096)).IsSuccess);
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,2UL);
        PlatformProviderIncarnation source =spy.Inner.CurrentIncarnation;
        spy.Query="exact";spy.AfterQuery=()=>{spy.AfterQuery=null;spy.SourceOverride=new(source.Value==ulong.MaxValue?1:source.Value+1);};
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.QueryVirtualDomain(foreignOwner,live.Domain).IsSuccess);
        spy.SourceOverride=null;Assert.True(kernel.ConfigureVirtualDomain(foreignOwner,live.Domain,live.ConfigureCapability).IsSuccess);
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(2,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(noEffect?0:1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
        Assert.Equal(noEffect?1:2,spy.Inner.RevokeVirtualDomainCallCount);
    }

    [Theory]
    [InlineData(false,false,false,false)] [InlineData(true,false,false,false)] [InlineData(false,true,false,false)] [InlineData(true,true,false,false)]
    [InlineData(false,false,false,true)] [InlineData(true,false,false,true)]
    [InlineData(false,false,true,false)]
    public void DirectCallerExitQueriesOriginalLostModelCreation(bool noEffect,bool reentry,bool partial,bool copiedFact)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)proxy; RuntimeKernel kernel =new RuntimeKernel(proxy);
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        ProcessHandle caller =TestFixtures.Create(kernel,3031,30310).Handle; ProcessHandle admin =TestFixtures.Create(kernel,3032,30320).Handle;
        CapabilityId budgetCap =kernel.MintCapability(new(30320),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,budgetCap,caller,"model-direct",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(admin,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30310),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        Assert.False(kernel.CreateVirtualDomain(caller,cap,new(1,4096)).IsSuccess);
        if(copiedFact)
        {
            IDictionary routes =(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_unresolvedVirtualDomainCreates",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
            object key =Assert.Single(routes.Keys.Cast<object>()); object original =routes[key]!;routes.Remove(key);
            Assert.False(kernel.CreateVirtualDomain(caller,cap,new(1,4096)).IsSuccess);
            Assert.Equal(1,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(64UL,Used());
            routes[key]=original;
        }
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        spy.Query="copy";Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        spy.Query="exact";
        if(copiedFact)
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            IDictionary unresolved =(global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_unresolvedVirtualDomainCreates",flags)!.GetValue(kernel.PlatformAuthority)!;
            object key =Assert.Single(unresolved.Keys.Cast<object>()); object original =unresolved[key]!;
            unresolved[key]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
            Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);Assert.Equal(0x71,foreign.Span[0]);
            unresolved[key]=original;
            unresolved.Remove(key);
            Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);Assert.Equal(0x71,foreign.Span[0]);
            unresolved[key]=original;
        }
        if(reentry)spy.AfterQuery=()=>
        {
            spy.AfterQuery=null;
            PlatformDomainIdentity subject =new PlatformDomainIdentity(new(30310),caller);
            Task<KernelResult> worker =Task.Run(()=>kernel.PlatformAuthority.CloseUnresolvedModelCreations(subject));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));Assert.False(worker.Result.IsSuccess);
            Assert.Equal(64UL,Used());Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        };
        if(partial)
        {
            spy.AfterRevoke=()=>{spy.AfterRevoke=null;spy.ThrowSource=true;};
            Assert.False(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(64UL,Used());
            Assert.Equal(1,spy.Inner.RevokeVirtualDomainCallCount);Assert.Equal(0x71,foreign.Span[0]);
            spy.ThrowSource=false;
        }
        KernelResult closed =kernel.TerminateProcess(caller);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(noEffect?0:1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0UL,Used());Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(admin).IsSuccess);
    }

    public interface IRuntime : IPlatformAuthorityProvider,IPlatformFeatureProvider,IPlatformVirtualizationProvider,
        IPlatformProviderIncarnationSource,IPlatformOriginalModelCreateProvider,IPlatformOriginalVirtualDomainRevokeProvider { }
    public class Spy : DispatchProxy
    {
        public HostPlatformAuthorityProvider Inner=new();public bool Lose=true;public string Query="lost";
        public Action? AfterQuery;public Action? AfterSource;
        public Action? AfterRevoke;public bool ThrowSource;
        public PlatformProviderIncarnation? SourceOverride;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method!.Name=="get_CurrentIncarnation" && ThrowSource)throw new IOException("source observation lost");
            if(method.Name=="get_CurrentIncarnation" && SourceOverride is { } source)return source;
            if(method!.Name=="QueryOriginalModelCreate" && Query=="lost")throw new IOException("query lost");
            object? result =method.Invoke(Inner,args);
            if(method.Name=="get_CurrentIncarnation")AfterSource?.Invoke();
            if(method.Name=="QueryOriginalModelCreate")AfterQuery?.Invoke();
            if(method.Name=="RevokeOriginalVirtualDomain")AfterRevoke?.Invoke();
            if(method.Name=="CreateOriginalModelDomain" && Lose)throw new IOException("reply lost after effect");
            if(method.Name=="QueryOriginalModelCreate" && Query=="copy" && result is PlatformOriginalModelCreateObservation raw)return raw with {};
            return result;
        }
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task GeneratedLostCreateUsesSameOriginalQueryAndIndependentReclaim(bool copy,bool noEffect)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,Spy>(); Spy spy =(Spy)(object)proxy; RuntimeKernel kernel =new RuntimeKernel(proxy);
        if(noEffect)typeof(HostPlatformAuthorityProvider).GetField("_nextVirtualDomainId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        ProcessHandle caller =TestFixtures.Create(kernel,3021,30210).Handle; ProcessHandle service =TestFixtures.Create(kernel,3022,30220).Handle;
        ProcessHandle foreignOwner =TestFixtures.Create(kernel,3023,30230).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(caller,64).Value!;buffer.Span[0]=0x48;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(foreignOwner,16).Value!;foreign.Span[0]=0x71;
        CapabilityId cap =kernel.MintCapability(new(30210),caller,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"original-model",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(caller,descriptor,[]).Value; RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        ValueTask<VirtualDomainAuthorityResponse> reply =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session)).CreateAsync(new(cap,new(1,4096)));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await reply);
        Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(1,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);
        if(copy){spy.Query="copy";Assert.False(kernel.TerminateProcess(service).IsSuccess);Assert.Equal(0,spy.Inner.RevokeVirtualDomainCallCount);}
        spy.Query="exact"; KernelResult closed =kernel.TerminateProcess(service);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Inner.CreateVirtualDomainCallCount);Assert.Equal(noEffect?0:1,spy.Inner.RevokeVirtualDomainCallCount);
        Assert.Equal(0x48,buffer.Span[0]);Assert.Equal(0x71,foreign.Span[0]);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(0x71,foreign.Span[0]);Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
    }
}
