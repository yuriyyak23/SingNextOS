using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class VirtualizationServiceGuestMapClosureTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeRootWithoutDependentsRejectsCopiedOwnerInBothRoutingIndexes(bool adopted)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        NeutralDomainRuntimeFacade native = new NeutralDomainRuntimeFacade();
        IParentLossProvider proxy = DispatchProxy.Create<IParentLossProvider, ParentLossSpy>();
        ParentLossSpy spy = (ParentLossSpy)(object)proxy; spy.Inner = new HybridCpuPlatformAuthorityProvider(native); spy.LoseBind = adopted;
        RuntimeKernel kernel = new RuntimeKernel(proxy);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 3011, 30110);
        ProcessHandle admin = TestFixtures.Create(kernel, 3012, 30120).Handle;
        CapabilityId configure = kernel.MintCapability(new(30120), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, configure, owner, "root-owner", [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> backing = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(admin, 8).Value!; foreign.Span.Fill(0x7D);
        KernelResult<PlatformDomainBinding> bound = kernel.BindPlatformDomain(owner); Assert.Equal(!adopted, bound.IsSuccess);
        if (adopted)
        {
            spy.LoseBind = false;
            Assert.True(kernel.PlatformAuthority.ResolveOriginalAdmissionsForCleanup(new(new(30110), owner)).IsSuccess);
        }
        IDictionary domains = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        IDictionary subjects = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = Assert.Single(domains.Values.Cast<object>());
        PlatformDomainBinding root = (PlatformDomainBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
        object copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        domains[root.BindingId] = copy; subjects[root.Subject] = copy;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, native.ActiveBindingCount);
        Assert.Equal(1, spy.Binds);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == backing.Handle).State);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        domains[root.BindingId] = original; subjects[root.Subject] = original;
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(0, native.ActiveBindingCount);
        Assert.Equal(1, spy.Binds);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == backing.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void NativeRootCleanupRequiresOriginalBindInvocation(bool copied, bool adopted)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        NeutralDomainRuntimeFacade native = new NeutralDomainRuntimeFacade();
        IParentLossProvider proxy = DispatchProxy.Create<IParentLossProvider, ParentLossSpy>();
        ParentLossSpy spy = (ParentLossSpy)(object)proxy; spy.Inner = new HybridCpuPlatformAuthorityProvider(native); spy.LoseBind = adopted;
        RuntimeKernel kernel = new RuntimeKernel(proxy);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 3001, 30010);
        ProcessHandle admin = TestFixtures.Create(kernel, 3002, 30020).Handle;
        CapabilityId cap = kernel.MintCapability(new(30020), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, cap, owner, "original-root-fact", [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> backing = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(admin, 8).Value!; foreign.Span.Fill(0x7D);
        KernelResult<PlatformDomainBinding> root = kernel.BindPlatformDomain(owner); Assert.Equal(!adopted, root.IsSuccess);
        if (adopted)
        {
            spy.LoseBind = false;
            Assert.True(kernel.PlatformAuthority.ResolveOriginalAdmissionsForCleanup(new(new(30010), owner)).IsSuccess);
        }
        IDictionary domains = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object record = Assert.Single(domains.Values.Cast<object>());
        PropertyInfo property = record.GetType().GetProperty("BindInvocation")!; object? original = property.GetValue(record); Assert.NotNull(original);
        property.SetValue(record, copied ? typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null) : null);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, native.ActiveBindingCount);
        Assert.Equal(1, spy.Binds);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == backing.Handle).State);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        property.SetValue(record, original);
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(0, native.ActiveBindingCount);
        Assert.Equal(1, spy.Binds);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == backing.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void NativeRootBindResolutionCannotAdoptLostRetainedObservation(bool noEffect, bool substituted)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        NeutralDomainRuntimeFacade native = new NeutralDomainRuntimeFacade();
        IParentLossProvider proxy = DispatchProxy.Create<IParentLossProvider, ParentLossSpy>();
        ParentLossSpy spy = (ParentLossSpy)(object)proxy; spy.Inner = new HybridCpuPlatformAuthorityProvider(native); spy.LoseBind = true;
        RuntimeKernel kernel = new RuntimeKernel(proxy);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 2991, 29910);
        ProcessHandle admin = TestFixtures.Create(kernel, 2992, 29920).Handle;
        CapabilityId configure = kernel.MintCapability(new(29920), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, configure, owner, "root-resolution", [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> backing = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(admin, 8).Value!; foreign.Span.Fill(0x7D);
        if (noEffect) typeof(NeutralDomainRuntimeFacade).GetField("_nextHandle", flags)!.SetValue(native, ulong.MaxValue);
        Assert.False(kernel.BindPlatformDomain(owner).IsSuccess); Assert.Equal(noEffect ? 0 : 1, native.ActiveBindingCount);
        IDictionary unresolved = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_unresolvedDomainBinds", flags)!.GetValue(kernel.PlatformAuthority)!;
        object fact = Assert.Single(unresolved.Values.Cast<object>());
        PropertyInfo observation = fact.GetType().GetProperty("ResolutionObservation")!;
        spy.LoseBind = false;
        spy.BeforeSource = () =>
        {
            if (observation.GetValue(fact) is PlatformAuthorityResult<PlatformDomainBindResolution> raw)
                observation.SetValue(fact, substituted ? raw with { Value = raw.Value with { Request = raw.Value.Request with { InvocationId = Guid.NewGuid() } } } : null);
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(noEffect ? 0 : 1, native.ActiveBindingCount); Assert.Single(unresolved); Assert.Equal(1, spy.Binds);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == backing.Handle).State);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        spy.BeforeSource = null;
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(0, native.ActiveBindingCount);
        Assert.Equal(1, spy.Binds);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == backing.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeParentResolutionMustRetainObservationAcrossSourceCallbacksBeforeCleanupAdoption(bool substituted)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        NeutralDomainRuntimeFacade native = new NeutralDomainRuntimeFacade();
        IParentLossProvider proxy = DispatchProxy.Create<IParentLossProvider, ParentLossSpy>();
        ParentLossSpy spy = (ParentLossSpy)(object)proxy;
        spy.Inner = new HybridCpuPlatformAuthorityProvider(native, new HybridCpuExecutableChildAdapter());
        RuntimeKernel kernel = new RuntimeKernel(proxy);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 2981, 29810);
        ProcessHandle foreignOwner = TestFixtures.Create(kernel, 2982, 29820).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(29820), foreignOwner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(foreignOwner, budgetCap, owner, "native-parent-resolution",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 64), new(ServiceBudgetDimension.GuestMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(foreignOwner, 8).Value!; foreign.Span.Fill(0x7D);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        CapabilityId create = kernel.MintCapability(new(29810), owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        CapabilityId memory = kernel.MintCapability(new(29810), owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.False(kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, memory, buffer.Handle, new(0, 64), GuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, native.ActiveOwnedRegionMappingCount); Assert.Equal(1, spy.Maps);
        IDictionary mappings = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
        object original = Assert.Single(mappings.Values.Cast<object>());
        PropertyInfo observation = original.GetType().GetProperty("AdmissionResolutionObservation")!;
        spy.Lose = false;
        spy.BeforeSource = () =>
        {
            if (observation.GetValue(original) is PlatformAuthorityResult<PlatformOwnedRegionMapResolution> raw)
                observation.SetValue(original, substituted ? raw with { Value = raw.Value with { Request = raw.Value.Request with { InvocationId = Guid.NewGuid() } } } : null);
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, native.ActiveOwnedRegionMappingCount); Assert.Equal(1, spy.Maps);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        foreach (ServiceBudgetDimension dimension in new[] { ServiceBudgetDimension.OwnedMemoryBytes, ServiceBudgetDimension.PinnedMappedMemoryBytes, ServiceBudgetDimension.GuestMemoryBytes })
            Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == dimension).Used);
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        spy.BeforeSource = null;
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(0, native.ActiveOwnedRegionMappingCount); Assert.Equal(1, spy.Maps);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NativeMapCapacityLookupRequiresOriginalParentHistoryAndKnownDecision(int fault)
    {
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest =new NeutralOriginalDomainBindRequest(Guid.NewGuid(),((INeutralOriginalMappingClosureProvider)native).MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root =native.Bind(rootRequest).Observation.Lease;
        NeutralOriginalOwnedRegionMapRequest peerRequest =new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),rootRequest.RuntimeIncarnation,rootRequest.InvocationId,root,new(128,64,NeutralMemoryAccess.Read));
        Assert.True(native.MapOwnedRegion(peerRequest).Observation.IsMapped);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",flags)!.SetValue(native,ulong.MaxValue);
        NeutralOriginalOwnedRegionMapRequest request =peerRequest with {InvocationId=Guid.NewGuid(),Slice=new(0,64,NeutralMemoryAccess.Read)};
        NeutralOriginalOwnedRegionMapObservation raw =native.MapOwnedRegion(request);Assert.True(raw.ProvenNoEffect);Assert.False(raw.Observation.IsMapped);
        Assert.Equal(raw,native.ResolveOriginalOwnedRegionMap(request));
        Assert.Null(native.ResolveOriginalOwnedRegionMap(request with {DomainBindInvocationId=Guid.NewGuid()}));
        Assert.False(native.MapOwnedRegion(request).ProvenNoEffect);
        object dependencies =typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(native)!;
        IDictionary domains =(IDictionary)dependencies.GetType().GetField("Domains",flags)!.GetValue(dependencies)!;
        object parent =Assert.Single(domains.Values.Cast<object>());
        IList facts =(IList)parent.GetType().GetProperty("OriginalMaps")!.GetValue(parent)!;
        object original =facts[1]!;
        if(fault==0)facts[1]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
        else if(fault==1)facts.RemoveAt(1);
        else original.GetType().GetProperty("Raw")!.SetValue(original,raw with {Observation=new(){Decision=(NeutralOwnedRegionMapDecision)999}});
        Assert.Null(native.ResolveOriginalOwnedRegionMap(request));
        Assert.Equal(1,native.ActiveOwnedRegionMappingCount);Assert.Equal(1,native.ActiveBindingCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ParentCapacityNoEffectClosesOnlyOriginalFailedStageBeforeCallerReclaim(bool lost)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade(); INativeMapLossRuntime proxy =DispatchProxy.Create<INativeMapLossRuntime,NativeMapLossSpy>();
        NativeMapLossSpy spy =(NativeMapLossSpy)(object)proxy;spy.Inner=native;spy.Lose=false;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy,new HybridCpuExecutableChildAdapter()));
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel,2971,29710); ProcessHandle service =TestFixtures.Create(kernel,2972,29720).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(owner,64).Value!; OwnedBuffer<byte> peer =kernel.AllocateBuffer<byte>(owner,64).Value!;peer.Span[0]=0x6B;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;
        CapabilityId cap =kernel.MintCapability(new(29710),owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,cap,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        CapabilityId Memory(RegionHandle region)=>kernel.MintCapability(new(29710),owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(region.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> independent =kernel.MapGuestRegion(owner,domain.Domain,domain.MemoryCapability,Memory(peer.Handle),peer.Handle,new(128,64),GuestMemoryAccess.Read);
        Assert.True(independent.IsSuccess,independent.Message);
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
        spy.Lose=lost;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"parent-capacity",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        ValueTask<GuestRegionMappingResponse> pending =client.MapGuestRegionAsync(new(domain.Domain,domain.MemoryCapability,Memory(buffer.Handle),buffer.Handle,new(0,64),GuestMemoryAccess.Read));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.True(host.HasPendingResult);
        FieldInfo custodyField =host.GetType().GetField("_pendingOperation",flags)!;
        object custody =custodyField.GetValue(host)!;
        custodyField.SetValue(host,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(custody,null));
        Assert.Equal(KernelError.PlatformFaulted,host.Drain().Error);
        custodyField.SetValue(host,custody);
        FieldInfo lifetimeField =custody.GetType().GetField("Lifetime",flags)!;
        object lifetime =lifetimeField.GetValue(custody)!;
        lifetimeField.SetValue(custody,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(lifetime,null));
        Assert.Equal(KernelError.WrongSessionOwner,host.Drain().Error);
        lifetimeField.SetValue(custody,lifetime);
        EndpointSessionInvocationRegistry invocationRegistry =(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        IDictionary routes =(IDictionary)invocationRegistry.GetType().GetField("_records",flags)!.GetValue(invocationRegistry)!;
        object key =Assert.Single(routes.Keys.Cast<object>());
        object originalRecord =routes[key]!;
        routes[key]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(originalRecord,null);
        Assert.Equal(KernelError.WrongSessionOwner,host.Drain().Error);
        routes[key]=originalRecord;
        PropertyInfo outcomeProperty =originalRecord.GetType().GetProperty("SettlementOutcome")!;
        ResponseEnvelope outcome =(ResponseEnvelope)outcomeProperty.GetValue(originalRecord)!;
        outcomeProperty.SetValue(originalRecord,outcome with {});
        Assert.False(host.Drain().IsSuccess); // Equal tuple is not original cancellation.
        outcomeProperty.SetValue(originalRecord,outcome);
        Assert.True(host.AdvancePendingResult().IsSuccess); // Custody handoff only.
        Assert.False(host.HasPendingResult);
        Assert.Same(((ChannelEnvelope)custody.GetType().GetField("Request",flags)!.GetValue(custody)!),
            originalRecord.GetType().GetProperty("OriginalServiceRequest")!.GetValue(originalRecord));
        Assert.False(host.ProcessNext().IsSuccess); // Draining; no effect replay.
        Assert.Equal(2,spy.Maps);Assert.Equal(1,native.ActiveOwnedRegionMappingCount);Assert.Equal(0x6B,peer.Span[0]);
        Assert.False(kernel.AcquireRegionUse(owner,peer.Handle,RegionUseMode.ExclusiveRead,new(0,64)).IsSuccess);
        if(lost)
        {
            Assert.False(kernel.AcquireRegionUse(owner,buffer.Handle,RegionUseMode.ExclusiveRead,new(0,64)).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);spy.Lose=false;
        }
        KernelResult closed =kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.False(host.HasPendingResult);
        Assert.Equal(2,spy.Maps);Assert.Equal(0,native.ActiveOwnedRegionMappingCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==peer.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
    }
    public interface INativeMapLossRuntime : INeutralDomainRuntime,INeutralOriginalDomainCloseProvider,INeutralOriginalMapOutcomeProvider { }
    public class NativeMapLossSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=null!;
        public bool Lose=true;
        public int Maps;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOriginalOwnedRegionMap")throw new IOException("Native Map query lost");
            object? result =method!.Invoke(Inner,args);
            if(method.Name=="MapOwnedRegion"){Maps++;if(Lose)throw new IOException("Native Map reply lost after effect");}
            return result;
        }
    }
    public interface IParentLossProvider : OriginalRegionRevocationResolutionTests.IAdmissionProvider,
        IPlatformChildDomainProvider,IPlatformGuestMemoryProvider,IPlatformOriginalChildCreateProvider,
        IPlatformProviderIncarnationSource { }
    public class ParentLossSpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner=null!;
        public bool Lose=true;
        public int Maps;
        public Action? BeforeSource;
        public bool LoseBind;
        public int Binds;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if (method!.Name == "get_CurrentIncarnation") BeforeSource?.Invoke();
            if (LoseBind && method.Name == "ResolveDomainBind") throw new IOException("Original root query lost");
            if(Lose && method!.Name=="ResolveOwnedRegionMap")throw new IOException("Original parent query lost");
            object? result =method!.Invoke(Inner,args);
            if (method.Name == "BindDomain") Binds++;
            if (LoseBind && method.Name == "BindDomain") throw new IOException("Original root Bind reply lost");
            if(method.Name=="MapOwnedRegionSlice")
            {Maps++;if(Lose)throw new IOException("Original parent reply lost after effect");}
            return result;
        }
    }
    public class MapLossSpy : ExactNestedGuestConsumerTests.GuestSpy
    {
        public bool Lose=true;
        public Action? AfterMap;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveOriginalGuestMap")throw new IOException("Original Map query lost");
            object? result =base.Invoke(method,args);
            if(method!.Name=="MapOriginalGuest")AfterMap?.Invoke();
            if(Lose && method!.Name=="MapOriginalGuest")throw new IOException("Original Map reply lost after effect");
            return result;
        }
    }

    [Theory]
    [InlineData(false,false,false,false)] [InlineData(true,false,false,false)] [InlineData(false,true,false,false)] [InlineData(false,false,true,false)]
    [InlineData(false,false,false,true)]
    public async Task OriginalServiceMapClosesInvocationAfterExactGuestParentAndCharge(bool copiedOwner,bool parentLoss,bool nativeLoss,bool closeRoute)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        ExactNestedArtifactConsumerTests.IOriginalNestedStartRuntime runtime =DispatchProxy.Create<ExactNestedArtifactConsumerTests.IOriginalNestedStartRuntime,MapLossSpy>();
        MapLossSpy spy =(MapLossSpy)(object)runtime;
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
        INativeMapLossRuntime nativeProxy =DispatchProxy.Create<INativeMapLossRuntime,NativeMapLossSpy>();
        NativeMapLossSpy nativeSpy =(NativeMapLossSpy)(object)nativeProxy;nativeSpy.Inner=native;
        HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(nativeLoss?nativeProxy:native,new HybridCpuExecutableChildAdapter(spy.Inner,runtime));
        IParentLossProvider parentProxy =DispatchProxy.Create<IParentLossProvider,ParentLossSpy>();
        ParentLossSpy parentSpy =(ParentLossSpy)(object)parentProxy;parentSpy.Inner=provider;
        spy.Lose=!parentLoss && !nativeLoss;
        RuntimeKernel kernel =new RuntimeKernel(parentLoss?parentProxy:provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel,2601,26010);
        ProcessHandle service =TestFixtures.Create(kernel,2602,26020).Handle;
        ProcessHandle admin =TestFixtures.Create(kernel,2603,26030).Handle;
        OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x70;
        const ulong bytes=1024*1024;
        CapabilityId ac =kernel.MintCapability(new(26030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,ac,owner,"Map service",
            [new(ServiceBudgetDimension.OwnedMemoryBytes,bytes*2),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,bytes),new(ServiceBudgetDimension.GuestMemoryBytes,bytes)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(owner,(int)bytes).Value!;
        CapabilityId create =kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain =kernel.CreateVirtualDomain(owner,create,new(1,(long)bytes)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        CapabilityId memory =kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Execute).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"Map service",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
        RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        if(closeRoute)spy.AfterMap=()=>
        {
            Assert.Equal(KernelError.InvalidTransition,host.ProcessNext().Error);
            EndpointSessionRegistry.Record originalSession =kernel.EndpointSessions.ResolveForService(session,service).Value!;
            Assert.True(kernel.Channels.Close(originalSession.ServiceEndpoint).IsSuccess);
        };
        ValueTask<GuestRegionMappingResponse> pending =client.MapGuestRegionAsync(new(domain.Domain,domain.MemoryCapability,memory,buffer.Handle,new(0,(long)bytes),GuestMemoryAccess.Read|GuestMemoryAccess.Execute){MaximumPrivateExecutableDataBytes=bytes});
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.True(host.HasPendingResult);
        Assert.True(host.AdvancePendingResult().IsSuccess); // Provider/map/charges remain pinned below.
        Assert.False(host.ProcessNext().IsSuccess); // No second request/effect.
        Assert.Equal(bytes*2,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.AcquireRegionUse(owner,buffer.Handle,RegionUseMode.ExclusiveRead,new(0,(long)bytes)).IsSuccess);
        EndpointSessionInvocationRegistry registry =(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        IDictionary records =(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        object record =Assert.Single(records.Values.Cast<object>());
        Assert.NotNull(record.GetType().GetProperty("OriginalResponse")!.GetValue(record));
        PropertyInfo property =record.GetType().GetProperty("OriginalVirtualArtifactOwner")!;
        object original =property.GetValue(record)!;
        GuestRegionMappingHandle mapping =(GuestRegionMappingHandle)record.GetType().GetProperty("VirtualArtifactMapping")!.GetValue(record)!;
        Assert.False(registry.ConsumeVirtualArtifactMappingClosure(service,original,mapping).IsSuccess);
        Assert.False(registry.ConsumeVirtualArtifactMappingClosure(owner,new object(),mapping).IsSuccess);
        spy.Lose=false;
        if(parentLoss || nativeLoss)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(bytes*2,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(bytes,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.GuestMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            Assert.Equal(1,native.ActiveOwnedRegionMappingCount);Assert.Equal(1,nativeLoss?nativeSpy.Maps:parentSpy.Maps);
        }
        parentSpy.Lose=false;
        nativeSpy.Lose=false;
        if(copiedOwner)
        {
            property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False((bool)record.GetType().GetProperty("VirtualArtifactContained")!.GetValue(record)!);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            property.SetValue(record,original);
        }
        KernelResult closed =kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.False(host.HasPendingResult);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        if(parentLoss || nativeLoss){Assert.Equal(1,nativeLoss?nativeSpy.Maps:parentSpy.Maps);Assert.Equal(0,spy.Calls.GetValueOrDefault("MapOriginalGuest"));}
        else{Assert.Equal(1,spy.Calls["MapOriginalGuest"]);Assert.Equal(1,spy.Calls["UnmapOriginalGuest"]);}
        Assert.Equal(0,native.ActiveOwnedRegionMappingCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(0x70,foreign.Span[0]);Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
}
